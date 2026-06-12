using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Amazon;
using Amazon.BedrockRuntime;
using Jarvis.AiGateway.Middleware;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

var gatewayOptions = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwtOptions.Authority;
        options.Audience = jwtOptions.Audience;
        options.RequireHttpsMetadata = jwtOptions.RequireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        if (!string.IsNullOrWhiteSpace(jwtOptions.ValidIssuer))
        {
            options.TokenValidationParameters.ValidIssuer = jwtOptions.ValidIssuer;
        }

        // This allows Open WebUI or an identity-aware reverse proxy to pass the user's IdP token
        // in X-Jarvis-User-Token while using X-Jarvis-Gateway-Key for service-to-service auth.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Headers.TryGetValue(gatewayOptions.UserTokenHeader, out var forwardedToken) &&
                    !string.IsNullOrWhiteSpace(forwardedToken))
                {
                    context.Token = forwardedToken.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
                    return Task.CompletedTask;
                }

                var authorization = context.Request.Headers.Authorization.ToString();
                if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = authorization["Bearer ".Length..].Trim();
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-user", httpContext =>
    {
        var key = httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.Identity?.Name
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, gatewayOptions.RateLimit.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, gatewayOptions.RateLimit.WindowSeconds)),
            QueueLimit = Math.Max(0, gatewayOptions.RateLimit.QueueLimit),
            AutoReplenishment = true
        });
    });
});

builder.Services.AddSingleton<IAmazonBedrockRuntime>(_ =>
{
    var region = RegionEndpoint.GetBySystemName(gatewayOptions.AwsRegion);
    var config = new AmazonBedrockRuntimeConfig
    {
        RegionEndpoint = region,
        AuthenticationRegion = gatewayOptions.AwsRegion
    };

    if (!string.IsNullOrWhiteSpace(gatewayOptions.BedrockRuntimeEndpointDns))
    {
        var endpoint = gatewayOptions.BedrockRuntimeEndpointDns.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? gatewayOptions.BedrockRuntimeEndpointDns
            : $"https://{gatewayOptions.BedrockRuntimeEndpointDns}";
        config.ServiceURL = endpoint;
    }

    return new AmazonBedrockRuntimeClient(config);
});

builder.Services.AddSingleton<IUserContextFactory, UserContextFactory>();
builder.Services.AddSingleton<IRequestContextFactory, RequestContextFactory>();
builder.Services.AddSingleton<IContentRedactor, RegexContentRedactor>();
builder.Services.AddSingleton<IPolicyEngine, PolicyEngine>();
builder.Services.AddSingleton<IBedrockChatClient, BedrockConverseChatClient>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ServiceApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = gatewayOptions.ServiceName,
    environment = gatewayOptions.EnvironmentName,
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/v1/models", (
        ClaimsPrincipal principal,
        IUserContextFactory userContextFactory,
        IPolicyEngine policyEngine) =>
    {
        var user = userContextFactory.Create(principal);
        var response = new OpenAiModelListResponse
        {
            Data = policyEngine.GetVisibleModels(user)
                .Select(m => new OpenAiModelInfo { Id = m.Alias, OwnedBy = "jarvis-ai-gateway" })
                .ToList()
        };

        return Results.Json(response);
    })
    .RequireAuthorization();

app.MapPost("/v1/chat/completions", async (
        HttpContext httpContext,
        OpenAiChatCompletionRequest request,
        IUserContextFactory userContextFactory,
        IRequestContextFactory requestContextFactory,
        IPolicyEngine policyEngine,
        IBedrockChatClient bedrockClient,
        IContentRedactor redactor,
        IAuditLogger auditLogger,
        IOptions<GatewayOptions> options,
        CancellationToken cancellationToken) =>
    {
        var stopwatch = Stopwatch.StartNew();
        var user = userContextFactory.Create(httpContext.User);
        var requestContext = requestContextFactory.Create(httpContext, request);
        var promptText = string.Join("\n", request.Messages.Select(m => m.GetTextContent()));
        var audit = new GatewayAuditEvent
        {
            RequestId = requestContext.RequestId,
            CorrelationId = requestContext.CorrelationId,
            UserSubject = user.Subject,
            UserEmail = user.Email,
            UserGroups = user.Groups.ToArray(),
            WorkspaceId = requestContext.WorkspaceId,
            DataLabel = requestContext.DataLabel,
            ItarMode = requestContext.ItarMode,
            RequestedModelAlias = request.Model,
            Region = options.Value.AwsRegion,
            EndpointMode = string.IsNullOrWhiteSpace(options.Value.BedrockRuntimeEndpointDns)
                ? "regional-dns"
                : "vpce-override",
            PromptCharacters = promptText.Length
        };

        var logRedaction = options.Value.Redaction.RedactBeforeLogging
            ? redactor.Redact(promptText)
            : new RedactionResult(promptText, 0);
        audit.RedactionCount = logRedaction.RedactionCount;

        var decision = policyEngine.Authorize(user, requestContext, request);
        audit.Decision = decision.Allowed ? "ALLOW" : "DENY";
        audit.DenyReason = decision.Allowed ? null : decision.Reason;
        audit.ResolvedBedrockModelId = decision.Model?.BedrockModelId;

        if (!decision.Allowed || decision.Model is null)
        {
            stopwatch.Stop();
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(
                OpenAiErrorResponse.Create(decision.Reason, "policy_denied", "policy_denied"),
                cancellationToken);
            return;
        }

        try
        {
            var bedrockResult = await bedrockClient.CompleteAsync(request, decision.Model, requestContext, cancellationToken);
            stopwatch.Stop();

            audit.InputTokens = bedrockResult.InputTokens;
            audit.OutputTokens = bedrockResult.OutputTokens;
            audit.TotalTokens = bedrockResult.TotalTokens;
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);

            if (request.Stream)
            {
                await WriteOpenAiCompatibleStreamAsync(httpContext, request.Model, bedrockResult.Text, bedrockResult.StopReason, cancellationToken);
                return;
            }

            var response = new OpenAiChatCompletionResponse
            {
                Model = request.Model,
                Choices =
                [
                    new OpenAiChoice
                    {
                        Index = 0,
                        Message = new OpenAiAssistantMessage
                        {
                            Role = "assistant",
                            Content = bedrockResult.Text
                        },
                        FinishReason = NormalizeFinishReason(bedrockResult.StopReason)
                    }
                ],
                Usage = new OpenAiUsage
                {
                    PromptTokens = bedrockResult.InputTokens,
                    CompletionTokens = bedrockResult.OutputTokens,
                    TotalTokens = bedrockResult.TotalTokens
                }
            };

            await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            stopwatch.Stop();
            audit.Decision = "ERROR";
            audit.DenyReason = $"Bedrock error: {ex.ErrorCode}";
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);

            httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
            await httpContext.Response.WriteAsJsonAsync(
                OpenAiErrorResponse.Create("Bedrock invocation failed. See gateway logs for the correlation ID.", "bedrock_error", ex.ErrorCode),
                cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            audit.Decision = "ERROR";
            audit.DenyReason = ex.GetType().Name;
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(
                OpenAiErrorResponse.Create("Gateway request failed. See gateway logs for the correlation ID.", "gateway_error", ex.GetType().Name),
                cancellationToken);
        }
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

app.Run();

static string NormalizeFinishReason(string? bedrockStopReason)
{
    if (string.IsNullOrWhiteSpace(bedrockStopReason)) return "stop";
    if (bedrockStopReason.Contains("max", StringComparison.OrdinalIgnoreCase)) return "length";
    if (bedrockStopReason.Contains("tool", StringComparison.OrdinalIgnoreCase)) return "tool_calls";
    return "stop";
}

static async Task WriteOpenAiCompatibleStreamAsync(
    HttpContext context,
    string model,
    string text,
    string stopReason,
    CancellationToken cancellationToken)
{
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    var id = $"chatcmpl-{Guid.NewGuid():N}";
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    var roleChunk = new
    {
        id,
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[]
        {
            new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
        }
    };

    await WriteSseDataAsync(context, roleChunk, cancellationToken);

    var contentChunk = new
    {
        id,
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[]
        {
            new { index = 0, delta = new { content = text }, finish_reason = (string?)null }
        }
    };

    await WriteSseDataAsync(context, contentChunk, cancellationToken);

    var doneChunk = new
    {
        id,
        @object = "chat.completion.chunk",
        created,
        model,
        choices = new[]
        {
            new { index = 0, delta = new { }, finish_reason = NormalizeFinishReason(stopReason) }
        }
    };

    await WriteSseDataAsync(context, doneChunk, cancellationToken);
    await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
}

static async Task WriteSseDataAsync(HttpContext context, object payload, CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

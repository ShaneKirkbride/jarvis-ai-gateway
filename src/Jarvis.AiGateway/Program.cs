using System.Security.Claims;
using Amazon;
using Amazon.Bedrock;
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
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

var gatewayOptions = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Math.Max(1024, gatewayOptions.MaxRequestBodyBytes);
});

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
builder.Services.AddMemoryCache();

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

builder.Services.AddSingleton<IAmazonBedrock>(_ =>
{
    var region = RegionEndpoint.GetBySystemName(gatewayOptions.AwsRegion);
    var config = new AmazonBedrockConfig
    {
        RegionEndpoint = region,
        AuthenticationRegion = gatewayOptions.AwsRegion
    };

    if (!string.IsNullOrWhiteSpace(gatewayOptions.BedrockEndpointDns))
    {
        config.ServiceURL = NormalizeEndpoint(gatewayOptions.BedrockEndpointDns);
    }

    return new AmazonBedrockClient(config);
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
        config.ServiceURL = NormalizeEndpoint(gatewayOptions.BedrockRuntimeEndpointDns);
    }

    return new AmazonBedrockRuntimeClient(config);
});

builder.Services.AddSingleton<IUserContextFactory, UserContextFactory>();
builder.Services.AddSingleton<IRequestContextFactory, RequestContextFactory>();
builder.Services.AddSingleton<IContentRedactor, RegexContentRedactor>();
builder.Services.AddSingleton<IBedrockModelDiscoveryService, BedrockModelDiscoveryService>();
builder.Services.AddSingleton<IInvokeModelPayloadAdapter, AmazonTitanTextInvokeModelPayloadAdapter>();
builder.Services.AddSingleton<IInvokeModelPayloadAdapter, MetaLlamaInvokeModelPayloadAdapter>();
builder.Services.AddSingleton<IInvokeModelPayloadAdapter, MistralInvokeModelPayloadAdapter>();
builder.Services.AddSingleton<IModelRegistry, ModelRegistry>();
builder.Services.AddSingleton<IPolicyRule, ModelConfiguredRule>();
builder.Services.AddSingleton<IPolicyRule, ModelEnabledRule>();
builder.Services.AddSingleton<IPolicyRule, ModelTextOutputRule>();
builder.Services.AddSingleton<IPolicyRule, GroupAuthorizationRule>();
builder.Services.AddSingleton<IPolicyRule, PromptSizeRule>();
builder.Services.AddSingleton<IPolicyRule, BlockedPatternRule>();
builder.Services.AddSingleton<IPolicyRule, ItarModelRule>();
builder.Services.AddSingleton<IPolicyRule, ItarWorkspaceRule>();
builder.Services.AddSingleton<IPolicyEngine, PolicyEngine>();
builder.Services.AddSingleton<IOpenAiChatRequestValidator, OpenAiChatRequestValidator>();
builder.Services.AddSingleton<IReadinessCheck, GatewayReadinessCheck>();
builder.Services.AddSingleton<IGatewayMetrics, GatewayMetrics>();
builder.Services.AddSingleton<IBedrockInvocationStrategy, BedrockConverseInvocationStrategy>();
builder.Services.AddSingleton<IBedrockInvocationStrategy, BedrockInvokeModelTextInvocationStrategy>();
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

app.MapGet("/readyz", (IReadinessCheck readinessCheck) =>
{
    var result = readinessCheck.Check();
    return Results.Json(new
    {
        status = result.Ready ? "ready" : "not_ready",
        failedChecks = result.FailedChecks,
        timeUtc = DateTimeOffset.UtcNow
    }, statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/v1/models", async (
        ClaimsPrincipal principal,
        IUserContextFactory userContextFactory,
        IPolicyEngine policyEngine,
        CancellationToken cancellationToken) =>
    {
        var user = userContextFactory.Create(principal);
        var visibleModels = await policyEngine.GetVisibleModelsAsync(user, cancellationToken);
        var response = new OpenAiModelListResponse
        {
            Data = visibleModels
                .Select(m => new OpenAiModelInfo { Id = m.Id, OwnedBy = "aws-bedrock" })
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
        IOpenAiChatRequestValidator requestValidator,
        IGatewayMetrics metrics,
        IEnumerable<IBedrockInvocationStrategy> strategies,
        IContentRedactor redactor,
        IAuditLogger auditLogger,
        IOptions<GatewayOptions> options,
        CancellationToken cancellationToken) =>
    {
        var stopwatch = Stopwatch.StartNew();
        var user = userContextFactory.Create(httpContext.User);
        var requestContext = requestContextFactory.Create(httpContext, request);
        var promptText = string.Empty;
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
            RequestedModelAlias = request.Model ?? string.Empty,
            Region = options.Value.AwsRegion,
            EndpointMode = string.IsNullOrWhiteSpace(options.Value.BedrockRuntimeEndpointDns) ? "regional-dns" : "vpce-override",
            PromptCharacters = 0
        };

        try
        {
            var validation = await requestValidator.ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                audit.Decision = "DENY";
                audit.DenyReason = validation.Message;
                metrics.RecordPolicyDenial(validation.Code ?? "validation_error", request.Model ?? string.Empty);
                return WriteDenied(auditLogger, audit, stopwatch, StatusCodes.Status400BadRequest, validation.Message ?? "Invalid request.", validation.Code);
            }

            promptText = string.Join("\n", request.Messages.Select(m => m.GetTextContent()));
            audit.PromptCharacters = promptText.Length;

            var logRedaction = options.Value.Redaction.RedactBeforeLogging ? redactor.Redact(promptText) : new RedactionResult(promptText, 0);
            audit.RedactionCount = logRedaction.RedactionCount;

            var decision = await policyEngine.AuthorizeAsync(user, requestContext, request, cancellationToken);
            audit.Decision = decision.Allowed ? "ALLOW" : "DENY";
            audit.DenyReason = decision.Allowed ? null : decision.Reason;
            audit.ResolvedBedrockModelId = decision.Model?.BedrockModelId;
            audit.Provider = decision.Model?.ProviderName ?? "aws-bedrock";
            audit.SupportsConverse = decision.Model?.SupportsConverse;
            audit.StreamingSupported = decision.Model?.ResponseStreamingSupported;
            audit.PolicyDecision = decision.Reason;
            audit.PolicyRuleId = decision.RuleId;

            if (!decision.Allowed || decision.Model is null)
            {
                metrics.RecordPolicyDenial(decision.RuleId, request.Model ?? string.Empty);
                return WriteDenied(auditLogger, audit, stopwatch, StatusCodes.Status403Forbidden, decision.Reason, decision.RuleId);
            }

            var orderedStrategies = strategies.OrderBy(s => s is BedrockConverseInvocationStrategy ? 0 : 1).ToArray();
            var strategy = orderedStrategies.FirstOrDefault(s => s.CanHandle(decision.Model, request));
            if (strategy is null)
            {
                audit.Decision = "DENY";
                audit.DenyReason = BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage;
                return WriteDenied(auditLogger, audit, stopwatch, StatusCodes.Status501NotImplemented, audit.DenyReason, "unsupported_model");
            }

            audit.InvocationStrategy = strategy.Name;
            metrics.RecordRequest(request.Model ?? string.Empty);
            var bedrockStopwatch = Stopwatch.StartNew();
            var response = await strategy.InvokeAsync(decision.Model, request, requestContext, cancellationToken);
            metrics.RecordBedrockInvocation(strategy.Name, bedrockStopwatch.Elapsed, success: true);
            response.Model = decision.Model.Id;

            if (options.Value.Redaction.Enabled)
            {
                foreach (var choice in response.Choices)
                {
                    var redacted = redactor.Redact(choice.Message.Content);
                    choice.Message.Content = redacted.Text;
                    audit.RedactionCount += redacted.RedactionCount;
                }
            }

            audit.InputTokens = response.Usage?.PromptTokens;
            audit.OutputTokens = response.Usage?.CompletionTokens;
            audit.TotalTokens = response.Usage?.TotalTokens;
            audit.TokenEstimate = response.Usage?.TotalTokens ?? EstimateTokens(promptText);
            metrics.RecordTokenUsage(request.Model ?? string.Empty, audit.InputTokens ?? 0, audit.OutputTokens ?? 0);
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);
            metrics.RecordLatency(request.Model ?? string.Empty, stopwatch.Elapsed);
            return Results.Json(response);
        }
        catch (NotSupportedException ex)
        {
            audit.Decision = "DENY";
            audit.DenyReason = ex.Message;
            return WriteDenied(auditLogger, audit, stopwatch, StatusCodes.Status501NotImplemented, ex.Message, "unsupported_model");
        }
        catch (Exception ex)
        {
            audit.Decision = "ERROR";
            audit.DenyReason = ex.Message;
            audit.LatencyMs = stopwatch.ElapsedMilliseconds;
            auditLogger.Write(audit);
            metrics.RecordBedrockError(request.Model ?? string.Empty);
            metrics.RecordServerError("chat_completions");
            return Results.Json(OpenAiErrorResponse.Create("Bedrock invocation failed.", "server_error", "bedrock_error"), statusCode: StatusCodes.Status502BadGateway);
        }
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

app.Run();

static string NormalizeEndpoint(string endpoint) => endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"https://{endpoint}";


public partial class Program;

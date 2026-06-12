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

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
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
builder.Services.AddSingleton<IOpenAiErrorMapper, OpenAiErrorMapper>();
builder.Services.AddSingleton<IChatCompletionOrchestrator, ChatCompletionOrchestrator>();
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
        IChatCompletionOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        return await orchestrator.CompleteAsync(httpContext, request, cancellationToken);
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

app.Run();

static string NormalizeEndpoint(string endpoint) => endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"https://{endpoint}";


public partial class Program;

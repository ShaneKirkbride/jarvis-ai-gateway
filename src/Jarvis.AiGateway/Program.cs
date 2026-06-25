using System.Security.Claims;
using System.Text.Json;
using Amazon;
using Azure.Identity;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Jarvis.AiGateway.Middleware;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Polly;
using Polly.Retry;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Allow in-flight Bedrock calls up to 30 s to drain on SIGTERM before the process exits.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"));
// ValidateOnStart()/IValidateOptions registration is intentionally NOT used. The same strict
// GatewayOptionsValidator rules now run inside IConfigHealth and gate readiness (503 + protected
// routes fail closed) instead of throwing at startup and crash-looping the container. Hard startup
// failures are reserved for unrecoverable problems (DI graph, programming errors). Security-critical
// misconfiguration still fails closed — it just surfaces via /healthz/ready and a 503 on /v1/*.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

var gatewayOptions = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Math.Max(1024, gatewayOptions.MaxRequestBodyBytes);
});

// Auth pipeline branches on the identity-broker switch.  When the broker is disabled the
// legacy JwtBearer flow remains active so PR 1 ships with production behaviour unchanged.
// When enabled, JwtBearer is not registered at all — IdentityBrokerMiddleware sets
// HttpContext.User itself and the standard UseAuthentication() middleware is omitted.
if (!gatewayOptions.IdentityBroker.Enabled)
{
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
}
else
{
    // The broker middleware sets HttpContext.User directly under a custom scheme; we still
    // need the authorization services registered for [Authorize] / RequireAuthorization()
    // to function.  An empty AddAuthentication is the minimum.
    builder.Services.AddAuthentication();
}

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();

// Identity-broker services.  Registered as a unit only when the broker is enabled — the
// chain (validators → resolver → broker → middleware) has no value when the legacy
// JwtBearer path is active and would force IGraphGroupQueryExecutor to be present even
// when no Graph credentials are configured.  Unit tests construct these types directly
// against mocks; this DI block exists purely for the production-on path.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISubjectHasher, SubjectHasher>();
if (gatewayOptions.IdentityBroker.Enabled)
{
    builder.Services.AddSingleton<IIdentityAssertionValidator, OwuiSessionJwtValidator>();
    builder.Services.AddSingleton<IIdentityAssertionValidator, OwuiTrustedHeaderValidator>();
    builder.Services.AddSingleton<IGraphGroupQueryExecutor, MicrosoftGraphGroupQueryExecutor>();
    builder.Services.AddSingleton<IGraphGroupResolver, GraphGroupResolver>();
    builder.Services.AddSingleton<IIdentityBroker, IdentityBroker>();
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-user", httpContext =>
    {
        // Per-key limiting for developer API keys (so one key cannot exhaust a user's whole
        // budget across keys); per-user for everyone else.
        var key = httpContext.User.FindFirstValue(DeveloperApiKeyClaims.KeyIdClaim) is { Length: > 0 } apiKeyId
            ? $"apikey:{apiKeyId}"
            : httpContext.User.FindFirstValue("sub")
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

// ── OpenTelemetry metrics ────────────────────────────────────────────────────
// Picks up the existing GatewayMetrics counters/histograms registered under
// the "Jarvis.AiGateway" meter.
//
// Production export pattern (AWS GovCloud):
//   Run the AWS Distro for OpenTelemetry (ADOT) Collector as an ECS sidecar.
//   Set OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 in the task definition
//   and the ADOT Collector config routes metrics to CloudWatch.
//   See deploy/environment.example for the complete environment variable list.
//
// Development: no endpoint is required — metrics are collected but not exported,
// which is safe and does not affect application behaviour.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: gatewayOptions.ServiceName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(GatewayMetrics.MeterName);

        // OTLP exporter is activated by environment variable — no code change needed
        // when switching between development (no export) and production (ADOT sidecar).
        // Set OTEL_EXPORTER_OTLP_ENDPOINT to enable; leave unset to disable.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    });

// ── Bedrock resilience pipeline ──────────────────────────────────────────────
// Retry up to 3 times with exponential backoff + jitter on transient provider errors.
// Does NOT retry: policy denials, validation errors, cancellation, or access-denied.
// Circuit-breaker can be layered on later via AddCircuitBreaker() on the same builder.
builder.Services.AddSingleton(_ =>
    new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<Amazon.BedrockRuntime.Model.ThrottlingException>()
                .Handle<AmazonBedrockRuntimeException>(ex =>
                    ex is not Amazon.BedrockRuntime.Model.AccessDeniedException
                    && ex.StatusCode >= System.Net.HttpStatusCode.InternalServerError),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .Build());

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
builder.Services.AddSingleton<IPolicyRule, DeveloperKeyModelAllowlistRule>();
builder.Services.AddSingleton<IPolicyRule, GroupAuthorizationRule>();
builder.Services.AddSingleton<IPolicyRule, PromptSizeRule>();
builder.Services.AddSingleton<IPolicyRule, BlockedPatternRule>();
builder.Services.AddSingleton<IPolicyRule, ItarModelRule>();
builder.Services.AddSingleton<IPolicyRule, ItarWorkspaceRule>();
builder.Services.AddSingleton<IPolicyEngine>(sp => new PolicyEngine(sp.GetRequiredService<IModelRegistry>(), sp.GetServices<IPolicyRule>()));
builder.Services.AddSingleton<IOpenAiChatRequestValidator, OpenAiChatRequestValidator>();
builder.Services.AddSingleton<IOpenAiErrorMapper, OpenAiErrorMapper>();
// Explicit factory: ChatCompletionOrchestrator has two public constructors (production +
// strategy-based test convenience) that are not parameter supersets of one another, which the
// built-in container would treat as ambiguous. Selecting the production constructor here removes
// the ambiguity.
builder.Services.AddSingleton<IChatCompletionOrchestrator>(sp => new ChatCompletionOrchestrator(
    sp.GetRequiredService<IUserContextFactory>(),
    sp.GetRequiredService<IRequestContextFactory>(),
    sp.GetRequiredService<IOpenAiChatRequestValidator>(),
    sp.GetRequiredService<IPolicyEngine>(),
    sp.GetServices<IAiProvider>(),
    sp.GetRequiredService<IContentRedactor>(),
    sp.GetRequiredService<IAuditLogger>(),
    sp.GetRequiredService<IOpenAiErrorMapper>(),
    sp.GetRequiredService<IGatewayMetrics>(),
    sp.GetRequiredService<IOptions<GatewayOptions>>(),
    sp.GetRequiredService<ResiliencePipeline>()));
builder.Services.AddSingleton<IEmbeddingsOrchestrator>(sp => new EmbeddingsOrchestrator(
    sp.GetRequiredService<IUserContextFactory>(),
    sp.GetRequiredService<IRequestContextFactory>(),
    sp.GetRequiredService<IPolicyEngine>(),
    sp.GetServices<IAiProvider>(),
    sp.GetRequiredService<IAuditLogger>(),
    sp.GetRequiredService<IOpenAiErrorMapper>(),
    sp.GetRequiredService<IGatewayMetrics>(),
    sp.GetRequiredService<IOptions<GatewayOptions>>(),
    sp.GetRequiredService<ResiliencePipeline>()));
builder.Services.AddSingleton<ICompletionsOrchestrator>(sp => new CompletionsOrchestrator(
    sp.GetRequiredService<IUserContextFactory>(),
    sp.GetRequiredService<IRequestContextFactory>(),
    sp.GetRequiredService<IPolicyEngine>(),
    sp.GetServices<IAiProvider>(),
    sp.GetRequiredService<IContentRedactor>(),
    sp.GetRequiredService<IAuditLogger>(),
    sp.GetRequiredService<IOpenAiErrorMapper>(),
    sp.GetRequiredService<IGatewayMetrics>(),
    sp.GetRequiredService<IOptions<GatewayOptions>>(),
    sp.GetRequiredService<ResiliencePipeline>()));
// Anthropic Messages API compatibility (Phase 4). A wire-format adapter over the same chat
// pipeline (validator/policy/provider/redaction/audit); text content only, non-streaming.
builder.Services.AddSingleton<IMessagesOrchestrator>(sp => new MessagesOrchestrator(
    sp.GetRequiredService<IUserContextFactory>(),
    sp.GetRequiredService<IRequestContextFactory>(),
    sp.GetRequiredService<IOpenAiChatRequestValidator>(),
    sp.GetRequiredService<IPolicyEngine>(),
    sp.GetServices<IAiProvider>(),
    sp.GetRequiredService<IContentRedactor>(),
    sp.GetRequiredService<IAuditLogger>(),
    sp.GetRequiredService<IOpenAiErrorMapper>(),
    sp.GetRequiredService<IGatewayMetrics>(),
    sp.GetRequiredService<IOptions<GatewayOptions>>(),
    sp.GetRequiredService<ResiliencePipeline>()));
builder.Services.AddSingleton<IReadinessCheck, GatewayReadinessCheck>();
builder.Services.AddSingleton<IGatewayMetrics, GatewayMetrics>();
builder.Services.AddSingleton<IBedrockInvocationStrategy, BedrockConverseInvocationStrategy>();
builder.Services.AddSingleton<IBedrockInvocationStrategy, BedrockInvokeModelTextInvocationStrategy>();
builder.Services.AddSingleton<IBedrockStreamingStrategy, BedrockConverseStreamInvocationStrategy>();

// ── AI providers ─────────────────────────────────────────────────────────────
// The orchestrator routes by GatewayModel.ProviderName. BedrockProvider wraps the existing
// Bedrock strategies; AzureOpenAiProvider talks to Azure OpenAI (incl. Government) over HTTP.
builder.Services.AddOptions<AzureOpenAiOptions>()
    .Bind(builder.Configuration.GetSection("Gateway:AzureOpenAi"));
builder.Services.AddHttpClient(AzureOpenAiProvider.HttpClientName);
builder.Services.AddSingleton<IAzureOpenAiCredential>(sp =>
{
    var azureOptions = sp.GetRequiredService<IOptions<AzureOpenAiOptions>>();
    if (azureOptions.Value.AuthenticationMode == AzureOpenAiAuthenticationMode.ManagedIdentity)
    {
        // Select the AAD authority by cloud so the managed-identity token is issued by the
        // correct sovereign cloud (Government vs. commercial), inferred from the endpoint host.
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (AzureOpenAiScopes.ForEndpoint(azureOptions.Value.Endpoint) == AzureOpenAiScopes.UsGovernment)
        {
            credentialOptions.AuthorityHost = AzureAuthorityHosts.AzureGovernment;
        }

        return new ManagedIdentityAzureOpenAiCredential(new DefaultAzureCredential(credentialOptions), azureOptions);
    }

    return new ApiKeyAzureOpenAiCredential(azureOptions);
});
builder.Services.AddSingleton<IAiProvider, BedrockProvider>();
builder.Services.AddSingleton<IAiProvider, AzureOpenAiProvider>();

// Config health: strict validation that gates readiness instead of crash-looping the container.
builder.Services.AddSingleton<IConfigHealth, ConfigHealth>();

// ── Developer (IDE/client) API-key authentication ────────────────────────────
// Separate from the service-to-service key. Off unless Gateway:DeveloperAuth:Enabled=true.
// The authenticator resolves the key owner's LIVE Entra groups via IGraphGroupResolver (injected
// only when the identity broker is configured), so a developer key is authorization-equivalent to
// a JWT/broker user and can never escalate beyond the owner's current membership.
builder.Services.AddSingleton<IDeveloperApiKeyHasher, DeveloperApiKeyHasher>();
builder.Services.AddSingleton<IDeveloperApiKeyStore, ConfiguredDeveloperApiKeyStore>();
builder.Services.AddSingleton<IDeveloperApiKeyAuthenticator, DeveloperApiKeyAuthenticator>();

// Internal API discoverability (OpenAPI JSON at /openapi/v1.json). Off by default; enable only on
// internal builds. Still subject to the normal auth pipeline (service or developer key).
if (gatewayOptions.Discovery.OpenApiEnabled)
{
    builder.Services.AddOpenApi();
}

builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

var app = builder.Build();

// Surface configuration problems once at startup so operators see WHY the gateway is not ready,
// instead of an opaque CrashLoopBackOff.  The process stays up to serve health endpoints.
var configHealth = app.Services.GetRequiredService<IConfigHealth>();
if (!configHealth.IsReady)
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    foreach (var problem in configHealth.Problems)
    {
        startupLogger.LogError("Gateway configuration problem: {Path} — {Message}", problem.Path, problem.Message);
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
// Fail protected /v1 routes closed (503) when critical config is invalid — never serve against a
// misconfigured gateway. Placed before auth so a misconfigured deploy returns a clear 503.
app.UseMiddleware<ConfigHealthGateMiddleware>();
// Developer API-key auth runs before the service-key and broker middlewares: a valid jrvs_ bearer
// establishes the user principal and a server-side flag so those middlewares pass it through; a
// present-but-invalid developer key fails closed here. No-op when no jrvs_ bearer is present.
app.UseMiddleware<DeveloperApiKeyMiddleware>();
app.UseMiddleware<ServiceApiKeyMiddleware>();

if (gatewayOptions.IdentityBroker.Enabled)
{
    // Pre-auth limiter protects identity resolution (validator + Graph) from a token-less
    // flood; post-auth per-user limit ("per-user" policy) still runs at the chat endpoint
    // once identity is established.  Both middlewares short-circuit at runtime when
    // IdentityBroker.Enabled is false in the resolved IOptions (e.g. a test override that
    // landed after this synchronous capture), so the legacy auth path keeps working
    // even if the up-front decision and the runtime config disagree.
    app.UseMiddleware<PreAuthRateLimiterMiddleware>();
    app.UseMiddleware<IdentityBrokerMiddleware>();
}

// UseAuthentication is always in the pipeline: when the broker is on it is a no-op (no
// default scheme is registered), when the legacy JwtBearer path is on it authenticates
// the request, and test infrastructure that swaps IAuthenticationSchemeProvider plugs in
// here regardless of which up-front branch ran.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (gatewayOptions.Discovery.OpenApiEnabled)
{
    app.MapOpenApi();
}

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = gatewayOptions.ServiceName,
    environment = gatewayOptions.EnvironmentName,
    timeUtc = DateTimeOffset.UtcNow
}));

// Liveness: the process is running. Always 200 unless the host is truly dead — never gated on
// configuration, so a misconfigured-but-running container is not killed and recycled forever.
app.MapGet("/healthz/live", () => Results.Ok(new { status = "alive", timeUtc = DateTimeOffset.UtcNow }));

// Readiness: 200 only when critical config is valid and providers are initialized; otherwise a
// safe, redacted 503 listing the configuration problems by path.
app.MapGet("/healthz/ready", (IConfigHealth health) => health.IsReady
    ? Results.Json(new { status = "ready", timeUtc = DateTimeOffset.UtcNow })
    : Results.Json(new
    {
        status = "unhealthy",
        code = ConfigHealth.InvalidCode,
        errors = health.Problems.Select(p => new { path = p.Path, message = p.Message })
    }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/readyz", async (IReadinessCheck readinessCheck, CancellationToken cancellationToken) =>
{
    var result = await readinessCheck.CheckAsync(cancellationToken);
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
        var chatModels = await policyEngine.GetVisibleModelsAsync(user, cancellationToken);
        var embeddingModels = await policyEngine.GetVisibleEmbeddingModelsAsync(user, cancellationToken);
        var completionModels = await policyEngine.GetVisibleCompletionModelsAsync(user, cancellationToken);
        var response = new OpenAiModelListResponse
        {
            Data = chatModels.Concat(embeddingModels).Concat(completionModels)
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(ModelCapabilityMapper.ToModelInfo)
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

app.MapPost("/v1/embeddings", async (
        HttpContext httpContext,
        OpenAiEmbeddingsRequest request,
        IEmbeddingsOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        return await orchestrator.EmbedAsync(httpContext, request, cancellationToken);
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

app.MapPost("/v1/completions", async (
        HttpContext httpContext,
        OpenAiCompletionRequest request,
        ICompletionsOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        return await orchestrator.CompleteAsync(httpContext, request, cancellationToken);
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

// Anthropic-compatible Messages endpoint (Phase 4) so Claude-native clients (Claude Code, Zed, …)
// can point at the gateway. Maps onto the same chat policy/provider/redaction/audit pipeline.
app.MapPost("/v1/messages", async (
        HttpContext httpContext,
        AnthropicMessagesRequest request,
        IMessagesOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        return await orchestrator.CreateMessageAsync(httpContext, request, cancellationToken);
    })
    .RequireAuthorization()
    .RequireRateLimiting("per-user");

app.Run();

static string NormalizeEndpoint(string endpoint) =>
    endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"https://{endpoint}";


public partial class Program;

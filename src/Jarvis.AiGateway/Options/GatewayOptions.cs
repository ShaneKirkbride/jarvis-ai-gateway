namespace Jarvis.AiGateway.Options;

public sealed class GatewayOptions
{
    public string ServiceName { get; set; } = "jarvis-ai-gateway";
    public string EnvironmentName { get; set; } = "dev";
    public string AwsRegion { get; set; } = "us-gov-west-1";
    public string? BedrockEndpointDns { get; set; }
    public string? BedrockRuntimeEndpointDns { get; set; }
    public bool RequireServiceApiKey { get; set; }
    public string? ServiceApiKey { get; set; }
    public string ServiceApiKeyHeader { get; set; } = "X-Jarvis-Gateway-Key";
    // Group display names to assign to the service principal established by ServiceApiKeyMiddleware.
    // These feed policy group authorization so the M2M caller passes RequiredGroups checks without
    // bypassing policy.  Configure the groups that match the target models' RequiredGroups.
    public List<string> ServiceApiKeyGroups { get; set; } = [];
    public string UserTokenHeader { get; set; } = "X-Jarvis-User-Token";
    public List<string> GroupClaimNames { get; set; } = ["groups", "roles", "cognito:groups"];
    public List<string> EmailClaimNames { get; set; } = ["email", "preferred_username", "upn"];
    public bool RequireItarWorkspaceForItarRequests { get; set; } = true;
    public List<string> ItarApprovedWorkspaceIds { get; set; } = [];
    public List<string> BlockedPromptPatterns { get; set; } = [];
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;
    public int ProviderTimeoutSeconds { get; set; } = 120;
    public int ModelDiscoveryTimeoutSeconds { get; set; } = 10;
    public int ReadinessTimeoutSeconds { get; set; } = 5;
    public RequestLimitOptions RequestLimits { get; set; } = new();
    public RedactionOptions Redaction { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public ModelDiscoveryOptions ModelDiscovery { get; set; } = new();
    public GatewayPolicyOptions Policy { get; set; } = new();
    public StreamingOptions Streaming { get; set; } = new();
    public RequestValidationOptions RequestValidation { get; set; } = new();
    public ReadinessOptions Readiness { get; set; } = new();
    public IdentityBrokerOptions IdentityBroker { get; set; } = new();
    public AzureOpenAiOptions AzureOpenAi { get; set; } = new();
    public DeveloperAuthOptions DeveloperAuth { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
    public CompletionsOptions Completions { get; set; } = new();
    public List<ModelRouteOptions> Models { get; set; } = [];
}

/// <summary>
/// Controls for /v1/completions (FIM/autocomplete, Phase 3).  Autocomplete is the highest-egress
/// surface (it can ship surrounding file context on every keystroke-batch), so it is disabled for
/// ITAR requests by default and the inbound context is hard-capped.
/// </summary>
public sealed class CompletionsOptions
{
    // Disable autocomplete for ITAR workspaces/requests (default true = fail-safe).
    public bool DisableForItar { get; set; } = true;

    // Hard cap on prompt+suffix length (characters). Clients must send small, intentional context.
    public int MaxContextCharacters { get; set; } = 8000;
}

public sealed class RequestLimitOptions
{
    public int MaxMetadataEntries { get; set; } = 16;
    public int MaxMetadataKeyLength { get; set; } = 64;
    public int MaxMetadataValueLength { get; set; } = 512;
    public int MaxGatewayHeaderLength { get; set; } = 512;
    public int MaxStopSequenceCount { get; set; } = 4;
    public int MaxStopSequenceLength { get; set; } = 200;
    public int MaxMessageCount { get; set; } = 128;
    public int RegexTimeoutMilliseconds { get; set; } = 250;
}

public sealed class RedactionOptions
{
    public bool Enabled { get; set; } = true;
    // Default is true: redact PII/secrets before sending content to Bedrock.
    // In ITAR mode the orchestrator enforces this regardless of this setting.
    // Set to false in development to inspect prompts in provider logs.
    public bool RedactBeforeBedrock { get; set; } = true;
    public bool RedactBeforeLogging { get; set; } = true;
}

public sealed class ReadinessOptions
{
    // When true, /readyz attempts a ListFoundationModels call to verify Bedrock SDK
    // connectivity and VPC endpoint reachability.  Requires the IAM permission
    // bedrock:ListFoundationModels on the task role.  Defaults to false so that
    // deployments using explicit aliases without discovery do not need the extra permission.
    public bool CheckBedrockConnectivity { get; set; } = false;
}

public sealed class RateLimitOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; } = 0;
}

public sealed class ModelDiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int CacheSeconds { get; set; } = 300;
    public List<string> IncludeLifecycleStatuses { get; set; } = ["ACTIVE"];
    public List<string> IncludeOutputModalities { get; set; } = ["TEXT"];
    public List<string> ExcludeProviders { get; set; } = [];
    public List<string> IncludeProviders { get; set; } = [];
    public bool ExposeRawBedrockModelIds { get; set; } = false;
}

public sealed class GatewayPolicyOptions
{
    public bool RequireExplicitModelAllowlist { get; set; } = true;
    public List<string> DeniedModelIdPatterns { get; set; } = [];
    public List<string> AllowedModelIdPatterns { get; set; } = [];
    public bool AllowDiscoveredModelsForNonItar { get; set; } = false;
    public bool AllowDiscoveredModelsForItar { get; set; } = false;
}

public sealed class StreamingOptions
{
    public bool FallbackToNonStreaming { get; set; } = false;
}

public sealed class RequestValidationOptions
{
    public float MinimumTemperature { get; set; } = 0.0f;
    public float MaximumTemperature { get; set; } = 1.0f;
    public int MaxStopSequences { get; set; } = 4;
    public int MaxStopSequenceCharacters { get; set; } = 200;
    public int MaxMetadataBytes { get; set; } = 8192;

    // Tool/function calling (Phase 1) request-shape limits.
    public int MaxTools { get; set; } = 64;
    public int MaxToolSchemaBytes { get; set; } = 32768;

    // Embeddings (Phase 2): max number of inputs per /v1/embeddings request.
    public int MaxEmbeddingInputs { get; set; } = 256;
}

public sealed class ModelRouteOptions
{
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Provider this model routes to. "aws-bedrock" (default) or "azure-openai".
    public string ProviderName { get; set; } = "aws-bedrock";

    public string BedrockModelId { get; set; } = string.Empty;

    // Azure OpenAI deployment name. Required when ProviderName is "azure-openai".
    public string AzureDeploymentName { get; set; } = string.Empty;

    // Optional underlying Azure model (e.g. "gpt-5.1") used to apply model-family request
    // compatibility rules such as the GPT-5 max_completion_tokens requirement.
    public string AzureModelName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public bool ItarApproved { get; set; }

    // Display-name authorization fields.  Retained for backwards compatibility while the
    // configuration migrates to Entra group object IDs.  These fields are FORBIDDEN on any
    // model with ItarApproved=true once IdentityBroker is enabled (enforced by readiness).
    // They will be removed in a follow-up release.
    public List<string> AllowedGroups { get; set; } = [];
    public List<string> RequiredGroups { get; set; } = [];

    // Object-ID authorization fields used by the identity-broker policy path.  Values are
    // Entra group object IDs (GUIDs).  Authorization compares these against the
    // Graph-resolved DirectoryGroupRef.Id set carried on the validated principal.
    public List<string> AllowedGroupIds { get; set; } = [];
    public List<string> RequiredGroupIds { get; set; } = [];

    public int MaxInputCharacters { get; set; } = 120000;
    public int MaxOutputTokens { get; set; } = 2048;

    // Optional advertised context window (tokens), surfaced in /v1/models capability metadata.
    public int? ContextWindowTokens { get; set; }

    public string InvocationMode { get; set; } = "Auto";
    public bool? SupportsConverse { get; set; }

    // Enables tool/function calling for this model (Phase 1). Default false = fail-closed.
    public bool SupportsTools { get; set; }

    // Marks this model as usable on /v1/embeddings (Phase 2). Default false = fail-closed.
    public bool SupportsEmbeddings { get; set; }

    // Marks this model as usable on /v1/completions (FIM/autocomplete, Phase 3). Default false.
    public bool SupportsFim { get; set; }

    public List<string> OutputModalities { get; set; } = [];
    public List<string> InputModalities { get; set; } = [];
}

// ── Identity broker options ────────────────────────────────────────────────────────────
// The gateway accepts a signed OWUI-originated identity assertion (current OWUI session
// JWT or a future Jarvis-minted assertion) on a configurable header, validates it, and
// resolves AD group membership via Microsoft Graph.  Policy authorizes on Entra group
// object IDs only.  See docs/identity-broker-plan.md (Revision 2) for the full design.

public sealed class IdentityBrokerOptions
{
    // Master switch.  Defaults to true so new deployments get the broker by default.
    // Set to false to revert to the legacy ASP.NET Core JwtBearer pipeline while a
    // deployment migrates — the legacy code path is intentionally retained so the
    // rollback is a config change, not a code revert.
    public bool Enabled { get; set; } = true;

    // Header carrying the assertion.  Defaults match the existing GatewayOptions.UserTokenHeader
    // so deployments do not need to change anything.
    public string TokenHeader { get; set; } = "X-Jarvis-User-Token";

    // Lifetime guards.  exp must be unexpired (clock skew applied) AND (now - iat) must be
    // within MaxAssertionAgeSeconds.  Both checks must pass — a long-lived `exp` cannot
    // outlive the max-age bound, which protects against stolen long-lived tokens.
    public int ClockSkewSeconds { get; set; } = 120;
    public int MaxAssertionAgeSeconds { get; set; } = 900;

    // When true, /v1/models requires a validated user assertion and returns a user-filtered
    // list (Option A in the plan).  Switch to false only if the deployed Open WebUI version
    // calls /v1/models in a service context without a user session.
    public bool ModelsEndpointRequiresUser { get; set; } = true;

    // Salt applied to SHA-256 hashes of canonical subject and Entra oid in audit events.
    // Required at startup when Enabled=true; readiness fails closed if absent.
    public string? AuditSubjectSalt { get; set; }

    public OwuiSessionJwtOptions OwuiSessionJwt { get; set; } = new();
    public OwuiTrustedHeaderOptions OwuiTrustedHeader { get; set; } = new();
    public JarvisAssertionOptions JarvisAssertion { get; set; } = new();
    public GraphOptions Graph { get; set; } = new();
    public PreAuthRateLimitOptions PreAuthRateLimit { get; set; } = new();
    public MtlsOptions Mtls { get; set; } = new();
}

public sealed class OwuiTrustedHeaderOptions
{
    // Recommended deployment path with stock OWUI v0.8.6+: enable
    // ENABLE_FORWARD_USER_INFO_HEADERS on OWUI and trust the X-OpenWebUI-User-* headers
    // it forwards.  Cryptographic origin trust is provided by ALB-terminated mTLS,
    // not by a token signature — readiness fails closed if mTLS subject pinning is not
    // also configured.
    public bool Enabled { get; set; } = false;

    // Email is the canonical identity for Graph lookup.  Header names are configurable in
    // case a future OWUI release renames them.
    public string EmailHeader { get; set; } = "X-OpenWebUI-User-Email";
    public string IdHeader { get; set; } = "X-OpenWebUI-User-Id";
    public string NameHeader { get; set; } = "X-OpenWebUI-User-Name";
    public string RoleHeader { get; set; } = "X-OpenWebUI-User-Role";

    // When this validator is the active identity path (and no signed-assertion validator
    // covers the request), require Mtls.RequireSubjectCheck=true so the trust chain is at
    // least anchored to a pinned client certificate.  Enforced at startup by readiness.
    public bool RequireMtlsSubjectPinning { get; set; } = true;
}

public sealed class OwuiSessionJwtOptions
{
    // When false the gateway does not register the OWUI session JWT validator at all.
    // Useful once deployments migrate to Jarvis-minted assertions.
    public bool Enabled { get; set; } = true;

    // HS256 symmetric key.  Sourced from Secrets Manager — never committed.  When Enabled=true
    // this is required; readiness fails closed if empty.
    public string? SigningKey { get; set; }

    public string? Issuer { get; set; }
    public string? Audience { get; set; }

    // Optional per-validator override for the global MaxAssertionAgeSeconds.
    public int? MaxAssertionAgeSeconds { get; set; }
}

public sealed class JarvisAssertionOptions
{
    // Scaffolded for the future Jarvis-specific assertion path.  PR 1 leaves this disabled.
    public bool Enabled { get; set; } = false;

    public string? SigningKey { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }

    // Recommended ~60 seconds when this validator becomes the active one.
    public int? MaxAssertionAgeSeconds { get; set; }
}

public sealed class GraphOptions
{
    // Entra tenant + app registration used for the client-credentials flow.  Secret sourced
    // from Secrets Manager.  Required when IdentityBroker.Enabled=true; readiness fails
    // closed if absent.
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // Positive-cache TTL for resolved group sets.  Stale entries are NEVER served on
    // Graph failure — the broker fails closed instead.
    public int CacheSeconds { get; set; } = 300;

    // Short-lived negative cache so a typo'd email does not hammer Graph.
    public int NegativeCacheSeconds { get; set; } = 30;

    // Per-call timeout for Graph requests.
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class PreAuthRateLimitOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 10;
    public int QueueLimit { get; set; } = 0;

    // How the limiter buckets traffic.  Auto picks MtlsSubject when ALB-forwarded subject
    // metadata is present and falls back to Ip otherwise.
    public PreAuthRateLimitPartition PartitionBy { get; set; } = PreAuthRateLimitPartition.Auto;
}

public enum PreAuthRateLimitPartition
{
    Auto,
    Ip,
    MtlsSubject
}

public sealed class MtlsOptions
{
    // AlbVerify (the chosen design) trusts ALB-enforced certificate validation and may
    // additionally pin subject/serial from ALB-forwarded metadata headers.  KestrelMtls is
    // documented as a future option requiring full in-process certificate handling.
    public MtlsMode Mode { get; set; } = MtlsMode.AlbVerify;

    // Default true as of PR 4: when the broker is on (also default-on since PR 3) the
    // trust chain must be anchored to a pinned client certificate, otherwise the
    // OwuiTrustedHeader path could be exercised by any caller that reaches the gateway.
    // Readiness fails closed when this is true but no subjects/serials are configured,
    // so an unprepared deploy refuses to start rather than silently permits traffic.
    public bool RequireSubjectCheck { get; set; } = true;
    public List<string> AcceptedClientCertSubjects { get; set; } = [];
    public List<string> AcceptedClientCertSerials { get; set; } = [];
}

public enum MtlsMode
{
    AlbVerify,
    KestrelMtls
}

public sealed class JwtOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string? ValidIssuer { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}

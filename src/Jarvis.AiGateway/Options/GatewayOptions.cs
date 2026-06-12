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
    public string UserTokenHeader { get; set; } = "X-Jarvis-User-Token";
    public List<string> GroupClaimNames { get; set; } = ["groups", "roles", "cognito:groups"];
    public List<string> EmailClaimNames { get; set; } = ["email", "preferred_username", "upn"];
    public bool RequireItarWorkspaceForItarRequests { get; set; } = true;
    public List<string> ItarApprovedWorkspaceIds { get; set; } = [];
    public List<string> BlockedPromptPatterns { get; set; } = [];
    public RedactionOptions Redaction { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public ModelDiscoveryOptions ModelDiscovery { get; set; } = new();
    public GatewayPolicyOptions Policy { get; set; } = new();
    public StreamingOptions Streaming { get; set; } = new();
    public RequestValidationOptions RequestValidation { get; set; } = new();
    public List<ModelRouteOptions> Models { get; set; } = [];
}

public sealed class RedactionOptions
{
    public bool Enabled { get; set; } = true;
    public bool RedactBeforeBedrock { get; set; } = false;
    public bool RedactBeforeLogging { get; set; } = true;
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
}

public sealed class ModelRouteOptions
{
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BedrockModelId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool ItarApproved { get; set; }
    public List<string> AllowedGroups { get; set; } = [];
    public List<string> RequiredGroups { get; set; } = [];
    public int MaxInputCharacters { get; set; } = 120000;
    public int MaxOutputTokens { get; set; } = 2048;
    public string InvocationMode { get; set; } = "Auto";
    public bool? SupportsConverse { get; set; }
    public List<string> OutputModalities { get; set; } = [];
    public List<string> InputModalities { get; set; } = [];
}

public sealed class JwtOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string? ValidIssuer { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}

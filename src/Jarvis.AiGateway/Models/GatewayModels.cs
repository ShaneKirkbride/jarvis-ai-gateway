using Jarvis.AiGateway.Options;

namespace Jarvis.AiGateway.Models;

public sealed record UserContext(
    string Subject,
    string Email,
    IReadOnlySet<string> Groups,
    IReadOnlyDictionary<string, string> Claims)
{
    // Entra group object IDs resolved through Microsoft Graph by the identity broker.
    // Authoritative input for policy authorization once the broker is enabled.  When the
    // broker is disabled, this set is empty and policy falls back to the legacy
    // display-name <see cref="Groups"/> match against <c>GatewayModel.RequiredGroups</c>.
    public IReadOnlySet<string> GroupIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RequestContext(
    string RequestId,
    string CorrelationId,
    string WorkspaceId,
    string DataLabel,
    bool ItarMode);

public sealed record RedactionResult(string Text, int RedactionCount);

public sealed record BedrockChatResult(
    string Text,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    string StopReason);

public sealed class DiscoveredBedrockModel
{
    public string ModelId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public IReadOnlyList<string> InputModalities { get; set; } = [];
    public IReadOnlyList<string> OutputModalities { get; set; } = [];
    public IReadOnlyList<string> InferenceTypesSupported { get; set; } = [];
    public bool ResponseStreamingSupported { get; set; }
    public string LifecycleStatus { get; set; } = string.Empty;
    public bool SupportsConverse { get; set; }
}

public sealed class GatewayModel
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BedrockModelId { get; set; } = string.Empty;

    // Azure OpenAI deployment name (used instead of a model ID when ProviderName is "azure-openai").
    public string AzureDeploymentName { get; set; } = "";

    // Underlying Azure model behind the deployment (e.g. "gpt-5.1", "gpt-4.1-mini").  Optional;
    // used only to apply model-family request-compatibility rules (e.g. GPT-5 token parameter).
    public string AzureModelName { get; set; } = "";

    public string ProviderName { get; set; } = "aws-bedrock";
    public bool Enabled { get; set; } = true;
    public bool ItarApproved { get; set; }

    // Display-name policy input.  Retained for backwards compatibility with deployments
    // that have not yet migrated to Entra group object IDs.  Policy uses these ONLY when
    // RequiredGroupIds is empty.  Forbidden for ITAR-approved models once the identity
    // broker is enabled — enforced by GatewayOptionsValidator.
    public IReadOnlyList<string> RequiredGroups { get; set; } = [];

    // Entra group object IDs (GUIDs).  When non-empty, this is the only authorization
    // input the policy engine consults — display names are ignored even if they match.
    public IReadOnlyList<string> RequiredGroupIds { get; set; } = [];

    public int MaxInputCharacters { get; set; } = 120000;
    public int MaxOutputTokens { get; set; } = 2048;

    // Optional advertised context window (tokens) for /v1/models capability metadata. Null = unset.
    public int? ContextWindowTokens { get; set; }

    public string InvocationMode { get; set; } = "Auto";
    public IReadOnlyList<string> InputModalities { get; set; } = [];
    public IReadOnlyList<string> OutputModalities { get; set; } = ["TEXT"];
    public IReadOnlyList<string> InferenceTypesSupported { get; set; } = [];
    public bool ResponseStreamingSupported { get; set; }
    public string LifecycleStatus { get; set; } = "CONFIGURED";
    public bool SupportsConverse { get; set; }

    // Tool/function calling capability (Phase 1). Defaults false: tools are rejected unless a model
    // is explicitly configured to support them (fail-closed; capability-gated).
    public bool SupportsTools { get; set; }

    // Embeddings capability (Phase 2). Defaults false: a model is only usable on /v1/embeddings
    // when explicitly configured (fail-closed; capability-gated).
    public bool SupportsEmbeddings { get; set; }

    // Completion / fill-in-the-middle capability (Phase 3). Defaults false (fail-closed).
    public bool SupportsFim { get; set; }

    public bool IsConfiguredAlias { get; set; }
    public bool IsRawBedrockModelId { get; set; }
    public bool IsDiscovered { get; set; }

    public bool HasTextOutput => OutputModalities.Count == 0 || OutputModalities.Any(m => m.Equals("TEXT", StringComparison.OrdinalIgnoreCase));

    // Streaming capability for /v1/models metadata: Azure OpenAI chat deployments always stream;
    // Bedrock streams via Converse (InvokeModel-only models do not). Mirrors the gateway's actual
    // streaming routing (IStreamingAiProvider.CanStream) at the catalogue level.
    public bool SupportsStreaming =>
        ProviderName.Equals("azure-openai", StringComparison.OrdinalIgnoreCase) || SupportsConverse;

    public static GatewayModel FromConfigured(ModelRouteOptions options, DiscoveredBedrockModel? discovered = null) => new()
    {
        Id = options.Alias,
        Alias = options.Alias,
        DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? options.Alias : options.DisplayName,
        BedrockModelId = options.BedrockModelId,
        AzureDeploymentName = options.AzureDeploymentName,
        AzureModelName = options.AzureModelName,
        // An explicit per-model ProviderName wins (e.g. "azure-openai"); otherwise fall back to the
        // discovered Bedrock provider name, then the Bedrock default.
        ProviderName = !string.IsNullOrWhiteSpace(options.ProviderName) ? options.ProviderName : discovered?.ProviderName ?? "aws-bedrock",
        Enabled = options.Enabled,
        ItarApproved = options.ItarApproved,
        // Display-name lists feed into the legacy RequiredGroups path.  Object-ID lists feed
        // into RequiredGroupIds which the policy engine prefers when populated.
        RequiredGroups = options.RequiredGroups.Count > 0 ? options.RequiredGroups : options.AllowedGroups,
        RequiredGroupIds = options.RequiredGroupIds.Count > 0 ? options.RequiredGroupIds : options.AllowedGroupIds,
        MaxInputCharacters = options.MaxInputCharacters,
        MaxOutputTokens = options.MaxOutputTokens,
        ContextWindowTokens = options.ContextWindowTokens,
        SupportsTools = options.SupportsTools,
        SupportsEmbeddings = options.SupportsEmbeddings,
        SupportsFim = options.SupportsFim,
        InvocationMode = options.InvocationMode,
        InputModalities = options.InputModalities.Count > 0 ? options.InputModalities : discovered?.InputModalities ?? [],
        OutputModalities = options.OutputModalities.Count > 0 ? options.OutputModalities : discovered?.OutputModalities ?? ["TEXT"],
        InferenceTypesSupported = discovered?.InferenceTypesSupported ?? [],
        ResponseStreamingSupported = discovered?.ResponseStreamingSupported ?? false,
        LifecycleStatus = discovered?.LifecycleStatus ?? "CONFIGURED",
        SupportsConverse = options.SupportsConverse ?? discovered?.SupportsConverse ?? true,
        IsConfiguredAlias = true,
        IsDiscovered = discovered is not null
    };

    public static GatewayModel FromDiscovered(DiscoveredBedrockModel discovered, bool exposeRawId) => new()
    {
        Id = discovered.ModelId,
        Alias = discovered.ModelId,
        DisplayName = discovered.ModelName,
        BedrockModelId = discovered.ModelId,
        ProviderName = discovered.ProviderName,
        Enabled = true,
        InputModalities = discovered.InputModalities,
        OutputModalities = discovered.OutputModalities,
        InferenceTypesSupported = discovered.InferenceTypesSupported,
        ResponseStreamingSupported = discovered.ResponseStreamingSupported,
        LifecycleStatus = discovered.LifecycleStatus,
        SupportsConverse = discovered.SupportsConverse,
        IsRawBedrockModelId = exposeRawId,
        IsDiscovered = true
    };
}

public static class BedrockModelCapabilities
{
    public static bool SupportsConverse(string modelId, string? providerName = null)
    {
        var id = modelId.ToLowerInvariant();
        var provider = providerName?.ToLowerInvariant() ?? string.Empty;
        if (id.Contains("embed") || id.Contains("image") || id.Contains("stable-diffusion")) return false;

        return provider.Contains("anthropic") || provider.Contains("amazon") || provider.Contains("meta") ||
               provider.Contains("mistral") || provider.Contains("cohere") || provider.Contains("ai21") ||
               id.Contains("anthropic.") || id.Contains("amazon.nova") || id.Contains("amazon.titan-text") ||
               id.Contains("meta.llama") || id.Contains("mistral.") || id.Contains("cohere.command") ||
               id.Contains("ai21.");
    }
}

using Jarvis.AiGateway.Options;

namespace Jarvis.AiGateway.Models;

public sealed record UserContext(
    string Subject,
    string Email,
    IReadOnlySet<string> Groups,
    IReadOnlyDictionary<string, string> Claims);

public sealed record RequestContext(
    string RequestId,
    string CorrelationId,
    string WorkspaceId,
    string DataLabel,
    bool ItarMode);

public sealed record PolicyDecision(
    bool Allowed,
    string Reason,
    GatewayModel? Model);

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
    public string ProviderName { get; set; } = "aws-bedrock";
    public bool Enabled { get; set; } = true;
    public bool ItarApproved { get; set; }
    public IReadOnlyList<string> RequiredGroups { get; set; } = [];
    public int MaxInputCharacters { get; set; } = 120000;
    public int MaxOutputTokens { get; set; } = 2048;
    public string InvocationMode { get; set; } = "Auto";
    public IReadOnlyList<string> InputModalities { get; set; } = [];
    public IReadOnlyList<string> OutputModalities { get; set; } = ["TEXT"];
    public IReadOnlyList<string> InferenceTypesSupported { get; set; } = [];
    public bool ResponseStreamingSupported { get; set; }
    public string LifecycleStatus { get; set; } = "CONFIGURED";
    public bool SupportsConverse { get; set; }
    public bool IsConfiguredAlias { get; set; }
    public bool IsRawBedrockModelId { get; set; }
    public bool IsDiscovered { get; set; }

    public bool HasTextOutput => OutputModalities.Count == 0 || OutputModalities.Any(m => m.Equals("TEXT", StringComparison.OrdinalIgnoreCase));

    public static GatewayModel FromConfigured(ModelRouteOptions options, DiscoveredBedrockModel? discovered = null) => new()
    {
        Id = options.Alias,
        Alias = options.Alias,
        DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? options.Alias : options.DisplayName,
        BedrockModelId = options.BedrockModelId,
        ProviderName = discovered?.ProviderName ?? "aws-bedrock",
        Enabled = options.Enabled,
        ItarApproved = options.ItarApproved,
        RequiredGroups = options.RequiredGroups.Count > 0 ? options.RequiredGroups : options.AllowedGroups,
        MaxInputCharacters = options.MaxInputCharacters,
        MaxOutputTokens = options.MaxOutputTokens,
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

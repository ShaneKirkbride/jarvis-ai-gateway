using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Maps a resolved <see cref="GatewayModel"/> to the OpenAI-compatible <see cref="OpenAiModelInfo"/>
/// with additive capability metadata.  Capability flags reflect what the GATEWAY currently exposes
/// (Phase 0: chat + streaming only) — not raw provider features — so IDE clients never advertise a
/// capability the gateway does not yet implement.
/// </summary>
public static class ModelCapabilityMapper
{
    public static OpenAiModelInfo ToModelInfo(GatewayModel model) => new()
    {
        Id = model.Id,
        OwnedBy = model.ProviderName,
        Provider = model.ProviderName,
        DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName,
        SupportsChat = true,
        SupportsStreaming = model.SupportsStreaming,
        SupportsTools = model.SupportsTools,
        SupportsEmbeddings = model.SupportsEmbeddings,
        SupportsFim = model.SupportsFim,
        // Phase 4+ surface — not implemented yet, advertised as false until it ships.
        SupportsVision = false,
        ContextWindow = model.ContextWindowTokens,
        MaxOutputTokens = model.MaxOutputTokens,
        ApprovedForItar = model.ItarApproved
    };
}

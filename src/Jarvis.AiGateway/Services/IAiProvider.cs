using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Provider-neutral seam for executing a chat completion against a backing model provider
/// (Amazon Bedrock, Azure OpenAI, …).  The orchestrator routes to an implementation based on
/// <see cref="GatewayModel.ProviderName"/>; everything upstream of this seam — identity
/// validation, policy/ITAR enforcement, request validation, inbound/outbound redaction
/// decisions, audit logging — is provider-agnostic and unchanged.
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable provider key matched against <see cref="GatewayModel.ProviderName"/>.</summary>
    string ProviderName { get; }

    Task<AiChatResult> CompleteAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability surface a provider implements when it can stream.  Kept separate from
/// <see cref="IAiProvider"/> so the core contract stays minimal, while still letting the
/// orchestrator make the existing "stream vs. fall back to a single non-streaming completion"
/// decision and label the audit event with the concrete invocation strategy name.
/// </summary>
/// <summary>
/// Optional capability surface for providers that can produce embeddings (Phase 2).  Kept separate
/// from <see cref="IAiProvider"/> (ISP) so a chat-only provider is never forced to implement it; the
/// embeddings orchestrator probes <c>provider is IEmbeddingProvider</c> and fails closed otherwise.
/// </summary>
public interface IEmbeddingProvider
{
    Task<AiEmbeddingsResult> EmbedAsync(
        GatewayModel model,
        AiEmbeddingsRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability surface for providers that can do text completion / fill-in-the-middle
/// (Phase 3).  Probed via <c>provider is ICompletionProvider</c>; fail-closed otherwise.
/// </summary>
public interface ICompletionProvider
{
    Task<AiCompletionResult> CompleteTextAsync(
        GatewayModel model,
        AiCompletionRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IStreamingAiProvider
{
    /// <summary>True when this provider can stream the given model + request.</summary>
    bool CanStream(GatewayModel model, AiChatRequest request);

    /// <summary>
    /// Name recorded as <c>audit.InvocationStrategy</c> for a streamed request (e.g.
    /// "converse-stream", "azure-openai-stream").  Resolved per request so a provider that
    /// fronts multiple strategies reports the one that will actually handle the request.
    /// </summary>
    string StreamInvocationName(GatewayModel model, AiChatRequest request);
}

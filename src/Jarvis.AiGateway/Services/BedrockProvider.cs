using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// <see cref="IAiProvider"/> for Amazon Bedrock.  This is a thin façade over the existing
/// <see cref="IBedrockInvocationStrategy"/> / <see cref="IBedrockStreamingStrategy"/> machinery:
/// it preserves the original strategy selection (Converse preferred over InvokeModel, capability
/// probing via <c>CanHandle</c>) and the original inbound/ITAR redaction performed inside each
/// strategy.  Wrapping rather than replacing the strategies keeps every existing Bedrock test and
/// DI override (which register fake strategies) working untouched.
/// </summary>
public sealed class BedrockProvider(
    IEnumerable<IBedrockInvocationStrategy> strategies,
    IEnumerable<IBedrockStreamingStrategy> streamingStrategies) : IAiProvider, IStreamingAiProvider
{
    public const string ProviderKey = "aws-bedrock";

    private readonly IReadOnlyList<IBedrockInvocationStrategy> _strategies = strategies.ToList();
    private readonly IReadOnlyList<IBedrockStreamingStrategy> _streamingStrategies = streamingStrategies.ToList();

    public string ProviderName => ProviderKey;

    public Task<AiChatResult> CompleteAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        // Converse is preferred over InvokeModel, mirroring the orchestrator's historical ordering.
        var strategy = _strategies
            .OrderBy(s => s is BedrockConverseInvocationStrategy ? 0 : 1)
            .FirstOrDefault(s => s.CanHandle(model, request))
            ?? throw new NotSupportedException(BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage);

        return strategy.InvokeAsync(model, request, context, cancellationToken);
    }

    // Returns the selected strategy's stream directly (rather than re-wrapping it in an iterator)
    // so the underlying enumerator's lifecycle — including a throwing DisposeAsync — is owned by
    // the caller (OpenAiSseStreamResult), preserving the original disposal semantics.
    public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var strategy = _streamingStrategies.FirstOrDefault(s => s.CanHandle(model, request))
            ?? throw new NotSupportedException("No Bedrock streaming strategy can handle this model.");

        return strategy.StreamAsync(model, request, context, cancellationToken);
    }

    public bool CanStream(GatewayModel model, AiChatRequest request) =>
        _streamingStrategies.Any(s => s.CanHandle(model, request));

    public string StreamInvocationName(GatewayModel model, AiChatRequest request) =>
        _streamingStrategies.FirstOrDefault(s => s.CanHandle(model, request))?.Name ?? ProviderName;
}

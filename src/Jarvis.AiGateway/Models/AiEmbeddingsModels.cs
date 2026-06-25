namespace Jarvis.AiGateway.Models;

/// <summary>Provider-neutral embeddings request. Inputs are plain text (one vector per input).</summary>
public sealed record AiEmbeddingsRequest(string Model, IReadOnlyList<string> Inputs, int? Dimensions);

/// <summary>One embedding vector with the index of the input it corresponds to.</summary>
public sealed record AiEmbedding(int Index, IReadOnlyList<float> Vector);

public sealed record AiEmbeddingsResult(
    IReadOnlyList<AiEmbedding> Data,
    TokenUsage Usage,
    ProviderInvocationMetadata ProviderMetadata);

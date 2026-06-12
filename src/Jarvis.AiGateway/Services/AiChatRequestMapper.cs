using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public static class AiChatRequestMapper
{
    public static AiChatRequest FromValidatedOpenAi(OpenAiChatCompletionRequest request, IReadOnlyList<string>? stopSequences = null)
    {
        var messages = request.Messages.Select(m =>
        {
            if (!m.TryGetTextContent(out var text, out var error))
            {
                throw new InvalidOperationException(error ?? "Unsupported message content.");
            }

            return new AiMessage(string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role, text);
        }).ToArray();

        return new AiChatRequest(
            request.Model,
            messages,
            new AiGenerationOptions(request.Temperature, request.TopP, request.MaxTokens, stopSequences ?? []),
            new Dictionary<string, string>(),
            request.Stream);
    }
}

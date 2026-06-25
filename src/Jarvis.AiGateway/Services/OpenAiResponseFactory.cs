using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Maps a provider-neutral <see cref="AiChatResult"/> to the OpenAI chat-completion response shape,
/// including tool/function calls (Phase 1).  Pure mapping logic — no provider SDK types — so it is
/// unit-tested directly.
/// </summary>
public static class OpenAiResponseFactory
{
    public static OpenAiChatCompletionResponse FromResult(string modelId, AiChatResult result)
    {
        var hasToolCalls = result.ToolCalls is { Count: > 0 };

        var message = new OpenAiAssistantMessage
        {
            Role = "assistant",
            Content = result.Text,
            ToolCalls = hasToolCalls
                ? result.ToolCalls!.Select(c => new OpenAiToolCall
                {
                    Id = c.Id,
                    Type = "function",
                    Function = new OpenAiFunctionCall { Name = c.Name, Arguments = c.ArgumentsJson }
                }).ToList()
                : null
        };

        return new OpenAiChatCompletionResponse
        {
            Model = modelId,
            Choices =
            [
                new OpenAiChoice
                {
                    Index = 0,
                    Message = message,
                    // A turn that produced tool calls always reports finish_reason "tool_calls".
                    FinishReason = hasToolCalls ? "tool_calls" : NormalizeFinishReason(result.FinishReason)
                }
            ],
            Usage = new OpenAiUsage
            {
                PromptTokens = result.Usage.PromptTokens,
                CompletionTokens = result.Usage.CompletionTokens,
                TotalTokens = result.Usage.TotalTokens == 0
                    ? result.Usage.PromptTokens + result.Usage.CompletionTokens
                    : result.Usage.TotalTokens
            }
        };
    }

    public static OpenAiChatCompletionResponse FromText(string modelId, string text, int inputTokens = 0, int outputTokens = 0, int totalTokens = 0, string finishReason = "stop") => new()
    {
        Model = modelId,
        Choices =
        [
            new OpenAiChoice
            {
                Index = 0,
                Message = new OpenAiAssistantMessage { Role = "assistant", Content = text },
                FinishReason = NormalizeFinishReason(finishReason)
            }
        ],
        Usage = new OpenAiUsage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = totalTokens == 0 ? inputTokens + outputTokens : totalTokens
        }
    };

    private static string NormalizeFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "stop";
        if (reason.Equals("tool_use", StringComparison.OrdinalIgnoreCase) || reason.Equals("tool_calls", StringComparison.OrdinalIgnoreCase)) return "tool_calls";
        return reason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) || reason.Equals("length", StringComparison.OrdinalIgnoreCase) ? "length" : "stop";
    }
}

using System.Text.RegularExpressions;
using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Per-deployment request-shape compatibility for Azure OpenAI.  Centralizes the rules that
/// differ by model family so the provider has no scattered inline conditionals.
///
/// <para>The GPT-5 family rejects <c>max_tokens</c> and requires <c>max_completion_tokens</c>;
/// earlier families (e.g. gpt-4.1-mini) keep <c>max_tokens</c>.  Exactly one token-limit
/// parameter is ever produced, so an outbound request never carries both.</para>
/// </summary>
public static class AzureOpenAiRequestCompatibility
{
    public const string MaxTokensParam = "max_tokens";
    public const string MaxCompletionTokensParam = "max_completion_tokens";

    // GPT-5 family (gpt-5, gpt-5.1, gpt-5-mini, …). Matched case-insensitively against the
    // configured Azure model name, falling back to the deployment name.
    private static readonly Regex GptFive = new(@"gpt-5", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The single outbound token-limit parameter chosen for a deployment.</summary>
    public sealed record TokenParameter(string Name, int Value);

    /// <summary>True when the deployment must use max_completion_tokens instead of max_tokens.</summary>
    public static bool RequiresMaxCompletionTokens(GatewayModel model)
    {
        var identifier = !string.IsNullOrWhiteSpace(model.AzureModelName)
            ? model.AzureModelName
            : model.AzureDeploymentName;
        return !string.IsNullOrWhiteSpace(identifier) && GptFive.IsMatch(identifier);
    }

    /// <summary>
    /// Resolves the single token-limit parameter to send for <paramref name="model"/>, honoring
    /// inbound max_completion_tokens, then inbound max_tokens, then the model's MaxOutputTokens
    /// fallback — all clamped to MaxOutputTokens.  Returns null when no positive limit applies.
    /// For GPT-5 the parameter name is max_completion_tokens; otherwise max_tokens.
    /// </summary>
    public static TokenParameter? NormalizeForDeployment(GatewayModel model, AiChatRequest request)
    {
        var requested = request.Options.MaxCompletionTokens ?? request.Options.MaxTokens;
        var ceiling = model.MaxOutputTokens;
        var effective = ceiling > 0
            ? Math.Min(requested ?? ceiling, ceiling)
            : requested ?? 0;

        if (effective <= 0)
        {
            return null;
        }

        var name = RequiresMaxCompletionTokens(model) ? MaxCompletionTokensParam : MaxTokensParam;
        return new TokenParameter(name, effective);
    }
}

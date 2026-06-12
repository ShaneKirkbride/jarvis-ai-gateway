using System.Text.RegularExpressions;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IPolicyEngine
{
    PolicyDecision Authorize(UserContext user, RequestContext context, OpenAiChatCompletionRequest request);
    IReadOnlyList<ModelRouteOptions> GetVisibleModels(UserContext user);
}

public sealed class PolicyEngine(IOptions<GatewayOptions> options) : IPolicyEngine
{
    private readonly GatewayOptions _options = options.Value;

    public IReadOnlyList<ModelRouteOptions> GetVisibleModels(UserContext user)
    {
        return _options.Models
            .Where(m => m.Enabled)
            .Where(m => IsUserInAllowedGroup(user, m))
            .ToList();
    }

    public PolicyDecision Authorize(UserContext user, RequestContext context, OpenAiChatCompletionRequest request)
    {
        var model = _options.Models.FirstOrDefault(m =>
            m.Enabled && string.Equals(m.Alias, request.Model, StringComparison.OrdinalIgnoreCase));

        if (model is null)
        {
            return new PolicyDecision(false, $"Model alias '{request.Model}' is not enabled or is not configured.", null);
        }

        if (string.IsNullOrWhiteSpace(model.BedrockModelId) || model.BedrockModelId.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
        {
            return new PolicyDecision(false, $"Model alias '{model.Alias}' does not have a real Bedrock model ID configured.", model);
        }

        if (!IsUserInAllowedGroup(user, model))
        {
            return new PolicyDecision(false, "User is not in an approved group for the requested model.", model);
        }

        var allPromptText = string.Join("\n", request.Messages.Select(m => m.GetTextContent()));
        if (allPromptText.Length > model.MaxInputCharacters)
        {
            return new PolicyDecision(false, $"Prompt exceeds configured maximum input size for model alias '{model.Alias}'.", model);
        }

        foreach (var pattern in _options.BlockedPromptPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (Regex.IsMatch(allPromptText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return new PolicyDecision(false, "Prompt matched a blocked policy pattern.", model);
            }
        }

        if (context.ItarMode || context.DataLabel.Contains("ITAR", StringComparison.OrdinalIgnoreCase))
        {
            if (!model.ItarApproved)
            {
                return new PolicyDecision(false, "ITAR-labeled request attempted to use a non-ITAR-approved model alias.", model);
            }

            if (_options.RequireItarWorkspaceForItarRequests &&
                !_options.ItarApprovedWorkspaceIds.Contains(context.WorkspaceId, StringComparer.OrdinalIgnoreCase))
            {
                return new PolicyDecision(false, "ITAR-labeled request did not originate from an approved ITAR workspace.", model);
            }
        }

        return new PolicyDecision(true, "ALLOW", model);
    }

    private static bool IsUserInAllowedGroup(UserContext user, ModelRouteOptions model)
    {
        if (model.AllowedGroups.Count == 0) return true;
        return model.AllowedGroups.Any(g => user.Groups.Contains(g));
    }
}

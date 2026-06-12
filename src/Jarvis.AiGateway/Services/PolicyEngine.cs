using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Jarvis.AiGateway.Services;

public interface IPolicyEngine
{
    Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken);
}

public interface IPolicyRule
{
    string RuleId { get; }
    PolicyRuleResult Evaluate(PolicyEvaluationContext context);
}

public sealed class PolicyEngine(IOptions<GatewayOptions> options, IModelRegistry modelRegistry, IEnumerable<IPolicyRule>? rules = null) : IPolicyEngine
{
    private readonly IReadOnlyList<IPolicyRule> _rules = (rules ?? DefaultRules(options.Value)).ToArray();

    public async Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken)
    {
        var models = await modelRegistry.GetChatModelsAsync(cancellationToken);
        return models.Where(m => IsUserInAllowedGroup(user, m)).ToList();
    }

    public async Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken)
    {
        var model = await modelRegistry.FindChatModelAsync(request.Model, cancellationToken);
        if (model is null)
        {
            return new PolicyDecision(false, $"Model '{request.Model}' is not enabled, not allowed by policy, or not supported for chat.", null)
            {
                RuleId = PolicyRuleIds.ModelNotFound
            };
        }

        var evaluationContext = new PolicyEvaluationContext(user, context, request, model);
        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(evaluationContext);
            if (!result.Allowed)
            {
                return new PolicyDecision(false, result.Reason, model) { RuleId = result.RuleId };
            }
        }

        return new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow };
    }

    internal static bool IsUserInAllowedGroup(UserContext user, GatewayModel model)
    {
        if (model.RequiredGroups.Count == 0) return true;
        return model.RequiredGroups.Any(g => user.Groups.Contains(g));
    }

    private static IReadOnlyList<IPolicyRule> DefaultRules(GatewayOptions options) =>
    [
        new ModelConfiguredRule(),
        new ModelEnabledRule(),
        new ModelTextOutputRule(),
        new GroupAuthorizationRule(),
        new PromptSizeRule(),
        new BlockedPatternRule(Microsoft.Extensions.Options.Options.Create(options)),
        new ItarModelRule(),
        new ItarWorkspaceRule(Microsoft.Extensions.Options.Options.Create(options))
    ];
}

public sealed class ModelConfiguredRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelPlaceholderId;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var model = context.Model!;
        return string.IsNullOrWhiteSpace(model.BedrockModelId) || model.BedrockModelId.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase)
            ? PolicyRuleResult.Deny(RuleId, $"Model alias '{model.Alias}' does not have a real Bedrock model ID configured.")
            : PolicyRuleResult.Allow(RuleId);
    }
}

public sealed class ModelEnabledRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelDisabled;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => context.Model!.Enabled
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "Model is disabled.");
}

public sealed class ModelTextOutputRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelNoTextOutput;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => context.Model!.HasTextOutput
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "Model does not advertise TEXT output and cannot be used for /v1/chat/completions.");
}

public sealed class GroupAuthorizationRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.UserGroupDenied;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => PolicyEngine.IsUserInAllowedGroup(context.User, context.Model!)
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "User is not in an approved group for the requested model.");
}

public sealed class PromptSizeRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.PromptTooLarge;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var prompt = string.Join("\n", context.Request.Messages.Select(m => m.Content));
        return prompt.Length > context.Model!.MaxInputCharacters
            ? PolicyRuleResult.Deny(RuleId, $"Prompt exceeds configured maximum input size for model alias '{context.Model.Alias}'.")
            : PolicyRuleResult.Allow(RuleId);
    }
}

public sealed class BlockedPatternRule(IOptions<GatewayOptions> gatewayOptions) : IPolicyRule
{
    public string RuleId => PolicyRuleIds.PromptBlockedPattern;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var prompt = string.Join("\n", context.Request.Messages.Select(m => m.Content));
        foreach (var pattern in gatewayOptions.Value.BlockedPromptPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (GatewayRegex.IsMatch(prompt, pattern, gatewayOptions.Value, RegexOptions.IgnoreCase))
            {
                return PolicyRuleResult.Deny(RuleId, "Prompt matched a blocked policy pattern.");
            }
        }

        return PolicyRuleResult.Allow(RuleId);
    }
}

public sealed class ItarModelRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ItarModelDenied;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        if (!IsItar(context)) return PolicyRuleResult.Allow(RuleId);
        return context.Model!.ItarApproved
            ? PolicyRuleResult.Allow(RuleId)
            : PolicyRuleResult.Deny(RuleId, "ITAR-labeled request attempted to use a non-ITAR-approved model.");
    }

    internal static bool IsItar(PolicyEvaluationContext context) => context.RequestContext.ItarMode || DataLabelClassifier.IsItar(context.RequestContext.DataLabel);
}

public sealed class ItarWorkspaceRule(IOptions<GatewayOptions> gatewayOptions) : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ItarWorkspaceDenied;
    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        if (!ItarModelRule.IsItar(context) || !gatewayOptions.Value.RequireItarWorkspaceForItarRequests) return PolicyRuleResult.Allow(RuleId);
        return gatewayOptions.Value.ItarApprovedWorkspaceIds.Contains(context.RequestContext.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            ? PolicyRuleResult.Allow(RuleId)
            : PolicyRuleResult.Deny(RuleId, "ITAR-labeled request did not originate from an approved ITAR workspace.");
    }
}

namespace Jarvis.AiGateway.Models;

public static class PolicyRuleIds
{
    public const string Allow = "ALLOW";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string ModelPlaceholderId = "MODEL_PLACEHOLDER_ID";
    public const string ModelDisabled = "MODEL_DISABLED";
    public const string ModelNotInKeyScope = "MODEL_NOT_IN_KEY_SCOPE";
    public const string ModelNoTextOutput = "MODEL_NO_TEXT_OUTPUT";
    public const string UserGroupDenied = "USER_GROUP_DENIED";
    public const string PromptTooLarge = "PROMPT_TOO_LARGE";
    public const string PromptBlockedPattern = "PROMPT_BLOCKED_PATTERN";
    public const string ItarModelDenied = "ITAR_MODEL_DENIED";
    public const string ItarWorkspaceDenied = "ITAR_WORKSPACE_DENIED";
}

public sealed record PolicyEvaluationContext(
    UserContext User,
    RequestContext RequestContext,
    AiChatRequest Request,
    GatewayModel Model);

public sealed record PolicyRuleResult(bool Allowed, string RuleId, string Reason)
{
    public static PolicyRuleResult Allow(string ruleId) => new(true, ruleId, "ALLOW");
    public static PolicyRuleResult Deny(string ruleId, string reason) => new(false, ruleId, reason);
}

public sealed record PolicyDecision(bool Allowed, string Reason, GatewayModel? Model, string RuleId)
{
    public PolicyDecision(bool allowed, string reason, GatewayModel? model)
        : this(allowed, reason, model, allowed ? PolicyRuleIds.Allow : PolicyRuleIds.ModelNotFound)
    {
    }
}

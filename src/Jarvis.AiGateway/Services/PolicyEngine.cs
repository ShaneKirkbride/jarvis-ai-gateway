using System.Text.RegularExpressions;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IPolicyEngine
{
    Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken);

    // Embeddings authorization (Phase 2). Default implementations fail closed / return empty so
    // existing test stubs compile unchanged; the production PolicyEngine overrides them.
    Task<PolicyDecision> AuthorizeEmbeddingsAsync(UserContext user, RequestContext context, AiEmbeddingsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PolicyDecision(false, "Embeddings authorization is not configured.", null, PolicyRuleIds.ModelNotFound));

    Task<IReadOnlyList<GatewayModel>> GetVisibleEmbeddingModelsAsync(UserContext user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GatewayModel>>([]);

    // Completions/FIM authorization (Phase 3). Default fails closed / returns empty.
    Task<PolicyDecision> AuthorizeCompletionAsync(UserContext user, RequestContext context, AiCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PolicyDecision(false, "Completions authorization is not configured.", null, PolicyRuleIds.ModelNotFound));

    Task<IReadOnlyList<GatewayModel>> GetVisibleCompletionModelsAsync(UserContext user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GatewayModel>>([]);
}

public interface IPolicyRule
{
    string RuleId { get; }
    PolicyRuleResult Evaluate(PolicyEvaluationContext context);
}

public sealed class PolicyEngine : IPolicyEngine
{
    private readonly IModelRegistry modelRegistry;
    private readonly IReadOnlyList<IPolicyRule> _rules;

    public PolicyEngine(IModelRegistry modelRegistry, IEnumerable<IPolicyRule> rules)
    {
        this.modelRegistry = modelRegistry;
        _rules = rules.ToArray();
    }

    public PolicyEngine(IOptions<GatewayOptions> options, IModelRegistry modelRegistry)
        : this(modelRegistry, DefaultRules(options))
    {
    }

    private static IReadOnlyList<IPolicyRule> DefaultRules(IOptions<GatewayOptions> options) =>
    [
        new ModelConfiguredRule(),
        new ModelEnabledRule(),
        new ModelTextOutputRule(),
        new DeveloperKeyModelAllowlistRule(),
        new GroupAuthorizationRule(),
        new PromptSizeRule(),
        new BlockedPatternRule(options),
        new ItarModelRule(),
        new ItarWorkspaceRule(options)
    ];

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
            return new PolicyDecision(false, $"Model '{request.Model}' is not enabled, not allowed by policy, or not supported for chat.", null, PolicyRuleIds.ModelNotFound);
        }

        var evaluationContext = new PolicyEvaluationContext(user, context, request, model);
        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(evaluationContext);
            if (!result.Allowed)
            {
                return new PolicyDecision(false, result.Reason, model, result.RuleId);
            }
        }

        return new PolicyDecision(true, "ALLOW", model, PolicyRuleIds.Allow);
    }

    public async Task<IReadOnlyList<GatewayModel>> GetVisibleEmbeddingModelsAsync(UserContext user, CancellationToken cancellationToken)
    {
        var models = await modelRegistry.GetEmbeddingModelsAsync(cancellationToken);
        return models.Where(m => IsUserInAllowedGroup(user, m)).ToList();
    }

    public async Task<IReadOnlyList<GatewayModel>> GetVisibleCompletionModelsAsync(UserContext user, CancellationToken cancellationToken)
    {
        var models = await modelRegistry.GetCompletionModelsAsync(cancellationToken);
        return models.Where(m => IsUserInAllowedGroup(user, m)).ToList();
    }

    public async Task<PolicyDecision> AuthorizeCompletionAsync(UserContext user, RequestContext context, AiCompletionRequest request, CancellationToken cancellationToken)
    {
        var model = await modelRegistry.FindCompletionModelAsync(request.Model, cancellationToken);
        if (model is null)
        {
            return new PolicyDecision(false, $"Model '{request.Model}' is not enabled or not configured for completions.", null, PolicyRuleIds.ModelNotFound);
        }

        // Reuse the full rule pipeline: prompt (+ FIM suffix) become user content, so group, ITAR,
        // blocked-pattern, and prompt-size rules ALL apply to the autocomplete egress.
        var messages = new List<AiMessage> { new("user", request.Prompt) };
        if (!string.IsNullOrEmpty(request.Suffix))
        {
            messages.Add(new AiMessage("user", request.Suffix));
        }

        var synthetic = new AiChatRequest(request.Model, messages, new AiGenerationOptions(null, null, null, []), new Dictionary<string, string>(), false);
        var evaluationContext = new PolicyEvaluationContext(user, context, synthetic, model);
        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(evaluationContext);
            if (!result.Allowed)
            {
                return new PolicyDecision(false, result.Reason, model, result.RuleId);
            }
        }

        return new PolicyDecision(true, "ALLOW", model, PolicyRuleIds.Allow);
    }

    public async Task<PolicyDecision> AuthorizeEmbeddingsAsync(UserContext user, RequestContext context, AiEmbeddingsRequest request, CancellationToken cancellationToken)
    {
        var model = await modelRegistry.FindEmbeddingModelAsync(request.Model, cancellationToken);
        if (model is null)
        {
            return new PolicyDecision(false, $"Model '{request.Model}' is not enabled or not configured for embeddings.", null, PolicyRuleIds.ModelNotFound);
        }

        // Reuse the full rule pipeline by mapping each embedding input to user content, so group,
        // ITAR, blocked-pattern, and prompt-size rules ALL apply to this bulk source-code egress.
        var synthetic = new AiChatRequest(
            request.Model,
            request.Inputs.Select(i => new AiMessage("user", i)).ToList(),
            new AiGenerationOptions(null, null, null, []),
            new Dictionary<string, string>(),
            false);

        var evaluationContext = new PolicyEvaluationContext(user, context, synthetic, model);
        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(evaluationContext);
            if (!result.Allowed)
            {
                return new PolicyDecision(false, result.Reason, model, result.RuleId);
            }
        }

        return new PolicyDecision(true, "ALLOW", model, PolicyRuleIds.Allow);
    }

    /// <summary>
    /// Authorize by Entra group object IDs when the model is configured with them; only fall
    /// back to display-name matching when the model has no object-ID allowlist.
    /// <para>
    /// Display names are NEVER a tiebreaker — a user with a matching display name but a
    /// mismatching object ID is denied.  This invariant is asserted by
    /// <c>PolicyEngineGroupIdAuthorizationTests</c> and is the security claim that lets us
    /// rotate group names in Entra without affecting authorization.
    /// </para>
    /// </summary>
    internal static bool IsUserInAllowedGroup(UserContext user, GatewayModel model)
    {
        if (model.RequiredGroupIds.Count > 0)
        {
            return model.RequiredGroupIds.Any(id => user.GroupIds.Contains(id));
        }

        if (model.RequiredGroups.Count > 0)
        {
            return model.RequiredGroups.Any(g => user.Groups.Contains(g));
        }

        return true;
    }
}

public sealed class ModelConfiguredRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelPlaceholderId;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var model = context.Model;
        var provider = string.IsNullOrWhiteSpace(model.ProviderName) ? "aws-bedrock" : model.ProviderName.Trim();

        // Each provider has its own routing identifier; a missing or placeholder identifier means
        // the alias is not really configured.  An unknown provider fails closed.
        if (provider.Equals("aws-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return IsRealIdentifier(model.BedrockModelId)
                ? PolicyRuleResult.Allow(RuleId)
                : PolicyRuleResult.Deny(RuleId, $"Model alias '{model.Alias}' does not have a real Bedrock model ID configured.");
        }

        if (provider.Equals("azure-openai", StringComparison.OrdinalIgnoreCase))
        {
            return IsRealIdentifier(model.AzureDeploymentName)
                ? PolicyRuleResult.Allow(RuleId)
                : PolicyRuleResult.Deny(RuleId, $"Model alias '{model.Alias}' does not have a real Azure deployment name configured.");
        }

        return PolicyRuleResult.Deny(RuleId, $"Model alias '{model.Alias}' has an unsupported provider '{model.ProviderName}'.");
    }

    private static bool IsRealIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !GatewayOptionsValidator.LooksLikePlaceholder(value);
}

/// <summary>
/// Enforces a developer API key's optional model allowlist.  No-op for any non-developer-key
/// principal, so it never affects JWT/broker/service-key authorization.  This is a per-credential
/// restriction layered ON TOP of the existing group/ITAR rules — it can only further restrict,
/// never grant — so a developer key cannot become an authorization escalation path.
/// </summary>
public sealed class DeveloperKeyModelAllowlistRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelNotInKeyScope;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var claims = context.User.Claims;

        if (!claims.TryGetValue(DeveloperApiKeyClaims.AuthTypeClaim, out var authType) ||
            !string.Equals(authType, DeveloperApiKeyClaims.AuthTypeValue, StringComparison.Ordinal))
        {
            return PolicyRuleResult.Allow(RuleId);
        }

        if (!claims.TryGetValue(DeveloperApiKeyClaims.ModelAllowlistClaim, out var csv) || string.IsNullOrWhiteSpace(csv))
        {
            return PolicyRuleResult.Allow(RuleId);
        }

        var allowed = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var model = context.Model;
        var permitted = allowed.Any(a =>
            a.Equals(model.Id, StringComparison.OrdinalIgnoreCase) ||
            a.Equals(model.Alias, StringComparison.OrdinalIgnoreCase));

        return permitted
            ? PolicyRuleResult.Allow(RuleId)
            : PolicyRuleResult.Deny(RuleId, $"Model '{model.Alias}' is not in the developer API key's allowed model list.");
    }
}

public sealed class ModelEnabledRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelDisabled;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => context.Model.Enabled
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "Model is disabled.");
}

public sealed class ModelTextOutputRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.ModelNoTextOutput;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => context.Model.HasTextOutput
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "Model does not advertise TEXT output and cannot be used for /v1/chat/completions.");
}

public sealed class GroupAuthorizationRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.UserGroupDenied;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context) => PolicyEngine.IsUserInAllowedGroup(context.User, context.Model)
        ? PolicyRuleResult.Allow(RuleId)
        : PolicyRuleResult.Deny(RuleId, "User is not in an approved group for the requested model.");
}

public sealed class PromptSizeRule : IPolicyRule
{
    public string RuleId => PolicyRuleIds.PromptTooLarge;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        var prompt = string.Join("\n", context.Request.Messages.Select(m => m.Content));
        return prompt.Length > context.Model.MaxInputCharacters
            ? PolicyRuleResult.Deny(RuleId, $"Prompt exceeds configured maximum input size for model alias '{context.Model.Alias}'.")
            : PolicyRuleResult.Allow(RuleId);
    }
}

public sealed class BlockedPatternRule : IPolicyRule
{
    // Compile blocked patterns once at construction (rule is singleton).
    // Startup validation in GatewayOptionsValidator already ensures patterns are valid regex.
    private readonly IReadOnlyList<Regex> _compiledPatterns;

    public BlockedPatternRule(IOptions<GatewayOptions> gatewayOptions)
    {
        var options = gatewayOptions.Value;
        _compiledPatterns = options.BlockedPromptPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => GatewayRegex.Create(p, options, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
    }

    public string RuleId => PolicyRuleIds.PromptBlockedPattern;

    public PolicyRuleResult Evaluate(PolicyEvaluationContext context)
    {
        if (_compiledPatterns.Count == 0) return PolicyRuleResult.Allow(RuleId);
        var prompt = string.Join("\n", context.Request.Messages.Select(m => m.Content));
        foreach (var regex in _compiledPatterns)
        {
            if (regex.IsMatch(prompt))
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
        return context.Model.ItarApproved
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

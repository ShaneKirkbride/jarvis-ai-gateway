using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class DeveloperApiKeyPolicyAndModelsTests
{
    private readonly DeveloperKeyModelAllowlistRule _rule = new();

    [Fact]
    public void Allowlist_rule_allows_model_in_key_scope()
    {
        var ctx = Context(DevUser("general,fast"), Model("general"));
        Assert.True(_rule.Evaluate(ctx).Allowed);
    }

    [Fact]
    public void Allowlist_rule_denies_model_outside_key_scope()
    {
        var result = _rule.Evaluate(Context(DevUser("general"), Model("restricted")));
        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.ModelNotInKeyScope, result.RuleId);
    }

    [Fact]
    public void Allowlist_rule_is_noop_when_key_has_no_allowlist()
    {
        var ctx = Context(DevUser(allowlist: null), Model("anything"));
        Assert.True(_rule.Evaluate(ctx).Allowed);
    }

    [Fact]
    public void Allowlist_rule_is_noop_for_non_developer_principals()
    {
        // A normal JWT/broker user has no developer auth_type claim — the rule must not restrict.
        var user = new UserContext("u", "u@example.test", new HashSet<string>(), new Dictionary<string, string>());
        Assert.True(_rule.Evaluate(Context(user, Model("anything"))).Allowed);
    }

    [Fact]
    public void Capability_mapper_maps_azure_chat_model()
    {
        var info = ModelCapabilityMapper.ToModelInfo(new GatewayModel
        {
            Id = "jarvis2-chat",
            Alias = "jarvis2-chat",
            DisplayName = "Jarvis 2 Chat",
            ProviderName = "azure-openai",
            MaxOutputTokens = 4096,
            ContextWindowTokens = 128000,
            ItarApproved = true,
            SupportsConverse = true
        });

        Assert.Equal("jarvis2-chat", info.Id);
        Assert.Equal("azure-openai", info.OwnedBy);
        Assert.Equal("azure-openai", info.Provider);
        Assert.Equal("Jarvis 2 Chat", info.DisplayName);
        Assert.True(info.SupportsChat);
        Assert.True(info.SupportsStreaming);
        Assert.False(info.SupportsTools);
        Assert.False(info.SupportsEmbeddings);
        Assert.False(info.SupportsFim);
        Assert.False(info.SupportsVision);
        Assert.Equal(128000, info.ContextWindow);
        Assert.Equal(4096, info.MaxOutputTokens);
        Assert.True(info.ApprovedForItar);
    }

    [Fact]
    public void Capability_mapper_reflects_supports_tools()
    {
        Assert.True(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "t", ProviderName = "azure-openai", SupportsTools = true, SupportsConverse = true }).SupportsTools);
        Assert.False(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "n", ProviderName = "azure-openai", SupportsTools = false }).SupportsTools);
    }

    [Fact]
    public void Capability_mapper_marks_invoke_model_only_bedrock_as_non_streaming()
    {
        var info = ModelCapabilityMapper.ToModelInfo(new GatewayModel
        {
            Id = "legacy",
            ProviderName = "aws-bedrock",
            SupportsConverse = false,
            DisplayName = ""
        });

        Assert.False(info.SupportsStreaming);
        Assert.Null(info.DisplayName);   // empty display name omitted
        Assert.Null(info.ContextWindow); // unset context window omitted
    }

    private static UserContext DevUser(string? allowlist)
    {
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DeveloperApiKeyClaims.AuthTypeClaim] = DeveloperApiKeyClaims.AuthTypeValue,
            [DeveloperApiKeyClaims.KeyIdClaim] = "k1"
        };
        if (!string.IsNullOrEmpty(allowlist))
        {
            claims[DeveloperApiKeyClaims.ModelAllowlistClaim] = allowlist;
        }

        return new UserContext("dev", "dev@example.test", new HashSet<string>(), claims);
    }

    private static GatewayModel Model(string id) => new() { Id = id, Alias = id };

    private static PolicyEvaluationContext Context(UserContext user, GatewayModel model) => new(
        user,
        new RequestContext("rid", "cid", "ws", "GENERAL", false),
        new AiChatRequest(model.Id, [new AiMessage("user", "hi")], new AiGenerationOptions(null, null, null, []), new Dictionary<string, string>(), false),
        model);
}

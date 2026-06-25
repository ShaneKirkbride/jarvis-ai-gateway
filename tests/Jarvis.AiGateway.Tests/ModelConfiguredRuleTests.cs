using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ModelConfiguredRuleTests
{
    private readonly ModelConfiguredRule _rule = new();

    [Fact]
    public void Allows_azure_model_with_real_deployment_name()
    {
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "jarvis2-chat",
            ProviderName = "azure-openai",
            AzureDeploymentName = "jarvis2-chat"
        }));

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_WITH_DEPLOYMENT")]
    public void Denies_azure_model_without_real_deployment_name(string deployment)
    {
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "jarvis2-chat",
            ProviderName = "azure-openai",
            AzureDeploymentName = deployment
        }));

        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.ModelPlaceholderId, result.RuleId);
    }

    [Fact]
    public void Allows_bedrock_model_with_real_model_id()
    {
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "general",
            ProviderName = "aws-bedrock",
            BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0"
        }));

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REPLACE_WITH_GOVCLOUD_BEDROCK_MODEL_ID")]
    public void Denies_bedrock_model_without_real_model_id(string modelId)
    {
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "general",
            ProviderName = "aws-bedrock",
            BedrockModelId = modelId
        }));

        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.ModelPlaceholderId, result.RuleId);
    }

    [Fact]
    public void Blank_provider_name_is_treated_as_bedrock()
    {
        // Defensive: a model with no ProviderName behaves like the historical Bedrock-only path.
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "legacy",
            ProviderName = "",
            BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0"
        }));

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Unknown_provider_fails_closed()
    {
        var result = _rule.Evaluate(Context(new GatewayModel
        {
            Alias = "mystery",
            ProviderName = "openai-direct",
            BedrockModelId = "x",
            AzureDeploymentName = "y"
        }));

        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.ModelPlaceholderId, result.RuleId);
        Assert.Contains("unsupported provider", result.Reason);
    }

    private static PolicyEvaluationContext Context(GatewayModel model) => new(
        new UserContext("sub", "user@example.test", new HashSet<string>(), new Dictionary<string, string>()),
        new RequestContext("rid", "cid", "ws", "GENERAL", false),
        new AiChatRequest(model.Alias, [new AiMessage("user", "hi")], new AiGenerationOptions(null, null, null, []), new Dictionary<string, string>(), false),
        model);
}

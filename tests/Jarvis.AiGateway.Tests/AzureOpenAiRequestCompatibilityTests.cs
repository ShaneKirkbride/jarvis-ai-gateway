using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class AzureOpenAiRequestCompatibilityTests
{
    [Fact]
    public void Gpt5_inbound_max_tokens_becomes_max_completion_tokens()
    {
        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(Gpt5Model(), Request(maxTokens: 123));

        Assert.NotNull(result);
        Assert.Equal("max_completion_tokens", result!.Name);
        Assert.Equal(123, result.Value);
    }

    [Fact]
    public void Gpt5_inbound_max_completion_tokens_stays_max_completion_tokens()
    {
        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(Gpt5Model(), Request(maxCompletionTokens: 77));

        Assert.NotNull(result);
        Assert.Equal("max_completion_tokens", result!.Name);
        Assert.Equal(77, result.Value);
    }

    [Fact]
    public void Gpt5_no_explicit_limit_uses_model_max_output_tokens_as_max_completion_tokens()
    {
        var model = Gpt5Model();
        model.MaxOutputTokens = 555;

        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, Request());

        Assert.NotNull(result);
        Assert.Equal("max_completion_tokens", result!.Name);
        Assert.Equal(555, result.Value);
    }

    [Theory]
    [InlineData(50, null)]
    [InlineData(null, 50)]
    [InlineData(null, null)]
    public void Gpt5_never_emits_max_tokens(int? maxTokens, int? maxCompletionTokens)
    {
        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(Gpt5Model(), Request(maxTokens, maxCompletionTokens));

        Assert.NotNull(result);
        Assert.NotEqual("max_tokens", result!.Name);
    }

    [Fact]
    public void Gpt41_mini_still_uses_max_tokens()
    {
        var model = new GatewayModel { Alias = "jarvis2-fast", ProviderName = "azure-openai", AzureDeploymentName = "jarvis2-fast", AzureModelName = "gpt-4.1-mini", MaxOutputTokens = 2048 };

        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, Request(maxTokens: 200));

        Assert.NotNull(result);
        Assert.Equal("max_tokens", result!.Name);
        Assert.Equal(200, result.Value);
    }

    [Fact]
    public void Requested_value_is_clamped_to_model_max_output_tokens()
    {
        var model = Gpt5Model();
        model.MaxOutputTokens = 100;

        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, Request(maxTokens: 99999));

        Assert.Equal(100, result!.Value);
    }

    [Fact]
    public void Falls_back_to_deployment_name_when_model_name_absent()
    {
        // No AzureModelName configured, but the deployment itself is named for a GPT-5 model.
        var model = new GatewayModel { Alias = "x", ProviderName = "azure-openai", AzureDeploymentName = "gpt-5-chat", MaxOutputTokens = 64 };

        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, Request(maxTokens: 10));

        Assert.Equal("max_completion_tokens", result!.Name);
    }

    [Fact]
    public void Returns_null_when_no_positive_limit_applies()
    {
        var model = new GatewayModel { Alias = "x", ProviderName = "azure-openai", AzureDeploymentName = "jarvis2-fast", MaxOutputTokens = 0 };

        var result = AzureOpenAiRequestCompatibility.NormalizeForDeployment(model, Request());

        Assert.Null(result);
    }

    private static GatewayModel Gpt5Model() => new()
    {
        Alias = "jarvis2-chat",
        ProviderName = "azure-openai",
        AzureDeploymentName = "jarvis2-chat",
        AzureModelName = "gpt-5.1",
        MaxOutputTokens = 2048
    };

    private static AiChatRequest Request(int? maxTokens = null, int? maxCompletionTokens = null) =>
        new("m", [new AiMessage("user", "hi")], new AiGenerationOptions(null, null, maxTokens, [], maxCompletionTokens), new Dictionary<string, string>(), false);
}

using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class RedactionValidationAndAdapterTests
{
    [Fact]
    public void Regex_redactor_redacts_all_builtin_secret_patterns_and_counts_matches()
    {
        var redactor = new RegexContentRedactor(MsOptions.Create(new GatewayOptions()));
        var text = "AKIA1234567890ABCDEF aws_secret_access_key = abcdefghijklmnopqrstuvwxyzABCDEF api_key=abcdefghijklmnop password=hunter2 123-45-6789 4111 1111 1111 1111 -----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----";

        var result = redactor.Redact(text);

        Assert.Equal(7, result.RedactionCount);
        Assert.DoesNotContain("AKIA1234567890ABCDEF", result.Text);
        Assert.Contains("[REDACTED_AWS_ACCESS_KEY]", result.Text);
        Assert.Contains("[REDACTED_AWS_SECRET]", result.Text);
        Assert.Contains("[REDACTED_API_KEY]", result.Text);
        Assert.Contains("[REDACTED_PASSWORD]", result.Text);
        Assert.Contains("[REDACTED_SSN]", result.Text);
        Assert.Contains("[REDACTED_POSSIBLE_CARD]", result.Text);
        Assert.Contains("[REDACTED_PRIVATE_KEY]", result.Text);
    }

    [Fact]
    public void Regex_redactor_returns_original_text_when_disabled_or_empty()
    {
        Assert.Equal(new RedactionResult("secret", 0), new RegexContentRedactor(MsOptions.Create(new GatewayOptions { Redaction = new RedactionOptions { Enabled = false } })).Redact("secret"));
        Assert.Equal(new RedactionResult(string.Empty, 0), new RegexContentRedactor(MsOptions.Create(new GatewayOptions())).Redact(string.Empty));
    }

    [Fact]
    public void Gateway_options_validator_reports_all_invalid_startup_configuration()
    {
        var options = new GatewayOptions
        {
            AwsRegion = " ",
            ModelDiscovery = new ModelDiscoveryOptions { CacheSeconds = 0 },
            Models =
            [
                new ModelRouteOptions { Alias = " ", BedrockModelId = " ", MaxInputCharacters = 0, MaxOutputTokens = 0 }
            ]
        };

        var result = new GatewayOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AwsRegion"));
        Assert.Contains(result.Failures!, f => f.Contains("CacheSeconds"));
        Assert.Contains(result.Failures!, f => f.Contains("Alias"));
        Assert.Contains(result.Failures!, f => f.Contains("BedrockModelId"));
        Assert.Contains(result.Failures!, f => f.Contains("MaxInputCharacters"));
        Assert.Contains(result.Failures!, f => f.Contains("MaxOutputTokens"));

        var missingSecretResult = new GatewayOptionsValidator().Validate(null, new GatewayOptions { RequireServiceApiKey = true });
        var productionResult = new GatewayOptionsValidator().Validate(null, new GatewayOptions { EnvironmentName = "Production", RequireServiceApiKey = false });
        var placeholderSecretResult = new GatewayOptionsValidator().Validate(null, new GatewayOptions { RequireServiceApiKey = true, ServiceApiKey = "REPLACE_WITH_SECRET" });
        var invalidBoundsResult = new GatewayOptionsValidator().Validate(null, new GatewayOptions { RequestValidation = new RequestValidationOptions { MinimumTemperature = 0.7f, MaximumTemperature = 0.5f, MaxStopSequences = -1, MaxStopSequenceCharacters = 0, MaxMetadataBytes = 0 } });

        Assert.True(missingSecretResult.Failed);
        Assert.Contains(missingSecretResult.Failures!, f => f.Contains("ServiceApiKey"));
        Assert.True(productionResult.Failed);
        Assert.Contains(productionResult.Failures!, f => f.Contains("RequireServiceApiKey"));
        Assert.True(placeholderSecretResult.Failed);
        Assert.Contains(placeholderSecretResult.Failures!, f => f.Contains("ServiceApiKey"));
        Assert.True(invalidBoundsResult.Failed);
        Assert.Contains(invalidBoundsResult.Failures!, f => f.Contains("temperature"));
        Assert.Contains(invalidBoundsResult.Failures!, f => f.Contains("MaxStopSequences"));
        Assert.Contains(invalidBoundsResult.Failures!, f => f.Contains("MaxStopSequenceCharacters"));
        Assert.Contains(invalidBoundsResult.Failures!, f => f.Contains("MaxMetadataBytes"));
        Assert.True(new GatewayOptionsValidator().Validate(null, new GatewayOptions()).Succeeded);
    }

    [Fact]
    public void Openai_message_extracts_text_from_all_supported_content_shapes()
    {
        Assert.Equal("hello", Message(Json("hello")).GetTextContent());
        Assert.Equal("a\nb", Message(Json(new object[] { new { type = "text", text = "a" }, new { type = "text", text = "b" } })).GetTextContent());
        Assert.Equal("object text", Message(Json(new { type = "text", text = "object text" })).GetTextContent());
        Assert.Throws<InvalidOperationException>(() => Message(Json(123)).GetTextContent());
    }

    [Fact]
    public void Request_helpers_build_prompt_and_stop_sequences()
    {
        var stopArray = Request("alias", stop: Json(new[] { "one", "two", "three", "four", "five" }));
        var prompt = OpenAiRequestHelpers.Prompt(ToAi(new OpenAiChatCompletionRequest
        {
            Model = "alias",
            Messages =
            [
                new OpenAiMessage { Role = "", Content = Json("hello") },
                new OpenAiMessage { Role = "assistant", Content = Json("hi") }
            ]
        }));

        Assert.Equal("user: hello\nassistant: hi\nassistant: ", prompt.ReplaceLineEndings("\n"));
        Assert.Equal(["stop"], OpenAiRequestHelpers.GetStopSequences(ToAi(Request("alias", stop: Json("stop")), ["stop"])));
        Assert.Equal(["one", "two", "three", "four"], OpenAiRequestHelpers.GetStopSequences(ToAi(stopArray, ["one", "two", "three", "four"])));
        Assert.Empty(OpenAiRequestHelpers.GetStopSequences(ToAi(Request("alias", stop: Json(123)))));
        Assert.Empty(OpenAiRequestHelpers.GetStopSequences(ToAi(Request("alias"))));
    }

    [Fact]
    public void Response_factory_normalizes_finish_reasons_and_token_totals()
    {
        Assert.Equal("length", OpenAiResponseFactory.FromText("model", "text", 1, 2, finishReason: "max_tokens").Choices[0].FinishReason);
        Assert.Equal("length", OpenAiResponseFactory.FromText("model", "text", finishReason: "length").Choices[0].FinishReason);
        var response = OpenAiResponseFactory.FromText("model", "text", 1, 2, 10, "unknown");
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Equal(10, response.Usage!.TotalTokens);
    }

    [Fact]
    public void Invoke_model_payload_adapters_build_provider_payloads_and_parse_responses()
    {
        var context = new RequestContext("r", "c", "w", "NON_ITAR", false);
        var request = Request("alias", maxTokens: 5000, stop: Json(new[] { "END" }));
        var titanModel = Model("amazon.titan-text-express-v1", "Amazon", 100, "titan");
        var llamaModel = Model("meta.llama3-8b-instruct-v1:0", "Meta", 100, "llama");
        var mistralModel = Model("mistral.mistral-7b-instruct-v0:2", "Mistral AI", 100, "mistral");
        var titan = new AmazonTitanTextInvokeModelPayloadAdapter();
        var llama = new MetaLlamaInvokeModelPayloadAdapter();
        var mistral = new MistralInvokeModelPayloadAdapter();

        Assert.True(titan.CanHandle(titanModel));
        Assert.True(llama.CanHandle(llamaModel));
        Assert.True(mistral.CanHandle(mistralModel));
        Assert.False(titan.CanHandle(Model("other", "Other", 100, "other")));
        Assert.False(llama.CanHandle(Model("other", "Other", 100, "other")));
        Assert.False(mistral.CanHandle(Model("other", "Other", 100, "other")));
        Assert.Contains("maxTokenCount\":100", titan.BuildRequestBody(titanModel, ToAi(request, ["END"]), context));
        Assert.Contains("max_gen_len\":100", llama.BuildRequestBody(llamaModel, ToAi(request, ["END"]), context));
        Assert.Contains("max_tokens\":100", mistral.BuildRequestBody(mistralModel, ToAi(request, ["END"]), context));

        Assert.Equal("titan output", titan.ParseResponseBody(titanModel, "{\"inputTextTokenCount\":3,\"results\":[{\"outputText\":\"titan output\",\"tokenCount\":4}]}", context).Text);
        var llamaResponse = llama.ParseResponseBody(llamaModel, "{\"generation\":\"llama output\",\"prompt_token_count\":5,\"generation_token_count\":6,\"stop_reason\":\"length\"}", context);
        Assert.Equal("length", llamaResponse.FinishReason);
        Assert.Equal(11, llamaResponse.Usage.TotalTokens);
        Assert.Equal("mistral output", mistral.ParseResponseBody(mistralModel, "{\"outputs\":[{\"text\":\"mistral output\",\"stop_reason\":\"stop\"}]}", context).Text);
        Assert.Equal(string.Empty, titan.ParseResponseBody(titanModel, "{}", context).Text);
        Assert.Equal(string.Empty, llama.ParseResponseBody(llamaModel, "{}", context).Text);
        Assert.Equal(string.Empty, mistral.ParseResponseBody(mistralModel, "{}", context).Text);
    }

    private static OpenAiMessage Message(JsonElement content) => new() { Content = content };

    private static OpenAiChatCompletionRequest Request(string model, int? maxTokens = null, JsonElement? stop = null, IReadOnlyList<string>? stops = null) => new()
    {
        Model = model,
        MaxTokens = maxTokens,
        Temperature = 0.7F,
        TopP = 0.8F,
        Stop = stop,
        Messages = [new OpenAiMessage { Role = "user", Content = Json("hello") }]
    };

    private static AiChatRequest ToAi(OpenAiChatCompletionRequest request, IReadOnlyList<string>? stops = null) => AiChatRequestMapper.FromValidatedOpenAi(request, stops);

    private static GatewayModel Model(string bedrockId, string provider, int maxTokens, string alias) => new()
    {
        Id = alias,
        Alias = alias,
        BedrockModelId = bedrockId,
        ProviderName = provider,
        MaxOutputTokens = maxTokens,
        OutputModalities = ["TEXT"]
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

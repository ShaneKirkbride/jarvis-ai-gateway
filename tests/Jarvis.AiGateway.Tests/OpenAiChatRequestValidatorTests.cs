using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class OpenAiChatRequestValidatorTests
{
    [Theory]
    [InlineData("", "model_required")]
    [InlineData("valid", null)]
    public async Task Validator_requires_model_and_accepts_minimal_valid_request(string model, string? expectedCode)
    {
        var result = await Validator().ValidateAsync(Request(model), CancellationToken.None);

        Assert.Equal(expectedCode is null, result.IsValid);
        if (expectedCode is not null) Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public async Task Validator_rejects_missing_messages_system_only_bad_roles_and_bad_content()
    {
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid" }, "messages_required");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "system", Content = Json("rules") }] }, "conversation_message_required");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "tool", Content = Json("hello") }] }, "unsupported_role");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new { text = "object" }) }] }, "unsupported_content");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { "bad" }) }] }, "unsupported_content_part");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { new { text = "missing-type" } }) }] }, "unsupported_content_part");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { new { type = 123, text = "bad" } }) }] }, "unsupported_content_part");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { new { type = "image_url", text = "bad" } }) }] }, "unsupported_content_part");
        await AssertInvalid(new OpenAiChatCompletionRequest { Model = "valid", Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { new { type = "text" } }) }] }, "unsupported_content_part");
    }

    [Fact]
    public async Task Validator_accepts_supported_text_content_parts_and_assistant_messages()
    {
        var request = new OpenAiChatCompletionRequest
        {
            Model = "valid",
            Messages =
            [
                new OpenAiMessage { Role = "assistant", Content = Json(new object[] { new { type = "text", text = "hello" }, new { type = "input_text", text = "world" } }) }
            ]
        };

        var result = await Validator().ValidateAsync(request, CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validator_rejects_out_of_range_sampling_tokens_stop_metadata_and_streaming()
    {
        await AssertInvalid(Request("valid", temperature: 1.1f), "temperature_out_of_range");
        await AssertInvalid(Request("valid", topP: -0.1f), "top_p_out_of_range");
        await AssertInvalid(Request("valid", maxTokens: 0), "max_tokens_out_of_range");
        await AssertInvalid(Request("valid", maxTokens: 11), "max_tokens_exceeds_model_limit");
        await AssertInvalid(Request("valid", stop: Json(new[] { "1", "2", "3", "4", "5" })), "too_many_stop_sequences");
        await AssertInvalid(Request("valid", stop: Json(new[] { new { value = "bad" } })), "invalid_stop");
        await AssertInvalid(Request("valid", stop: Json(new { value = "bad" })), "invalid_stop");
        await AssertInvalid(Request("valid", stop: Json(new string('x', 201))), "stop_sequence_too_long");

        Assert.True((await Validator().ValidateAsync(Request("valid", stop: Json("END")), CancellationToken.None)).IsValid);
        Assert.True((await Validator().ValidateAsync(Request("valid", stop: Json((string?)null)), CancellationToken.None)).IsValid);
        Assert.True((await Validator().ValidateAsync(Request("missing-model", maxTokens: 11), CancellationToken.None)).IsValid);
        await AssertInvalid(Request("valid", metadata: new Dictionary<string, JsonElement> { ["large"] = Json(new string('x', 9000)) }), "metadata_too_large");
        await AssertInvalid(Request("valid", stream: true), "streaming_disabled");
    }

    [Fact]
    public async Task Validator_allows_stream_when_fallback_is_explicitly_enabled()
    {
        var validator = Validator(new GatewayOptions { Streaming = new StreamingOptions { FallbackToNonStreaming = true } });
        var result = await validator.ValidateAsync(Request("valid", stream: true), CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validator_handles_null_request()
    {
        var result = await Validator().ValidateAsync(null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("request_required", result.Code);
    }

    private static async Task AssertInvalid(OpenAiChatCompletionRequest request, string code)
    {
        var result = await Validator().ValidateAsync(request, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal(code, result.Code);
    }

    private static OpenAiChatRequestValidator Validator(GatewayOptions? options = null) => new(MsOptions.Create(options ?? new GatewayOptions()), new StaticRegistry());

    private static OpenAiChatCompletionRequest Request(string model, float? temperature = null, float? topP = null, int? maxTokens = null, JsonElement? stop = null, Dictionary<string, JsonElement>? metadata = null, bool stream = false) => new()
    {
        Model = model,
        Temperature = temperature,
        TopP = topP,
        MaxTokens = maxTokens,
        Stop = stop,
        Metadata = metadata,
        Stream = stream,
        Messages = [new OpenAiMessage { Role = "user", Content = Json("hello") }]
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class StaticRegistry : IModelRegistry
    {
        private static readonly GatewayModel Model = new() { Id = "valid", Alias = "valid", BedrockModelId = "bedrock", MaxOutputTokens = 10, OutputModalities = ["TEXT"], SupportsConverse = true };
        public Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([Model]);
        public Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken) => Task.FromResult(requestedModel == "valid" ? Model : null);
    }
}

using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class ToolCallingValidatorTests
{
    [Fact]
    public void Tools_are_accepted_for_a_tools_capable_model()
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather", new { type = "object", properties = new { city = new { type = "string" } } })];
        request.ToolChoice = Json("auto");

        var result = Validate(request, ToolsModel());

        Assert.True(result.IsValid);
        var tool = Assert.Single(result.AiRequest!.Tools!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("auto", result.AiRequest.ToolChoice!.Mode);
    }

    [Fact]
    public void Tools_are_rejected_for_a_non_tools_model()
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather")];

        var result = Validate(request, ToolsModel(supportsTools: false));

        Assert.False(result.IsValid);
        Assert.Equal("tools_not_supported", result.Code);
    }

    [Fact]
    public void Streaming_with_tools_is_rejected()
    {
        var request = UserRequest();
        request.Stream = true;
        request.Tools = [Tool("get_weather")];

        var result = Validate(request, ToolsModel());

        Assert.False(result.IsValid);
        Assert.Equal("tool_streaming_not_supported", result.Code);
    }

    [Theory]
    [InlineData("\"auto\"", true, "auto")]
    [InlineData("\"none\"", true, "none")]
    [InlineData("\"required\"", true, "required")]
    [InlineData("\"bogus\"", false, null)]
    public void Tool_choice_string_is_validated(string toolChoiceJson, bool valid, string? expectedMode)
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather")];
        request.ToolChoice = JsonDocument.Parse(toolChoiceJson).RootElement.Clone();

        var result = Validate(request, ToolsModel());

        Assert.Equal(valid, result.IsValid);
        if (valid) Assert.Equal(expectedMode, result.AiRequest!.ToolChoice!.Mode);
        else Assert.Equal("invalid_tool_choice", result.Code);
    }

    [Fact]
    public void Tool_choice_named_function_must_be_declared()
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather")];

        request.ToolChoice = Json(new { type = "function", function = new { name = "get_weather" } });
        Assert.True(Validate(request, ToolsModel()).IsValid);

        request.ToolChoice = Json(new { type = "function", function = new { name = "unknown_fn" } });
        var result = Validate(request, ToolsModel());
        Assert.False(result.IsValid);
        Assert.Equal("tool_choice_unknown_function", result.Code);
    }

    [Fact]
    public void Too_many_tools_is_rejected()
    {
        var request = UserRequest();
        request.Tools = [Tool("a"), Tool("b")];

        var result = Validate(request, ToolsModel(), new GatewayOptions { RequestValidation = new RequestValidationOptions { MaxTools = 1 } });

        Assert.False(result.IsValid);
        Assert.Equal("too_many_tools", result.Code);
    }

    [Fact]
    public void Tool_without_function_name_is_rejected()
    {
        var request = UserRequest();
        request.Tools = [new OpenAiTool { Function = new OpenAiFunctionDefinition { Name = "" } }];

        var result = Validate(request, ToolsModel());

        Assert.False(result.IsValid);
        Assert.Equal("invalid_tool", result.Code);
    }

    [Fact]
    public void Oversized_tool_schema_is_rejected()
    {
        var request = UserRequest();
        request.Tools = [Tool("big", new { type = "object", description = new string('x', 200) })];

        var result = Validate(request, ToolsModel(), new GatewayOptions { RequestValidation = new RequestValidationOptions { MaxToolSchemaBytes = 32 } });

        Assert.False(result.IsValid);
        Assert.Equal("tool_schema_too_large", result.Code);
    }

    [Fact]
    public void Multi_turn_tool_messages_map_to_neutral_request()
    {
        var request = new OpenAiChatCompletionRequest
        {
            Model = "m",
            Tools = [Tool("get_weather")],
            Messages =
            [
                new OpenAiMessage { Role = "user", Content = Json("weather in NYC?") },
                new OpenAiMessage
                {
                    Role = "assistant",
                    ToolCalls = [new OpenAiToolCall { Id = "call_1", Function = new OpenAiFunctionCall { Name = "get_weather", Arguments = "{\"city\":\"NYC\"}" } }]
                },
                new OpenAiMessage { Role = "tool", ToolCallId = "call_1", Content = Json("72F") }
            ]
        };

        var result = Validate(request, ToolsModel());

        Assert.True(result.IsValid);
        var messages = result.AiRequest!.Messages;
        Assert.Equal(3, messages.Count);
        var assistant = messages[1];
        Assert.Equal("call_1", Assert.Single(assistant.ToolCalls!).Id);
        Assert.Equal("get_weather", assistant.ToolCalls![0].Name);
        var tool = messages[2];
        Assert.Equal("tool", tool.Role);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("72F", tool.Content);
    }

    [Fact]
    public void Assistant_tool_call_missing_id_is_rejected()
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather")];
        request.Messages.Add(new OpenAiMessage
        {
            Role = "assistant",
            ToolCalls = [new OpenAiToolCall { Id = "", Function = new OpenAiFunctionCall { Name = "get_weather" } }]
        });

        var result = Validate(request, ToolsModel());

        Assert.False(result.IsValid);
        Assert.Equal("invalid_tool_call", result.Code);
    }

    [Fact]
    public void Tool_message_without_tool_call_id_is_rejected()
    {
        var request = UserRequest();
        request.Tools = [Tool("get_weather")];
        request.Messages.Add(new OpenAiMessage { Role = "tool", Content = Json("result") });

        var result = Validate(request, ToolsModel());

        Assert.False(result.IsValid);
        Assert.Equal("tool_call_id_required", result.Code);
    }

    [Fact]
    public void Non_tool_request_is_unaffected_and_text_only_default_preserved()
    {
        // No tools → ordinary text path, still valid.
        Assert.True(Validate(UserRequest(), ToolsModel()).IsValid);

        // Image content is still rejected even on a tools-capable model (only tools are relaxed).
        var imageRequest = new OpenAiChatCompletionRequest
        {
            Model = "m",
            Messages = [new OpenAiMessage { Role = "user", Content = Json(new object[] { new { type = "image_url", image_url = new { url = "x" } } }) }]
        };
        Assert.False(Validate(imageRequest, ToolsModel()).IsValid);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static OpenAiChatValidationResult Validate(OpenAiChatCompletionRequest request, GatewayModel model, GatewayOptions? options = null) =>
        new OpenAiChatRequestValidator(MsOptions.Create(options ?? new GatewayOptions())).Validate(new DefaultHttpContext(), request, model);

    private static GatewayModel ToolsModel(bool supportsTools = true) => new()
    {
        Id = "m",
        Alias = "m",
        SupportsTools = supportsTools,
        MaxOutputTokens = 1000,
        OutputModalities = ["TEXT"],
        SupportsConverse = true
    };

    private static OpenAiChatCompletionRequest UserRequest() => new()
    {
        Model = "m",
        Messages = [new OpenAiMessage { Role = "user", Content = Json("hello") }]
    };

    private static OpenAiTool Tool(string name, object? parameters = null) => new()
    {
        Type = "function",
        Function = new OpenAiFunctionDefinition
        {
            Name = name,
            Description = "desc",
            Parameters = parameters is null ? null : Json(parameters)
        }
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

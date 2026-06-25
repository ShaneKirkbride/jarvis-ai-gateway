using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class OpenAiResponseFactoryTests
{
    [Fact]
    public void FromResult_maps_tool_calls_and_sets_finish_reason()
    {
        var result = new AiChatResult(
            "",
            new TokenUsage(5, 3, 8),
            "tool_use",
            new ProviderInvocationMetadata("azure-openai", "azure-openai", 1),
            [new AiToolCall("call_1", "get_weather", "{\"city\":\"NYC\"}")]);

        var response = OpenAiResponseFactory.FromResult("m", result);

        var choice = Assert.Single(response.Choices);
        Assert.Equal("tool_calls", choice.FinishReason);
        var toolCall = Assert.Single(choice.Message.ToolCalls!);
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("get_weather", toolCall.Function.Name);
        Assert.Equal("{\"city\":\"NYC\"}", toolCall.Function.Arguments);
        Assert.Equal(8, response.Usage!.TotalTokens);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("length", "length")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("", "stop")]
    public void FromResult_without_tool_calls_normalizes_finish_reason(string providerReason, string expected)
    {
        var result = new AiChatResult("hi", new TokenUsage(1, 1, 2), providerReason, new ProviderInvocationMetadata("p", "p", 1));

        var response = OpenAiResponseFactory.FromResult("m", result);

        Assert.Equal(expected, response.Choices[0].FinishReason);
        Assert.Null(response.Choices[0].Message.ToolCalls);
        Assert.Equal("hi", response.Choices[0].Message.Content);
    }

    [Fact]
    public void FromResult_computes_total_tokens_when_zero()
    {
        var result = new AiChatResult("hi", new TokenUsage(4, 6, 0), "stop", new ProviderInvocationMetadata("p", "p", 1));
        Assert.Equal(10, OpenAiResponseFactory.FromResult("m", result).Usage!.TotalTokens);
    }

    [Fact]
    public void FromText_produces_basic_completion()
    {
        var response = OpenAiResponseFactory.FromText("m", "hello", 1, 2, 0, "max_tokens");

        Assert.Equal("hello", response.Choices[0].Message.Content);
        Assert.Equal("length", response.Choices[0].FinishReason);
        Assert.Equal(3, response.Usage!.TotalTokens);
    }
}

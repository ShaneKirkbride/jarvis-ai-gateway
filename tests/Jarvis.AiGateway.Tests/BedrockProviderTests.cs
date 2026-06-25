using System.Runtime.CompilerServices;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class BedrockProviderTests
{
    [Fact]
    public void Provider_name_is_aws_bedrock()
    {
        var provider = new BedrockProvider([], []);
        Assert.Equal("aws-bedrock", provider.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_invokes_the_first_capable_strategy()
    {
        var handling = new FakeInvocationStrategy("converse", canHandle: true, "from-converse");
        var notHandling = new FakeInvocationStrategy("invoke-model", canHandle: false, "from-invoke");
        var provider = new BedrockProvider([notHandling, handling], []);

        var result = await provider.CompleteAsync(Model(), Request(), Context(), CancellationToken.None);

        Assert.Equal("from-converse", result.Text);
        Assert.Equal("converse", result.ProviderMetadata.InvocationStrategy);
    }

    [Fact]
    public async Task CompleteAsync_throws_unsupported_when_no_strategy_handles()
    {
        var provider = new BedrockProvider([new FakeInvocationStrategy("x", canHandle: false, "n")], []);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.CompleteAsync(Model(), Request(), Context(), CancellationToken.None));
        Assert.Equal(BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage, ex.Message);
    }

    [Fact]
    public async Task StreamAsync_relays_events_from_the_capable_streaming_strategy()
    {
        var strategy = new FakeStreamingStrategy("converse-stream", canHandle: true,
            [new AiChatTextDeltaEvent("hi"), new AiChatCompletionEvent("end_turn", null)]);
        var provider = new BedrockProvider([], [strategy]);

        var events = new List<AiChatStreamEvent>();
        await foreach (var ev in provider.StreamAsync(Model(), Request(), Context(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(2, events.Count);
        Assert.IsType<AiChatTextDeltaEvent>(events[0]);
        Assert.IsType<AiChatCompletionEvent>(events[1]);
    }

    [Fact]
    public async Task StreamAsync_throws_when_no_streaming_strategy_handles()
    {
        var provider = new BedrockProvider([], [new FakeStreamingStrategy("s", canHandle: false, [])]);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Model(), Request(), Context(), CancellationToken.None)) { }
        });
    }

    [Fact]
    public void CanStream_reflects_streaming_strategy_capability()
    {
        Assert.True(new BedrockProvider([], [new FakeStreamingStrategy("s", true, [])]).CanStream(Model(), Request()));
        Assert.False(new BedrockProvider([], [new FakeStreamingStrategy("s", false, [])]).CanStream(Model(), Request()));
    }

    [Fact]
    public void StreamInvocationName_uses_strategy_name_or_falls_back_to_provider_name()
    {
        Assert.Equal("converse-stream",
            new BedrockProvider([], [new FakeStreamingStrategy("converse-stream", true, [])]).StreamInvocationName(Model(), Request()));
        Assert.Equal("aws-bedrock",
            new BedrockProvider([], [new FakeStreamingStrategy("s", false, [])]).StreamInvocationName(Model(), Request()));
    }

    private static GatewayModel Model() => new() { Alias = "general", ProviderName = "aws-bedrock", SupportsConverse = true, MaxOutputTokens = 100 };

    private static AiChatRequest Request() =>
        new("general", [new AiMessage("user", "hello")], new AiGenerationOptions(null, null, null, []), new Dictionary<string, string>(), false);

    private static RequestContext Context() => new("rid", "cid", "ws", "GENERAL", false);

    private sealed class FakeInvocationStrategy(string name, bool canHandle, string text) : IBedrockInvocationStrategy
    {
        public string Name => name;
        public bool CanHandle(GatewayModel model, AiChatRequest request) => canHandle;
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult(text, new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("aws-bedrock", name, 1)));
    }

    private sealed class FakeStreamingStrategy(string name, bool canHandle, IReadOnlyList<AiChatStreamEvent> events) : IBedrockStreamingStrategy
    {
        public string Name => name;
        public bool CanHandle(GatewayModel model, AiChatRequest request) => canHandle;

        public async IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
            GatewayModel model,
            AiChatRequest request,
            RequestContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var ev in events) yield return ev;
        }
    }
}

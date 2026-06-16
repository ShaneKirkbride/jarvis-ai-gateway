using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class GraphGroupResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task First_lookup_calls_executor_and_caches()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => Task.FromResult(new GraphQueryResult(
                true,
                new[] { new DirectoryGroupRef("00000000-0000-0000-0000-000000000001", "Engineering") },
                "oid-1",
                AssertionFailureReason.None,
                null))
        };
        var resolver = CreateResolver(executor, out _);

        var first = await resolver.ResolveAsync("user@example.test", CancellationToken.None);
        var second = await resolver.ResolveAsync("user@example.test", CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Single(first.Groups);
        Assert.False(first.WasCached);
        Assert.True(second.WasCached);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task User_not_found_is_negative_cached()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => Task.FromResult(new GraphQueryResult(
                false,
                Array.Empty<DirectoryGroupRef>(),
                null,
                AssertionFailureReason.GraphUserNotFound,
                "Request_ResourceNotFound"))
        };
        var resolver = CreateResolver(executor, out _);

        var first = await resolver.ResolveAsync("missing@example.test", CancellationToken.None);
        var second = await resolver.ResolveAsync("missing@example.test", CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.Equal(AssertionFailureReason.GraphUserNotFound, first.FailureReason);
        Assert.True(second.WasCached);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Transient_failure_is_not_cached()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => Task.FromResult(new GraphQueryResult(
                false,
                Array.Empty<DirectoryGroupRef>(),
                null,
                AssertionFailureReason.GraphLookupFailed,
                "transient"))
        };
        var resolver = CreateResolver(executor, out _);

        var first = await resolver.ResolveAsync("user@example.test", CancellationToken.None);
        var second = await resolver.ResolveAsync("user@example.test", CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task Executor_throw_returns_lookup_failed_and_is_not_cached()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => throw new HttpRequestException("network down")
        };
        var resolver = CreateResolver(executor, out _);

        var result = await resolver.ResolveAsync("user@example.test", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AssertionFailureReason.GraphLookupFailed, result.FailureReason);

        // No caching of the failure — a second call attempts again.
        await resolver.ResolveAsync("user@example.test", CancellationToken.None);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task Executor_timeout_returns_lookup_failed()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // will be cancelled by resolver's timeout
                return new GraphQueryResult(true, Array.Empty<DirectoryGroupRef>(), null, AssertionFailureReason.None, null);
            }
        };
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        options.IdentityBroker.Graph.TimeoutSeconds = 1;
        var resolver = CreateResolver(executor, out _, options);

        var result = await resolver.ResolveAsync("user@example.test", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AssertionFailureReason.GraphLookupFailed, result.FailureReason);
    }

    [Fact]
    public async Task Concurrent_lookups_for_same_subject_coalesce_into_one_executor_call()
    {
        var tcs = new TaskCompletionSource<GraphQueryResult>();
        var executor = new FakeGraphGroupQueryExecutor { Behavior = (_, _) => tcs.Task };
        var resolver = CreateResolver(executor, out _);

        var t1 = resolver.ResolveAsync("user@example.test", CancellationToken.None);
        var t2 = resolver.ResolveAsync("user@example.test", CancellationToken.None);
        var t3 = resolver.ResolveAsync("user@example.test", CancellationToken.None);

        // Race-tolerance: give the resolver a moment to assemble the in-flight entry.
        await Task.Delay(50);

        tcs.SetResult(new GraphQueryResult(
            true,
            new[] { new DirectoryGroupRef("00000000-0000-0000-0000-000000000001", "Engineering") },
            "oid",
            AssertionFailureReason.None,
            null));

        var r1 = await t1;
        var r2 = await t2;
        var r3 = await t3;

        Assert.Equal(1, executor.CallCount);
        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.True(r3.IsSuccess);
    }

    [Fact]
    public async Task Display_name_preserved_exactly_for_diagnostics()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => Task.FromResult(new GraphQueryResult(
                true,
                new[] { new DirectoryGroupRef("00000000-0000-0000-0000-000000000001", "ITAR-Approved (Engineering)") },
                "oid",
                AssertionFailureReason.None,
                null))
        };
        var resolver = CreateResolver(executor, out _);

        var result = await resolver.ResolveAsync("user@example.test", CancellationToken.None);
        var first = result.Groups.First();

        Assert.Equal("00000000-0000-0000-0000-000000000001", first.Id);
        Assert.Equal("ITAR-Approved (Engineering)", first.DisplayName);   // not normalized
    }

    [Fact]
    public async Task Empty_subject_throws_argument_exception()
    {
        var resolver = CreateResolver(new FakeGraphGroupQueryExecutor(), out _);
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync("   ", CancellationToken.None));
    }

    private static GraphGroupResolver CreateResolver(IGraphGroupQueryExecutor executor, out IMemoryCache cache, GatewayOptions? options = null)
    {
        cache = new MemoryCache(MsOptions.Create(new MemoryCacheOptions()));
        var resolved = options ?? IdentityBrokerTestHelpers.DefaultBrokerOptions();
        return new GraphGroupResolver(
            executor,
            cache,
            new NoOpMetrics(),
            new TestTimeProvider(Now),
            MsOptions.Create(resolved),
            NullLogger<GraphGroupResolver>.Instance);
    }

    private sealed class NoOpMetrics : IGatewayMetrics
    {
        public void RecordRequest(string modelAlias) { }
        public void RecordLatency(string modelAlias, TimeSpan elapsed) { }
        public void RecordPolicyDenial(string ruleId, string modelAlias) { }
        public void RecordBedrockInvocation(string strategy, TimeSpan elapsed, bool success) { }
        public void RecordBedrockError(string modelAlias) { }
        public void RecordServerError(string route) { }
        public void RecordTokenUsage(string modelAlias, int inputTokens, int outputTokens) { }
        public void RecordIdentityLookupCacheHit() { }
        public void RecordIdentityLookupGraphCall(TimeSpan elapsed, bool success) { }
        public void RecordIdentityLookupFailure(string reason) { }
        public void RecordIdentityPreAuthRateLimited(string partition) { }
    }
}

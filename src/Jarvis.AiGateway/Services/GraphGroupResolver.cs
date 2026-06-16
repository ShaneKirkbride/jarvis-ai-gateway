using System.Collections.Concurrent;
using System.Diagnostics;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Caches and coalesces Microsoft Graph group lookups for the identity broker.
/// <para>
/// The cache layer is deliberately separated from the SDK call itself (delegated to
/// <see cref="IGraphGroupQueryExecutor"/>) so the time-bounded, fail-closed, single-flight
/// behaviour can be exhaustively unit-tested without spinning up a real Graph client.
/// </para>
/// <para>
/// Failure policy:
/// <list type="bullet">
///   <item>Positive results are cached for <c>Graph:CacheSeconds</c>.</item>
///   <item>User-not-found results are negative-cached for <c>Graph:NegativeCacheSeconds</c>
///         to bound the cost of a typo'd email or a deprovisioned user.</item>
///   <item>Transient Graph failures are NEVER cached and NEVER served as a stale result.
///         The broker surfaces them as <see cref="AssertionFailureReason.GraphLookupFailed"/>
///         and the middleware translates that to HTTP 503.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GraphGroupResolver : IGraphGroupResolver, IDisposable
{
    private readonly IGraphGroupQueryExecutor _executor;
    private readonly IMemoryCache _cache;
    private readonly IGatewayMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GraphGroupResolver> _logger;
    private readonly GraphOptions _options;

    // One Lazy<Task<...>> per in-flight subject so a thundering herd for the same user
    // produces a single Graph call.  Entries are removed once the underlying task settles.
    private readonly ConcurrentDictionary<string, Lazy<Task<GraphLookupResult>>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public GraphGroupResolver(
        IGraphGroupQueryExecutor executor,
        IMemoryCache cache,
        IGatewayMetrics metrics,
        TimeProvider timeProvider,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<GraphGroupResolver> logger)
    {
        _executor = executor;
        _cache = cache;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = gatewayOptions.Value.IdentityBroker.Graph;
    }

    public async Task<GraphLookupResult> ResolveAsync(string canonicalSubject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSubject);
        var key = canonicalSubject.Trim().ToLowerInvariant();

        if (TryReadCache(key, out var cached))
        {
            _metrics.RecordIdentityLookupCacheHit();
            return cached with { WasCached = true };
        }

        // Single-flight: the first caller for this subject creates the Lazy; others await
        // the same task.  WaitAsync ensures a caller can cancel their own wait without
        // aborting the underlying work for everyone else.
        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<GraphLookupResult>>(() => CallGraphAndCacheAsync(k)));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Don't remove the in-flight entry — let the underlying task complete so other
            // waiters can still receive its result.
            throw;
        }
    }

    private bool TryReadCache(string key, out GraphLookupResult cached)
    {
        if (_cache.TryGetValue<GraphLookupResult>(PositiveKey(key), out var positive) && positive is not null)
        {
            cached = positive;
            return true;
        }

        if (_cache.TryGetValue<GraphLookupResult>(NegativeKey(key), out var negative) && negative is not null)
        {
            cached = negative;
            return true;
        }

        cached = null!;
        return false;
    }

    private async Task<GraphLookupResult> CallGraphAndCacheAsync(string canonicalSubject)
    {
        var stopwatch = Stopwatch.StartNew();
        GraphQueryResult queryResult;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            queryResult = await _executor.ExecuteAsync(canonicalSubject, timeoutCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            _metrics.RecordIdentityLookupGraphCall(stopwatch.Elapsed, success: false);
            _metrics.RecordIdentityLookupFailure(AssertionFailureReason.GraphLookupFailed.ToString());
            _logger.LogWarning("Graph lookup timed out for hashed subject after {Elapsed}ms.", stopwatch.ElapsedMilliseconds);
            _inFlight.TryRemove(canonicalSubject, out _);
            return GraphLookupResult.Failure(AssertionFailureReason.GraphLookupFailed, "timeout:" + ex.GetType().Name, wasCached: false);
        }
        catch (Exception ex)
        {
            // Any unexpected SDK exception fails closed.  We never cache the failure so a
            // transient outage clears itself the moment Graph recovers.
            stopwatch.Stop();
            _metrics.RecordIdentityLookupGraphCall(stopwatch.Elapsed, success: false);
            _metrics.RecordIdentityLookupFailure(AssertionFailureReason.GraphLookupFailed.ToString());
            _logger.LogWarning(ex, "Graph lookup threw {ExceptionType} after {Elapsed}ms.", ex.GetType().Name, stopwatch.ElapsedMilliseconds);
            _inFlight.TryRemove(canonicalSubject, out _);
            return GraphLookupResult.Failure(AssertionFailureReason.GraphLookupFailed, ex.GetType().Name, wasCached: false);
        }

        stopwatch.Stop();
        _metrics.RecordIdentityLookupGraphCall(stopwatch.Elapsed, success: queryResult.IsSuccess);

        try
        {
            return CacheAndProject(canonicalSubject, queryResult);
        }
        finally
        {
            _inFlight.TryRemove(canonicalSubject, out _);
        }
    }

    private GraphLookupResult CacheAndProject(string canonicalSubject, GraphQueryResult queryResult)
    {
        if (queryResult.IsSuccess)
        {
            var groupSet = new HashSet<DirectoryGroupRef>(queryResult.Groups, DirectoryGroupRefIdComparer.Instance);
            var result = GraphLookupResult.Success(groupSet, queryResult.EntraObjectId, wasCached: false);
            _cache.Set(PositiveKey(canonicalSubject), result, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = _timeProvider.GetUtcNow().AddSeconds(Math.Max(1, _options.CacheSeconds))
            });
            return result;
        }

        if (queryResult.FailureReason == AssertionFailureReason.GraphUserNotFound)
        {
            var negative = GraphLookupResult.Failure(AssertionFailureReason.GraphUserNotFound, queryResult.DiagnosticHint, wasCached: false);
            _cache.Set(NegativeKey(canonicalSubject), negative, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = _timeProvider.GetUtcNow().AddSeconds(Math.Max(1, _options.NegativeCacheSeconds))
            });
            _metrics.RecordIdentityLookupFailure(AssertionFailureReason.GraphUserNotFound.ToString());
            return negative;
        }

        // Any other failure (auth-denied, transient, etc.) is NOT cached — see class summary.
        _metrics.RecordIdentityLookupFailure(queryResult.FailureReason.ToString());
        return GraphLookupResult.Failure(queryResult.FailureReason, queryResult.DiagnosticHint, wasCached: false);
    }

    private static string PositiveKey(string canonicalSubject) => $"groups:{canonicalSubject}";
    private static string NegativeKey(string canonicalSubject) => $"groups-miss:{canonicalSubject}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inFlight.Clear();
    }

    /// <summary>
    /// Equality by group object ID only.  Display names are diagnostic and intentionally
    /// excluded from set membership semantics — the same group renamed in Entra remains
    /// the same set member here.
    /// </summary>
    private sealed class DirectoryGroupRefIdComparer : IEqualityComparer<DirectoryGroupRef>
    {
        public static readonly DirectoryGroupRefIdComparer Instance = new();

        public bool Equals(DirectoryGroupRef? x, DirectoryGroupRef? y) =>
            string.Equals(x?.Id, y?.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DirectoryGroupRef obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id);
    }
}

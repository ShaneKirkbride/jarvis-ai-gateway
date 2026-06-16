using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jarvis.AiGateway.Services;

public interface IGatewayMetrics
{
    void RecordRequest(string modelAlias);
    void RecordLatency(string modelAlias, TimeSpan elapsed);
    void RecordPolicyDenial(string ruleId, string modelAlias);
    void RecordBedrockInvocation(string strategy, TimeSpan elapsed, bool success);
    void RecordBedrockError(string modelAlias);
    void RecordServerError(string route);
    void RecordTokenUsage(string modelAlias, int inputTokens, int outputTokens);

    // Identity broker metrics — see docs/identity-broker-plan.md §5.
    void RecordIdentityLookupCacheHit();
    void RecordIdentityLookupGraphCall(TimeSpan elapsed, bool success);
    void RecordIdentityLookupFailure(string reason);
    void RecordIdentityPreAuthRateLimited(string partition);
}

public sealed class GatewayMetrics : IGatewayMetrics
{
    public const string MeterName = "Jarvis.AiGateway";
    public const string ActivitySourceName = "Jarvis.AiGateway";

    private static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Counter<long> _requestCounter = Meter.CreateCounter<long>("jarvis.gateway.requests");
    private readonly Histogram<double> _requestLatency = Meter.CreateHistogram<double>("jarvis.gateway.request.duration_ms");
    private readonly Counter<long> _policyDenials = Meter.CreateCounter<long>("jarvis.gateway.policy.denials");
    private readonly Histogram<double> _bedrockLatency = Meter.CreateHistogram<double>("jarvis.gateway.bedrock.duration_ms");
    private readonly Counter<long> _bedrockErrors = Meter.CreateCounter<long>("jarvis.gateway.bedrock.errors");
    private readonly Counter<long> _serverErrors = Meter.CreateCounter<long>("jarvis.gateway.server.errors");
    private readonly Counter<long> _inputTokens = Meter.CreateCounter<long>("jarvis.gateway.tokens.input");
    private readonly Counter<long> _outputTokens = Meter.CreateCounter<long>("jarvis.gateway.tokens.output");

    private readonly Counter<long> _identityCacheHits = Meter.CreateCounter<long>("jarvis.gateway.identity.cache_hits");
    private readonly Counter<long> _identityGraphCalls = Meter.CreateCounter<long>("jarvis.gateway.identity.graph_calls");
    private readonly Histogram<double> _identityLookupLatency = Meter.CreateHistogram<double>("jarvis.gateway.identity.lookup_duration_ms");
    private readonly Counter<long> _identityLookupFailures = Meter.CreateCounter<long>("jarvis.gateway.identity.lookup_failures");
    private readonly Counter<long> _identityPreAuthRateLimited = Meter.CreateCounter<long>("jarvis.gateway.identity.preauth_rate_limited");

    public void RecordRequest(string modelAlias) => _requestCounter.Add(1, Tag("model", modelAlias));

    public void RecordLatency(string modelAlias, TimeSpan elapsed) => _requestLatency.Record(elapsed.TotalMilliseconds, Tag("model", modelAlias));

    public void RecordPolicyDenial(string ruleId, string modelAlias) => _policyDenials.Add(1, Tag("rule_id", ruleId), Tag("model", modelAlias));

    public void RecordBedrockInvocation(string strategy, TimeSpan elapsed, bool success) =>
        _bedrockLatency.Record(elapsed.TotalMilliseconds, Tag("strategy", strategy), Tag("success", success.ToString()));

    public void RecordBedrockError(string modelAlias) => _bedrockErrors.Add(1, Tag("model", modelAlias));

    public void RecordServerError(string route) => _serverErrors.Add(1, Tag("route", route));

    public void RecordTokenUsage(string modelAlias, int inputTokens, int outputTokens)
    {
        _inputTokens.Add(inputTokens, Tag("model", modelAlias));
        _outputTokens.Add(outputTokens, Tag("model", modelAlias));
    }

    public void RecordIdentityLookupCacheHit() => _identityCacheHits.Add(1);

    public void RecordIdentityLookupGraphCall(TimeSpan elapsed, bool success)
    {
        _identityGraphCalls.Add(1, Tag("success", success.ToString()));
        _identityLookupLatency.Record(elapsed.TotalMilliseconds, Tag("success", success.ToString()));
    }

    public void RecordIdentityLookupFailure(string reason) => _identityLookupFailures.Add(1, Tag("reason", reason));

    public void RecordIdentityPreAuthRateLimited(string partition) => _identityPreAuthRateLimited.Add(1, Tag("partition", partition));

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}

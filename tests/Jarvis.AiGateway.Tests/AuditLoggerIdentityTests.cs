using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class AuditLoggerIdentityTests
{
    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("unknown", LogLevel.Information)]   // unknown level defaults to Information
    public void WriteIdentity_emits_event_at_mapped_log_level(string level, LogLevel expected)
    {
        var logger = new RecordingLogger();
        var auditLogger = new AuditLogger(logger);

        auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "identity.resolved",
            Level = level,
            CorrelationId = "corr-1",
            HashedSubject = "deadbeef",
            EmailDomain = "example.test",
            HashedOid = "feedface",
            GroupCount = 2,
            GroupIds = ["g-1", "g-2"],
            AssertionKind = "OwuiSessionJwt",
            IdentitySource = "ValidatorGraphFresh"
        });

        Assert.Single(logger.Entries);
        Assert.Equal(expected, logger.Entries[0].Level);
    }

    [Fact]
    public void WriteIdentity_attaches_structured_scope_with_audit_keys()
    {
        var logger = new RecordingLogger();
        var auditLogger = new AuditLogger(logger);

        auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "identity.token.missing",
            Level = "warn",
            CorrelationId = "corr-1",
            HashedSubject = "deadbeef"
        });

        var scope = logger.LastScope!;
        Assert.True(scope.ContainsKey("audit.event_type"));
        Assert.Equal("identity.token.missing", scope["audit.event_type"]);
        Assert.True(scope.ContainsKey("audit.identity.hashed_subject"));
        Assert.Equal("deadbeef", scope["audit.identity.hashed_subject"]);
    }

    private sealed class RecordingLogger : ILogger<AuditLogger>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public Dictionary<string, object?>? LastScope { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                LastScope = kvps.ToDictionary(k => k.Key, k => k.Value);
            }
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

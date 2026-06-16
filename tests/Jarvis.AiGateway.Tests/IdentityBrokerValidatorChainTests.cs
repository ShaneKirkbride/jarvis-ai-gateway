using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class IdentityBrokerValidatorChainTests
{
    [Fact]
    public async Task Selects_first_validator_whose_can_handle_returns_true()
    {
        var first = new StubValidator { CanHandleResult = false };
        var second = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", "user@example.test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var third = new StubValidator { CanHandleResult = true };

        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), "oid-123", wasCached: false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)first, second, third }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(1, second.ValidateCalls);
        Assert.Equal(0, third.ValidateCalls);   // first matching wins
    }

    [Fact]
    public async Task No_validator_handles_returns_validator_not_found()
    {
        var validators = new IIdentityAssertionValidator[]
        {
            new StubValidator { CanHandleResult = false },
            new StubValidator { CanHandleResult = false }
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), null, false));
        var broker = new IdentityBroker(validators, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("anything", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.ValidatorNotFound, result.FailureReason);
    }

    [Fact]
    public async Task Missing_assertion_returns_token_missing()
    {
        var validators = new[] { new StubValidator { CanHandleResult = true } };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), null, false));
        var broker = new IdentityBroker(validators, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(IdentityAssertionInput.Empty, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenMissing, result.FailureReason);
        Assert.Equal(0, validators[0].ValidateCalls);
    }

    [Fact]
    public async Task Validator_throws_is_treated_as_token_invalid_not_500()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateThrows = new InvalidOperationException("boom")
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), null, false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenInvalid, result.FailureReason);
    }

    [Fact]
    public async Task Can_handle_throws_is_skipped_silently()
    {
        var thrower = new StubValidator { CanHandleThrows = new InvalidOperationException("ouch") };
        var working = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", "user@example.test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), "oid", false));
        var broker = new IdentityBroker(new IIdentityAssertionValidator[] { thrower, working }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Upn_preferred_over_email_as_canonical_subject()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", "user@example.test", "user-upn@example.test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), "oid", false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("user-upn@example.test", result.CanonicalSubject);
        Assert.Equal("user-upn@example.test", graph.LastSubject);
    }

    [Fact]
    public async Task Graph_failure_is_propagated_without_partial_success()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", "user@example.test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var graph = new StubGraphResolver(GraphLookupResult.Failure(AssertionFailureReason.GraphLookupFailed, "timeout", false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.GraphLookupFailed, result.FailureReason);
        Assert.Equal("user@example.test", result.CanonicalSubject);
    }

    [Fact]
    public async Task Graph_cached_result_surfaces_cached_identity_source()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", "user@example.test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var groups = new HashSet<DirectoryGroupRef> { new("00000000-0000-0000-0000-000000000001", "Engineering") };
        var graph = new StubGraphResolver(GraphLookupResult.Success(groups, "oid-cached", wasCached: true));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(IdentitySource.ValidatorGraphCached, result.IdentitySource);
        Assert.Equal("oid-cached", result.EntraObjectId);
    }

    [Fact]
    public async Task Validator_returns_invalid_result_propagated_without_calling_graph()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Failure("Stub", AssertionFailureReason.TokenExpired, "exp-past")
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), null, false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenExpired, result.FailureReason);
        Assert.Equal(0, graph.ResolveCalls);
    }

    [Fact]
    public async Task Successful_validator_with_no_canonical_subject_returns_weak_identity()
    {
        var validator = new StubValidator
        {
            CanHandleResult = true,
            ValidateResult = ValidatedAssertion.Success("Stub", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var graph = new StubGraphResolver(GraphLookupResult.Success(new HashSet<DirectoryGroupRef>(), null, false));
        var broker = new IdentityBroker(new[] { (IIdentityAssertionValidator)validator }, graph, NullLogger<IdentityBroker>.Instance);

        var result = await broker.ResolveAsync(new IdentityAssertionInput("assertion", new Dictionary<string, string?>()), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenWeakIdentity, result.FailureReason);
    }

    private sealed class StubValidator : IIdentityAssertionValidator
    {
        public bool CanHandleResult { get; set; }
        public Exception? CanHandleThrows { get; set; }
        public ValidatedAssertion ValidateResult { get; set; } = ValidatedAssertion.Failure("Stub", AssertionFailureReason.TokenInvalid);
        public Exception? ValidateThrows { get; set; }
        public int ValidateCalls;

        public bool CanHandle(IdentityAssertionInput input)
        {
            if (CanHandleThrows is not null) throw CanHandleThrows;
            return CanHandleResult;
        }

        public Task<ValidatedAssertion> ValidateAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ValidateCalls);
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.FromResult(ValidateResult);
        }
    }

    private sealed class StubGraphResolver : IGraphGroupResolver
    {
        private readonly GraphLookupResult _result;
        public int ResolveCalls;
        public string? LastSubject;

        public StubGraphResolver(GraphLookupResult result) => _result = result;

        public Task<GraphLookupResult> ResolveAsync(string canonicalSubject, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCalls);
            LastSubject = canonicalSubject;
            return Task.FromResult(_result);
        }
    }
}

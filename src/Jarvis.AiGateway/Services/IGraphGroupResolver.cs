using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Resolves the authoritative Entra group membership for a canonical subject (UPN/email).
/// Implementations MUST never serve stale data on Graph failure — the broker fails closed.
/// </summary>
public interface IGraphGroupResolver
{
    Task<GraphLookupResult> ResolveAsync(string canonicalSubject, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of a Graph group lookup, including cache provenance for the audit log.
/// </summary>
public sealed record GraphLookupResult
{
    public bool IsSuccess { get; init; }
    public bool WasCached { get; init; }
    public IReadOnlySet<DirectoryGroupRef> Groups { get; init; } = new HashSet<DirectoryGroupRef>();
    public string? EntraObjectId { get; init; }
    public AssertionFailureReason FailureReason { get; init; } = AssertionFailureReason.None;
    public string? DiagnosticHint { get; init; }

    public static GraphLookupResult Success(IReadOnlySet<DirectoryGroupRef> groups, string? entraObjectId, bool wasCached) =>
        new() { IsSuccess = true, Groups = groups, EntraObjectId = entraObjectId, WasCached = wasCached };

    public static GraphLookupResult Failure(AssertionFailureReason reason, string? hint, bool wasCached) =>
        new() { IsSuccess = false, FailureReason = reason, DiagnosticHint = hint, WasCached = wasCached };
}

/// <summary>
/// Inner abstraction over the actual Microsoft Graph SDK call.  Kept on its own interface so
/// the cache / coalescing / fail-mode logic in <see cref="GraphGroupResolver"/> can be unit
/// tested with a fake executor, while the real <c>MicrosoftGraphGroupQueryExecutor</c>
/// remains coverage-excluded as an SDK adapter.
/// </summary>
public interface IGraphGroupQueryExecutor
{
    Task<GraphQueryResult> ExecuteAsync(string canonicalSubject, CancellationToken cancellationToken);
}

public sealed record GraphQueryResult(
    bool IsSuccess,
    IReadOnlyList<DirectoryGroupRef> Groups,
    string? EntraObjectId,
    AssertionFailureReason FailureReason,
    string? DiagnosticHint);

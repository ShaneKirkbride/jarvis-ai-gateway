using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Top-level facade composing the assertion validator chain (see
/// <see cref="IIdentityAssertionValidator"/>) and the Graph group resolver
/// (<see cref="IGraphGroupResolver"/>) into a single per-request identity-resolution call.
/// <para>
/// The broker NEVER trusts group claims from the inbound assertion; groups always come
/// from <see cref="IGraphGroupResolver"/>.  Authorization is keyed on Entra group object IDs.
/// </para>
/// </summary>
public interface IIdentityBroker
{
    Task<IdentityAssertionResult> ResolveAsync(IdentityAssertionInput input, CancellationToken cancellationToken);
}

/// <summary>
/// Per-request outcome of identity resolution.  Carries the canonical subject, the
/// Graph-resolved group object IDs, and the identity source (fresh vs cached) for audit.
/// On failure, the structured <see cref="FailureReason"/> drives the middleware's HTTP
/// mapping.
/// </summary>
public sealed record IdentityAssertionResult
{
    public bool IsValid { get; init; }
    public string? CanonicalSubject { get; init; }
    public string? Email { get; init; }
    public string? Upn { get; init; }

    /// <summary>Authoritative Entra object id, populated only by Graph (never from the inbound assertion).</summary>
    public string? EntraObjectId { get; init; }

    public IReadOnlySet<DirectoryGroupRef> Groups { get; init; } = new HashSet<DirectoryGroupRef>();

    public IdentitySource IdentitySource { get; init; } = IdentitySource.Unresolved;
    public string? AssertionKind { get; init; }
    public AssertionFailureReason FailureReason { get; init; } = AssertionFailureReason.None;
    public string? DiagnosticHint { get; init; }
}

/// <summary>
/// Where the identity-and-groups answer came from.  Audited so reviewers can tell whether
/// a decision was based on a fresh Graph call or a cached one (and whether identity was
/// established at all).
/// </summary>
public enum IdentitySource
{
    Unresolved = 0,
    ValidatorGraphFresh,
    ValidatorGraphCached
}

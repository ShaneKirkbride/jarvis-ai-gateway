namespace Jarvis.AiGateway.Models;

/// <summary>
/// Reference to an Entra (Azure AD) directory group resolved through Microsoft Graph.
/// <para>
/// Authorization compares on <see cref="Id"/> only.  <see cref="DisplayName"/> is preserved
/// strictly for human-readable diagnostics in audit events; it is mutable in Entra, not
/// guaranteed to be unique, and must never be a policy input.  This invariant is enforced
/// by <c>PolicyEngine</c> and by readiness validation that rejects ITAR-approved model
/// routes configured with display-name allowlists while the identity broker is enabled.
/// </para>
/// </summary>
public sealed record DirectoryGroupRef(string Id, string? DisplayName);

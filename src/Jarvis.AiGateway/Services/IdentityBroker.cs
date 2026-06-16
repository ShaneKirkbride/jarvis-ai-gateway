using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Default <see cref="IIdentityBroker"/> implementation.
/// <para>
/// Flow:
/// <list type="number">
///   <item>Dispatch the raw assertion through the validator chain
///         (<see cref="IIdentityAssertionValidator"/>).  If no validator can handle the
///         assertion, fail with <see cref="AssertionFailureReason.ValidatorNotFound"/>.</item>
///   <item>If the chosen validator returns invalid, propagate its failure reason.</item>
///   <item>Compute the canonical subject (UPN preferred, email fallback).</item>
///   <item>Resolve group membership through <see cref="IGraphGroupResolver"/>.  The broker
///         NEVER trusts the validator's payload for groups; the Graph result is the only
///         input the policy engine sees.</item>
///   <item>If Graph fails or the user is unknown, propagate the structured failure to the
///         middleware.  No partial successes — fail closed.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IdentityBroker : IIdentityBroker
{
    private readonly IReadOnlyList<IIdentityAssertionValidator> _validators;
    private readonly IGraphGroupResolver _graphResolver;
    private readonly ILogger<IdentityBroker> _logger;

    public IdentityBroker(
        IEnumerable<IIdentityAssertionValidator> validators,
        IGraphGroupResolver graphResolver,
        ILogger<IdentityBroker> logger)
    {
        _validators = validators.ToList();
        _graphResolver = graphResolver;
        _logger = logger;
    }

    public async Task<IdentityAssertionResult> ResolveAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
    {
        var hasAssertion = !string.IsNullOrWhiteSpace(input.RawAssertion);
        var hasTrustedHeaders = input.TrustedHeaders.Any(kv => !string.IsNullOrWhiteSpace(kv.Value));
        if (!hasAssertion && !hasTrustedHeaders)
        {
            return Failure(AssertionFailureReason.TokenMissing, null, null);
        }

        var validator = SelectValidator(input);
        if (validator is null)
        {
            // Provide a non-secret diagnostic hint so audit can reason about why dispatch
            // missed (e.g. a fragment count) without surfacing token contents.
            var hint = hasAssertion
                ? $"segments={input.RawAssertion!.Count(c => c == '.') + 1}"
                : $"trusted-headers={input.TrustedHeaders.Count(kv => !string.IsNullOrWhiteSpace(kv.Value))}";
            return Failure(AssertionFailureReason.ValidatorNotFound, null, hint);
        }

        ValidatedAssertion validated;
        try
        {
            validated = await validator.ValidateAsync(input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: validators are documented to never throw, but if one does we must
            // still fail closed rather than 500.
            _logger.LogWarning(ex, "Identity validator {Kind} threw {ExceptionType}.", validator.GetType().Name, ex.GetType().Name);
            return Failure(AssertionFailureReason.TokenInvalid, validator.GetType().Name, ex.GetType().Name);
        }

        if (!validated.IsValid)
        {
            return Failure(validated.FailureReason, validated.AssertionKind, validated.DiagnosticHint);
        }

        var canonicalSubject = SelectCanonicalSubject(validated);
        if (canonicalSubject is null)
        {
            return Failure(AssertionFailureReason.TokenWeakIdentity, validated.AssertionKind, "no-canonical-subject");
        }

        var graph = await _graphResolver.ResolveAsync(canonicalSubject, cancellationToken);
        if (!graph.IsSuccess)
        {
            return new IdentityAssertionResult
            {
                IsValid = false,
                AssertionKind = validated.AssertionKind,
                CanonicalSubject = canonicalSubject,
                Email = validated.Email,
                Upn = validated.Upn,
                FailureReason = graph.FailureReason,
                DiagnosticHint = graph.DiagnosticHint,
                IdentitySource = IdentitySource.Unresolved
            };
        }

        return new IdentityAssertionResult
        {
            IsValid = true,
            AssertionKind = validated.AssertionKind,
            CanonicalSubject = canonicalSubject,
            Email = validated.Email,
            Upn = validated.Upn,
            EntraObjectId = graph.EntraObjectId,
            Groups = graph.Groups,
            IdentitySource = graph.WasCached ? IdentitySource.ValidatorGraphCached : IdentitySource.ValidatorGraphFresh,
            FailureReason = AssertionFailureReason.None
        };
    }

    private IIdentityAssertionValidator? SelectValidator(IdentityAssertionInput input)
    {
        foreach (var validator in _validators)
        {
            try
            {
                if (validator.CanHandle(input))
                {
                    return validator;
                }
            }
            catch (Exception ex)
            {
                // CanHandle is meant to be cheap and exception-free; if a validator
                // misbehaves, log and continue to the next rather than aborting dispatch.
                _logger.LogDebug(ex, "Validator {Kind}.CanHandle threw.", validator.GetType().Name);
            }
        }
        return null;
    }

    /// <summary>
    /// Canonical subject ordering — UPN if present, email otherwise.  Both are already
    /// normalized (trimmed, lowercased) by the validator.
    /// </summary>
    private static string? SelectCanonicalSubject(ValidatedAssertion assertion)
    {
        if (!string.IsNullOrWhiteSpace(assertion.Upn)) return assertion.Upn;
        if (!string.IsNullOrWhiteSpace(assertion.Email)) return assertion.Email;
        return null;
    }

    private static IdentityAssertionResult Failure(AssertionFailureReason reason, string? assertionKind, string? hint) => new()
    {
        IsValid = false,
        AssertionKind = assertionKind,
        FailureReason = reason,
        DiagnosticHint = hint,
        IdentitySource = IdentitySource.Unresolved
    };
}

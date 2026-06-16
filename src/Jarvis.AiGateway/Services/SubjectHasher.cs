using System.Security.Cryptography;
using System.Text;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Salt-and-hash helper for audit-time identifiers.  Canonical subjects (email/upn) and
/// Entra <c>oid</c> values are hashed before being written to CloudWatch so log exfiltration
/// is bounded while still allowing in-SIEM correlation across events from the same
/// deployment (the salt is consistent for the process lifetime).
/// </summary>
public interface ISubjectHasher
{
    /// <summary>
    /// Returns a lowercase hex SHA-256 of <c>salt || value</c>.  Returns <c>null</c> when
    /// the input is null/empty or when no salt is configured — in the latter case readiness
    /// has already failed closed and the gateway is not serving traffic.
    /// </summary>
    string? Hash(string? value);
}

public sealed class SubjectHasher : ISubjectHasher
{
    private readonly string? _salt;

    public SubjectHasher(IOptions<GatewayOptions> gatewayOptions)
    {
        _salt = gatewayOptions.Value.IdentityBroker.AuditSubjectSalt;
    }

    public string? Hash(string? value)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(_salt))
        {
            return null;
        }

        // Salt is prepended (not HMAC-keyed) because audit hashes do not need authenticated
        // origin — they exist to obscure the cleartext while preserving correlation.  An
        // attacker cannot brute force a 254-char email space practically, and even if they
        // could, the salt invalidates rainbow tables against captured logs.
        var bytes = Encoding.UTF8.GetBytes(_salt + value.ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}

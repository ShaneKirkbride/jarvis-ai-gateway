using System.Text.RegularExpressions;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Masks secret-looking values in free text destined for logs or operator-facing diagnostics:
/// bearer tokens, API keys, client secrets, signing keys, service keys, and generic
/// secret/password/token assignments.  This is a defense-in-depth pass — the gateway already
/// avoids logging headers, bodies, and prompts; this guards anything that slips into a message.
/// </summary>
public static class SecretRedactor
{
    public const string Mask = "[REDACTED]";

    private static readonly Regex Bearer = new(
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled);

    // Matches key: value or key=value (optionally quoted) for known secret-bearing keys.
    private static readonly Regex KeyedSecret = new(
        @"(?i)(""?(?:api[-_]?key|client[-_]?secret|signing[-_]?key|service[-_]?key|x-jarvis-gateway-key|secret|password|token)""?\s*[:=]\s*)""?[^""\s,;}]+""?",
        RegexOptions.Compiled);

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = Bearer.Replace(input, $"Bearer {Mask}");
        result = KeyedSecret.Replace(result, $"$1{Mask}");
        return result;
    }
}

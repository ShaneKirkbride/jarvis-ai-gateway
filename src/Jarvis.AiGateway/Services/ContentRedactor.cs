using System.Text.RegularExpressions;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IContentRedactor
{
    RedactionResult Redact(string text);
}

public sealed class RegexContentRedactor(IOptions<GatewayOptions> options) : IContentRedactor
{
    private readonly GatewayOptions _options = options.Value;

    private static readonly (string Pattern, string Replacement)[] BuiltInPatterns =
    [
        (@"AKIA[0-9A-Z]{16}", "[REDACTED_AWS_ACCESS_KEY]"),
        (@"(?i)(aws_secret_access_key\s*[=:]\s*)[A-Za-z0-9/+=]{32,}", "$1[REDACTED_AWS_SECRET]"),
        (@"(?i)(api[_-]?key\s*[=:]\s*)[A-Za-z0-9_\-]{16,}", "$1[REDACTED_API_KEY]"),
        (@"(?i)(password\s*[=:]\s*)\S+", "$1[REDACTED_PASSWORD]"),
        (@"\b\d{3}-\d{2}-\d{4}\b", "[REDACTED_SSN]"),
        (@"\b(?:\d[ -]*?){13,16}\b", "[REDACTED_POSSIBLE_CARD]"),
        (@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", "[REDACTED_PRIVATE_KEY]")
    ];

    public RedactionResult Redact(string text)
    {
        if (!_options.Redaction.Enabled || string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text, 0);
        }

        var count = 0;
        var redacted = text;

        foreach (var (pattern, replacement) in BuiltInPatterns)
        {
            var regex = GatewayRegex.Create(pattern, _options, RegexOptions.Compiled);
            var matches = regex.Matches(redacted).Count;
            if (matches > 0)
            {
                redacted = regex.Replace(redacted, replacement);
                count += matches;
            }
        }

        return new RedactionResult(redacted, count);
    }
}

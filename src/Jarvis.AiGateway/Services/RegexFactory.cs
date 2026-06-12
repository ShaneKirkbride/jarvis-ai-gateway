using System.Text.RegularExpressions;
using Jarvis.AiGateway.Options;

namespace Jarvis.AiGateway.Services;

public static class GatewayRegex
{
    public static Regex Create(string pattern, GatewayOptions options, RegexOptions regexOptions = RegexOptions.None) =>
        new(pattern, regexOptions | RegexOptions.CultureInvariant, RegexTimeout(options));

    public static bool IsMatch(string input, string pattern, GatewayOptions options, RegexOptions regexOptions = RegexOptions.None)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        return Create(pattern, options, regexOptions).IsMatch(input);
    }

    public static TimeSpan RegexTimeout(GatewayOptions options) =>
        TimeSpan.FromMilliseconds(Math.Clamp(options.RequestLimits.RegexTimeoutMilliseconds, 100, 500));
}

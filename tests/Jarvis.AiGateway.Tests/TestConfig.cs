using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Test-config helpers.  WebApplicationFactory&lt;Program&gt; loads the app's appsettings*.json, which
/// now contains real provider model entries.  Tests that define their own Gateway:Models must drop
/// those JSON sources first, otherwise index-based configuration merge leaks fields (provider name,
/// deployment, ITAR flag) from appsettings models into the test models — which previously routed a
/// Bedrock test model to the real Azure endpoint.
/// </summary>
internal static class TestConfig
{
    public static void RemoveJsonSources(IConfigurationBuilder config)
    {
        foreach (var source in config.Sources.OfType<JsonConfigurationSource>().ToList())
        {
            config.Sources.Remove(source);
        }
    }
}

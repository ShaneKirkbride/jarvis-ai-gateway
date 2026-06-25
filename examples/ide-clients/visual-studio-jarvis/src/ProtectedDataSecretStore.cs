using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Jarvis.VisualStudio.Example;

public sealed class ProtectedDataSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Jarvis.VisualStudio.Example.DeveloperApiKey.v1");
    private readonly string _secretPath;

    public ProtectedDataSecretStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisGateway",
            "VisualStudio",
            "developer-api-key.bin"))
    {
    }

    public ProtectedDataSecretStore(string secretPath) => _secretPath = secretPath ?? throw new ArgumentNullException(nameof(secretPath));

    public async Task<string?> GetDeveloperApiKeyAsync()
    {
        if (!File.Exists(_secretPath))
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = await ReadAllBytesAsync(_secretPath).ConfigureAwait(false);
            byte[] unprotectedBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            string apiKey = Encoding.UTF8.GetString(unprotectedBytes);
            return apiKey.StartsWith("jrvs_", StringComparison.Ordinal) ? apiKey : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public async Task SaveDeveloperApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("jrvs_", StringComparison.Ordinal))
        {
            throw new GatewayClientException("Jarvis developer keys must start with jrvs_.");
        }

        string? directory = Path.GetDirectoryName(_secretPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);
        byte[] protectedBytes = ProtectedData.Protect(apiKeyBytes, Entropy, DataProtectionScope.CurrentUser);
        await WriteAllBytesAsync(_secretPath, protectedBytes).ConfigureAwait(false);
    }

    private static Task<byte[]> ReadAllBytesAsync(string path) => Task.Run(() => File.ReadAllBytes(path));

    private static Task WriteAllBytesAsync(string path, byte[] bytes) => Task.Run(() => File.WriteAllBytes(path, bytes));
}

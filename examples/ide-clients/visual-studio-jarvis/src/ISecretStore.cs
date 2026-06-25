using System.Threading.Tasks;

namespace Jarvis.VisualStudio.Example;

public interface ISecretStore
{
    Task<string?> GetDeveloperApiKeyAsync();
    Task SaveDeveloperApiKeyAsync(string apiKey);
}

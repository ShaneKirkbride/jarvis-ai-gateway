using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IModelRegistry
{
    Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken);
    Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken);

    // Embedding catalogue (Phase 2). Default implementations return empty so existing test stubs
    // of this interface compile unchanged; the production ModelRegistry overrides them.
    Task<IReadOnlyList<GatewayModel>> GetEmbeddingModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GatewayModel>>([]);

    Task<GatewayModel?> FindEmbeddingModelAsync(string requestedModel, CancellationToken cancellationToken) =>
        Task.FromResult<GatewayModel?>(null);

    // Completion/FIM catalogue (Phase 3). Default implementations return empty so existing stubs
    // compile unchanged; the production ModelRegistry overrides them.
    Task<IReadOnlyList<GatewayModel>> GetCompletionModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GatewayModel>>([]);

    Task<GatewayModel?> FindCompletionModelAsync(string requestedModel, CancellationToken cancellationToken) =>
        Task.FromResult<GatewayModel?>(null);
}

public sealed class ModelRegistry(
    IBedrockModelDiscoveryService discoveryService,
    IEnumerable<IInvokeModelPayloadAdapter> adapters,
    IOptions<GatewayOptions> options,
    ILogger<ModelRegistry> logger) : IModelRegistry
{
    private readonly GatewayOptions _options = options.Value;

    public async Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken)
    {
        var discovered = await discoveryService.DiscoverAsync(cancellationToken);
        var discoveredById = discovered.ToDictionary(m => m.ModelId, StringComparer.OrdinalIgnoreCase);
        var models = new List<GatewayModel>();

        foreach (var configured in _options.Models.Where(m => m.Enabled))
        {
            // Only aws-bedrock aliases are enriched from Bedrock discovery.  Non-Bedrock providers
            // (e.g. azure-openai) are configured directly and must not depend on Bedrock discovery
            // results — they resolve even when discovery is disabled or unreachable.
            DiscoveredBedrockModel? discoveredModel = null;
            if (IsBedrock(configured.ProviderName))
            {
                discoveredById.TryGetValue(configured.BedrockModelId, out discoveredModel);
            }

            var model = GatewayModel.FromConfigured(configured, discoveredModel);
            if (IsChatCandidate(model) && IsPolicyAllowed(model))
            {
                models.Add(model);
            }
        }

        if (_options.ModelDiscovery.ExposeRawBedrockModelIds)
        {
            foreach (var discoveredModel in discovered)
            {
                if (_options.Models.Any(m => m.BedrockModelId.Equals(discoveredModel.ModelId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var model = GatewayModel.FromDiscovered(discoveredModel, exposeRawId: true);
                if (IsChatCandidate(model) && IsPolicyAllowed(model))
                {
                    models.Add(model);
                }
            }
        }

        return models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedModel)) return null;
        var models = await GetChatModelsAsync(cancellationToken);
        var match = models.FirstOrDefault(m => m.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase) ||
                                               m.Alias.Equals(requestedModel, StringComparison.OrdinalIgnoreCase) ||
                                               (_options.ModelDiscovery.ExposeRawBedrockModelIds && m.BedrockModelId.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
        {
            logger.LogWarning("Requested model {RequestedModel} was not present in the allowed chat model registry.", requestedModel);
        }

        return match;
    }

    // Embedding models come ONLY from explicit configuration (SupportsEmbeddings=true) — never from
    // Bedrock chat discovery — and are still subject to the deny/allow policy patterns.
    public Task<IReadOnlyList<GatewayModel>> GetEmbeddingModelsAsync(CancellationToken cancellationToken)
    {
        var models = _options.Models
            .Where(m => m.Enabled && m.SupportsEmbeddings)
            .Select(m => GatewayModel.FromConfigured(m))
            .Where(IsPolicyAllowed)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<GatewayModel>>(models);
    }

    public async Task<GatewayModel?> FindEmbeddingModelAsync(string requestedModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedModel)) return null;
        var models = await GetEmbeddingModelsAsync(cancellationToken);
        var match = models.FirstOrDefault(m =>
            m.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase) ||
            m.Alias.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            logger.LogWarning("Requested model {RequestedModel} was not present in the allowed embedding model registry.", requestedModel);
        }

        return match;
    }

    // Completion/FIM models come ONLY from explicit configuration (SupportsFim=true), subject to
    // the deny/allow policy patterns.
    public Task<IReadOnlyList<GatewayModel>> GetCompletionModelsAsync(CancellationToken cancellationToken)
    {
        var models = _options.Models
            .Where(m => m.Enabled && m.SupportsFim)
            .Select(m => GatewayModel.FromConfigured(m))
            .Where(IsPolicyAllowed)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<GatewayModel>>(models);
    }

    public async Task<GatewayModel?> FindCompletionModelAsync(string requestedModel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedModel)) return null;
        var models = await GetCompletionModelsAsync(cancellationToken);
        var match = models.FirstOrDefault(m =>
            m.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase) ||
            m.Alias.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            logger.LogWarning("Requested model {RequestedModel} was not present in the allowed completion model registry.", requestedModel);
        }

        return match;
    }

    private static bool IsBedrock(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) || providerName.Equals("aws-bedrock", StringComparison.OrdinalIgnoreCase);

    private bool IsChatCandidate(GatewayModel model)
    {
        if (!model.Enabled || !model.HasTextOutput) return false;

        // SupportsConverse and InvokeModel adapters are Bedrock-specific concepts.  A non-Bedrock
        // configured alias (e.g. azure-openai) is chat-capable by virtue of being configured.
        if (model.IsConfiguredAlias && !IsBedrock(model.ProviderName)) return true;

        return model.SupportsConverse || adapters.Any(a => a.CanHandle(model));
    }

    private bool IsPolicyAllowed(GatewayModel model)
    {
        if (IsDenied(model)) return false;
        if (model.IsConfiguredAlias) return true;

        var policy = _options.Policy;
        if (model.ItarApproved && policy.AllowDiscoveredModelsForItar) return true;
        if (!model.ItarApproved && policy.AllowDiscoveredModelsForNonItar) return true;

        if (policy.AllowedModelIdPatterns.Any(p => IsMatch(model.BedrockModelId, p))) return true;
        return !policy.RequireExplicitModelAllowlist;
    }

    private bool IsDenied(GatewayModel model) => _options.Policy.DeniedModelIdPatterns.Any(p => IsMatch(model.BedrockModelId, p));

    private bool IsMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        return GatewayRegex.IsMatch(value, pattern, _options, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

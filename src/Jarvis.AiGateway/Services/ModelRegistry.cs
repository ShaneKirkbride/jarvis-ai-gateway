using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IModelRegistry
{
    Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken);
    Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken);
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
            discoveredById.TryGetValue(configured.BedrockModelId, out var discoveredModel);
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

    private bool IsChatCandidate(GatewayModel model)
    {
        if (!model.Enabled || !model.HasTextOutput) return false;
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

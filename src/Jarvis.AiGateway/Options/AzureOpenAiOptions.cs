namespace Jarvis.AiGateway.Options;

/// <summary>
/// Authentication strategy for outbound Azure OpenAI calls.  API key is the current default;
/// Managed Identity is supported for deployments that bind a workload/managed identity to the
/// Cognitive Services data-plane role and want to avoid distributing a static key.
/// </summary>
public enum AzureOpenAiAuthenticationMode
{
    ApiKey,
    ManagedIdentity
}

/// <summary>
/// Configuration for the Azure OpenAI provider.  Bound from the <c>Gateway:AzureOpenAi</c>
/// section so all provider configuration lives under the gateway root.
///
/// <para><b>Government cloud:</b> no special flag is required — point <see cref="Endpoint"/> at
/// the Azure Government host (for example <c>https://&lt;resource&gt;.openai.azure.us/</c>) and
/// the provider derives the correct AAD token scope from the host suffix for Managed Identity.
/// All outbound traffic targets this single configured host (no SSRF surface).</para>
/// </summary>
public sealed class AzureOpenAiOptions
{
    // Resource endpoint, e.g. https://azr-gov-jarvis2-openai.openai.azure.us/.  Trailing slash
    // is optional and normalized by the provider.
    public string Endpoint { get; set; } = "";

    // Data-plane API version. Azure routes by deployment + api-version query parameter.
    public string ApiVersion { get; set; } = "2025-04-01-preview";

    public AzureOpenAiAuthenticationMode AuthenticationMode { get; set; } = AzureOpenAiAuthenticationMode.ApiKey;

    // Required when AuthenticationMode = ApiKey. Sourced from a secret store — never committed.
    public string ApiKey { get; set; } = "";

    // Optional AAD scope override for Managed Identity. When empty the scope is derived from the
    // endpoint host (…openai.azure.us → https://cognitiveservices.azure.us/.default, otherwise
    // the commercial …azure.com scope).
    public string ManagedIdentityScope { get; set; } = "";
}

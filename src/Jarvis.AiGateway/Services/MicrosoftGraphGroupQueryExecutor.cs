using Azure.Core;
using Azure.Identity;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Real Microsoft Graph SDK adapter for <see cref="IGraphGroupQueryExecutor"/>.
/// <para>
/// Auth is client-credentials via Azure.Identity.  The gateway's Entra app registration
/// supplies <c>TenantId</c>, <c>ClientId</c>, and <c>ClientSecret</c> from Secrets Manager.
/// The required Graph permissions are validated end-to-end before being locked into
/// <c>docs/iam-matrix.md</c> (see plan §6).
/// </para>
/// <para>
/// This class is intentionally an SDK adapter only — it does NO caching, NO single-flight
/// coordination, and NO failure interpretation beyond mapping the OData error code into a
/// stable <see cref="AssertionFailureReason"/>.  All higher-level concerns live in
/// <see cref="GraphGroupResolver"/>.
/// </para>
/// </summary>
public sealed class MicrosoftGraphGroupQueryExecutor : IGraphGroupQueryExecutor
{
    // Single page cap.  Users in more than 200 groups will see truncation; if that becomes
    // common, switch to PageIterator.  Documented in docs/runbook.md for ops awareness.
    private const int PageSize = 200;

    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<MicrosoftGraphGroupQueryExecutor> _logger;
    private readonly Lazy<GraphServiceClient?> _client;

    public MicrosoftGraphGroupQueryExecutor(IOptions<GatewayOptions> gatewayOptions, ILogger<MicrosoftGraphGroupQueryExecutor> logger)
    {
        _gatewayOptions = gatewayOptions;
        _logger = logger;
        // Build the GraphServiceClient lazily so a host with missing/incomplete Graph
        // credentials still boots.  Credential presence is the responsibility of
        // GatewayOptionsValidator.ValidateOnStart and /readyz; this adapter fails closed
        // with GraphLookupFailed if it is invoked without configured secrets rather than
        // throwing inside the DI container.
        _client = new Lazy<GraphServiceClient?>(BuildClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private GraphServiceClient? BuildClient()
    {
        var options = _gatewayOptions.Value.IdentityBroker.Graph;
        if (string.IsNullOrWhiteSpace(options.TenantId) ||
            string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            _logger.LogWarning("Microsoft Graph credentials are not configured; ExecuteAsync will fail closed with GraphLookupFailed until they are supplied.");
            return null;
        }

        TokenCredential credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
        return new GraphServiceClient(credential, scopes: ["https://graph.microsoft.com/.default"]);
    }

    public async Task<GraphQueryResult> ExecuteAsync(string canonicalSubject, CancellationToken cancellationToken)
    {
        var client = _client.Value;
        if (client is null)
        {
            return new GraphQueryResult(false, [], null, AssertionFailureReason.GraphLookupFailed, "graph-not-configured");
        }

        try
        {
            // Fetch the user object so we can surface the authoritative Entra oid back to the
            // broker.  oid is logged (hashed) for audit-time correlation but is never trusted
            // from the inbound assertion.
            var user = await client.Users[canonicalSubject].GetAsync(req =>
            {
                req.QueryParameters.Select = ["id"];
            }, cancellationToken);

            if (user?.Id is null)
            {
                return new GraphQueryResult(false, [], null, AssertionFailureReason.GraphUserNotFound, "user-id-null");
            }

            var memberOf = await client.Users[canonicalSubject].TransitiveMemberOf.GetAsync(req =>
            {
                req.QueryParameters.Top = PageSize;
                req.QueryParameters.Select = ["id", "displayName"];
            }, cancellationToken);

            var groups = ProjectToGroups(memberOf);
            return new GraphQueryResult(true, groups, user.Id, AssertionFailureReason.None, null);
        }
        catch (ODataError ex) when (IsUserNotFound(ex))
        {
            return new GraphQueryResult(false, [], null, AssertionFailureReason.GraphUserNotFound, ex.Error?.Code);
        }
        catch (ODataError ex)
        {
            _logger.LogWarning("Graph OData error {Code} on user lookup: {Message}.", ex.Error?.Code, ex.Error?.Message);
            return new GraphQueryResult(false, [], null, AssertionFailureReason.GraphLookupFailed, ex.Error?.Code);
        }
    }

    private static IReadOnlyList<DirectoryGroupRef> ProjectToGroups(DirectoryObjectCollectionResponse? response)
    {
        if (response?.Value is null) return [];

        var groups = new List<DirectoryGroupRef>(response.Value.Count);
        foreach (var member in response.Value)
        {
            // transitiveMemberOf may include non-Group directory objects (e.g. directoryRoles).
            // Filter aggressively — only Group entries with a non-empty Id are authoritative.
            if (member is Group group && !string.IsNullOrWhiteSpace(group.Id))
            {
                groups.Add(new DirectoryGroupRef(group.Id, group.DisplayName));
            }
        }
        return groups;
    }

    private static bool IsUserNotFound(ODataError error) =>
        string.Equals(error.Error?.Code, "Request_ResourceNotFound", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(error.Error?.Code, "ResourceNotFound", StringComparison.OrdinalIgnoreCase);
}

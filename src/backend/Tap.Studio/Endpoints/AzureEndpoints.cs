using Tap.Studio.AzureDiscovery;
using Tap.Studio.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/azure/*</c> — Azure discovery for the Key Vault picker dialog. Read-only ARM
/// listings authenticated via the Azure CLI credential; a missing/expired <c>az login</c>
/// surfaces as a 400 whose message the dialog shows verbatim.
/// </summary>
public static class AzureEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/azure/subscriptions", async (AzureDiscoveryService azure, CancellationToken ct) =>
        {
            try
            {
                var subs = await azure.ListSubscriptionsAsync(ct).ConfigureAwait(false);
                return Results.Ok(subs
                    .Select(s => new AzureSubscriptionDto(s.SubscriptionId, s.DisplayName, s.TenantId, s.State))
                    .ToArray());
            }
            catch (AzureDiscoveryService.AzureDiscoveryException ex)
            {
                return Results.BadRequest(new { code = "azure-discovery-failed", message = ex.Message });
            }
        });

        app.MapGet("/api/azure/subscriptions/{subscriptionId}/keyvaults", async (
            string subscriptionId,
            AzureDiscoveryService azure,
            CancellationToken ct) =>
        {
            try
            {
                var vaults = await azure.ListKeyVaultsAsync(subscriptionId, ct).ConfigureAwait(false);
                return Results.Ok(vaults
                    .Select(v => new AzureKeyVaultDto(v.Name, v.ResourceGroup, v.Location))
                    .ToArray());
            }
            catch (AzureDiscoveryService.AzureDiscoveryException ex)
            {
                return Results.BadRequest(new { code = "azure-discovery-failed", message = ex.Message });
            }
        });
    }
}

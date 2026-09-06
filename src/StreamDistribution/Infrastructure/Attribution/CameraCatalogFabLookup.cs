using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartSentinelEye.StreamDistribution.Infrastructure.Attribution;

/// <summary>
/// Reads the camera catalogue over HTTP to learn each camera's fab
/// (ADR-0116). The one cross-context call StreamDistribution makes, and only
/// at startup for streams that have no fab yet.
///
/// <para>
/// The listing is fab-scoped like every other read, so the service account
/// this presents belongs to every fab group — a stream's fab is precisely
/// what is unknown, so the query cannot be narrowed in advance. That is why
/// the client holds <c>sse.cameras.read</c> and nothing else.
/// </para>
///
/// <para>
/// <c>includeRetired=true</c> is load-bearing, not tidiness — do not remove
/// it. A decommissioned camera still <em>had</em> a fab: the stream's history
/// belongs to the plant the hardware stood in, and the catalogue keeps the row
/// precisely because retirement records that the hardware <em>was</em> there.
/// Without the parameter the listing omits those rows, and a stream whose
/// camera was later decommissioned can never be attributed — silently, because
/// the pass only logs a count (spec 083).
/// </para>
///
/// <para>
/// It widens no authorization. <c>GET /cameras/{camera}</c> already returns a
/// retired camera, with its status, under this same <c>sse.cameras.read</c>
/// scope, so this principal could already read every retired row one at a
/// time. The listing's exclusion is a usefulness default, not a trust
/// boundary.
/// </para>
/// </summary>
public sealed class CameraCatalogFabLookup(
    HttpClient httpClient,
    IOptions<StreamFabAttributionOptions> options) : ICameraFabLookup
{
    public async Task<IReadOnlyDictionary<Guid, string>> FabsByCameraAsync(CancellationToken cancellationToken)
    {
        int pageSize = options.Value.PageSize;
        Dictionary<Guid, string> fabs = [];

        int offset = 0;
        int fetched;
        do
        {
            fetched = await ReadPageAsync(fabs, offset, pageSize, cancellationToken);
            offset += pageSize;
        }
        while (fetched == pageSize);

        return fabs;
    }

    /// <summary>Adds one page to <paramref name="fabs"/>; returns its row count.</summary>
    private async Task<int> ReadPageAsync(
        Dictionary<Guid, string> fabs, int offset, int pageSize, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            $"/cameras?offset={offset}&limit={pageSize}&includeRetired=true", cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonElement page = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        JsonElement items = page.GetProperty("items");

        foreach (JsonElement row in items.EnumerateArray())
        {
            string? fab = row.GetProperty("fab").GetString();
            if (!string.IsNullOrWhiteSpace(fab))
            {
                fabs[row.GetProperty("cameraIdentifier").GetGuid()] = fab;
            }
        }

        return items.GetArrayLength();
    }
}

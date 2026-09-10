using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.SystemVariables.Application.Resolution;

namespace SmartSentinelEye.SystemVariables.Infrastructure.Resolution;

/// <summary>
/// Seeds the in-memory <see cref="IReverseIndex"/> on startup by
/// calling <c>GET /overlays?state=Published</c> on the
/// overlay-designer service (spec 005 plan.md). Best-effort — if
/// overlay-designer is down the index starts empty and self-heals as
/// new <c>OverlayRevisionPublishedV1</c> events arrive via Wolverine.
///
/// <para>
/// The HTTP call uses Aspire's <c>http://overlay-designer</c> service
/// discovery URI and carries the <c>system-variables-seeder</c>
/// service account, attached by
/// <see cref="OverlayDesignerAuthorizationHandler"/> on the named
/// client. Spec 005 T061 specified that account; it did not ship, and
/// once <c>GET /overlays</c> gained <c>sse.overlays.read</c> every cold
/// start was refused (#2158, spec 126).
/// </para>
///
/// <para>
/// <b>A refusal is not a self-healing condition and is no longer
/// reported as one.</b> Self-healing needs an event, and an overlay
/// published before this process started raises none — so a 401 or 403
/// leaves the index empty until someone fixes the credential.
/// <see cref="Log.SeedRefused"/> says that at <c>Error</c>. It still
/// does not stop the host: refusing to start would trade a degraded
/// index for no SystemVariables at all, which is the trade ADR-0116
/// declined for StreamDistribution's startup attribution.
/// </para>
/// </summary>
public sealed class ReverseIndexSeederHostedService(
    IHttpClientFactory httpClientFactory,
    IReverseIndex reverseIndex,
    ILogger<ReverseIndexSeederHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The factory, not a typed client: this is a hosted service and so a
            // singleton, and a typed client held by one never gets its handler
            // rotated. The base address comes from the registration.
            using HttpClient client = httpClientFactory.CreateClient("overlay-designer");

            using HttpResponseMessage response = await client
                .GetAsync("/overlays?state=Published", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    logger.SeedRefused(response.StatusCode);
                }
                else
                {
                    logger.SeedNonSuccessStatus(response.StatusCode);
                }

                return;
            }

            JsonElement payload = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!payload.TryGetProperty("published", out JsonElement published))
            {
                logger.SeedMissingPublishedKey();
                return;
            }

            int seeded = 0;
            foreach (JsonElement overlay in published.EnumerateArray())
            {
                if (!overlay.TryGetProperty("overlayIdentifier", out JsonElement idElement))
                {
                    continue;
                }

                if (!overlay.TryGetProperty("text", out JsonElement textElement))
                {
                    continue;
                }

                Guid id = idElement.GetGuid();
                string text = textElement.GetString() ?? string.Empty;
                reverseIndex.UpsertOverlayReferences(id, text);
                seeded++;
            }

            logger.SeededOverlays(seeded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.SeedFailed(ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using System.Net;
using System.Net.Http.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 006 T062 (#596). Nothing asserted that the ingest surfaces refuse a
/// caller who presents no credentials at all.
///
/// <para>
/// The gap is narrow and old. Specs 013, 018 and 021 hardened this context
/// repeatedly — who may read which plant's events, who may file one against
/// which plant — and every one of those tests authenticates first. They answer
/// "which fab may this operator touch", never "must there be an operator at
/// all". A regression that dropped the authentication requirement would leave
/// all of them green.
/// </para>
///
/// <para>
/// This list used to name a third item, "whether a revoked webhook integration
/// still works". <b>Those specs did not cover it.</b> Nothing anywhere revoked
/// an integration and then attempted a delivery through it until spec 102
/// (#603), whose <c>WebhookRevocationRefusesDeliveryIntegrationTests</c> is both
/// the disproof of the claim and the coverage it asserted. Corrected here rather
/// than filed, because a reader meeting the claim would conclude the area was
/// already covered — this repo's named failure mode.
/// </para>
///
/// <para>
/// The write is the one that matters. An event filed into a plant's stream
/// drives that plant's automation rules and changes what its operators see —
/// the only path in the product by which one caller alters another plant's
/// state (spec 018). The read leaks; the write manipulates.
/// </para>
///
/// <para>
/// <b>401 rather than 403.</b> The distinction is the point of the test: no
/// credential must fail before authorisation is considered, or a
/// misconfiguration that silently treats anonymous callers as some default
/// principal would surface as "forbidden" and read like working scoping.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class AnonymousIngestIsRefusedTests(AspireFixture aspire)
{
    /// <summary>
    /// The manipulation path. `aspire.EventIngestion` carries no token — the
    /// per-test clients elsewhere in this namespace are built by
    /// <c>CreateAdminClientAsync</c>, which attaches one.
    /// </summary>
    [Fact]
    public async Task Anonymous_manual_ingest_is_refused()
    {
        HttpResponseMessage response = await aspire.EventIngestion.PostAsJsonAsync(
            "/events/manual?fabId=munich",
            new
            {
                source = "operator",
                deviceId = "station-4",
                kind = "OperatorAnnotation",
                occurredAt = DateTimeOffset.UtcNow,
                payload = new { note = "filed by nobody" },
            });

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "an unauthenticated caller was able to reach manual ingest, which is the "
            + "one path by which a caller alters a plant's state");
    }

    /// <summary>The disclosure path, for the same reason at lower stakes.</summary>
    [Fact]
    public async Task Anonymous_event_read_is_refused()
    {
        HttpResponseMessage response = await aspire.EventIngestion.GetAsync("/events?fabId=munich");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Dead letters carry the rejected payload verbatim — one plant's
    /// unvalidated production data — which is why spec 018 scoped them by fab.
    /// Scoping is worth nothing if the list answers an anonymous caller.
    /// </summary>
    [Fact]
    public async Task Anonymous_dead_letter_read_is_refused()
    {
        HttpResponseMessage response = await aspire.EventIngestion.GetAsync("/events/dead-letters");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

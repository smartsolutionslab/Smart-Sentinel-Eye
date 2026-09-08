using System.Net.Http.Headers;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 102 (#603) — revocation as a containment action rather than a registry
/// annotation. The domain flips correctly and the endpoint consults the flag on
/// one line (<c>EventsEndpoints.Writes.AuthenticateWebhookAsync</c>), but until
/// now nothing anywhere revoked an integration and then attempted a delivery
/// through it: the two files that POST to <c>/events/webhook/</c> never revoke,
/// and the two that revoke assert registry state only. A regression making the
/// revocation clause inert would leave all of them green.
///
/// <para>
/// The pre-revoke delivery asserting 201 is the control, not a warm-up. Without
/// it the closing 401 could equally mean a mis-captured token, the wrong plant
/// or the wrong name; with it, the revocation is the only variable. For the same
/// reason both deliveries name the integration's own plant — a cross-fab
/// delivery is refused a step later by the fab comparison and would prove
/// nothing about revocation.
/// </para>
///
/// <para>
/// No <c>[Trait("Category", …)]</c>: the CI filter is a deny-list over
/// Measurement/Disruptive/Maintenance, and three files in this folder carry
/// Disruptive. An untraited class is the one CI actually runs.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class WebhookRevocationRefusesDeliveryIntegrationTests(AspireFixture aspire)
{
    private const string Fab = "munich";

    private static readonly JsonElement Payload =
        JsonDocument.Parse("""{"severity":"high"}""").RootElement;

    [Fact]
    public async Task A_credential_that_worked_stops_working_once_it_is_revoked()
    {
        using HttpClient admin = await aspire.CreateAdminClientAsync("event-ingestion");
        string name = UniqueName("revoked");
        string token = await RegisterAndCaptureTokenAsync(admin, name);

        HttpResponseMessage accepted = await PostWebhookAsync(name, Fab, token);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(accepted));

        await RevokeAsync(admin, name);

        HttpResponseMessage refused = await PostWebhookAsync(name, Fab, token);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DiagnoseAsync(refused));
    }

    /// <summary>
    /// Pins today's collapse: the endpoint answers a revoked integration exactly
    /// as it answers one that was never registered, so the refusal is no oracle
    /// for which integrations exist. Whether it *should* distinguish them is an
    /// open question filed separately — asserting the collapse makes any future
    /// change to it deliberate rather than silent.
    /// </summary>
    [Fact]
    public async Task A_revoked_integrations_refusal_is_the_one_an_unknown_integration_gets()
    {
        using HttpClient admin = await aspire.CreateAdminClientAsync("event-ingestion");
        string name = UniqueName("silent");
        string token = await RegisterAndCaptureTokenAsync(admin, name);
        await RevokeAsync(admin, name);

        HttpResponseMessage revoked = await PostWebhookAsync(name, Fab, token);
        HttpResponseMessage neverRegistered = await PostWebhookAsync(UniqueName("gone"), Fab, token);

        revoked.StatusCode.ShouldBe(neverRegistered.StatusCode, await DiagnoseAsync(revoked));
        (await revoked.Content.ReadAsStringAsync())
            .ShouldBe(await neverRegistered.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The plaintext bearer exists exactly once, in the registration response —
    /// the row keeps only a hash — so it is captured here and held in a local.
    /// </summary>
    private static async Task<string> RegisterAndCaptureTokenAsync(HttpClient admin, string name)
    {
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            "/webhook-integrations", new { name, defaultKind = "WebhookAlarm" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// Revoke carrying the version the listing just reported (ADR-0113). The
    /// 200 is asserted because a 428 or 409 here would otherwise surface as an
    /// unexplained pass — a delivery still accepted after a revoke that never
    /// landed.
    /// </summary>
    private async Task RevokeAsync(HttpClient admin, string name)
    {
        int version = (await FindAsync(admin, name)).GetProperty("version").GetInt32();

        HttpResponseMessage revoked = await admin.SendAsync(Conditional(name, version));

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await DiagnoseAsync(revoked));
    }

    private static async Task<JsonElement> FindAsync(HttpClient admin, string name)
    {
        HttpResponseMessage listed = await admin.GetAsync("/webhook-integrations");
        listed.EnsureSuccessStatusCode();

        JsonElement rows = await listed.Content.ReadFromJsonAsync<JsonElement>();

        return rows.EnumerateArray().Single(row =>
            string.Equals(row.GetProperty("name").GetString(), name, StringComparison.Ordinal));
    }

    private static HttpRequestMessage Conditional(string name, int version)
    {
        HttpRequestMessage request = new(HttpMethod.Delete, $"/webhook-integrations/{name}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");

        return request;
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string name, string fabId, string bearer)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post, $"/events/webhook/{name}?fabId={fabId}")
        {
            Content = JsonContent.Create(new { payload = Payload }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await aspire.EventIngestion.SendAsync(request);
    }

    private static string UniqueName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant()[..Math.Min(63, prefix.Length + 33)];

    /// <summary>
    /// CI has no other route to the service's stack trace, so an unexpected
    /// status carries the body and the service's recent output with it.
    /// </summary>
    private async Task<string> DiagnoseAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        return $"body: {body}{Environment.NewLine}event-ingestion log:{Environment.NewLine}{aspire.RecentLogs("event-ingestion")}";
    }
}

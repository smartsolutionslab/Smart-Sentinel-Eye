using System.Net.Http.Headers;
using System.Text.Json;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.EventIngestion.Domain.WebhookIntegration;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.Shared.Kernel;

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
/// reason every delivery names the integration's own plant — a cross-fab
/// delivery is refused a step later by the fab comparison and would prove
/// nothing about revocation.
/// </para>
///
/// <para>
/// <b>Both validation modes, because the clause is shared and the branch is
/// not.</b> The revocation check sits above the <c>StaticHash</c>/<c>Jwt</c>
/// branch, so relocating it into the <c>StaticHash</c> arm compiles, keeps the
/// hash-mode facts green, and leaves every rotated integration accepting a
/// revoked credential. The JWT fact exists for exactly that mutation.
/// </para>
///
/// <para>
/// <b>This is not a scope test.</b> <c>CreateAdminClientAsync</c> mints the
/// legacy <c>sse.management</c> bundle (<c>AspireFixture.Auth.cs:112</c> —
/// <c>FetchAccessTokenAsync(…, ClientId, "openid sse.management")</c>), not the
/// <c>sse.webhooks.write</c> the registry endpoint declares. Swapping that
/// declaration for any other catalogued scope leaves all three facts green.
/// The admin identity is therefore broader than a real
/// <c>sse.webhooks.write</c> operator — accepted deliberately, because it is
/// the house fixture every neighbour in this folder uses and revocation, not
/// scope, is the variable under test. The fab half does bind: admin holds
/// <c>/fabs/munich</c> only, which is what makes the pre-revoke 201 a control.
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

    // management-web is a public client with the password grant enabled; a
    // token minted through it for 'admin' carries azp=management-web,
    // groups=[/fabs/munich] and sse.events.write — exactly what
    // ValidateJwtAsync checks. See the JWT fact for why the real rotate path
    // is not used to mint it.
    private const string JwtClientId = "management-web";
    private const string JwtScope = "openid sse.events.write";

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
    /// The same refusal for a <b>rotated</b> integration, whose bearer is a
    /// Keycloak JWT rather than a hashed token. The revocation clause is one
    /// line above the mode branch, so moving it into the <c>StaticHash</c> arm
    /// would compile and leave the hash-mode facts green while every rotated
    /// integration ignored revocation entirely. This fact is what catches that.
    ///
    /// <para>
    /// The JWT is minted from <c>management-web</c> rather than through the
    /// real rotate path, which today issues a credential carrying no
    /// <c>groups</c> claim — <c>ValidateJwtAsync</c>'s final check refuses it,
    /// so the pre-revoke 201 that makes the closing 401 attributable would be
    /// unreachable (#2206 — not this slice's to fix). JWT mode has no
    /// registration endpoint either, so the row is seeded straight into the
    /// database, the idiom
    /// <c>WebhookBearerValidationIntegrationTests.SeedJwtIntegrationAsync</c>
    /// established.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rotated_integrations_token_stops_working_once_it_is_revoked()
    {
        using HttpClient admin = await aspire.CreateAdminClientAsync("event-ingestion");
        string name = UniqueName("jwt-revoked");
        await SeedJwtIntegrationAsync(name, JwtClientId);
        string jwt = await aspire.GetAccessTokenForClientAsync(
            JwtClientId, AspireFixture.AdminUsername, AspireFixture.AdminPassword, JwtScope);

        HttpResponseMessage accepted = await PostWebhookAsync(name, Fab, jwt);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(accepted));

        await RevokeAsync(admin, name);

        HttpResponseMessage refused = await PostWebhookAsync(name, Fab, jwt);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DiagnoseAsync(refused));
    }

    /// <summary>
    /// Pins today's collapse: the endpoint answers a revoked integration exactly
    /// as it answers one that was never registered, so the refusal is no oracle
    /// for which integrations exist. Whether it *should* distinguish them is an
    /// open question filed separately — asserting the collapse makes any future
    /// change to it deliberate rather than silent.
    ///
    /// <para>
    /// "Exactly" is checked across all three observables a caller can see:
    /// status, body, and <c>WWW-Authenticate</c> — the one place an ASP.NET
    /// challenge would leak the distinction. The status carries an absolute
    /// (401) <em>before</em> the comparison, so a change moving both sides
    /// together still reddens rather than passing as a tautology. The header
    /// carries no absolute: asserting it absent would over-pin something the
    /// open question may legitimately change, while comparing the two responses
    /// names no requirement and extends the same indistinguishability oracle to
    /// the third observable.
    /// </para>
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

        revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DiagnoseAsync(revoked));
        revoked.StatusCode.ShouldBe(neverRegistered.StatusCode, await DiagnoseAsync(revoked));
        (await revoked.Content.ReadAsStringAsync())
            .ShouldBe(await neverRegistered.Content.ReadAsStringAsync());
        revoked.Headers.WwwAuthenticate.ToString()
            .ShouldBe(neverRegistered.Headers.WwwAuthenticate.ToString());
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
    /// JWT mode has no registration endpoint — rotation flips an integration
    /// into it — so the row is written straight to the database. Seeded in
    /// <see cref="Fab"/> so admin, who holds <c>/fabs/munich</c> only, can list
    /// and revoke it through the registry endpoints like any other row.
    /// </summary>
    private async Task SeedJwtIntegrationAsync(string name, string keycloakClientId)
    {
        await using EventIngestionDbContext context = await aspire.CreateEventIngestionDbContextAsync();

        SystemClock clock = new();
        (WebhookIntegration integration, _) = WebhookIntegration.Register(
            WebhookIntegrationName.From(name), FabIdentifier.From(Fab), Kind.From("WebhookAlarm"), clock);
        integration.MarkAsRotated(KeycloakClientIdentifier.From(keycloakClientId), clock);
        integration.ClearPendingEvents();

        context.WebhookIntegrations.Add(integration);
        await context.SaveChangesAsync();
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

        using HttpRequestMessage request = Conditional(name, version);
        HttpResponseMessage revoked = await admin.SendAsync(request);

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await DiagnoseAsync(revoked));
    }

    /// <summary>
    /// <c>includeRevoked=false</c> is the server default and is stated anyway,
    /// so the lookup does not silently depend on it — the sibling
    /// <c>WebhookIntegrationConcurrencyIntegrationTests.FindAsync</c> passes the
    /// flag for the same reason.
    /// </summary>
    private static async Task<JsonElement> FindAsync(HttpClient admin, string name)
    {
        HttpResponseMessage listed = await admin.GetAsync("/webhook-integrations?includeRevoked=false");
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

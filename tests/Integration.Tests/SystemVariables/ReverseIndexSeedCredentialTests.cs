using System.Net.Http.Headers;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.SystemVariables;

/// <summary>
/// Spec 126 (#2158) — the credential the seeder was always specified to have.
///
/// <para>
/// Spec 005 T061 said the reverse-index seeder "authenticates with Keycloak
/// (service account)". The seeder shipped and the authentication did not, and
/// <c>GET /overlays</c> has since become
/// <c>.RequireAuthorization(Scope.Sse.Overlays.Read)</c>. So every cold start
/// of <c>system-variables</c> is refused and begins with an empty index; a
/// variable change then fans out to nothing until the overlays happen to be
/// republished. That was observed on the dev stack before any code moved
/// (spec 126 §"Observed, not inferred").
/// </para>
///
/// <para>
/// <b>Why this belongs against the real stack.</b> The token has to be minted
/// by the real Keycloak from the realm import, and the scope decision has to be
/// enforced by the real endpoints. A unit test can assert that a bearer is
/// attached; only this can assert that the bearer is <i>accepted</i> by
/// overlay-designer and <i>refused</i> everywhere else, which is the whole of
/// FR-003.
/// </para>
///
/// <para>
/// <b>Two refusals, not one.</b> A grant that opened the listing would satisfy
/// the first fact alone while handing a startup reader the ability to author
/// overlays — which is precisely why reusing <c>scenario-simulator</c> was
/// rejected. The second and third facts are the boundary:
/// <c>sse.overlays.write</c> is what a copy-paste from the overlay client would
/// attract, and <c>sse.variables.write</c> is what a copy-paste from a
/// SystemVariables sibling would.
/// </para>
///
/// <para>
/// Minted from the fixture's Keycloak client, which points at Aspire's proxied
/// endpoint. A token minted from the container's mapped port carries an issuer
/// the APIs do not accept, and every call 401s regardless of scope.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class ReverseIndexSeedCredentialTests(AspireFixture aspire)
{
    private const string SeederClientId = "system-variables-seeder";
    private const string SeederClientSecret = "dev-only-system-variables-seeder-secret";
    private const string TokenEndpoint = "/realms/smart-sentinel-eye/protocol/openid-connect/token";

    /// <summary>
    /// FR-001 and FR-003's positive half: the one call the seeder makes, with
    /// the credential it is meant to make it with. The <c>published</c> key is
    /// asserted because that is what the seeder reads — a 200 carrying some
    /// other shape would leave the index empty just as surely as a 401.
    /// </summary>
    [Fact]
    public async Task The_seeder_service_account_can_list_published_overlays()
    {
        string token = await SeederTokenAsync();
        using HttpClient overlays = aspire.CreateServiceClient("overlay-designer");

        using HttpResponseMessage response =
            await SendAsync(overlays, HttpMethod.Get, "/overlays?state=Published", token);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the seeder's service account cannot read the published overlays, so every cold start "
            + $"of system-variables begins with an empty reverse index (#2158). {await BodyAsync(response)}");

        JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.TryGetProperty("published", out _).ShouldBeTrue(
            "the seeder reads the 'published' array and nothing else; without that key it logs "
            + "SeedMissingPublishedKey and leaves the index empty");
    }

    /// <summary>
    /// FR-003's first boundary. A token holding <c>sse.overlays.write</c> would
    /// pass the fact above and fail this one, which is the point: the seeder is
    /// a reader started at boot, and nothing about reading a listing needs the
    /// ability to author the overlays in it.
    /// </summary>
    [Fact]
    public async Task The_seeder_service_account_cannot_author_an_overlay()
    {
        string token = await SeederTokenAsync();
        using HttpClient overlays = aspire.CreateServiceClient("overlay-designer");

        using HttpResponseMessage response = await SendAsync(
            overlays, HttpMethod.Post, "/overlays", token, DraftOverlayBody());

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "the seed credential can author overlays. OverlayDesigner runs no fab guard, so the "
            + "scope is the only thing that can refuse here and it did not (FR-003). "
            + await BodyAsync(response));
    }

    /// <summary>
    /// FR-003's second boundary, in the context the seeder itself runs in.
    /// <c>sse.variables.write</c> is the scope a copy-paste from a SystemVariables
    /// sibling would most plausibly attract, and it is the one that would let a
    /// startup reader rewrite the fab's variables.
    ///
    /// <para>
    /// Unlike the overlay case, a 403 here has two possible authors: the scope
    /// policy, or — for a token that did hold the scope — the fab guard, since
    /// the seed credential carries no <c>sse-groups</c>. Both are refusals of
    /// the same write, so the assertion stands either way; it is recorded
    /// because a future reader should not take this as proof the scope alone
    /// did the work.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seeder_service_account_cannot_write_a_system_variable()
    {
        string token = await SeederTokenAsync();
        using HttpClient variables = aspire.CreateServiceClient("system-variables");

        using HttpResponseMessage response = await SendAsync(
            variables, HttpMethod.Post, "/system-variables", token, DefineVariableBody());

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "the seed credential can define system variables; it is meant to read overlays and "
            + $"nothing else (FR-003). {await BodyAsync(response)}");
    }

    /// <summary>
    /// The <c>client_credentials</c> grant, hand-rolled here rather than added to
    /// <see cref="AspireFixture"/> — <c>VariableReadScopeIntegrationTests</c>,
    /// <c>FabGroupClaimIntegrationTests</c> and
    /// <c>StreamFabAttributionIntegrationTests</c> each hold their own.
    ///
    /// <para>
    /// The refusal is asserted rather than dereferenced, because until T010 adds
    /// the realm client this is the failure every test in the file will show,
    /// and "the client does not exist yet" must read as that rather than as a
    /// <c>NullReferenceException</c> on a missing <c>access_token</c>.
    /// </para>
    /// </summary>
    private async Task<string> SeederTokenAsync()
    {
        using HttpClient keycloak = aspire.CreateKeycloakClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = SeederClientId,
            ["client_secret"] = SeederClientSecret,
        });

        using HttpResponseMessage response = await keycloak.PostAsync(TokenEndpoint, form);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"Keycloak refused the client_credentials grant for '{SeederClientId}'. The realm has no "
            + "such client, or it is not a confidential client with service accounts enabled "
            + $"(spec 126 T010). Keycloak answered {(int)response.StatusCode} {response.StatusCode}: {body}");

        using JsonDocument payload = JsonDocument.Parse(body);
        string? accessToken = payload.RootElement.GetProperty("access_token").GetString();
        accessToken.ShouldNotBeNullOrWhiteSpace();

        return accessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        // A per-request header, never client.DefaultRequestHeaders: the fixture's
        // clients are collection-scoped, and client-wide state carrying
        // per-request data is the failure AuthorizingHandler exists to remove.
        using HttpRequestMessage request = new(method, url)
        {
            // The runtime type, not the static one: the bodies below are anonymous,
            // and serializing them as `object` would send `{}`.
            Content = body is null ? null : JsonContent.Create(body, body.GetType()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    /// <summary>
    /// A body the API would accept, so that a credential wrongly holding
    /// <c>sse.overlays.write</c> answers 201 rather than 400 — the counterfactual
    /// this test is measured against. Nothing is created when the refusal works.
    /// </summary>
    private static object DraftOverlayBody() => new
    {
        name = $"Seed-{Guid.NewGuid():N}"[..16],
        label = new
        {
            text = "spec 126 scope probe",
            normalizedX = 0.1m,
            normalizedY = 0.1m,
            normalizedWidth = 0.2m,
            normalizedHeight = 0.1m,
            fontSizePx = 24,
        },
    };

    private static object DefineVariableBody() => new
    {
        name = $"seedScope{Guid.NewGuid():N}"[..24],
        type = "String",
        initialValue = "spec 126",
        truthyLabel = (string?)null,
        falsyLabel = (string?)null,
    };

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        $"body: {await response.Content.ReadAsStringAsync()}";
}

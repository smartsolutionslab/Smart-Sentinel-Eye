using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin;

/// <summary>
/// Spec 122 (#2166) — <b>a compensating delete that is refused must say so.</b>
///
/// <para>
/// <c>TryDeleteClientAsync</c> declares its <c>HttpResponseMessage</c>, disposes
/// it, and never reads it. A DELETE answering <c>409</c> or <c>500</c> leaves a
/// client stamped <c>sse.kind=kiosk</c> in the realm, holding the inherited
/// realm roles, with a secret the caller never received — and writes nothing
/// anywhere. The existence probe then answers <i>already enrolled</i> for it, so
/// that kiosk cannot be enrolled again.
/// </para>
///
/// <para>
/// <b>The startup sweep is not the backstop the comment claims.</b> Spec 092
/// registered it, but <c>KioskPrivilegeSweep.SweepAsync</c> calls
/// <c>StripInheritedRealmRolesAsync</c> and nothing else: it takes the residue's
/// privileges away and leaves the residue. The re-enrolment block survives every
/// sweep, on every start, until a human deletes the client.
/// </para>
///
/// <para>
/// <b>Driven through the real client, at the transport seam.</b> The defect is
/// in a private method, so it is reached the way production reaches it —
/// <c>CreateClientAsync</c> succeeds, the strip is refused, the compensating
/// delete runs — rather than by calling it. A double standing in for
/// <c>IKeycloakAdminClient</c> could not see this at all.
/// </para>
/// </summary>
public class HalfEnrolledClientCompensationTests
{
    private const string Realm = "smart-sentinel-eye";
    private const string ClientId = "kiosk-a";
    private const string ClientUuid = "11111111-1111-1111-1111-111111111111";

    /// <summary>The generator names each event after its log method.</summary>
    private const string ResidueLine = "HalfEnrolledClientSurvived";
    private const string AbsenceLine = "HalfEnrolledClientWasAlreadyAbsent";

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_compensation_names_the_client_it_left_behind(HttpStatusCode refusal)
    {
        (StubKeycloakHandler keycloak, CapturingLogger<HttpKeycloakAdminClient> logger) =
            await EnrolAndFailAsync(refusal);

        // The discriminator. Without it the whole test passes for a flow that
        // threw before compensation was ever reached, and would keep passing if
        // the delete were removed outright.
        keycloak.Requests.ShouldContain(
            request => request.Method == "DELETE"
                && request.PathAndQuery.EndsWith(ClientUuid, StringComparison.Ordinal),
            "the compensating delete never ran, so this asserts nothing about it. Sent: "
            + string.Join(", ", keycloak.Requests.Select(request => request.Method + " " + request.PathAndQuery)));

        LoggedEntry residue = logger.Named(ResidueLine).ShouldHaveSingleItem(
            "Keycloak answered " + (int)refusal + " to the compensating delete, so the half-enrolled "
            + "client is still in the realm holding offline_access, and the existence probe "
            + "answers already-enrolled for it forever. Nothing records that today (#2166).");

        // Structured fields, not message text: the field is what an operator
        // queries by, and a message can name the right client while the field
        // carries the wrong one.
        residue.Field("ClientId").ShouldBe(
            ClientId,
            "the clientId is what the existence probe collides on, and what someone "
            + "searching the realm has in hand");
        residue.Field("ClientUuid").ShouldBe(
            ClientUuid,
            "and the uuid is what a manual DELETE against the admin API needs");
        residue.Field("StatusCode").ShouldBe(
            refusal.ToString(),
            "the refusal itself, so a Keycloak that said no can be told from one that "
            + "could not be reached");

        residue.Level.ShouldBe(
            LogLevel.Error,
            "nothing in the system clears this. The sweep strips the residue's privileges and "
            + "leaves the client, so the kiosk stays un-enrollable until a human intervenes — "
            + "which is an error, not a warning about something that self-heals.");
    }

    /// <summary>
    /// FR-006, and a restraint that discriminates: had the compensation been made
    /// to throw, the caller would receive the delete's status (<c>409</c>) in
    /// place of the strip's (<c>500</c>). So this fails on exactly the change it
    /// forbids, rather than merely observing that something was thrown.
    /// </summary>
    [Fact]
    public async Task The_caller_still_receives_the_failure_that_caused_the_compensation()
    {
        StubKeycloakHandler keycloak = new(EnrolmentFlow)
        {
            Refuse = Refusing(HttpStatusCode.Conflict),
        };

        HttpRequestException thrown = await Should.ThrowAsync<HttpRequestException>(
            () => CreateAsync(keycloak, new CapturingLogger<HttpKeycloakAdminClient>()));

        thrown.StatusCode.ShouldBe(
            HttpStatusCode.InternalServerError,
            "the strip failing is what explains why compensation was needed at all. A delete "
            + "that threw would replace it with Conflict and lose the original cause (#2166).");
    }

    /// <summary>
    /// FR-004. A <c>404</c> is Keycloak saying the client is not there — the
    /// compensation's goal, reached — so reporting a surviving residue would
    /// state something false. Measured against Keycloak 26.6: a delete of an
    /// unknown client answers <c>404</c> with <c>Could not find client</c>.
    ///
    /// <para>
    /// The empty assertion below is not idle. It fails against the obvious
    /// alternative implementation — one residue line for every non-success —
    /// which is the design decision this test exists to pin.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_compensation_finding_nothing_to_delete_does_not_claim_a_residue()
    {
        (StubKeycloakHandler _, CapturingLogger<HttpKeycloakAdminClient> logger) =
            await EnrolAndFailAsync(HttpStatusCode.NotFound);

        logger.Named(ResidueLine).ShouldBeEmpty(
            "a 404 means Keycloak has no such client, so nothing was left behind and no one "
            + "should be sent looking for it");

        LoggedEntry absent = logger.Named(AbsenceLine).ShouldHaveSingleItem(
            "it is still worth a line: the post-create re-probe found this client moments "
            + "earlier, and one call later it is gone");
        absent.Field("ClientId").ShouldBe(ClientId);
        absent.Level.ShouldBe(
            LogLevel.Warning,
            "unexpected, but nothing survives and nobody has to act");
    }

    /// <summary>
    /// The other side of the same guard: a compensation that worked is silent.
    /// Without this, "log the residue" is satisfiable by logging on every delete,
    /// which would report a residue that does not exist.
    /// </summary>
    [Fact]
    public async Task A_compensation_that_succeeds_says_nothing()
    {
        (StubKeycloakHandler keycloak, CapturingLogger<HttpKeycloakAdminClient> logger) =
            await EnrolAndFailAsync(refusal: null);

        keycloak.Requests.ShouldContain(
            request => request.Method == "DELETE"
                && request.PathAndQuery.EndsWith(ClientUuid, StringComparison.Ordinal),
            "the delete must have run for the silence to mean anything");

        logger.Entries.ShouldBeEmpty(
            "the half-made client is gone. The caller's own error is the whole story, and a "
            + "line here would send an operator hunting a client that no longer exists.");
    }

    private static async Task<(StubKeycloakHandler Keycloak, CapturingLogger<HttpKeycloakAdminClient> Logger)>
        EnrolAndFailAsync(HttpStatusCode? refusal)
    {
        StubKeycloakHandler keycloak = new(EnrolmentFlow) { Refuse = Refusing(refusal) };
        CapturingLogger<HttpKeycloakAdminClient> logger = new();

        await Should.ThrowAsync<HttpRequestException>(() => CreateAsync(keycloak, logger));

        return (keycloak, logger);
    }

    private static Task<KeycloakClientCredentials> CreateAsync(
        StubKeycloakHandler keycloak, CapturingLogger<HttpKeycloakAdminClient> logger) =>
        new HttpKeycloakAdminClient(
                new HttpClient(keycloak) { BaseAddress = new Uri("http://keycloak.test/") },
                Options.Create(new KeycloakAdminOptions { Realm = Realm }),
                logger)
            .CreateClientAsync(Representation(), "/fabs/munich", CancellationToken.None);

    /// <summary>
    /// The strip is always refused — that is what makes compensation run at all.
    /// The compensating delete answers <paramref name="clientDelete"/>, or
    /// succeeds when it is <c>null</c>.
    /// </summary>
    private static Func<RecordedRequest, HttpStatusCode?> Refusing(HttpStatusCode? clientDelete) =>
        request => request switch
        {
            { Method: "DELETE", PathAndQuery: var path }
                when path.EndsWith("/role-mappings/realm", StringComparison.Ordinal) =>
                HttpStatusCode.InternalServerError,
            { Method: "DELETE", PathAndQuery: var path }
                when path.EndsWith(ClientUuid, StringComparison.Ordinal) => clientDelete,
            _ => null,
        };

    private static KeycloakClientRepresentation Representation() =>
        new(ClientId: ClientId,
            Name: "Kiosk " + ClientId,
            ServiceAccountsEnabled: true,
            StandardFlowEnabled: false,
            DirectAccessGrantsEnabled: false,
            PublicClient: false,
            DefaultClientScopes: ["sse.streams.view"],
            OptionalClientScopes: [],
            Attributes: new Dictionary<string, string> { ["sse.kind"] = "kiosk" });

    private static string? EnrolmentFlow(StubKeycloakHandler keycloak, RecordedRequest request) => request switch
    {
        { PathAndQuery: var path } when path.Contains("clientId=", StringComparison.Ordinal) =>
            keycloak.CountOf("clientId=") == 1 ? "[]" : ClientRows,
        { PathAndQuery: var path } when path.EndsWith("/service-account-user", StringComparison.Ordinal) =>
            """{"id":"22222222-2222-2222-2222-222222222222"}""",
        { PathAndQuery: var path } when path.Contains("/group-by-path/", StringComparison.Ordinal) =>
            """{"id":"33333333-3333-3333-3333-333333333333","name":"munich","path":"/fabs/munich"}""",
        // Non-empty, unlike the happy-path flow's "[]": an account the realm
        // reports as already stripped returns before sending its delete, so the
        // strip would succeed and compensation would never run.
        { Method: "GET", PathAndQuery: var path }
            when path.EndsWith("/role-mappings/realm", StringComparison.Ordinal) =>
            """[{"id":"44444444-4444-4444-4444-444444444444","name":"offline_access"}]""",
        _ => null,
    };

    private const string ClientRows =
        $$"""[{"id":"{{ClientUuid}}","clientId":"{{ClientId}}"}]""";
}

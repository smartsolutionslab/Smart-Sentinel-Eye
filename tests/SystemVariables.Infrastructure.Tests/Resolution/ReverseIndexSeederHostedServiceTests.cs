using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.SystemVariables.Infrastructure.Resolution;
using SmartSentinelEye.SystemVariables.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.SystemVariables.Infrastructure.Tests.Resolution;

/// <summary>
/// Spec 126 (#2158) — the seeder's refusal semantics, driven through a
/// hand-written <see cref="HttpMessageHandler"/> stub (ADR-0054: no mocking
/// framework at the transport seam).
///
/// <para>
/// <b>Why the log is the assertion.</b> A refused seed and a successful one
/// differ in nothing the seeder returns — <c>StartAsync</c> completes either
/// way (FR-006), and the index is simply left as it was. The only surviving
/// difference is what was written to the log, so the level and the words are
/// the behaviour under test rather than a convenience.
/// </para>
///
/// <para>
/// <b>The discriminator is deliberate.</b> Raising every non-success status to
/// <c>Error</c> would satisfy the two refusal facts below and be wrong:
/// FR-005 keeps an overlay-designer outage at <c>Warning</c>, because that one
/// genuinely does self-heal from <c>OverlayRevisionPublishedV1</c> events. The
/// 503 fact is green before this change and must stay green after it.
/// </para>
/// </summary>
public class ReverseIndexSeederHostedServiceTests
{
    private const string OverlayDesignerClientName = "overlay-designer";
    private const string LabelText = "OEE {{oeeLine1}}";

    private static readonly Guid Overlay = Guid.CreateVersion7();

    private static readonly string PublishedListing = $$"""
        {
          "chains": [],
          "published": [
            {
              "overlayIdentifier": "{{Overlay}}",
              "name": "Line 1 OEE",
              "revisionNumber": 3,
              "text": "{{LabelText}}",
              "publishedAt": "2026-09-10T08:00:00Z"
            }
          ]
        }
        """;

    // ---- a refusal (FR-004) ----

    /// <summary>
    /// A 401 that reaches the seeder has already survived the standard
    /// resilience handler's retries (ADR-0143 retries <c>GET</c>), so it is not
    /// a blip: the credential is wrong, missing or disabled. That is an
    /// operator-actionable misconfiguration, and a <c>Warning</c> among the
    /// startup noise is how it went unnoticed from spec 005 to #2158.
    /// </summary>
    [Fact]
    public async Task A_refused_seed_is_logged_as_an_error()
    {
        Seed seed = await RunAsync(HttpStatusCode.Unauthorized);

        seed.Logger.Entries.Count.ShouldBe(1, Transcript(seed));
        seed.Logger.Entries[0].Level.ShouldBe(
            LogLevel.Error,
            "a 401 means the seeder's credential was refused, which cannot repair itself. "
            + Transcript(seed));
    }

    /// <summary>
    /// A 403 is the same defect wearing a different status — the client
    /// authenticated but holds no <c>sse.overlays.read</c> — and takes the same
    /// branch. Separated from the 401 fact because an implementation that
    /// branched on <c>Unauthorized</c> alone would pass that one.
    /// </summary>
    [Fact]
    public async Task A_forbidden_seed_is_logged_as_an_error()
    {
        Seed seed = await RunAsync(HttpStatusCode.Forbidden);

        seed.Logger.Entries.Count.ShouldBe(1, Transcript(seed));
        seed.Logger.Entries[0].Level.ShouldBe(
            LogLevel.Error,
            "a 403 means the token carries no sse.overlays.read, which cannot repair itself. "
            + Transcript(seed));
    }

    /// <summary>
    /// The message that ships today ends "The index will populate as new
    /// OverlayRevisionPublishedV1 events arrive." That sentence is true of an
    /// overlay-designer outage and false of a rejected credential: no event
    /// republishes the overlays that were already published before the process
    /// started, so the index stays empty for the life of the host. A message
    /// that is confidently wrong is worse than no message.
    ///
    /// <para>
    /// The two banned phrases are the affirmative promises the two existing
    /// swallow messages make ("will populate", "will kick in"). Only the
    /// affirmative forms are banned, so a replacement is free to say the
    /// opposite — "the index will not populate until the credential is fixed"
    /// reads as it should and passes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_seed_does_not_promise_a_self_heal()
    {
        Seed seed = await RunAsync(HttpStatusCode.Unauthorized);

        string message = seed.Logger.Entries.Single().Message;

        message.Contains("will populate", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            $"a refused credential does not self-heal, but the message promises it does: '{message}'");
        message.Contains("will kick in", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            $"a refused credential does not self-heal, but the message promises it does: '{message}'");
    }

    // ---- everything else keeps the warning (FR-005) ----

    /// <summary>
    /// The discriminator. An overlay-designer that is down or broken genuinely
    /// does self-heal — it will republish, and those events refill the index —
    /// so FR-005 leaves this at <c>Warning</c> with the message it already has.
    /// Green before spec 126 and green after; an implementation that lifted
    /// every non-success status to <c>Error</c> fails here and nowhere else.
    /// </summary>
    [Fact]
    public async Task A_server_error_stays_a_warning()
    {
        Seed seed = await RunAsync(HttpStatusCode.ServiceUnavailable);

        seed.Logger.Entries.Count.ShouldBe(1, Transcript(seed));
        seed.Logger.Entries[0].Level.ShouldBe(
            LogLevel.Warning,
            "a 503 is an overlay-designer outage, which the republish events do repair. "
            + Transcript(seed));
    }

    // ---- the host still starts (FR-006) ----

    /// <summary>
    /// The trade spec 126 declines to make. Throwing from
    /// <c>IHostedService.StartAsync</c> would swap a degraded reverse index for
    /// a total SystemVariables outage — no variable reads, no writes, no API.
    /// The sibling precedent settled this the same way and keeps it settled by
    /// assertion: <c>StreamFabAttributionFailureTests
    /// .An_unreachable_camera_catalog_does_not_block_host_start</c> (ADR-0116).
    /// </summary>
    [Fact]
    public async Task A_refused_seed_leaves_the_host_started()
    {
        // No throw: StartAsync completing is the host starting.
        Seed seed = await RunAsync(HttpStatusCode.Unauthorized);

        seed.Index.AllOverlays().ShouldBeEmpty("a refusal leaves the index as it was; it does not fail");
    }

    // ---- the parse, unchanged ----

    /// <summary>
    /// The green companion. Spec 126 changes the credential and one log branch
    /// and nothing else; this is what keeps the parse of the listing — and the
    /// success path through it — from moving underneath the change.
    /// </summary>
    [Fact]
    public async Task A_successful_seed_populates_the_index()
    {
        Seed seed = await RunAsync(HttpStatusCode.OK, PublishedListing);

        seed.Index.LookupOverlays("oeeLine1").ShouldBe([Overlay]);
        seed.Index.LookupLabelText(Overlay).ShouldBe(LabelText);
    }

    // ---- the credential itself ----

    /// <summary>
    /// The defect, stated as narrowly as it can honestly be stated here.
    ///
    /// <para>
    /// The bearer spec 126 adds arrives through a delegating handler on the
    /// named <c>overlay-designer</c> client, which is DI wiring this test does
    /// not build — the stub client handed back by the factory has no such
    /// handler, so this fact is green before the change and green after it.
    /// <b>The wiring test is the integration one</b>,
    /// <c>Integration.Tests/SystemVariables/ReverseIndexSeedCredentialTests</c>,
    /// which mints the real service account against the real Keycloak.
    /// </para>
    ///
    /// <para>
    /// What it does pin is the seam: the seeder asks the factory for
    /// <c>overlay-designer</c> and sets no header of its own, so the credential
    /// has to come from that client's pipeline. An implementation that instead
    /// injected a token provider into the seeder — changing the constructor the
    /// plan says stays — would fail here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seed_request_today_carries_no_authorization_header()
    {
        Seed seed = await RunAsync(HttpStatusCode.OK, PublishedListing);

        seed.Factory.RequestedNames.ShouldBe([OverlayDesignerClientName]);
        seed.Transport.Authorization.ShouldBeNull(
            "the seeder sets no Authorization header itself; the bearer must come from the "
            + "named client's handler pipeline");
    }

    // ---- a refused token mint (#2158, found at phase 5) ----

    /// <summary>
    /// The refusal a broken deployment actually produces. The bearer is minted
    /// by <c>ClientCredentialsTokenProvider</c> inside the named client's
    /// handler pipeline, so a missing, disabled or wrong-secret
    /// <c>system-variables-seeder</c> is refused by <b>Keycloak</b>, not by
    /// overlay-designer: the mint's <c>EnsureSuccessStatusCode()</c> throws an
    /// <see cref="HttpRequestException"/> carrying the 401, which surfaces out
    /// of the seeder's <c>GetAsync</c> as an exception rather than as a
    /// response.
    ///
    /// <para>
    /// So the status branch of FR-004 never runs, and the most likely
    /// misconfiguration of all takes the catch-all instead — <c>SeedFailed</c>
    /// at <c>Warning</c>, promising a repair that cannot happen. Same defect,
    /// different door. Observed on the dev stack at phase 5, against a Keycloak
    /// that did not yet hold the client.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_token_mint_is_logged_as_an_error()
    {
        FailedSeed seed = await RunThrowingAsync(RefusedMint());

        seed.Logger.Entries.Count.ShouldBe(1, FailureTranscript(seed));
        seed.Logger.Entries[0].Level.ShouldBe(
            LogLevel.Error,
            "a 401 thrown out of the token mint is the same refused credential as a 401 "
            + "response, and no more able to repair itself. " + FailureTranscript(seed));
    }

    /// <summary>
    /// The words, for the same reason as
    /// <see cref="A_refused_seed_does_not_promise_a_self_heal"/>: nothing
    /// republishes an overlay that was already published, so the index stays
    /// empty for the life of the host. Today this path says "Self-heal will
    /// kick in as overlay V1 events arrive", which is a promise the process
    /// cannot keep.
    ///
    /// <para>
    /// Only the affirmative promises are banned, so a replacement is free to
    /// say the opposite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_token_mint_does_not_promise_a_self_heal()
    {
        FailedSeed seed = await RunThrowingAsync(RefusedMint());

        string message = seed.Logger.Entries.Single().Message;

        message.Contains("will populate", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            $"a refused mint does not self-heal, but the message promises it does: '{message}'");
        message.Contains("will kick in", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            $"a refused mint does not self-heal, but the message promises it does: '{message}'");
    }

    /// <summary>
    /// The discriminator for the throwing path, and the reason the fix cannot
    /// be "lift the catch-all to <c>Error</c>". A transport failure carrying no
    /// status — overlay-designer unreachable, DNS not up yet, connection
    /// refused during a rolling start — is the outage case FR-005 keeps at
    /// <c>Warning</c>, and it genuinely does self-heal from
    /// <c>OverlayRevisionPublishedV1</c> events.
    ///
    /// <para>
    /// Green before this change and green after it. An implementation that
    /// discriminates on the exception's <c>StatusCode</c> passes this and the
    /// two facts above; one that raises every caught exception fails here and
    /// nowhere else.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_transport_failure_that_is_not_a_refusal_stays_a_warning()
    {
        HttpRequestException unreachable = new("overlay-designer unreachable");

        FailedSeed seed = await RunThrowingAsync(unreachable);

        seed.Logger.Entries.Count.ShouldBe(1, FailureTranscript(seed));
        seed.Logger.Entries[0].Level.ShouldBe(
            LogLevel.Warning,
            "an unreachable overlay-designer is an outage, which the republish events do repair. "
            + FailureTranscript(seed));
        seed.Logger.Entries[0].Exception.ShouldBeSameAs(
            unreachable, "the outage branch still carries the cause. " + FailureTranscript(seed));
        seed.Logger.Entries[0].Message.Contains("refused", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse(
                "an outage is not a refused credential and must not be reported as one. "
                + FailureTranscript(seed));
    }

    /// <summary>
    /// FR-006 for the throwing path.
    /// <see cref="A_refused_seed_leaves_the_host_started"/> covers a 401
    /// <i>response</i>; a 401 that arrives as an exception is a different
    /// branch, and the trade ADR-0116 settled is the same one — a degraded
    /// index beats no SystemVariables at all. Green today, and it is what keeps
    /// "make the refusal loud" from becoming "make the refusal fatal".
    /// </summary>
    [Fact]
    public async Task A_refused_token_mint_leaves_the_host_started()
    {
        // No throw: StartAsync completing is the host starting.
        FailedSeed seed = await RunThrowingAsync(RefusedMint());

        seed.Index.AllOverlays().ShouldBeEmpty(
            "a refused mint leaves the index as it was; it does not fail");
    }

    // ---- scaffolding ----

    private static async Task<Seed> RunAsync(HttpStatusCode status, string body = "{}")
    {
        StubOverlayDesigner transport = new(status, body);
        SingleClientFactory factory = new(
            new HttpClient(transport) { BaseAddress = new Uri("http://overlay-designer") });
        InMemoryReverseIndex index = new();
        CapturingLogger<ReverseIndexSeederHostedService> logger = new();

        ReverseIndexSeederHostedService seeder = new(factory, index, logger);
        await seeder.StartAsync(CancellationToken.None);

        return new Seed(logger, index, transport, factory);
    }

    /// <summary>Everything that was logged, so a failure names the level and the words.</summary>
    private static string Transcript(Seed seed) =>
        "logged: " + string.Join(
            " | ", seed.Logger.Entries.Select(entry => $"[{entry.Level}] {entry.Message}"));

    private sealed record Seed(
        CapturingLogger<ReverseIndexSeederHostedService> Logger,
        InMemoryReverseIndex Index,
        StubOverlayDesigner Transport,
        SingleClientFactory Factory);

    /// <summary>
    /// One prepared answer, and the <c>Authorization</c> header the request
    /// carried. Hand-written (ADR-0054) and at the transport seam because the
    /// header is what spec 126 adds — a double standing in for the seeder's
    /// collaborators could not see it.
    /// </summary>
    private sealed class StubOverlayDesigner(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Hands back one prepared client whatever name is asked for, recording the
    /// names. Mirrors <c>Integration.Tests.Fixtures.FakeHttpClientFactory</c>;
    /// copied rather than shared, since test projects do not reference one
    /// another.
    /// </summary>
    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return client;
        }
    }

    /// <summary>
    /// The shape <c>EnsureSuccessStatusCode()</c> throws inside
    /// <c>ClientCredentialsTokenProvider.MintAsync</c> when Keycloak refuses
    /// the client, after ADR-0143's <c>RetryEveryMethod</c> attempts are spent.
    /// </summary>
    private static HttpRequestException RefusedMint() =>
        new(
            "Response status code does not indicate success: 401 (Unauthorized).",
            inner: null,
            statusCode: HttpStatusCode.Unauthorized);

    /// <summary>
    /// The throwing sibling of <see cref="RunAsync"/>, kept separate so the
    /// answering runner and its stub stay exactly as the existing facts left
    /// them. There is no <c>Authorization</c> header to observe here: the
    /// failure happens in the handler pipeline before a request is answered.
    /// </summary>
    private static async Task<FailedSeed> RunThrowingAsync(Exception failure)
    {
        ThrowingOverlayDesigner transport = new(failure);
        SingleClientFactory factory = new(
            new HttpClient(transport) { BaseAddress = new Uri("http://overlay-designer") });
        InMemoryReverseIndex index = new();
        CapturingLogger<ReverseIndexSeederHostedService> logger = new();

        ReverseIndexSeederHostedService seeder = new(factory, index, logger);
        await seeder.StartAsync(CancellationToken.None);

        return new FailedSeed(logger, index);
    }

    /// <summary>Everything that was logged, so a failure names the level and the words.</summary>
    private static string FailureTranscript(FailedSeed seed) =>
        "logged: " + string.Join(
            " | ", seed.Logger.Entries.Select(entry => $"[{entry.Level}] {entry.Message}"));

    private sealed record FailedSeed(
        CapturingLogger<ReverseIndexSeederHostedService> Logger,
        InMemoryReverseIndex Index);

    /// <summary>
    /// A transport that never answers. Stands in for the whole named client's
    /// pipeline: in production the exception is raised by the token handler in
    /// front of the request, and the seeder cannot tell the difference — both
    /// arrive out of <c>client.GetAsync(...)</c>.
    /// </summary>
    private sealed class ThrowingOverlayDesigner(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }
}

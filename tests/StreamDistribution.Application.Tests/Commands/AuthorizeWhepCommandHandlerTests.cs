using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Application.Commands;
using SmartSentinelEye.StreamDistribution.Application.Commands.Handlers;
using SmartSentinelEye.StreamDistribution.Application.Tests.Fakes;
using SmartSentinelEye.StreamDistribution.Domain.Stream;
using SmartSentinelEye.StreamDistribution.Domain.Tests.Stream.Builders;

namespace SmartSentinelEye.StreamDistribution.Application.Tests.Commands;

public class AuthorizeWhepCommandHandlerTests
{
    private static readonly DateTimeOffset FixedMoment =
        DateTimeOffset.Parse("2026-05-26T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly OperatorIdentifier AnAdmin =
        OperatorIdentifier.From(Guid.CreateVersion7());

    /// <summary>
    /// The scopes an enrolled kiosk device holds — <c>KeycloakScopeBundles.Kiosk</c>,
    /// and what the browser kiosk's client grants from spec 041. Written out
    /// rather than referenced: Application tests do not reach into another
    /// bounded context, and <c>KioskScopeParityTests</c> is what keeps the two
    /// lists agreed.
    /// </summary>
    private static readonly string[] AKioskPersona =
    [
        "openid",
        "sse.cameras.read",
        "sse.streams.read",
        "sse.layouts.read",
        "sse.overlays.read",
        "sse.variables.read",
        "sse.events.write",
    ];

    /// <summary>
    /// management-web's actual token shape: the grandfathered bundle and
    /// <b>no</b> <c>sse.streams.read</c>. It is the case the fallback exists to
    /// keep working, and exactly what a naively-narrowed gate would break.
    /// </summary>
    [Fact]
    public async Task Authorize_with_a_grandfathered_management_token_returns_success()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("admin-id", ["openid", "sse.management"])),
        };
        InMemoryStreamRepository streams = new();
        AuthorizeWhepCommandHandler handler = new(validator, streams, NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.xyz",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// <b>Spec 041.</b> Before this, the gate asked for the management bundle
    /// alone, so <em>no</em> kiosk could open a stream — not the browser kiosk,
    /// and not an enrolled device, whose bundle has never carried it. A kiosk
    /// that cannot watch video cannot do the only thing a kiosk is for.
    /// </summary>
    [Fact]
    public async Task Authorize_with_a_kiosk_token_returns_success()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Authorize_with_an_empty_token_returns_Unauthorized()
    {
        AuthorizeWhepCommandHandler handler = new(new FakeWhepAuthValidator(), new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.Unauthorized>();
    }

    /// <summary>
    /// A token the validator could not attribute arrives here as
    /// <see cref="Option{T}.None"/> — including one carrying no <c>sub</c>,
    /// which is what a client with no sub mapper mints (spec 041). Still
    /// refused: an unattributable viewer stays refused.
    /// </summary>
    [Fact]
    public async Task Authorize_with_an_invalid_token_returns_Unauthorized()
    {
        FakeWhepAuthValidator validator = new() { Subject = Option<WhepAuthSubject>.None };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.invalid",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.Unauthorized>();
    }

    [Fact]
    public async Task Authorize_with_a_token_granting_neither_the_read_scope_nor_the_bundle_returns_Forbidden()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("user-id", ["openid", "profile"])),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.scoped-wrong",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.Forbidden>();
    }

    [Fact]
    public async Task Authorize_for_an_Offline_stream_returns_StreamUnavailable()
    {
        CameraIdentifier camera = SomeCamera();
        InMemoryStreamRepository streams = new();
        Domain.Stream.Stream stream = new StreamBuilder()
            .ForCamera(camera)
            .ProvisionedBy(AnAdmin)
            .At(FixedMoment)
            .Build();
        stream.ReportHealthy(TranscodeMode.Passthrough, new FixedClock(FixedMoment));
        stream.ReportDegraded(StreamError.From("source unreachable"), new FixedClock(FixedMoment.AddSeconds(15)));
        stream.ReportOffline(StreamError.From("retry exhausted"), new FixedClock(FixedMoment.AddMinutes(5)));
        streams.Add(stream);
        await streams.SaveAsync(CancellationToken.None);

        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("admin-id", ["sse.management"])),
        };
        AuthorizeWhepCommandHandler handler = new(validator, streams, NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(camera),
                "Bearer.xyz",
                Option<MediaMtxAction>.Some(MediaMtxAction.Read),
                ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.StreamUnavailable>();
    }

    /// <summary>
    /// A kiosk's own token, on the action a kiosk never asks for. The read scope
    /// is the scope a viewer holds; nothing in this product publishes through
    /// this hook, so holding it must not admit a publish.
    /// </summary>
    [Fact]
    public async Task Authorize_a_publish_with_the_read_scope_is_refused()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                Option<MediaMtxAction>.Some(MediaMtxAction.Publish),
                ReportedMediaMtxAction.TryFrom("publish")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionNotPermitted>();
    }

    /// <summary>
    /// The grandfathered bundle is the broadest token that reaches this hook.
    /// Breadth of scope is not the question a publish asks — the action is
    /// refused for everyone, so an admin token is refused too.
    /// </summary>
    [Fact]
    public async Task Authorize_a_publish_with_the_grandfathered_bundle_is_refused()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("admin-id", ["openid", "sse.management"])),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.xyz",
                Option<MediaMtxAction>.Some(MediaMtxAction.Publish),
                ReportedMediaMtxAction.TryFrom("publish")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionNotPermitted>();
    }

    /// <summary>
    /// The ordering assertion. An empty token would otherwise answer
    /// <c>401</c> — and a <c>401</c> is how an auth server asks the client to
    /// come back with credentials, an invitation no credential can satisfy for
    /// an action this hook never grants. So the action is answered first and the
    /// refusal is <c>403</c>, terminal.
    /// </summary>
    [Fact]
    public async Task Authorize_a_publish_with_no_token_is_refused_on_the_action_not_the_token()
    {
        AuthorizeWhepCommandHandler handler = new(new FakeWhepAuthValidator(), new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "",
                Option<MediaMtxAction>.Some(MediaMtxAction.Publish),
                ReportedMediaMtxAction.TryFrom("publish")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionNotPermitted>();
    }

    /// <summary>
    /// No action named at all — what a MediaMTX that stopped sending the field
    /// would post. Fail closed: an absent action is refused, not assumed to be
    /// the read it usually is.
    /// </summary>
    [Fact]
    public async Task Authorize_with_no_action_is_refused()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                Option<MediaMtxAction>.None,
                Option<ReportedMediaMtxAction>.None),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();
    }

    /// <summary>
    /// <c>api</c> is excluded from the hook by <c>mediamtx.yml:46-49</c>, so it
    /// reaches the command as absent. This is the shape the day an exclusion is
    /// deleted takes, and it is refused.
    /// </summary>
    [Fact]
    public async Task Authorize_with_an_unrecognised_action_is_refused()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                MediaMtxAction.TryFrom("api"),
                ReportedMediaMtxAction.TryFrom("api")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();
    }

    /// <summary>
    /// <b>Spec 115.</b> Failing closed is defensible only because the outage is
    /// meant to be recoverable in minutes, and it is not if the operator cannot
    /// learn which value to look up. The refusal itself is asserted first, so a
    /// failure here can only be the diagnosis and never the decision.
    /// </summary>
    [Fact]
    public async Task Authorize_with_an_action_this_build_does_not_recognise_names_the_value_that_arrived()
    {
        CapturingLogger<AuthorizeWhepCommandHandler> logger = new();
        AuthorizeWhepCommandHandler handler = new(AKioskValidator(), new InMemoryStreamRepository(), logger);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                MediaMtxAction.TryFrom("stream"),
                ReportedMediaMtxAction.TryFrom("stream")),
            CancellationToken.None);

        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();
        logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("stream");
    }

    /// <summary>
    /// <b>Spec 115.</b> "The field was absent" and "the field held something we
    /// do not know" are two different things to go and look at. One hedged
    /// message that covers both tells an operator neither.
    /// </summary>
    [Fact]
    public async Task An_absent_action_and_an_unrecognised_one_are_refused_with_different_messages()
    {
        MediaMtxPath path = MediaMtxPath.For(SomeCamera());
        CapturingLogger<AuthorizeWhepCommandHandler> absentLogger = new();
        CapturingLogger<AuthorizeWhepCommandHandler> reportedLogger = new();

        await new AuthorizeWhepCommandHandler(AKioskValidator(), new InMemoryStreamRepository(), absentLogger)
            .HandleAsync(
                new AuthorizeWhepCommand(path, "Bearer.kiosk", Option<MediaMtxAction>.None, Option<ReportedMediaMtxAction>.None),
                CancellationToken.None);

        await new AuthorizeWhepCommandHandler(AKioskValidator(), new InMemoryStreamRepository(), reportedLogger)
            .HandleAsync(
                new AuthorizeWhepCommand(path, "Bearer.kiosk", MediaMtxAction.TryFrom("stream"), ReportedMediaMtxAction.TryFrom("stream")),
                CancellationToken.None);

        string absent = absentLogger.Entries.ShouldHaveSingleItem().Message;
        string reported = reportedLogger.Entries.ShouldHaveSingleItem().Message;

        absent.ShouldNotBe(reported);
        absent.ShouldContain("absent");
        reported.ShouldNotContain("absent");
    }

    /// <summary>
    /// <b>Spec 115, security review F1.</b> The case that defeats the whole
    /// point in one step: MediaMTX ships a release sending <c>action: ""</c>
    /// rather than dropping the field. An unquoted placeholder renders a blank
    /// where the value should be, which reads as "no action was named" — so the
    /// operator hunts release notes for a field that never moved.
    ///
    /// <para>
    /// <c>ReportedMediaMtxActionTests</c> proves <c>TryFrom("")</c> is
    /// <c>Some</c>. Nothing proved what that then <em>reads</em> like, which is
    /// where the ambiguity came back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_empty_action_field_is_refused_differently_from_an_absent_one()
    {
        MediaMtxPath path = MediaMtxPath.For(SomeCamera());
        CapturingLogger<AuthorizeWhepCommandHandler> absentLogger = new();
        CapturingLogger<AuthorizeWhepCommandHandler> emptyLogger = new();

        await new AuthorizeWhepCommandHandler(AKioskValidator(), new InMemoryStreamRepository(), absentLogger)
            .HandleAsync(
                new AuthorizeWhepCommand(path, "Bearer.kiosk", Option<MediaMtxAction>.None, Option<ReportedMediaMtxAction>.None),
                CancellationToken.None);

        Result<MediaMtxPath, AuthorizeWhepError> result =
            await new AuthorizeWhepCommandHandler(AKioskValidator(), new InMemoryStreamRepository(), emptyLogger)
                .HandleAsync(
                    new AuthorizeWhepCommand(path, "Bearer.kiosk", MediaMtxAction.TryFrom(""), ReportedMediaMtxAction.TryFrom("")),
                    CancellationToken.None);

        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();

        string absent = absentLogger.Entries.ShouldHaveSingleItem().Message;
        string empty = emptyLogger.Entries.ShouldHaveSingleItem().Message;

        // The quotes are the fix: they make "a value arrived and it was empty"
        // legible as itself rather than as a gap in the sentence.
        empty.ShouldContain("action ''");
        empty.ShouldNotContain("absent");
        empty.ShouldNotBe(absent);
    }

    /// <summary>
    /// The same trap one step along: a blank-but-present value. Quoting is what
    /// keeps trailing whitespace visible at all.
    /// </summary>
    [Fact]
    public async Task A_blank_action_field_still_shows_what_arrived()
    {
        CapturingLogger<AuthorizeWhepCommandHandler> logger = new();
        AuthorizeWhepCommandHandler handler = new(AKioskValidator(), new InMemoryStreamRepository(), logger);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                MediaMtxAction.TryFrom("   "),
                ReportedMediaMtxAction.TryFrom("   ")),
            CancellationToken.None);

        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();
        logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("action '   '");
    }

    /// <summary>
    /// <b>Spec 115.</b> MediaMTX composes the hook body, so the value is only
    /// indirectly attacker-influenced — but an unbounded one in a log is a
    /// finding whoever put it there, and the whole value must not reach the sink.
    /// </summary>
    [Fact]
    public async Task A_value_longer_than_the_cap_is_truncated_in_the_refusal()
    {
        string raw = new('a', 200);
        CapturingLogger<AuthorizeWhepCommandHandler> logger = new();
        AuthorizeWhepCommandHandler handler = new(AKioskValidator(), new InMemoryStreamRepository(), logger);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                MediaMtxAction.TryFrom(raw),
                ReportedMediaMtxAction.TryFrom(raw)),
            CancellationToken.None);

        result.Error.ShouldBeOfType<AuthorizeWhepError.ActionUnknown>();

        string message = logger.Entries.ShouldHaveSingleItem().Message;
        message.ShouldNotContain(raw);
        message.ShouldContain(new string('a', ReportedMediaMtxAction.MaximumLength) + "…");
    }

    /// <summary>
    /// Reading a recording is a read. Admitted on the same scope, so narrowing
    /// the hook to <c>read</c> alone would be a regression rather than a fix.
    /// </summary>
    [Fact]
    public async Task Authorize_a_playback_with_the_read_scope_returns_success()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(
                MediaMtxPath.For(SomeCamera()),
                "Bearer.kiosk",
                Option<MediaMtxAction>.Some(MediaMtxAction.Playback),
                ReportedMediaMtxAction.TryFrom("playback")),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// The over-correction guard, and the only action a wall actually sends. It
    /// passes today and must still pass afterwards: a hook that refuses
    /// everything satisfies every refusal test above and takes every wall dark.
    /// The answer names the path it was asked about, so an admission cannot be
    /// mistaken for an admission of something else.
    /// </summary>
    [Fact]
    public async Task Authorize_a_read_with_the_read_scope_returns_success()
    {
        FakeWhepAuthValidator validator = new()
        {
            Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)),
        };
        AuthorizeWhepCommandHandler handler = new(validator, new InMemoryStreamRepository(), NullLogger<AuthorizeWhepCommandHandler>.Instance);
        MediaMtxPath path = MediaMtxPath.For(SomeCamera());

        Result<MediaMtxPath, AuthorizeWhepError> result = await handler.HandleAsync(
            new AuthorizeWhepCommand(path, "Bearer.kiosk", Option<MediaMtxAction>.Some(MediaMtxAction.Read), ReportedMediaMtxAction.TryFrom("read")),
            CancellationToken.None);

        result.Value.ShouldBe(path);
    }

    private static CameraIdentifier SomeCamera() => CameraIdentifier.From(Guid.CreateVersion7());

    private static FakeWhepAuthValidator AKioskValidator() =>
        new() { Subject = Option<WhepAuthSubject>.Some(new WhepAuthSubject("kiosk-id", AKioskPersona)) };
}

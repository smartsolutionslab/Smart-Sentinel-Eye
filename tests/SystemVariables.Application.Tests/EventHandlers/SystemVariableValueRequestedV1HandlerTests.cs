using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.Shared.Contracts;
using SmartSentinelEye.Shared.Contracts.SystemVariables;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.SystemVariables.Application.Commands.Handlers;
using SmartSentinelEye.SystemVariables.Application.EventHandlers;
using SmartSentinelEye.SystemVariables.Application.Tests.Fakes;
using SmartSentinelEye.SystemVariables.Domain.Tests.Variable.Builders;
using SmartSentinelEye.SystemVariables.Domain.Variable;

namespace SmartSentinelEye.SystemVariables.Application.Tests.EventHandlers;

public class SystemVariableValueRequestedV1HandlerTests
{
    private static readonly DateTimeOffset Moment =
        DateTimeOffset.Parse("2026-05-28T08:14:33Z", CultureInfo.InvariantCulture);
    private static readonly EventMetadata TestMetadata = MetadataFor("munich");

    /// <summary>
    /// Spec 014 made the fab load-bearing: a request without one applies to
    /// nothing. These tests are about dedup and dispatch, so they carry a fab
    /// and the fab-specific cases name their own.
    /// </summary>
    private static EventMetadata MetadataFor(string fab) => new(
        Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        DateTimeOffset.Parse("2026-05-29T08:00:00Z", CultureInfo.InvariantCulture),
        fab,
        null);

    private sealed class FakeDedupStore : IVariableValueRequestDedupStore
    {
        public HashSet<(string, string, Guid)> Reserved { get; } = [];

        // The fab is part of the key here exactly as in production. Keyed on
        // the pair alone, two fabs reacting to one ingested event would look
        // like a redelivery and the second fab's change would vanish — the bug
        // being fixed, reproduced inside the thing meant to detect it.
        public Task<bool> TryReserveAsync(
            FabIdentifier fab, string variableName, Guid causingEventIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(Reserved.Add((fab.Value, variableName, causingEventIdentifier)));
    }

    private static SetVariableValueCommandHandler BuildSetHandler(InMemoryVariableRepository repo) =>
        new(repo, new FakeClock(Moment),
            NullLogger<SetVariableValueCommandHandler>.Instance);

    [Fact]
    public async Task First_delivery_dispatches_SetVariableValue_against_the_existing_handler()
    {
        InMemoryVariableRepository repo = new();
        Variable oeeLine1 = new VariableBuilder()
            .Named("oeeLine1").OfType(VariableType.Number).At(Moment).Build();
        repo.Add(oeeLine1);

        FakeDedupStore dedup = new();
        SystemVariableValueRequestedV1Handler handler = new(
            dedup, BuildSetHandler(repo), new RecordingLatencyBudget(),
            NullLogger<SystemVariableValueRequestedV1Handler>.Instance);

        await handler.Handle(
            new SystemVariableValueRequestedV1("oeeLine1", "82.5", Moment, Guid.CreateVersion7(), Metadata: TestMetadata),
            CancellationToken.None);

        oeeLine1.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(82.5);
    }

    [Fact]
    public async Task Second_delivery_with_the_same_causing_event_is_a_no_op()
    {
        InMemoryVariableRepository repo = new();
        Variable oeeLine1 = new VariableBuilder()
            .Named("oeeLine1").OfType(VariableType.Number).At(Moment).Build();
        repo.Add(oeeLine1);

        FakeDedupStore dedup = new();
        SystemVariableValueRequestedV1Handler handler = new(
            dedup, BuildSetHandler(repo), new RecordingLatencyBudget(),
            NullLogger<SystemVariableValueRequestedV1Handler>.Instance);

        Guid causing = Guid.CreateVersion7();
        await handler.Handle(
            new SystemVariableValueRequestedV1("oeeLine1", "82.5", Moment, causing, Metadata: TestMetadata),
            CancellationToken.None);
        await handler.Handle(
            new SystemVariableValueRequestedV1("oeeLine1", "999", Moment, causing, Metadata: TestMetadata),
            CancellationToken.None);

        // Second delivery's value (999) MUST NOT win.
        oeeLine1.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(82.5);
    }

    /// <summary>
    /// <b>The two halves of this test are not equally strong, and saying which
    /// is which is the point</b> (#2151, census detection 4).
    ///
    /// <para>
    /// <c>repo.Variables.ShouldBeEmpty()</c> is true by construction: the
    /// handler dispatches <c>SetVariableValueCommand</c>, and setting a value
    /// never calls <c>IVariableRepository.Add</c>. There is no create path to
    /// get wrong, so no double can make that line red — it is a
    /// <b>forward-looking regression guard</b> on a create path nobody has
    /// written, and the double is already able to catch one (the repository is
    /// the same seam <c>DefineVariableCommandHandler</c> creates through, and
    /// this fake records every <c>Add</c>). It stays for that reason, not
    /// because it proves anything today.
    /// </para>
    ///
    /// <para>
    /// The half that <i>can</i> fail is the logging, and it was previously
    /// unasserted: the handler was handed a <c>NullLogger</c>, so the "is
    /// logged" in this test's own name was carried by nothing at all. A drop
    /// that says nothing — or that says something without naming the value that
    /// failed — is indistinguishable from automation quietly having stopped,
    /// which is the failure mode this file's neighbours were written for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Invalid_variable_name_is_logged_and_dropped()
    {
        InMemoryVariableRepository repo = new();
        FakeDedupStore dedup = new();
        CapturingLogger<SystemVariableValueRequestedV1Handler> logger = new();
        SystemVariableValueRequestedV1Handler handler = new(
            dedup, BuildSetHandler(repo), new RecordingLatencyBudget(), logger);

        // The handler should not throw; it logs + returns.
        await handler.Handle(
            new SystemVariableValueRequestedV1("1bad", "1", Moment, Guid.CreateVersion7(), Metadata: TestMetadata),
            CancellationToken.None);

        (LogLevel Level, string Message, Exception? Exception) entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(
            "1bad",
            customMessage: "the rejected name is the only thing that tells the Automation "
            + "team which rule authored it; a line without it sends nobody anywhere");

        repo.Variables.ShouldBeEmpty(
            "a value request must never bring a variable into existence — the create path "
            + "is DefineVariable's alone. Vacuous today (see the remarks): kept as the guard "
            + "on the day someone adds one");
    }

    // ---- spec 014 T020: the downstream effect, not merely "nothing threw" ----

    [Fact]
    public async Task A_munich_request_changes_munichs_variable_and_leaves_dresdens_alone()
    {
        InMemoryVariableRepository repo = new();
        Variable munich = Seed(repo, "munich", 1);
        Variable dresden = Seed(repo, "dresden", 2);

        SystemVariableValueRequestedV1Handler handler = BuildHandler(repo);

        await handler.Handle(
            new SystemVariableValueRequestedV1(
                "oeeLine1", "82.5", Moment, Guid.CreateVersion7(), MetadataFor("munich")),
            CancellationToken.None);

        munich.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(82.5);
        // The assertion that matters: a still-global write would have hit
        // whichever row the name matched, and passing on munich alone would
        // not notice.
        dresden.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(2);
    }

    [Fact]
    public async Task A_request_carrying_no_fab_changes_nothing()
    {
        InMemoryVariableRepository repo = new();
        Variable munich = Seed(repo, "munich", 1);

        SystemVariableValueRequestedV1Handler handler = BuildHandler(repo);

        await handler.Handle(
            new SystemVariableValueRequestedV1(
                "oeeLine1", "82.5", Moment, Guid.CreateVersion7(), MetadataFor(null!)),
            CancellationToken.None);

        munich.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_naming_another_fabs_variable_changes_nothing()
    {
        InMemoryVariableRepository repo = new();
        Variable munich = Seed(repo, "munich", 1);

        SystemVariableValueRequestedV1Handler handler = BuildHandler(repo);

        // dresden holds no oeeLine1; munich's must not stand in for it.
        await handler.Handle(
            new SystemVariableValueRequestedV1(
                "oeeLine1", "82.5", Moment, Guid.CreateVersion7(), MetadataFor("dresden")),
            CancellationToken.None);

        munich.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(1);
    }

    [Fact]
    public async Task Two_fabs_reacting_to_one_ingested_event_both_apply()
    {
        // The dedup key includes the fab, so a shared causing event identifier
        // is not a redelivery. Without this the second fab's legitimate change
        // is swallowed — the normal case once both fabs run rules on one
        // trigger, not an edge one.
        InMemoryVariableRepository repo = new();
        Variable munich = Seed(repo, "munich", 1);
        Variable dresden = Seed(repo, "dresden", 2);

        SystemVariableValueRequestedV1Handler handler = BuildHandler(repo);
        Guid causing = Guid.CreateVersion7();

        await handler.Handle(
            new SystemVariableValueRequestedV1("oeeLine1", "10", Moment, causing, MetadataFor("munich")),
            CancellationToken.None);
        await handler.Handle(
            new SystemVariableValueRequestedV1("oeeLine1", "20", Moment, causing, MetadataFor("dresden")),
            CancellationToken.None);

        munich.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(10);
        dresden.Value.ShouldBeOfType<VariableValue.NumberValue>().Value.ShouldBe(20);
    }

    // ---- spec 014 T021: the miss is diagnosable, not merely harmless ----

    [Fact]
    public async Task A_cross_fab_miss_is_logged_with_both_the_fab_and_the_name()
    {
        InMemoryVariableRepository repo = new();
        Seed(repo, "munich", 1);

        CapturingLogger<SystemVariableValueRequestedV1Handler> logger = new();
        SystemVariableValueRequestedV1Handler handler = new(
            new FakeDedupStore(), BuildSetHandler(repo), new RecordingLatencyBudget(), logger);

        await handler.Handle(
            new SystemVariableValueRequestedV1(
                "oeeLine1", "82.5", Moment, Guid.CreateVersion7(), MetadataFor("dresden")),
            CancellationToken.None);

        // The handler fails closed either way, so "nothing changed" cannot
        // tell this from a typo. Both identifiers must be present or the
        // operator cannot tell which fab was missing what.
        (LogLevel Level, string Message, Exception? Exception) entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("dresden");
        entry.Message.ShouldContain("oeeLine1");
    }

    [Fact]
    public async Task A_missing_fab_is_not_logged_as_a_malformed_name()
    {
        // #1252 hid for a release behind a shared message. These two drops have
        // different causes and different fixes, so they must not read alike.
        InMemoryVariableRepository repo = new();
        Seed(repo, "munich", 1);

        CapturingLogger<SystemVariableValueRequestedV1Handler> noFab = new();
        await new SystemVariableValueRequestedV1Handler(new FakeDedupStore(), BuildSetHandler(repo), new RecordingLatencyBudget(), noFab)
            .Handle(
                new SystemVariableValueRequestedV1(
                    "oeeLine1", "1", Moment, Guid.CreateVersion7(), MetadataFor(null!)),
                CancellationToken.None);

        CapturingLogger<SystemVariableValueRequestedV1Handler> badName = new();
        await new SystemVariableValueRequestedV1Handler(new FakeDedupStore(), BuildSetHandler(repo), new RecordingLatencyBudget(), badName)
            .Handle(
                new SystemVariableValueRequestedV1(
                    "1bad", "1", Moment, Guid.CreateVersion7(), TestMetadata),
                CancellationToken.None);

        noFab.Entries.ShouldHaveSingleItem().Message
            .ShouldNotBe(badName.Entries.ShouldHaveSingleItem().Message);
    }

    private static Variable Seed(InMemoryVariableRepository repo, string fab, double value)
    {
        Variable variable = new VariableBuilder()
            .WithFab(fab)
            .Named("oeeLine1")
            .OfType(VariableType.Number)
            .WithInitialValue(new VariableValue.NumberValue(value))
            .At(Moment)
            .Build();
        repo.Add(variable);

        return variable;
    }

    /// <summary>
    /// Spec 025 FR-005 / SC-006. The leg is only measurable when the effect has
    /// a plant-floor root; an operator setting a value by hand has none.
    ///
    /// <para>
    /// The failure this prevents is not a crash. It is a <c>0 ms</c> in the
    /// distribution — a perfect score for a journey nobody timed, which would
    /// drag a p99 down and make a breached budget look met. Silence that reads
    /// as success is the shape this programme keeps meeting.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_effect_with_no_plant_floor_root_is_not_timed()
    {
        InMemoryVariableRepository repo = new();
        Seed(repo, "munich", 1);

        RecordingLatencyBudget latency = new();
        SystemVariableValueRequestedV1Handler handler = new(
            new FakeDedupStore(), BuildSetHandler(repo), latency,
            NullLogger<SystemVariableValueRequestedV1Handler>.Instance);

        // MetadataFor leaves RootIngestedAt null, which is what a message
        // published before spec 025 — or by a path with no root — carries.
        await handler.Handle(
            new SystemVariableValueRequestedV1(
                "oeeLine1", "82.5", Moment, Guid.CreateVersion7(), MetadataFor("munich")),
            CancellationToken.None);

        latency.Recorded.ShouldHaveSingleItem().ShouldBeNull(
            "the handler must hand the absent moment to the budget and let it decide, "
            + "rather than substituting a zero that would read as an instant journey");
    }

    private static SystemVariableValueRequestedV1Handler BuildHandler(InMemoryVariableRepository repo) =>
        new(new FakeDedupStore(), BuildSetHandler(repo), new RecordingLatencyBudget(),
            NullLogger<SystemVariableValueRequestedV1Handler>.Instance);
}

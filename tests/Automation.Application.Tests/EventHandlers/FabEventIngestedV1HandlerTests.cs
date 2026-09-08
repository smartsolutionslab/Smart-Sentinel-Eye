using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.Automation.Application.Evaluation;
using SmartSentinelEye.Automation.Application.EventHandlers;
using SmartSentinelEye.Automation.Application.Tests.Fakes;
using SmartSentinelEye.Automation.Domain.Rule;
using SmartSentinelEye.Automation.Domain.Tests.Rule;
using SmartSentinelEye.Shared.Contracts;
using SmartSentinelEye.Shared.Contracts.EventIngestion;
using SmartSentinelEye.Shared.Contracts.LayoutComposition;
using SmartSentinelEye.Shared.Contracts.SystemVariables;
using SmartSentinelEye.Shared.Kernel;
using RuleAggregate = SmartSentinelEye.Automation.Domain.Rule.Rule;

namespace SmartSentinelEye.Automation.Application.Tests.EventHandlers;

public class FabEventIngestedV1HandlerTests
{
    private static readonly DateTimeOffset BaseMoment =
        DateTimeOffset.Parse("2026-05-28T08:00:00Z", CultureInfo.InvariantCulture);
    private static readonly EventMetadata TestMetadata = new(
        Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        DateTimeOffset.Parse("2026-05-29T08:00:00Z", CultureInfo.InvariantCulture),
        null,
        null);

    private static FabEventIngestedV1 PlcCycleStart(Guid? causing = null, string fab = "munich") =>
        new(
            EventIdentifier: causing ?? Guid.CreateVersion7(),
            Fab: fab,
            Source: "plc",
            Device: "station-4",
            Kind: "PlcCycleStart",
            OccurredAt: BaseMoment,
            IngestedAt: BaseMoment.AddSeconds(0.04),
            Payload: "{\"cycleTime\":27}",
            Metadata: TestMetadata);

    private static RuleAggregate ActiveSetVariableRule(
        string predicate, string valueExpression, string fab = "munich", string name = "test-rule")
    {
        RuleAggregate rule = new RuleBuilder()
            .WithFab(fab)
            .WithName(name)
            .WithPredicate(predicate)
            .WithAction(RuleAction.SetVariableValue.From("oeeLine1", valueExpression))
            .WithClock(BaseMoment)
            .Build();
        rule.Publish(new FakeClock(BaseMoment.AddMinutes(1)));
        return rule;
    }

    private static RuleAggregate ActiveHighlightRule(
        string name, Guid overlay, int durationMs, DateTimeOffset createdAt)
    {
        RuleAggregate rule = new RuleBuilder()
            .WithName(name)
            .WithAction(RuleAction.HighlightOverlay.From(overlay, durationMs))
            .WithClock(createdAt)
            .Build();
        rule.Publish(new FakeClock(createdAt.AddMinutes(1)));
        return rule;
    }

    [Fact]
    public async Task Matching_event_publishes_SystemVariableValueRequestedV1_with_the_causing_event_id()
    {
        InMemoryRuleCache cache = new();
        cache.Upsert(ActiveSetVariableRule(
            "$.payload.cycleTime <= 30",
            "100 - $.payload.cycleTime * 2"));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = new(
            new RuleEvaluator(cache, NullLogger<RuleEvaluator>.Instance),
            bus,
            new FakeClock(BaseMoment.AddSeconds(0.05)),
            NullLogger<FabEventIngestedV1Handler>.Instance);

        FabEventIngestedV1 ingested = PlcCycleStart();
        await handler.Handle(ingested, CancellationToken.None);

        SystemVariableValueRequestedV1 published = bus.Published
            .OfType<SystemVariableValueRequestedV1>()
            .ShouldHaveSingleItem();
        published.Name.ShouldBe("oeeLine1");
        published.Value.ShouldBe("46");
        published.CausingEventIdentifier.ShouldBe(ingested.EventIdentifier);
    }

    [Fact]
    public async Task HighlightOverlay_action_publishes_OverlayHighlightRequestedV1()
    {
        Guid overlay = Guid.CreateVersion7();
        InMemoryRuleCache cache = new();

        RuleAggregate rule = new RuleBuilder()
            .WithName("highlight-rule")
            .WithAction(RuleAction.HighlightOverlay.From(overlay, 10_000))
            .WithClock(BaseMoment)
            .Build();
        rule.Publish(new FakeClock(BaseMoment.AddMinutes(1)));
        cache.Upsert(rule);

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = new(
            new RuleEvaluator(cache, NullLogger<RuleEvaluator>.Instance),
            bus,
            new FakeClock(BaseMoment.AddSeconds(0.05)),
            NullLogger<FabEventIngestedV1Handler>.Instance);

        await handler.Handle(PlcCycleStart(), CancellationToken.None);

        OverlayHighlightRequestedV1 published = bus.Published
            .OfType<OverlayHighlightRequestedV1>()
            .ShouldHaveSingleItem();
        published.OverlayIdentifier.ShouldBe(overlay);
        published.DurationMs.ShouldBe(10_000);
    }

    [Fact]
    public async Task Two_highlight_actions_on_the_same_overlay_both_publish()
    {
        Guid overlay = Guid.CreateVersion7();
        InMemoryRuleCache cache = new();
        // Both survive the upsert: InMemoryRuleCache removes by rule identifier,
        // and Rule.Create mints a fresh one per Build. The names are only labels.
        cache.Upsert(ActiveHighlightRule("highlight-rule-a", overlay, 5_000, BaseMoment));
        cache.Upsert(ActiveHighlightRule(
            "highlight-rule-b", overlay, 12_000, BaseMoment.AddMinutes(5)));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = HandlerFor(cache, bus);

        FabEventIngestedV1 ingested = PlcCycleStart();
        await handler.Handle(ingested, CancellationToken.None);

        // Both windows ride the bus; the kiosk ORs them by later expiry
        // (CellPage.test.tsx:491), so the producer emits both rather than
        // picking. The shared CausingEventIdentifier is what tells "two
        // rules, one event" from "one rule, two events".
        OverlayHighlightRequestedV1[] published = bus.Published
            .OfType<OverlayHighlightRequestedV1>().ToArray();
        published.Length.ShouldBe(2);
        published.Select(p => p.OverlayIdentifier).ShouldBe([overlay, overlay]);
        published.Select(p => p.DurationMs).ShouldBe([5_000, 12_000]);
        published.Select(p => p.CausingEventIdentifier)
            .ShouldBe([ingested.EventIdentifier, ingested.EventIdentifier]);
    }

    [Fact]
    public async Task No_matching_rule_publishes_nothing()
    {
        InMemoryRuleCache cache = new();
        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = new(
            new RuleEvaluator(cache, NullLogger<RuleEvaluator>.Instance),
            bus,
            new FakeClock(BaseMoment),
            NullLogger<FabEventIngestedV1Handler>.Instance);

        await handler.Handle(PlcCycleStart(), CancellationToken.None);

        bus.Published.ShouldBeEmpty();
    }

    // ---- spec 013: the handler acts only on the originating fab (#1252) ----
    //
    // These assert the *published messages*, not that evaluation returned
    // empty. A handler that scoped evaluation correctly and then published
    // anyway would pass the weaker check while still writing another fab's
    // value — and a published SystemVariableValueRequestedV1 is what actually
    // changes state downstream.

    [Fact]
    public async Task An_event_publishes_nothing_for_a_rule_belonging_to_another_fab()
    {
        InMemoryRuleCache cache = new();
        cache.Upsert(ActiveSetVariableRule(
            "$.payload.cycleTime <= 30", "1", fab: "dresden", name: "dresden-rule"));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = HandlerFor(cache, bus);

        await handler.Handle(PlcCycleStart(fab: "munich"), CancellationToken.None);

        bus.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_originating_fabs_rule_produces_a_published_change()
    {
        InMemoryRuleCache cache = new();
        cache.Upsert(ActiveSetVariableRule(
            "$.payload.cycleTime <= 30", "100 - $.payload.cycleTime * 2",
            fab: "munich", name: "munich-rule"));
        cache.Upsert(ActiveSetVariableRule(
            "$.payload.cycleTime <= 30", "999", fab: "dresden", name: "dresden-rule"));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = HandlerFor(cache, bus);

        await handler.Handle(PlcCycleStart(fab: "munich"), CancellationToken.None);

        SystemVariableValueRequestedV1 published = bus.Published
            .OfType<SystemVariableValueRequestedV1>()
            .ShouldHaveSingleItem();
        published.Value.ShouldBe("46");
        // 999 is the dresden rule's value; seeing it here would mean another
        // fab's automation decided a munich variable.
        published.Value.ShouldNotBe("999");
    }

    [Fact]
    public async Task A_change_is_attributed_to_the_fab_the_event_came_from()
    {
        InMemoryRuleCache cache = new();
        cache.Upsert(ActiveSetVariableRule(
            "$.payload.cycleTime <= 30", "1", fab: "dresden", name: "dresden-rule"));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = HandlerFor(cache, bus);

        await handler.Handle(PlcCycleStart(fab: "dresden"), CancellationToken.None);

        SystemVariableValueRequestedV1 published = bus.Published
            .OfType<SystemVariableValueRequestedV1>()
            .ShouldHaveSingleItem();
        published.Metadata.Fab.ShouldBe("dresden");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAFab")]
    public async Task An_event_without_a_usable_fab_publishes_nothing(string fab)
    {
        // Fails closed. Falling back to evaluating everything is the defect
        // itself, so an unusable fab must trigger nothing rather than all.
        InMemoryRuleCache cache = new();
        cache.Upsert(ActiveSetVariableRule("$.payload.cycleTime <= 30", "1"));

        FakeEventBus bus = new();
        FabEventIngestedV1Handler handler = HandlerFor(cache, bus);

        await handler.Handle(PlcCycleStart(fab: fab), CancellationToken.None);

        bus.Published.ShouldBeEmpty();
    }

    /// <summary>
    /// A fab that will not parse silences every rule for that fab, and the
    /// handler fails closed either way — so "published nothing" cannot tell a
    /// diagnosable failure from a silent one. The value has to reach the log,
    /// or the only way to find this is to notice automation has stopped.
    /// </summary>
    [Fact]
    public async Task An_unparseable_fab_is_logged_with_the_value_that_failed()
    {
        CapturingLogger<FabEventIngestedV1Handler> logger = new();
        FabEventIngestedV1Handler handler = new(
            new RuleEvaluator(new InMemoryRuleCache(), NullLogger<RuleEvaluator>.Instance),
            new FakeEventBus(),
            new FakeClock(BaseMoment),
            logger);

        await handler.Handle(PlcCycleStart(fab: "NotAFab"), CancellationToken.None);

        (LogLevel Level, string Message, Exception? Exception) entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("NotAFab");
        entry.Exception.ShouldBeOfType<ArgumentException>();
    }

    /// <summary>
    /// The other half: an event that carries no fab at all is a publisher not
    /// stamping one, which is a different problem with a different fix, so it
    /// must not share a message with the case above.
    /// </summary>
    [Fact]
    public async Task An_absent_fab_is_logged_distinctly_from_one_that_will_not_parse()
    {
        CapturingLogger<FabEventIngestedV1Handler> absent = new();
        CapturingLogger<FabEventIngestedV1Handler> unparseable = new();

        // The same event identifier on both, or the rendered messages differ
        // on the id alone and this passes however identical the templates are.
        Guid causing = Guid.CreateVersion7();
        await HandlerWith(absent).Handle(PlcCycleStart(causing, ""), CancellationToken.None);
        await HandlerWith(unparseable).Handle(PlcCycleStart(causing, "NotAFab"), CancellationToken.None);

        absent.Entries.ShouldHaveSingleItem().Message
            .ShouldNotBe(unparseable.Entries.ShouldHaveSingleItem().Message);
    }

    private static FabEventIngestedV1Handler HandlerWith(ILogger<FabEventIngestedV1Handler> logger) =>
        new(new RuleEvaluator(new InMemoryRuleCache(), NullLogger<RuleEvaluator>.Instance),
            new FakeEventBus(),
            new FakeClock(BaseMoment),
            logger);

    private static FabEventIngestedV1Handler HandlerFor(InMemoryRuleCache cache, FakeEventBus bus) =>
        new(new RuleEvaluator(cache, NullLogger<RuleEvaluator>.Instance),
            bus,
            new FakeClock(BaseMoment.AddSeconds(0.05)),
            NullLogger<FabEventIngestedV1Handler>.Instance);
}

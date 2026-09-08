# Spec 105 — Two highlight rules on one overlay, both published

**Issue:** #729 (`[T079]`) · **Branch:** `test/729-two-highlights-on-one-overlay`
**Worktree:** `D:/Github/wt-729` · **Base:** `08782b7c`
**Phase:** 1 (Specify) · **Date:** 2026-09-08
**Feature bucket:** spec `specs/007-automation/`, story **US4**. T079 is
`specs/007-automation/tasks.md:174`, unchecked, and reads verbatim:
"`RuleEvaluatorTests.Two_highlight_actions_on_the_same_overlay_both_publish` —
both V1s ride the outbox; kiosk handles the OR."
**ADRs:** ADR-0037 (phases and gates), ADR-0053 (sentence-style test names),
ADR-0054 (hand-written builders, no AutoFixture), ADR-0052 (xUnit + Shouldly),
ADR-0109 (disjoint files — neither touched file is a contention file),
ADR-0144 (the lane may not weaken a gate and may not skip phase 4a).
**Constitution:** §Testing (two obligations). §IV latency: **N/A** — test-only,
no production code changes, no leg of the event→overlay path is touched.

## Why a spec at all

Because the premise needed checking and the checking produced a load-bearing
result: **the level the issue names is not the level that pins its claim.**
That belongs somewhere durable. Everything else here is two tests, and this
document is deliberately short.

## The issue as filed, and what survives contact with the repository

### Confirmed — every claim holds

- **`RuleEvaluatorTests` has nine tests** and none of them puts two
  `HighlightOverlay` actions in one cache. The two named near-misses are real:
  `HighlightOverlay_action_yields_an_overlay_effect`
  (`tests/Automation.Application.Tests/Evaluation/RuleEvaluatorTests.cs:92`) is
  one rule and one overlay, and
  `Conflict_two_rules_writing_the_same_variable_emit_both_in_createdAt_order`
  (`:114`) is two rules through the *other* action.
- **`FirstPublishPerTypeTests` does use a fresh distinct overlay per round.**
  This was the claim flagged as most likely to be wrong; it is right.
  `HighlightRoundAsync` opens `Guid overlay = Guid.NewGuid();`
  (`tests/Integration.Tests/Automation/FirstPublishPerTypeTests.cs:113`) and is
  called twice, rounds A and D, so the two highlights it publishes name two
  different overlays. It is also `[Trait("Category", "Measurement")]`, so it is
  not a coverage asset in the first place.
- **Every integration highlight scenario is single-rule/single-overlay.**
  `EventReachesItsEffectsTests.An_event_from_the_plant_floor_highlights_the_overlay_a_rule_names`
  activates one rule against one `Guid.CreateVersion7()` overlay.
- **Nothing in the repository builds two highlight rules.** `HighlightOverlay.From`
  appears eleven times across `tests/`; all eleven are single-rule arrangements
  or domain-level construction tests. The two-on-one case is asserted nowhere.
- **The kiosk really is built and tested for it.** `CellPage.test.tsx:491`,
  "Scenario 3: overlapping highlights on the same overlay survive until the
  later expiry".

### The case is reachable, and nothing dedupes

Two rules in one fab may both name the same overlay: rules are keyed by name,
`OverlayIdentifier` carries no uniqueness constraint anywhere in
`src/Automation/Domain/Rule/`, and `InMemoryRuleCache` buckets by
`(fab, source, kind)` and appends. `RuleEvaluator.Evaluate` appends one effect
per matching rule with no set, no distinct, no filter
(`src/Automation/Application/Evaluation/RuleEvaluator.cs:57-61`), and
`FabEventIngestedV1Handler` publishes one V1 per effect in an unguarded
`foreach` (`src/Automation/Application/EventHandlers/FabEventIngestedV1Handler.cs:74-105`).

**So "both publish" is true, and no finding blocks the work.**

### One correction — the issue names the wrong file for its own claim

The evaluator does not publish. It returns `IReadOnlyList<RuleActionEffect>`;
the *handler* turns each effect into an `OverlayHighlightRequestedV1` on the
bus, which is what "rides the outbox" means. A test in `RuleEvaluatorTests` is
blind to a dedupe added in the handler (counterfactual **M2**, `plan.md`), so a
test named `..._both_publish` living there would assert less than its name
claims.

Resolution: **the issue's name is kept verbatim and moved to the file where it
is true.** `Two_highlight_actions_on_the_same_overlay_both_publish` goes to
`FabEventIngestedV1HandlerTests`; the evaluator gets a sibling named for what
it actually returns.

**Two tests — but not because the two levels catch different mutations.**
That reason was written here first and it is true in only one direction. By
mutation detection the evaluator test is *strictly subsumed*: **M1** reddens
both, **M2** reddens only the handler test (`plan.md`), and no plausible
mutation — reversed effect order, a wrong duration, a wrong overlay mapping —
reddens the evaluator test while the handler test stays green. The handler
test is the one that earns its place on detection alone.

The evaluator test earns its place by **symmetry**, which is the reason the
docs originally omitted. The single-rule case is already asserted at both
levels: `RuleEvaluatorTests.HighlightOverlay_action_yields_an_overlay_effect`
beside
`FabEventIngestedV1HandlerTests.HighlightOverlay_action_publishes_OverlayHighlightRequestedV1`.
Adding the two-rule case at one level only would break a pairing that is
already there. Mirroring the existing pattern is the argument — not that two
are nicer than one.

## User story US1 — a producer that emits both highlights (P1)

As the kiosk, when two published rules in my fab both highlight overlay X on
one plant-floor event, I receive **two** `OverlayHighlightRequestedV1` frames,
so my later-expiry OR has something to OR.

### Acceptance scenarios

**Happy — the evaluator emits both.**
```gherkin
Given two Active rules in fab "munich" on trigger ("plc", "PlcCycleStart")
  And rule-a highlights overlay X for 5000 ms, created at T
  And rule-b highlights the same overlay X for 12000 ms, created at T+5min
When a matching PlcCycleStart event is evaluated for "munich"
Then two HighlightOverlay effects are returned
  And both name overlay X
  And they are in createdAt order: 5000 ms then 12000 ms
```

**Happy — both effects reach the bus.**
```gherkin
Given the same two rules and a real RuleEvaluator behind the handler
When FabEventIngestedV1Handler handles the matching event
Then two OverlayHighlightRequestedV1 are published
  And both carry overlay X and the same CausingEventIdentifier
  And their DurationMs are 5000 and 12000
```

**Conflict / bad-request / auth:** none apply. There is no request, no caller
and no boundary — this is an in-process evaluation of already-persisted rules.
The nearest thing to a conflict *is* the happy path: two rules competing for
one overlay is the scenario, and the resolution (both emitted, consumer ORs)
is the asserted behaviour, exactly as the variable sibling resolves its
conflict by emitting both in order.

### Why the durations differ

5000 vs 12000, not 5000 twice. Both V1s are produced from one event, so both
carry the same `requestedAt` and arrive together; "later expiry" can only be
decided by duration. Equal durations would leave the kiosk's Scenario-3 OR
with nothing to discriminate, and would let a mutation that emits one effect
twice pass unnoticed.

### Independent end-to-end test procedure

Not run against a live stack (unit level, no Aspire). The independent check is
the counterfactual in `plan.md`: apply mutation **M1** to `RuleEvaluator.cs`,
observe both new tests red and every existing test green; revert; apply **M2**
to `FabEventIngestedV1Handler.cs`, observe the handler test red and the
evaluator test green. Quote both runs in the PR body.

## Locked choices

xUnit + Shouldly (ADR-0052); `RuleBuilder` and the existing `InMemoryRuleCache`,
`FakeEventBus`, `FakeClock` fakes (ADR-0054); sentence-style names (ADR-0053).
No new fake, no new builder method, no production change.

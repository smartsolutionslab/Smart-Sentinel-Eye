# Spec 105 — Tasks

**Phase:** 3 (Tasks) · **Date:** 2026-09-08 · **Issue:** #729 (`[T079]`)
**Engineer:** `backend-engineer` (C# unit tests, xUnit + Shouldly, existing fakes)
**Phase 4a colour:** behaviour-**preserving** → characterisation, **observed
green**, evidenced by counterfactuals **M1** and **M2** (`plan.md`).
**New ADR needed:** **No.** ADR-0037, 0052, 0053, 0054, 0109 and 0144 cover
every choice. No architectural decision is taken and none is implied — the
"no dedupe by overlay" property is existing behaviour the kiosk was already
built against, not a new one.
**Production code:** **none.** A task that edits `src/` is out of scope; if the
tests cannot be made green without one, stop and report.

---

## User story US1 — a producer that emits both highlights

No task is `[P]`: T001 and T002 are in the same test project, and T003 must
observe both green before mutating anything.

- [ ] **T001 [US1]** Add
  `Two_highlight_actions_on_the_same_overlay_both_yield_an_effect` to
  `tests/Automation.Application.Tests/Evaluation/RuleEvaluatorTests.cs`,
  **immediately after** `Conflict_two_rules_writing_the_same_variable_emit_both_in_createdAt_order`
  (`:114-137`) — it is the overlay half of that pair and reads as one thought
  with it.

  Mirror that test exactly: one `InMemoryRuleCache`, two `cache.Upsert(ActiveRule(...))`
  calls using the file's existing `ActiveRule` helper, `BaseMoment` and
  `BaseMoment.AddMinutes(5)`, then `new RuleEvaluator(cache, NullLogger<RuleEvaluator>.Instance)`
  and `evaluator.Evaluate(FabIdentifier.From("munich"), "plc", "PlcCycleStart", Context(PlcCycleStartContext))`.

  One `Guid overlay = Guid.CreateVersion7();` shared by both rules:
  `RuleAction.HighlightOverlay.From(overlay, 5_000)` for `rule-a`,
  `RuleAction.HighlightOverlay.From(overlay, 12_000)` for `rule-b`. Both are
  inside `HighlightDuration`'s 500–60000 range
  (`src/Automation/Domain/Rule/HighlightDuration.cs:21-22`).

  Assert `effects.Count.ShouldBe(2)`, then index like the sibling does:
  `effects[0].ShouldBeOfType<RuleActionEffect.HighlightOverlay>()` with
  `.Overlay.ShouldBe(overlay)` and `.DurationMs.ShouldBe(5_000)`, and
  `effects[1]` the same with `12_000`. Order is asserted, not incidental —
  `InMemoryRuleCache` sorts by `CreatedAt` because production does.

  Carry a short comment in the sibling's register: the two windows are
  deliberately different so the kiosk's later-expiry OR
  (`apps/kiosk-web/src/features/cell/CellPage.test.tsx:491`) has something to
  discriminate; the producer emits both rather than picking.

- [ ] **T002 [US1]** Add
  `Two_highlight_actions_on_the_same_overlay_both_publish` — the issue's
  verbatim name — to
  `tests/Automation.Application.Tests/EventHandlers/FabEventIngestedV1HandlerTests.cs`,
  after `HighlightOverlay_action_publishes_OverlayHighlightRequestedV1`
  (`:80-108`).

  **This is the task that pins the issue's actual claim**; T001 alone cannot
  (counterfactual M2). Build the two rules with `RuleBuilder` the way the
  neighbour does — `.WithName("highlight-rule-a")` / `"-b"` (distinct names, so
  `InMemoryRuleCache` keeps both rather than replacing), `.WithAction(...)` with
  the shared overlay and 5000 / 12000 ms, `.WithClock(BaseMoment)` and
  `.WithClock(BaseMoment.AddMinutes(5))`, each `.Publish(new FakeClock(...))`
  before `cache.Upsert`.

  Then `FakeEventBus bus = new();`, a handler over a **real** `RuleEvaluator`
  (never a mock — the neighbours all construct one), `FabEventIngestedV1 ingested = PlcCycleStart();`
  and `await handler.Handle(ingested, CancellationToken.None);`.

  Assert on `bus.Published.OfType<OverlayHighlightRequestedV1>().ToArray()`:
  `Length.ShouldBe(2)`; both `.OverlayIdentifier.ShouldBe(overlay)`; the two
  `.DurationMs` are `[5_000, 12_000]`; and **both** carry
  `CausingEventIdentifier == ingested.EventIdentifier` — that shared identifier
  is what distinguishes "two rules, one event" from "one rule, two events", and
  is the reason the assertion is worth making rather than assumed. Depends on
  T001.

- [ ] **T003 [US1]** Run both new tests and the whole
  `Automation.Application.Tests` project; **observe green** and quote the
  verbatim output. Then apply **M1** (`plan.md`), re-run, quote; revert. Then
  apply **M2**, re-run, quote; revert. Confirm `git status` is clean of `src/`
  before committing.

  If a prediction misses — an existing test reddens under M1, or the evaluator
  test reddens under M2 — **record the actual result beside the prediction and
  say the prediction was wrong.** Do not retro-fit the prediction to the
  observation; spec 104's commit `08782b7c` exists because that distinction was
  worth keeping.

## Dependencies

T001 → T002 → T003, strictly serial. No foundational task: nothing in
`Shared.Kernel`, `Shared.Contracts`, `AppHost` or any Aspire resource is
touched, so there is nothing for an orchestrator to fan out behind.

## Gate (phase 3)

Tasks atomic; #729 on Project #13 as the feature-level issue (ADR-0144 lane —
no per-task issues, `/speckit-taskstoissues` is not run).

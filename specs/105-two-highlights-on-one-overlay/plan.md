# Spec 105 — Plan

**Phase:** 2 (Plan) · **Date:** 2026-09-08 · **Issue:** #729 (`[T079]`)

## Bounded context and layers

**Automation**, Application layer only, and only its tests. No Domain change,
no Infrastructure, no Api, no contract. `Shared.Contracts` is read
(`OverlayHighlightRequestedV1`) and not touched.

Two files, both existing:

| File | Level | What it pins |
|---|---|---|
| `tests/Automation.Application.Tests/Evaluation/RuleEvaluatorTests.cs` | evaluator | two effects for two rules on one overlay, in createdAt order |
| `tests/Automation.Application.Tests/EventHandlers/FabEventIngestedV1HandlerTests.cs` | handler | two `OverlayHighlightRequestedV1` on the bus |

Neither is on ADR-0109's contention list. Both are in one project, owned by one
engineer, so the two tasks are **not** `[P]` — same-project, and the handler
test is only worth writing once the evaluator's shape is settled.

## Entities, invariants, messaging

Nothing new. The invariant being documented is the *absence* of one: there is
no "one highlight per overlay per event" rule, deliberately, because the kiosk
resolves overlap by later expiry (`CellPage.test.tsx:491`) and the producer
must therefore hand it both windows rather than pick.

Domain → integration event, unchanged:
`RuleAction.HighlightOverlay` → `RuleActionEffect.HighlightOverlay` →
`OverlayHighlightRequestedV1`, one per effect, each with its own
`EventMetadata(Guid.CreateVersion7(), ...)` and a shared
`CausingEventIdentifier`. That shared identifier is worth asserting: it is what
lets a consumer tell "two rules, one event" from "one rule, two events".

## Boundary rules

No cross-context reference is added. The tests live in
`Automation.Application.Tests`, which already references
`Automation.Domain.Tests` for `RuleBuilder` and `Shared.Contracts` for the V1
types. NetArchTest is unaffected.

## Phase 4a colour — behaviour-preserving, characterisation, observed green

Both tests describe behaviour that exists at `08782b7c`. There is no red
available: the production code already emits and publishes both. A compile
error is not a red test (spec 061, `24e6fc4c`), so no red is manufactured.

The tests must therefore be **observed green on first run**, and the evidence
that they are not vacuous is the counterfactual below — run after they are
green, then reverted.

### M1 — dedupe in the evaluator

In `src/Automation/Application/Evaluation/RuleEvaluator.cs`, replace the
`HighlightOverlay` case (`:57-61`):

```csharp
case RuleAction.HighlightOverlay highlight:
    effects.Add(new RuleActionEffect.HighlightOverlay(
        highlight.Overlay.Value, highlight.Duration.Value));
    break;
```

with the same body guarded on the overlay already having an effect:

```csharp
case RuleAction.HighlightOverlay highlight:
    if (effects.OfType<RuleActionEffect.HighlightOverlay>()
        .All(existing => existing.Overlay != highlight.Overlay.Value))
    {
        effects.Add(new RuleActionEffect.HighlightOverlay(
            highlight.Overlay.Value, highlight.Duration.Value));
    }
    break;
```

**Written prediction.**

- `RuleEvaluatorTests.Two_highlight_actions_on_the_same_overlay_both_yield_an_effect`
  — **RED**, `effects.Count` 1, expected 2.
- `FabEventIngestedV1HandlerTests.Two_highlight_actions_on_the_same_overlay_both_publish`
  — **RED**, one `OverlayHighlightRequestedV1`, expected 2.
- **All nine existing `RuleEvaluatorTests` GREEN.** Every one of them produces
  at most one `HighlightOverlay` effect, so the `All(...)` guard is vacuously
  true on the only call that reaches it.
- **All existing `FabEventIngestedV1HandlerTests` GREEN**, same reason.
- **Every other test in the solution GREEN.** No integration test builds two
  highlight rules; `FirstPublishPerTypeTests` uses a fresh `Guid.NewGuid()` per
  round and is `Category=Measurement` besides.

M1 is the isolating mutation the brief asked for: it fails exactly the
two-on-one case and nothing else. If any existing test also reddens, the
prediction is wrong and that is the finding, not a reason to adjust the test.

### M2 — dedupe at publish

In `src/Automation/Application/EventHandlers/FabEventIngestedV1Handler.cs`,
declare `HashSet<Guid> highlighted = [];` before the `foreach` (`:75`) and gate
the highlight publish on `if (highlighted.Add(highlightOverlay.Overlay))`.

**Written prediction.**

- The **handler** test — **RED**, one V1, expected 2.
- The **evaluator** test — **GREEN**. It never reaches the handler.
- Everything else — **GREEN**.

**M2 is the whole argument for two tests.** The evaluator test is blind to it,
and M2 is precisely the "both V1s ride the outbox" claim failing. A single
evaluator-level test would leave the issue's own sentence unproved.

### What the counterfactual does not prove

Neither mutation is a bug anyone is likely to write by accident; they are
probes, not predictions of failure. What they establish is narrower and
sufficient: that the two new assertions are load-bearing rather than
restatements of coverage the existing nine already provide.

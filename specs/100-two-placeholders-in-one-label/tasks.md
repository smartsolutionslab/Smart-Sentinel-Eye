# Spec 100 — Tasks

**Phase:** 3 (Tasks) · **Date:** 2026-09-08 · **Issue:** #494 (`[T068]`)
**Engineer:** `backend-engineer` (C# integration test, Aspire fixture, ADR-0103)
**Phase 4a colour:** behaviour-**preserving** → characterisation, observed
green, evidenced by counterfactual **M1** (`plan.md`).
**New ADR needed:** **No.** ADR-0037, ADR-0103, ADR-0109, ADR-0113, ADR-0115
and ADR-0144 cover every choice here. No architectural decision is taken.

---

## User story US1 — both placeholders resolve over HTTP

No task is `[P]`. All four touch one new file, and T003 must observe T002's
green before mutating anything.

- [ ] **T001 [US1]** Create
  `tests/Integration.Tests/SystemVariables/TwoPlaceholdersInOneLabelTests.cs` —
  `[Collection(AspireCollection.Name)]`, **no `[Trait]`**, primary constructor
  `(AspireFixture aspire)`, `IAsyncLifetime` with
  `InitializeAsync() => aspire.ResetSystemVariablesAsync()`. Private helpers:
  `DefineAsync(client, name)`, `PublishOverlayReferencingAsync(overlays, nameA, nameB)`
  building `$"Line A: {{{{{nameA}}}}} / Line B: {{{{{nameB}}}}}"`,
  `ResolvedTextAsync(variables, overlay)`, and
  `WaitUntilBothResolvableAsync(...)` — the readiness wait must require **both**
  literals gone (see `plan.md` § Messaging; a one-name wait races the bug).
  Reuse `VariableRequests.SetValueAsync` for `If-Match` (ADR-0113) and
  `OverlayRequests.PostAsync` for the publish; add no new fixture helper.
  Class remark states what the file proves (the query handler's multi-name
  snapshot loop) and what it does not (the substitution mechanism, already
  covered by `VariableValueChangedPreCommitTests:128`).

- [ ] **T002 [US1]** `Two_placeholders_in_one_label_both_resolve` — define two
  Number variables, publish the two-placeholder overlay, wait for both to be
  resolvable, set `82.5` and `91.5`, `GET /system-variables/snapshot`, and
  assert `resolvedText.ShouldBe("Line A: 82.5 / Line B: 91.5")` — the **whole
  string**, not two `Contains` calls. Assert the `200` first, with the response
  body in the failure message (the neighbours' pattern). Depends on T001.

- [ ] **T003 [US1]** `An_unset_second_variable_leaves_only_its_own_placeholder_literal`
  — a **fresh pair**: `vC` set, `vD` defined but never set. Assert
  `"Line A: 82.5 / Line B: {{vD}}"`. This is the control that stops T002 passing
  against a resolver that writes one value into every placeholder. It is
  deliberately **not** the test M1 is scored on — M1 leaves it green. Depends on
  T001.

- [ ] **T004 [US1]** Run T002 and T003 against unmodified `develop` code and
  capture the **verbatim** output green (the characterisation baseline), then
  apply **M1** — `break;` after
  `src/SystemVariables/Application/Queries/Handlers/GetOverlaySnapshotQueryHandler.cs:63`
  — and re-run **the whole backend + integration suite**, not just the new file.
  Record which tests failed against the prediction table in `plan.md`.
  **Revert M1 and confirm green before committing.** Depends on T002, T003.

  Prediction to score against: T002 fails; T003, all five
  `GetOverlaySnapshotQueryHandlerTests`, `NFR_VariableResolutionLatencyTests`,
  every `ResolvedTextReachesItsFabTests` case,
  `VariableValueChangedPreCommitTests:128`, `PlaceholderParserTests`,
  `ResolverTests` and `InMemoryReverseIndexTests` all stay green. **A
  disproved prediction is the finding — report it; do not tune M1 to fit.**

---

## Dependencies

```
T001 ──┬── T002 ──┬── T004
       └── T003 ──┘
```

Nothing is foundational to other slices: no `Shared.Kernel`, no
`Shared.Contracts`, no `AppHost`, no Aspire resource. Per ADR-0109 this slice
owns exactly one new file and collides with nothing, so the orchestrator may
run it concurrently with any other board item.

## Gate

Feature-level issue **#494** on Project #13 (per `CLAUDE.md` — `/speckit-tasks`
adds nothing to the board):

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/494
```

## Verification (phase 5)

The independent procedure in `spec.md` § *Independent end-to-end test
procedure*, plus the M1 result from T004 quoted verbatim in the PR body. Green
alone is not the evidence here — the counterfactual is.

## Explicitly not in this slice

- Any edit under `src/`. If T004 shows the two-variable case resolving wrongly
  on unmodified code, **stop**: that is a bug fix, a second issue, and
  characterisation would encode the bug (ADR-0144).
- Auth, `400`, `403`, `404` assertions — already covered
  (`VariableFabResolutionIntegrationTests`, `VariableReadScopeIntegrationTests`).
- Three-or-more placeholders, repeated names, cross-fab labels, the push path.

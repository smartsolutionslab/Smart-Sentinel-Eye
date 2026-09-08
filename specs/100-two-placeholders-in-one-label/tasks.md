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
  `ResolvedTextAsync(variables, overlay)`, and a readiness wait.

  **Delivered as `WaitUntilResolvableAsync(variables, overlay, string name)`
  (`:214`), not `WaitUntilBothResolvableAsync`, and requiring a `200` rather
  than both literals gone.** Recorded here rather than left describing code that
  does not exist. Both deviations are corrections, not conveniences: the `200`
  is load-bearing (the neighbour's wait returns while the overlay is still
  404 — `plan.md` § Messaging), and requiring *both* literals gone would have
  hidden the failure this file exists to produce behind a 30 s timeout instead
  of an assertion diff.

  The parameter is a **single name**, not a list. It was a list first, which is
  generality with no user once readiness became the first name only — both call
  sites pass exactly one, and ADR-0036 rules that out. The timeout message
  reads better for it: it names the one variable that had to resolve rather
  than formatting a list of one.

  Reuse `VariableRequests.SetValueAsync` for `If-Match` (ADR-0113) and
  `OverlayRequests.PostAsync` for the publish; add no new fixture helper.
  Class remark states what the file proves (the query handler's multi-name
  snapshot loop) and what it does not (the substitution mechanism, already
  covered by `VariableValueChangedPreCommitTests:128`).

- [ ] **T002 [US1]** `Two_placeholders_in_one_label_both_resolve` — define two
  Number variables, publish the two-placeholder overlay, set `82.5` and `91.5`,
  wait until the snapshot answers `200` with the **first** literal gone,
  `GET /system-variables/snapshot`, and
  assert `resolvedText.ShouldBe("Line A: 82.5 / Line B: 91.5")` — the **whole
  string**, not two `Contains` calls. Assert the `200` first, with the response
  body in the failure message (the neighbours' pattern). Depends on T001.

- [ ] **T003 [US1]** `An_unset_first_variable_leaves_only_its_own_placeholder_literal`
  — a **fresh pair**, with the unset one **first**: `vC` defined but never set,
  `vD` set. Assert `"Line A: {{vC}} / Line B: 82.5"`.

  It is **not** a control against "one value written into every placeholder" —
  T002 catches that unaided, using two distinct values and asserting the whole
  string. Its ground is that **mixed resolve/unset in one label over this
  handler is covered nowhere** (`spec.md` § Partial resolution), and the unset
  name being first makes it the one case showing a `continue` exit does not end
  the loop. It is deliberately **not** the test M1 is scored on — M1's `break`
  follows the write that only the last, valued name reaches, so M1 leaves it
  green. Depends on T001.

- [ ] **T004 [US1]** Run T002 and T003 against unmodified `develop` code and
  capture the **verbatim** output green (the characterisation baseline), then
  apply **M1** — `break;` after
  `src/SystemVariables/Application/Queries/Handlers/GetOverlaySnapshotQueryHandler.cs:63`
  — and re-run the backend suite in full plus a **named subset** of the
  integration suite. Record which tests failed against the prediction table in
  `plan.md`. **Revert M1 and confirm green before committing.** Depends on T002,
  T003.

  **The two runs, and the narrowing stated rather than implied.** The backend
  half is the whole thing: every test project except `Integration.Tests`, 29
  projects, **2348 tests**. The integration half is **not** the whole thing: CI's
  `integration` job runs **450** cases (`ci.yml:179`; 463 discovered, 13 excluded
  by category), and the M1 run selects **8** of them —

  ```
  --filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance&(FullyQualifiedName~TwoPlaceholdersInOneLabelTests|FullyQualifiedName~ResolvedTextReachesItsFabTests|FullyQualifiedName~NFR_VariableResolutionLatencyTests)"
  ```

  — the two new cases plus the six neighbours the prediction table names. The
  narrowing is defensible because only these three files reach the mutated
  handler over HTTP, and it costs about 34 s against roughly 30 minutes; but a
  printed `Total: 8` must never be reported as if it were the suite. Any
  figure quoted from this run states the filter alongside it.

  Prediction to score against: T002 fails **on its `ShouldBe`**, with the
  expected and actual strings printed, not on the readiness timeout — the wait
  is on the first name precisely so that M1 surfaces as a diff; T003, all five
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

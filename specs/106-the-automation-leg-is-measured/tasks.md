# Tasks: The automation leg is measured

**Spec**: `specs/106-the-automation-leg-is-measured/spec.md` ·
**Plan**: `specs/106-the-automation-leg-is-measured/plan.md` · **Issue**: #749

---

## Declarations for the lane (ADR-0144)

**Engineer**: **backend**. The deliverable is one xUnit integration test using the Aspire
fixture, EF Core raw SQL and Postgres `percentile_cont`. Phase 4a's `test-writer` produces
essentially the whole artefact; `backend-engineer` (phase 4b) has work only if T004 shows
the jsonb path or the audit `event_kind` differs from what the plan predicts. No frontend,
no infra.

**New ADR?** **No.** Every mechanism is already decided: ADR-0103 (Aspire fixture),
ADR-0052/0053 (xUnit + Shouldly, sentence naming), ADR-0102 (uniform `EventMetadata`),
spec 025 (`RootIngestedAt`), and the `percentile_cont`-over-`audit_events` pattern that
`IngestSpanMeasurement` already ships. **The decision that would need one** is escalating
to NFR-001's literal span — stamping Automation's own consume moment on the wire, or
reading it from the trace store — because that adds a measurement surface to a V1 contract
or a new runtime dependency. That escalation is a phase-1 gate question (spec §Gate item
2), not something phase 4 may reach for.

**Phase 4a colour: characterisation, observed GREEN.**

This test adds no behaviour and changes none. There is no red available, and manufacturing
one would be dishonest: a compile error is not a red test (spec 061, `24e6fc4c`), and a
deliberately wrong budget that is then "fixed" is a red for the test's own constant, not
for the system. So the obligation is the other one in constitution §Testing — the test is
captured **passing** against unmodified production code, and its output is quoted in the
PR.

**But "observed green" is worth nothing here on its own**, because a latency test's
characteristic failure is passing while measuring the wrong thing. The green run is
therefore paired with a counterfactual, run and recorded:

### Counterfactual, with the prediction written down first

Two mutations, because the interesting one for a latency test is the one that makes it
pass *faster*.

**M1 — the trap. Make the measured path skip its work.**
In `src/Automation/Application/Evaluation/RuleEvaluator.cs`, return a hard-coded
`SetVariableValue` effect with value `"0"` without consulting the cache or evaluating the
predicate. Revert immediately after.

> **Prediction:** the p95 assertion **passes, and the figure improves** — this is precisely
> what a latency assertion cannot catch. The run fails instead on
> **`rowsWithExpectedValue`**, which drops to 0 because `"0"` is not in the expected set,
> and on the per-iteration sync poll, which times out at iteration 1 because the variable
> never reaches `98`. If either of those two assertions is absent or weakened, M1 is a
> silent green. **This is the single most important thing to verify about this test.**

**M2 — the honest failure. Make the measured path slower.**
Insert `await Task.Delay(150, cancellationToken);` in `FabEventIngestedV1Handler.Handle`
**before** `DateTimeOffset requestedAt = clock.UtcNow;` (line 75). Placement matters and is
itself the point: a delay inserted *after* that stamp would not appear in the figure,
which is the tail-undershoot the spec names. Revert immediately after.

> **Prediction:** p95 rises by ≈150 ms to ≈150–200 ms and the assertion **fails**, naming
> the figure. **No existing test reddens**: `EventReachesItsEffectsTests` and the other
> Automation integration tests use a 60 s deadline; `AelInterpreterBenchmarkTests` never
> enters this handler; `NFR_VariableResolutionLatencyTests` measures a different context's
> span and asserts at 800 ms. That "no existing test reddens" is the gap #749 exists to
> close, and the counterfactual is what turns that claim from an assertion into an
> observation. **If some other test does redden, say so — the premise was narrower than
> believed and that is a finding.**

Both mutations are made in a dirty working tree, observed, and reverted. Neither is
committed.

**Parallelism**: **none, and that is not an oversight.** Every task below writes the same
one file. `[P]` is reserved for disjoint files (ADR-0109) and there are no disjoint files
here. There is no foundational task to fan out from and nothing for the orchestrator to
run concurrently — the slice is one file deep on purpose.

---

## Phase 3 gate

- Tasks atomic ✅
- The feature's issue on Project #13 — **#749 is a `task`-labelled issue from spec 007's
  per-task era and is already the tracked item.** Verify it is on the board with
  `gh project item-list 13 --owner smartsolutionslab --limit 2000` (the default limit of 30
  makes a filled board look empty) and add it if it is not:
  `gh project item-add 13 --owner smartsolutionslab --url <issue-url>`. `item-add` prints
  nothing on success.

---

## User Story 1 — Automation's leg has a number (P1)

The independently-shippable slice. T001–T009 deliver it end to end.

- [ ] **T001 [US1]** *[amended 2026-09-09: shipped as `AcceptToDecideLatencyTests.cs` —
  spec FR-001]* Create
  `tests/Integration.Tests/Automation/NFR001_RuleEvaluationLatencyTests.cs` with the class
  skeleton: `[Collection(AspireCollection.Name)]`, primary constructor taking
  `AspireFixture` and `ITestOutputHelper`, `IAsyncLifetime`, and the named constants
  (`WarmupIterations = 20`, `MeasuredIterations = 100`, `BudgetMilliseconds = 100`,
  `EffectDeadline`, `PollIntervalMs`). Class remarks state the two moments, the head
  overshoot, the tail undershoot, and that a pass does not discharge NFR-001 (FR-010).
  *Depends on: nothing.*

- [ ] **T002 [US1]** Arrange helpers: define two `Number` variables (`warm…`, `meas…`),
  create and publish one rule per variable with distinct `triggerKind`, and **read back
  `state == "Active"`** before proceeding. Reuse `RuleRequests` / `VariableRequests`; mirror
  `EventReachesItsEffectsTests.ActivateAsync`. *Depends on: T001.*

- [ ] **T003 [US1]** Readiness wait, following
  `TwoPlaceholdersInOneLabelTests.WaitUntilResolvableAsync` (`:214-237`): require a **200**
  *and* the expected value, `Task.Delay` between polls, and a timeout message that
  distinguishes "not a 200" from "a 200 carrying the wrong value". **Do not copy
  `NFR_VariableResolutionLatencyTests.WaitUntilResolvableAsync`** — it returns on its first
  iteration against a 404 (#2201). *Depends on: T002.*

- [ ] **T004 [US1]** **Verify the read path against one real row before writing any
  assertion.** Boot the fixture, drive a single event, and dump the matching `audit_events`
  row: confirm `event_kind = 'SystemVariableValueRequestedV1'`, `resource_identifier` = the
  variable name, `payload->'Metadata'->>'RootIngestedAt'` non-null, and `payload->>'Value'`
  the computed value. Record the observed JSON casing in the PR body. *Depends on: T003.
  Blocks T006.*
  > This is the task that stops a wrong jsonb path from being diagnosed as a broken
  > pipeline. It is a throwaway probe, not a committed test.

- [ ] **T005 [US1]** The iteration loop: `cycleTime` cycling `1..30`, fresh `eventId` per
  iteration, publish via `PlantFloor.PublishRawAsync`, poll to `EffectDeadline`, and **fail
  naming the iteration index** on timeout — never `continue`. *Depends on: T003.*

- [ ] **T006 [US1]** The settle-and-query step: poll the audit row count to
  `MeasuredIterations` (bounded), then run the single `SqlQueryRaw` returning
  `[totalRows, rowsWithExpectedValue, p50, p95, p99, max]` per the plan's SQL, with
  `COALESCE(..., -1)` and **not** `COALESCE(..., 0)`. *Depends on: T004, T005.*

- [ ] **T007 [US1]** The assertions, **in order**: (1) `totalRows.ShouldBe(measured)`,
  (2) `rowsWithExpectedValue.ShouldBe(measured)`, (3) print the artefact line — `n`, p50,
  p95, p99, max, the span spelled out in words, the budget — (4)
  `p95.ShouldBeLessThanOrEqualTo(BudgetMilliseconds)` with a message repeating the span and
  warning that a failure alone does not convict Automation. *Depends on: T006.*

- [ ] **T008 [US1]** `DisposeAsync`: archive both variables (`VariableRequests.ArchiveAllAsync`)
  and both rules. A 120-write measurement run that leaves residue is #2004's finding.
  *Depends on: T002.*

- [ ] **T009 [US1]** Apply `[Trait("Category", "Measurement")]` to the P1 fact **only**, with
  an inline comment giving the reason from spec §Traiting — a figure taken on a shared
  runner is a figure about the runner — and naming the consequence, that an excluded test is
  one nobody watches. Do not put the trait on the class. *Depends on: T007.*

---

## User Story 2 — A regression is caught by someone (P2)

Shippable on its own after US1. May be deferred at the phase-3 gate.

- [ ] **T010 [US2]** Extract the body of the P1 fact into a private
  `MeasureAsync(int warmup, int measured, int budgetMilliseconds)` and have the P1 fact call
  it with `(20, 100, 100)`. Behaviour-preserving: the P1 fact's output must be unchanged.
  *Depends on: T009.*

- [ ] **T011 [US2]** *[shipped as `Accept_to_decide_has_not_regressed_by_an_order_of_magnitude`]*
  Add `Rule_evaluation_has_not_regressed_by_an_order_of_magnitude`,
  **no category trait**, calling `MeasureAsync(3, 5, 400)`. Remarks state that 400 ms is 4×
  the budget deliberately — the same reasoning
  `NFR_VariableResolutionLatencyTests.LegBudgetMs` gives — so it catches an
  order-of-magnitude regression on a shared runner without policing the budget.
  *Depends on: T010.*

- [ ] **T012 [US2]** Confirm selection: run
  `dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj --filter
  "Category!=Measurement&Category!=Disruptive&Category!=Maintenance" --list-tests` and check
  the guard fact appears and the measurement fact does not (SC-003). *Depends on: T011.*

---

## Phase 4a — capture the evidence

- [ ] **T013** Run the P1 fact on an idle machine with no other Aspire stack running.
  Capture the **verbatim** output — the artefact line and the pass — for the PR body. This
  is the "observed green" for a characterisation test. *Depends on: T009.*

- [ ] **T014** Run M1 (the skip-the-work mutation). Record which assertions failed and
  which passed, against the prediction above. Revert. *Depends on: T013.*

- [ ] **T015** Run M2 (the +150 ms mutation, before the `requestedAt` stamp). Record the
  figure and the failure. Run the Automation integration tests and
  `AelInterpreterBenchmarkTests` under the same mutation and record that none redden — or
  that some do, which is a finding. Revert. *Depends on: T014.*

---

## Phase 5 — verification note

- [ ] **T016** Run the P1 fact a **second** time and record both p95 figures. One run is
  not a measurement: the first run after machine churn looks exactly like a regression.
  *Depends on: T013.*

- [ ] **T017** Write `specs/106-the-automation-leg-is-measured/verification.md` containing:
  both p95 figures with `n`, p50, p99 and max; the machine and what else was running; the
  span spelled out; the counterfactual results from T014/T015 against their written
  predictions; and an explicit statement of **what the figures do not license** — not
  NFR-001 discharged, not §IV's leg measured. *Depends on: T015, T016.*

- [ ] **T018** State in the verification note and the PR body that **constitution §IV's
  table is unchanged**, and why: this measures accept→decide, a proper prefix of the
  accept→effect leg the *recorded, not yet readable* cell describes. If a human judges the
  cell should gain a note, that is theirs to write. *Depends on: T017.*

---

## Dependency graph

```
T001 ─► T002 ─► T003 ─► T004 ─┐
                  │            ├─► T006 ─► T007 ─► T009 ─► T010 ─► T011 ─► T012
                  └─► T005 ────┘                    │
        T002 ─► T008                                └─► T013 ─► T014 ─► T015 ─► T017 ─► T018
                                                       └─► T016 ─────────────────┘
```

No `[P]` markers: single file (see *Declarations*).

---

## Definition of done

- **[restated 2026-09-09]** An integration test in `tests/Integration.Tests/Automation/`
  asserts a p95 for a named span against a named budget, and prints `n`, min, p50, p95,
  p99 and max beside the span's definition. Shipped as
  `AcceptToDecideLatencyTests.Accept_to_decide_p95_stays_within_the_automation_leg_budget`.

  This criterion read: *"`NFR001_RuleEvaluationLatencyTests` exists, and `git grep -rln
  RuleEvaluationLatency` returns a test file rather than three prose files."* **It came
  out green for the wrong reason** — the class was renamed (see spec FR-001) and the grep
  matched only because the class remarks *mention* `NFR001_RuleEvaluationLatencyTests` in
  prose while explaining why the file is not called that. A check satisfied by what a
  comment says rather than by what the code does is this repository's recurring defect in
  miniature, so it is replaced by something the code can actually satisfy.
- Two p95 figures recorded from two runs (SC-001).
- **[restated 2026-09-09 to what was observed]** M1 was caught, and by the **readiness
  poll** — both facts failed at iteration 0 of the warm-up, ~120 s before any SQL ran
  (`never reached '98' within 120 s; a 200 carrying '0'`). A throwaway probe with the poll
  removed confirmed the counts are the backstop behind it: `totalRows=20
  rowsWithExpectedValue=0 … p95=14,4 ms`, with the probe printing *"a p95-only assertion
  at 100 ms would have PASSED"*.

  This line read *"the latency assertion passed and the count assertions caught it"*.
  **That is not what happened in the committed test**: M1 never reaches the latency
  assertion or the counts, because the poll fails first. The counts-catching-M1 outcome was
  shown only by the probe, which was never committed. Recorded as observed rather than as
  predicted — writing the prediction down as the outcome is the failure mode a
  counterfactual exists to prevent.
- M2's prediction confirmed: the test failed and no existing test did.
- `git diff --stat` touches only `tests/` and `specs/` (SC-004).
- Phase 4a evidence quoted verbatim in the PR body (ADR-0139).

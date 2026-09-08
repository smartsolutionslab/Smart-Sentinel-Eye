# Implementation Plan: The automation leg is measured

**Spec**: `specs/106-the-automation-leg-is-measured/spec.md` · **Issue**: #749 (spec 007 T099)

**Change class**: test-only. One new file under `tests/Integration.Tests/Automation/`.
No `src/` change, no package addition, no migration, no Aspire resource, no contract
change, no constitution edit.

---

## Why this plan has no bounded context, entities or messaging section

The usual plan shape — context, layers, aggregate, invariants, domain → integration event
— describes a change that adds behaviour. This adds none. Writing those sections anyway
would be four headings of "n/a" that make a reader assume the question was considered
somewhere else. What follows instead is the structure of the observation: which processes
are touched, which of their existing outputs are read, and where the boundary rules bite.

**Boundary rules still apply and are satisfied by construction.** `Integration.Tests`
already references every context's Infrastructure project (its `.csproj` lists ten), so
using `AuditObservabilityDbContext` to read `audit_events` introduces no new reference.
Nothing in `src/` gains a reference to anything. NetArchTest's cross-context rule is
untouched because no production project changes.

---

## What runs, and what the test reads

```
 test process                         Aspire stack
 ────────────                         ────────────
 PlantFloor.PublishRawAsync ──MQTT──► mosquitto ──► event-ingestion
                                                     │  accepts, stamps IngestedAt   ◄── Moment A
                                                     │  raises FabEventIngestedV1
                                                     ▼
                                                   rabbitmq (fanout per message type)
                                                 ┌───┴───────────────┐
                                                 ▼                   ▼
                                            automation          audit-observability
                                              │ evaluates             │
                                              │ stamps RequestedAt ◄── Moment B
                                              │ publishes            │
                                              │ SystemVariable       │
                                              │ ValueRequestedV1 ────┤ writes one row:
                                              │                      │   occurred_at = Moment B
                                              ▼                      │   payload->Metadata
                                          system-variables           │     ->RootIngestedAt = Moment A
                                              │ applies value        ▼
 GET /system-variables/{name} ◄───────────────┘                  audit_events
 (per-iteration sync point)
 SqlQueryRaw(percentile_cont …) ◄─────────────────────────────────────┘
 (once, after all iterations)
```

Two reads, with different jobs, and conflating them is the mistake to avoid:

- **The per-iteration poll** against SystemVariables is a *sync point*, not a
  measurement. Its latency is not in the figure. It exists so iteration *n+1* is published
  onto a quiet pipeline (FR-004) and so a lost effect is caught as a failure rather than
  as a missing row.
- **The single SQL query** at the end is the measurement. It reads stamps taken inside the
  services, so the test's own HTTP and polling costs are outside the figure entirely.

---

## File plan

One file:

```
tests/Integration.Tests/Automation/NFR001_RuleEvaluationLatencyTests.cs
```

Named exactly as spec 007 T099 and #749 name it, so the next `git grep
RuleEvaluationLatency` finds a test rather than three prose files.

**No new fixture helper.** `PlantFloor`, `RuleRequests`, `VariableRequests` and
`AspireFixture.CreateAuditObservabilityDbContextAsync` cover everything. The one candidate
for extraction — the percentile SQL — has a sibling in
`tests/Integration.Tests/AuditObservability/IngestSpanMeasurement.cs`, but that class
filters on a different `event_kind`, reads a different pair of stamps and lives in another
context's folder. Generalising it to serve both is a refactor of a working measurement in
the same PR as a new one; the house rule (smallest possible change; don't mix a refactor
with new work) says no. Recorded here so the duplication is a decision, not an oversight.

### Class shape

```
[Collection(AspireCollection.Name)]
public class NFR001_RuleEvaluationLatencyTests(AspireFixture, ITestOutputHelper) : IAsyncLifetime
```

- `IAsyncLifetime.DisposeAsync` archives the variables and the rule the run created
  (`VariableRequests.ArchiveAllAsync`, `RuleRequests.PostAsync(..., "archive")`). A
  measurement run that leaves residue is #2004's finding, and this one writes 120 values
  to a variable.
- Two `[Fact]`s over one private `MeasureAsync(warmup, measured, budgetMs)` helper:
  - `Rule_evaluation_p95_stays_within_the_automation_leg_budget` —
    `[Trait("Category", "Measurement")]`, `(20, 100, 100)`.
  - `Rule_evaluation_has_not_regressed_by_an_order_of_magnitude` — no trait,
    `(3, 5, 400)`.
- Constants named, not inlined: `WarmupIterations`, `MeasuredIterations`,
  `BudgetMilliseconds`, `GuardBudgetMilliseconds`, `EffectDeadline`, `PollIntervalMs`.

### Arrange

1. Admin clients for `system-variables` and `automation`.
2. Two variables: `warm{guid}` and `meas{guid}`, both `Number`, `initialValue = "0"`.
   **Two variables rather than one** is what keeps the warm-up out of the population by
   construction (FR-003) — the SQL selects on `resource_identifier`, which for this event
   is the variable `Name`, so warm-up rows are not merely skipped, they are never
   selected. Trimming the first 20 rows afterwards would depend on an ordering the query
   does not guarantee.
3. Two rules, one per variable, distinct `triggerKind` (`PlcWarm{n}` / `PlcMeas{n}`) so
   one event fires exactly one rule. Predicate `$.payload.cycleTime <= 30`, expression
   `100 - $.payload.cycleTime * 2` — the same rule the Automation integration tests
   already use, so this measures the shape the rest of the suite exercises.
4. Publish and read back `state == "Active"`, exactly as `EventReachesItsEffectsTests`
   does: a Draft rule cannot fire, and a run against one would fail on the count assertion
   for a reason that has nothing to do with latency.
5. **Readiness wait** before the first warm-up iteration: publish one event and poll until
   the variable carries its value, following
   `TwoPlaceholdersInOneLabelTests.WaitUntilResolvableAsync`'s corrected shape — require a
   **200** *and* the expected value, delay between polls, and a timeout message that
   distinguishes "not a 200" from "a 200 carrying the wrong value". Not
   `NFR_VariableResolutionLatencyTests.WaitUntilResolvableAsync`, which returns on its
   first iteration against a 404 (#2201).

### Iterate

`cycleTime` cycles `1..30`, so the expected value cycles `98, 96, … 40`. Two properties
follow, and both are needed:

- **Consecutive iterations expect different values**, so the sync-point poll is
  unambiguous — a poll waiting for a value the variable already holds returns instantly
  and stops being a sync point.
- **Every action payload's `Value` must fall in a known 30-element set**, which is what
  FR-007's correctness filter checks.

Per iteration: build the payload with a fresh `eventId` (Guid v7), publish via
`PlantFloor.PublishRawAsync`, poll `GET /system-variables/{name}` at `PollIntervalMs`
until `value == expected` or `EffectDeadline` expires. A timeout **fails the test naming
the iteration index** (FR/US1 scenario 5) — it must never `continue`, because a dropped
sample both shrinks the population and removes the slowest observation from it.

### Measure

One query, after the loop, against `AuditObservabilityDbContext`:

```sql
SELECT unnest(ARRAY[
  count(*)::float8,
  count(*) FILTER (WHERE delta IS NOT NULL AND value_expected)::float8,
  COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY delta), -1),
  COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY delta), -1),
  COALESCE(percentile_cont(0.99) WITHIN GROUP (ORDER BY delta), -1),
  COALESCE(max(delta), -1)]) AS "Value"
FROM (
  SELECT
    EXTRACT(EPOCH FROM (
      occurred_at - (payload->'Metadata'->>'RootIngestedAt')::timestamptz)) * 1000 AS delta,
    (payload->>'Value') = ANY({1}) AS value_expected
  FROM audit_events
  WHERE event_kind = 'SystemVariableValueRequestedV1'
    AND resource_identifier = ANY({0})
) samples
```

Notes on the shape, each of which is a decision:

- **`count(*)` and the filtered count are returned side by side.** A run where the rows
  exist but carry an unexpected value, and a run where they do not exist at all, are
  different findings and must not collapse into one number.
- **`COALESCE(..., -1)`, not `COALESCE(..., 0)`.** An empty population yields `NULL`, and a
  zero there is a perfect score for a journey nobody watched — the same reasoning
  `EventToOverlayLatency` gives for not recording zero when `RootIngestedAt` is absent, and
  the same reasoning `IngestSpanMeasurement` gives for not `COALESCE`-ing inside the
  samples CTE. `-1` cannot be mistaken for a good figure.
- **`percentile_cont`, matching `IngestSpanMeasurement.PercentilesAsync`** rather than a
  hand-rolled index into a sorted list. Two spellings of "p95" in one repository is how two
  figures come to disagree.
- **`payload->'Metadata'->>'RootIngestedAt'`** — PascalCase, because
  `IntegrationEventAuditHandler` serialises with `JsonSerializer.Serialize(message,
  message.GetType())` and default options. Precedent:
  `CameraAddressAuditIntegrationTests:106-109` reads `payload->>'PreviousUrl'`. **T004
  verifies this before the assertions are written** — a wrong path yields `NULL`, which
  the count assertion catches, but it would catch it as "the pipeline is broken", which is
  the wrong diagnosis.
- **Read once, after a settle wait.** Audit writes are batched (ADR-0127), so the last
  rows may not be visible the instant the last effect lands. The test polls the row count
  to the expected number (bounded) before running the percentile query, rather than
  sleeping a fixed interval.

### Assert, in this order

1. `totalRows.ShouldBe(measured)` — names the count found and says a low count means
   nothing fired, not that the system is fast.
2. `rowsWithExpectedValue.ShouldBe(measured)` — names that a mismatch means the pipeline
   ran and produced the wrong effect.
3. Print `n`, p50, p95, p99, max **with the span written out in words** and the budget
   beside it. This line is the artefact; SC-001 quotes it twice.
4. `p95.ShouldBeLessThanOrEqualTo(budget)` with a message that repeats the span and warns
   that a failure is not by itself proof Automation breached NFR-001.

Order matters: assertions 1 and 2 are the ones that fail on a dead or wrong system, and
they must run before any number is printed as if it meant something.

---

## Boundary rules and house conventions

| Rule | How this satisfies it |
|---|---|
| No cross-context project references (NetArchTest) | No `src/` project changes. `Integration.Tests` already references all ten Infrastructure projects. |
| Value objects in Domain (§II, `PrimitiveBoundaryTests`) | No domain type added. |
| `Ensure.That` for argument guards (ADR-0105) | No production guard added; test-local helpers take no untrusted input. |
| Handler deconstruction (`HandlerDeconstructionTests`) | No handler added. |
| Collection expressions (`List<T> x = [];`) | Applies to the test's local lists — enforced at `warning`, fails Release. |
| Private fields carry no underscore | Applies to the fixture-held `PlantFloor`. |
| `CancellationToken` last (ADR-0049) | Applies to the private async helpers. |
| Max 300 LOC/file, 30 LOC/method (ADR-0084) | The class is one arrange, one loop, one query, one assert block. If it exceeds 300 lines the SQL moves to a `private static` sibling in the same file, not to a new shared helper. |
| Test naming, sentence-style (ADR-0053) | Both fact names above. |
| Shouldly, no FluentAssertions (ADR-0052) | `ShouldBe`, `ShouldBeLessThanOrEqualTo`. |
| Coverage gates (ADR-0065) | Integration tests are not in the gated assemblies; no gate moves. |

---

## Risks, and what each would look like

| Risk | Symptom | Response |
|---|---|---|
| The jsonb path is wrong | Count assertion fails at 0 on a working stack | T004 proves the path against one real row before the assertions exist |
| p95 lands above 100 ms | Assertion 4 fails on a healthy stack | **A finding, not a licence to raise the number.** Split the span: `IngestThroughputMeasurementTests` already bounds ingest, so a large residual is Automation's. Report to a human. |
| The 100-iteration run is intolerably slow | The measurement fact takes longer than a human will wait | It is out of CI, so this costs patience rather than a build. If it must shrink, shrink *measured*, and say so — a p95 over 40 samples is a coarser p95, not a different quantity. |
| The audit rows have not settled when queried | Count assertion fails intermittently just under the target | The bounded row-count poll before the query; not a fixed sleep |
| Another Aspire stack is running on the machine | `FailedToStart`, reading exactly like a code defect | One machine, one stack — stop any other AppHost before a measurement run |
| The figure is quoted for NFR-001 or for §IV's leg | A discharge nobody earned | FR-008 and FR-010 put the span's definition in the printed line and in the file's remarks, so the figure cannot travel without it |

---

## What phase 4 must not do

- Not edit `.specify/memory/constitution.md`, and in particular not the §IV table. If the
  figure suggests a cell should change, that is a finding for a human.
- Not modify `NFR_VariableResolutionLatencyTests` to fix #2201. Different file, different
  context, different figure.
- Not add a package reference to reach the broker directly. If the chosen span proves
  unusable, that is a phase-3 escalation with an ADR behind it, not a phase-4 improvisation.
- Not change `ci.yml`. The trait does the selecting.
- Not weaken assertion 1 or 2 to `ShouldBeGreaterThan(0)`. They are the only defence
  against a green run on a dead pipeline.

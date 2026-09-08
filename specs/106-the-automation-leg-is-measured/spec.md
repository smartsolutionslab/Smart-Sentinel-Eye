# Feature Specification: The automation leg is measured

**Feature Branch**: `test/749-the-automation-leg-is-measured`

**Created**: 2026-09-08

**Status**: Draft

**Input**: #749 — `[T099] NFR001_RuleEvaluationLatencyTests` integration test: warm 20
iters + measure 100 iters; assert p95 ≤ 100 ms `FabEventIngestedV1` consume → action V1
published. (spec 007 T099, `specs/007-automation/tasks.md:219`.)

**Scope**: test-only. No production code changes. No edit to constitution §IV's table.

---

## Why this exists

Constitution §IV budgets `event → overlay state` at **200 ms** and records the leg as
*recorded, not yet readable*. Spec 007 divides that 200 ms into three named parts —
50 ms ingest, **100 ms automation**, 50 ms resolve-and-broadcast
(`specs/007-automation/spec.md:442-447`). Two of the three have a test. The middle one
has none, and that is the whole of this feature.

- SystemVariables' part is measured by
  `tests/Integration.Tests/SystemVariables/NFR_VariableResolutionLatencyTests.cs`.
- EventIngestion's part is measured by
  `tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs` and, for
  the audit pipeline, `NFR001_AuditIngestLatencyTests`.
- **Automation's part is asserted by nobody.** A regression in rule lookup, predicate
  evaluation or action fan-out would be noticed on a kiosk or not at all.

### The premise was checked, and it holds

`git grep -rln RuleEvaluationLatency` returns three files, all of them prose:
`specs/007-automation/plan.md:446`, `specs/007-automation/tasks.md:219`, and
`specs/014-system-variable-fab-scoping/research.md:100`. No test file names it. The
literal `p95` appears in exactly three test files —
`CameraCatalog/CommandLatencyTests`, `EventIngestion/IngestThroughputMeasurementTests`,
`StreamDistribution/WhepHandshakeLatencyTests` — and two further latency tests gate on
other percentiles (`NFR001_AuditIngestLatencyTests` on p99,
`NFR_VariableResolutionLatencyTests` on the median). **None of the five is Automation.**

**The near miss is a near miss.** `tests/Automation.Application.Tests/Ael/AelInterpreterBenchmarkTests.cs`,
`Predicate_evaluation_throughput_clears_NFR002`, times `AelInterpreter.Evaluate` in a
loop, in-process, against a parsed expression and a fixed `EvaluationContext`. It never
constructs a message, never touches `FabEventIngestedV1Handler`, never reaches the
outbox or the broker, and it gates the **median of ten batches** against 500 ms — a
budget for NFR-002 (interpreter throughput), not NFR-001 (pipeline latency). It does not
substitute, and its own remarks say which requirement it serves.

---

## The measurement, stated honestly before anything else

This is the section a later reader must not be able to skip, because a latency test that
passes for the wrong reason is the expensive failure here.

### The two observable moments

NFR-001's span is *`FabEventIngestedV1` consumed by Automation → action V1 on the bus*.
**Neither endpoint is observable from a test process**, because Automation stamps
neither. The test therefore measures a **different, adjacent span**, using two stamps
that already exist on the wire and are already persisted:

| | Stamp | Set by | Meaning |
|---|---|---|---|
| **Moment A** | `SystemVariableValueRequestedV1.Metadata.RootIngestedAt` | EventIngestion, forwarded unchanged by Automation (`FabEventIngestedV1Handler.cs:85-93`) | when EventIngestion accepted the plant-floor event |
| **Moment B** | `SystemVariableValueRequestedV1.Metadata.OccurredAt` (= `RequestedAt`) | Automation, `clock.UtcNow` taken after evaluation and before the publish loop (`FabEventIngestedV1Handler.cs:76`) | when Automation decided what to publish |

Both moments arrive at the test **on the same audit row**. AuditObservability subscribes
to `SystemVariableValueRequestedV1`
(`IntegrationEventAuditHandler.cs:53`) and writes `occurred_at` = Moment B plus the whole
serialised message into the `payload` jsonb column, from which Moment A is read as
`payload->'Metadata'->>'RootIngestedAt'`. No join, no second query, no cross-row
correlation. `resource_identifier` for this event is the variable name
(`V1ResourceMap.IdentifierPropertyNames` picks `Name`), which is how the run selects its
own rows — the same filter `IngestSpanMeasurement` already uses.

### What the measured span is not

**It overshoots at the head and undershoots at the tail. It is not a superset of
NFR-001's span, and a pass must not be reported as "NFR-001 holds".**

- **Head overshoot** — the measured span begins at EventIngestion's *acceptance*, so it
  also contains EventIngestion's domain-event dispatch and outbox release, one RabbitMQ
  hop, and Wolverine's deserialise. Spec 007's arithmetic budgets the whole ingest leg at
  50 ms, of which this is a part.
- **Tail undershoot** — `RequestedAt` is stamped *before* `events.PublishAsync`, so the
  capture into the ambient message context, Wolverine's flush at handler completion and
  the broker send are **not** measured. NFR-001 says "on the bus"; this says "decided".

So the honest statement of what a passing run licenses is:

> Over N events on this machine, the interval from EventIngestion accepting a plant-floor
> event to Automation deciding its action had a p95 of X ms.

and what it does **not** license:

> NFR-001 holds. / Automation's leg is within 100 ms. / §IV's `event → overlay state` leg
> is measured.

A **failure** is equally constrained: it does not prove Automation breached its budget,
because the ingest tail and the broker hop are inside the figure. A failure is a finding
to be split, not a verdict.

### Why not something tighter

| Alternative | Why rejected |
|---|---|
| Bind a test queue to Wolverine's exchange and publish `FabEventIngestedV1` from the test | Both moments would be read from one clock (the test's), which is the strongest property available — but it needs an AMQP client the test project does not reference, and a hand-built Wolverine envelope whose headers must match the listener convention. No test in this repository speaks AMQP; `FirstPublishPerTypeTests` reaches the broker only through the management HTTP API. Reserved as the escalation if the chosen span proves too noisy. |
| Use the `FabEventIngestedV1` audit row's `received_at` as a proxy for Automation's arrival | That is *AuditObservability's* consumer arriving on a sibling queue of the same fanout, not Automation's. The error term is the difference between two consumers' dispatch delays, it can go negative, and it drags a third service's scheduling into the figure. |
| `handler_entered_at` | Stamped only when `AuditMeasurementOptions.RecordIngestBreakdown` is on. A figure that silently becomes null is the wrong kind of dependency for a gate. |
| Drive `POST /rules/{name}/dry-run` in a loop | The exact "measured a fast path that skipped the work" trap. It never leaves the process. |
| Read the `LatencyBudget` OTel histogram (`EventToOverlayLatency`) | It records the *whole* leg (accept → effect applied), not this part, and it is precisely what §IV means by *recorded, not yet readable*. Making it readable is #1940's question, not this one. |

### What the test does if the pipeline is broken

Design requirement, not a hope. Three failure shapes and the assertion that catches each:

1. **Nothing fires** (rule cache empty, handler throwing, broker down). Zero audit rows
   match. `percentile_cont` over an empty set returns `NULL`. **A test that asserted only
   the percentile would pass on a dead system.** So the sample count is asserted **before**
   the percentile, at exactly the number of measured iterations.
2. **It fires, but skips the work** (a stubbed evaluator, a short-circuited predicate, a
   stale cache hit returning the wrong effect). The latency assertion passes *faster*. So
   the row selection also filters on the payload's `Value` matching the arithmetic the
   rule was given, and the count assertion then fails at zero.
3. **`RootIngestedAt` stops being forwarded.** The jsonb cast yields `NULL`, the row drops
   out of the population, and the count assertion fails. This is the failure spec 025
   built the field to make impossible to have silently.

The count assertion is therefore load-bearing, not hygiene. It is the only thing standing
between this test and a green run on a stack that does nothing.

### What makes the figure trustworthy, and what would make it a lie

**Trustworthy when**: 20 warm iterations precede the measured 100 (spec 023 measured the
first event of a cold stack at **10–16 s**, the first event of each *message type* at
3.6–4.8 s, and steady state at **134–199 ms** for the whole journey — the cold cost is
per message type and is paid inside the warm-up); the run is on an otherwise idle
machine; and the run is **repeated**, because this repository's own rule is that the
first measurement after machine churn looks exactly like a regression.

**A lie when**: quoted from a single run; quoted from a shared CI runner; quoted after a
run in which any iteration timed out and was silently dropped; or quoted as a figure for
NFR-001 or for §IV's leg rather than for the span named above.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Automation's leg has a number (Priority: P1)

An engineer changing rule lookup, the AEL interpreter or action fan-out can read what the
pipeline currently costs, and a run that has doubled it says so.

**Why this priority**: it is the whole issue. Everything else here is a refinement of it.

**Independent Test**: run the one test on a booted Aspire stack, twice, and read two p95
figures off the output. Delivers value with nothing else built.

**Acceptance Scenarios**

1. **Given** a booted stack with an Active `SetVariableValue` rule, **When** 20 warm-up
   events and then 100 measured events are published from the plant floor, **Then** the
   test prints `n`, p50, p95, p99 and max for the accept→decide span, and asserts
   **p95 ≤ 100 ms**.
2. **Given** the same run, **When** fewer than 100 audit rows carry both stamps and the
   expected value, **Then** the test fails naming the count it found, **before** any
   percentile is computed.
3. **Given** a stack on which no rule is Active, **When** the test runs, **Then** it fails
   on the count assertion — not on a percentile over an empty set, and never green.
4. **Given** an event whose predicate does not match, **When** it is published, **Then**
   no action V1 is published and no row joins the population — so a run whose predicate
   silently stopped matching fails by count rather than by an improved figure.
5. **Given** a measured iteration whose effect never lands within the per-iteration
   deadline, **When** the deadline expires, **Then** the test fails naming the iteration
   — a dropped sample must not be silently excluded from the percentile.

### User Story 2 — A regression is caught by someone (Priority: P2)

The full measurement is excluded from CI (see *Traiting*, below). A cheap variant of the
same helper runs in CI at a deliberately loose bound, so an order-of-magnitude regression
is caught by the build rather than by the next person who happens to run a measurement.

**Why this priority**: independently shippable, and it exists only because P1's honest
traiting decision leaves a gap. Deferring it is a legitimate choice for a human to make
at the phase-3 gate; shipping P1 alone is still a complete slice.

**Independent Test**: run the CI integration filter
(`Category!=Measurement&Category!=Disruptive&Category!=Maintenance`) and confirm this
fact is selected and green.

**Acceptance Scenarios**

1. **Given** the CI filter, **When** the integration suite runs, **Then** the guard fact
   is selected, publishes 3 warm-up and 5 measured events, and asserts p95 ≤ **400 ms**.
2. **Given** the same filter, **When** the integration suite runs, **Then** the P1
   measurement fact is **not** selected.

### Edge cases

- **Duplicate delivery.** Wolverine is at-least-once. A redelivered `FabEventIngestedV1`
  is stopped at ingestion (`IngestEventCommandHandler` returns `EventAlreadyIngested`
  before raising the V1), so it produces no second action row. A redelivered *action* V1
  is deduped by the audit insert's `ON CONFLICT (event_identifier, occurred_at) DO NOTHING`.
  The count assertion therefore expects exactly the iteration count, not "at least".
- **Clock provenance.** Moment A is EventIngestion's clock, Moment B is Automation's. On
  the Aspire fixture both processes share one OS clock, so the difference is meaningful.
  On a distributed deployment it would not be, and the figure must not be carried there.
  Recorded in the test's own remarks, not only here.
- **Audit batching (ADR-0127)** delays `written_at`, not `occurred_at` or the payload.
  The figure is unaffected; the *readback* is, so the test waits for the row count to
  settle before querying rather than querying once.
- **Negative deltas** would mean Automation's clock ran behind EventIngestion's. They are
  not clamped to zero — a clamp manufactures a perfect score. They are reported and fail
  the run.

---

## Requirements *(mandatory)*

### Functional

- **FR-001** A new integration test class, `NFR001_RuleEvaluationLatencyTests`, under
  `tests/Integration.Tests/Automation/`, in the `AspireCollection`.
- **FR-002** It publishes plant-floor events through the existing `PlantFloor` fixture
  helper over MQTT — the same ingress every other Automation integration test uses. It
  does not post to an HTTP endpoint to enter the chain in the middle.
- **FR-003** 20 warm-up iterations precede 100 measured iterations, and the warm-up's
  rows are excluded from the population by construction (a distinct variable name), not
  by trimming afterwards.
- **FR-004** Each iteration waits for its own effect before the next is published. The
  test measures single-event latency, not throughput under concurrency, and must not
  become a saturating burst.
- **FR-005** The population is read in **one** SQL query against `audit_events`, filtered
  on `event_kind = 'SystemVariableValueRequestedV1'` and `resource_identifier = ANY(...)`,
  computing `percentile_cont` over
  `EXTRACT(EPOCH FROM (occurred_at - (payload->'Metadata'->>'RootIngestedAt')::timestamptz)) * 1000`.
- **FR-006** The row count is asserted equal to the measured-iteration count **before**
  any percentile is asserted, with a failure message that names the count found.
- **FR-007** The selection also requires the payload's `Value` to equal the value the
  rule's expression yields for the event published, so a fan-out that produced the wrong
  effect reduces the count rather than improving the figure.
- **FR-008** n, p50, p95, p99 and max are written to the test output with the span named
  in words, so the figure cannot be quoted without its definition.
- **FR-009** The p95 assertion is **≤ 100 ms**, and its failure message states that the
  measured span is accept→decide, not NFR-001's consume→published.
- **FR-010** The class remarks state, in the file, what the two moments are, what the
  head overshoot and tail undershoot are, and that a pass does not discharge NFR-001.
- **FR-011** The P1 fact carries `[Trait("Category", "Measurement")]`; the P2 guard fact
  carries no category trait.
- **FR-012** No file under `src/` is modified. No file under `.specify/` is modified.

### Non-functional

- **NFR-A** The P1 fact completes within the integration suite's practical envelope on a
  developer machine. 120 iterations of mint-connect-publish-poll at the observed warm cost
  (~150–200 ms plus MQTT connect) is minutes, not seconds; that is acceptable for a
  human-run measurement and is part of why it is traited out of CI.
- **NFR-B** The P2 guard adds 8 iterations to the CI integration job.

### Out of scope

- Any production instrumentation that would make NFR-001's own two moments observable
  (an `AutomationConsumedAt` stamp, a read of the handler's OTel span). See *ADR* below.
- Any change to constitution §IV's table. If this measurement changes what §IV may say,
  that is a finding for a human.
- A dashboard. §VII's dashboard obligation (ADR-0117) for this leg is #1940 and is not
  settled here.
- Measuring the `HighlightOverlay` action. One action type is the smallest slice that
  proves the leg; the second travels the same handler and the same publish.

---

## Success Criteria

- **SC-001** Two independent runs on an idle machine each produce a p95, and both are
  recorded in the verification note. One run is not a measurement.
- **SC-002** The count assertion is demonstrated to fail on a stack with no Active rule
  — verified by the counterfactual below, not by assertion in prose.
- **SC-003** The CI integration job's selected-test count rises by exactly one (the P2
  guard) and the P1 fact does not appear in the trx.
- **SC-004** `git diff --stat` for the merged change touches only `tests/` and `specs/`.

---

## Traiting decision

**The P1 measurement is `[Trait("Category", "Measurement")]` and therefore excluded from
CI** by `.github/workflows/ci.yml:179`
(`--filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"`).

**The reason, stated rather than inherited.** ci.yml's own comment gives the test for this
category: *"a thirty-second saturating burst on a shared runner measures the runner, and
that number would later be quoted as if it measured this code."* This test is not a
saturating burst — FR-004 forbids that — but the second half of the sentence applies in
full. Its entire product is a **figure**, and a figure taken on a shared GitHub runner
alongside a Postgres, a RabbitMQ, a Keycloak and eight services is a figure about the
runner. A p95 gate of 100 ms taken there would either flake or, worse, pass and be
quoted. Duration is a secondary reason and not the deciding one.

**The consequence, stated honestly.** An excluded test is one nobody watches. This
repository has a documented history of tests that were green because they never ran — the
`WaitUntilResolvableAsync` defect below is one, `LogTailDeliversIntegrationTests`
describes another, and ci.yml's own comment concedes that spec 020's SC-002 has no CI
coverage as a result. Excluding this one costs the same thing. That cost is what US2
exists to pay down, and it is why US2 is in this spec rather than in a follow-up nobody
files.

**Precedent, checked rather than assumed.** `NFR001_AuditIngestLatencyTests` puts the
trait on individual `[Fact]`s, not on the class, so some facts in that file do run in CI.
`NFR_VariableResolutionLatencyTests` carries no trait at all and runs in CI at 4× its
budget. Both shapes exist; this spec takes the first.

---

## The wait: which pattern is followed, and why

The brief names two candidates and this spec follows the **second**.

`NFR_VariableResolutionLatencyTests.WaitUntilResolvableAsync` (`:144-162`) **does not
wait**. `ResolvedTextAsync` maps a non-200 to `string.Empty` (`:164-176`), and
`string.Empty.Contains("{{name}}")` is `false`, so the loop returns on its first iteration
while the overlay is still 404. It also has no `Task.Delay`, so were it ever to loop it
would spin. Filed as **#2201**.

`TwoPlaceholdersInOneLabelTests.WaitUntilResolvableAsync` (`:214-237`) is the corrected
shape: `ResolvedTextAsync` returns `string?`, readiness requires `resolved is not null`
(a 200) **and** the literal gone, the loop delays between polls, and the timeout message
distinguishes *"not a 200"* from *"resolved but stale"*.

**This spec follows the corrected shape**, for a reason specific to a latency test: a
readiness check that returns early does not merely wait less, it moves work that belongs
to set-up into the first measured sample. In the SystemVariables test that damage is
absorbed by `WarmupRounds = 3` and by `MeasureOneChangeAsync`'s own `Contains(expected)`
poll, which an empty string cannot satisfy — so **that test's §IV figure is not
compromised**, and this spec asserts nothing to the contrary. Its readiness check simply
contributes nothing. Here, the equivalent set-up wait guards the boundary between arrange
and warm-up, and a check that cannot fail is a check that will one day be believed.

Fixing #2201 is **not** in this spec's scope. It is a different file, a different context
and a different test's figure, and bundling it would make this change two things.

---

## Locked tech choices (no new decisions)

| Concern | Choice | Authority |
|---|---|---|
| Integration harness | `AspireFixture` + `AspireCollection`, no Testcontainers | ADR-0103 |
| Assertions | xUnit + Shouldly | ADR-0052 |
| Test naming | sentence-style with underscores | ADR-0053 |
| Percentiles over audit rows | `SqlQueryRaw` + `percentile_cont` on `audit_events` | existing: `IngestSpanMeasurement.PercentilesAsync` |
| jsonb payload access | `payload->>'Field'`, PascalCase | existing: `CameraAddressAuditIntegrationTests:106-109` |
| Plant-floor ingress | `PlantFloor` fixture helper over MQTT | existing: spec 022 |
| The carried acceptance moment | `EventMetadata.RootIngestedAt` | spec 025, ADR-0102 |
| CI category exclusion | `Category=Measurement` | `.github/workflows/ci.yml:179` |

**ADRs referenced**: ADR-015 (§IV latency budget), **ADR-0117** (§VII binds implemented
legs), ADR-0102 (uniform event metadata), ADR-0103 (Aspire fixture), ADR-0088 (Wolverine
outbox + queue isolation), ADR-0124/0126 (listener parallelism and native acks — they set
the delivery cost inside the measured span), ADR-0127 (audit batching — why `written_at`
is not used), ADR-0052/0053 (test framework and naming).

**Is a new ADR the honest answer? No.** Nothing here is an architectural decision: it
reuses the fixture, the percentile pattern, the audit table and a field spec 025 already
added for exactly this purpose. The decision that *would* need one is the escalation named
above — stamping Automation's own consume moment on the wire, or reading it from an OTel
span — because that adds a measurement surface to a V1 contract or a new dependency on the
trace store. **If a human decides the accept→decide proxy is not good enough, that is the
ADR, and this spec is the evidence for writing it.**

---

## Latency-budget impact (constitution §IV)

**Leg: `event → overlay state` (≤ 200 ms).** This feature adds no code to that path and
cannot erode it. It measures part of it.

**It does not change §IV's table**, and phase 4 must not edit it. The row reads *recorded,
not yet readable*, and that phrase is about `EventToOverlayLatency` — the histogram
covering **accept → effect applied**. This test covers **accept → decided**, a proper
prefix of that. Producing a figure for a prefix does not make the leg's own figure
readable, and writing *measured* into that cell on the strength of this run would be
exactly the clerical over-claim §IV warns about two paragraphs later: *"a leg recorded as
measured before anyone has read its figure claims a discharge nobody earned."*

What this feature does license, once SC-001 is satisfied, is a **note** beside that row —
which a human writes, not phase 4.

---

## Gate (Phase 1 → Phase 2)

1. No `[NEEDS CLARIFICATION]` markers remain. ✅
2. The head-overshoot / tail-undershoot statement is accepted as the honest description of
   what is measured, **or** the escalation (a test-owned AMQP consumer, or a production
   stamp behind an ADR) is chosen instead. **This is the one judgement a human should make
   before phase 2.**
3. The p95 gate of 100 ms is accepted on a span that is not NFR-001's, on the reasoning in
   *What the measured span is not*. If a run shows the ingest tail dominating, that is a
   finding to split — never a licence to raise the number.
4. The traiting decision (P1 out of CI, P2 in) is accepted, including its stated cost.
5. US2 is either kept or explicitly deferred.

### Recorded guesses

- **G1** That the accept→decide proxy is acceptable in place of NFR-001's own span. It is
  the only span two existing wire stamps can express. Marked because it is a substitution,
  not a measurement of what the issue literally asks for.
- **G2** That 100 ms is achievable for accept→decide. Spec 023 measured the *whole*
  journey at 134–199 ms warm, of which this is one part, so it is plausible but not
  established. If the first run lands above 100 ms, the figure is the deliverable and the
  budget question goes to a human.

# Feature Specification: The instrument before the latency

**Feature Branch**: `perf/1956-the-instrument-before-the-latency`

**Created**: 2026-09-09

**Status**: Draft — phase 1 (Specify) of ADR-0037, run in the ADR-0144 autonomous lane.

**Input**: #1956 — *"Audit ingest latency is seconds under sustained writes, not the
50 ms NFR-001 asks for — and NFR-001 has never been measured at load."* Queued
2026-09-06 **as an investigation**: *"the figure is measured and severe, but the cause
is not established, so phase 1 is diagnosis and the deliverable may be a finding rather
than a fix."* And: *"Do not raise the budget to fit an observation."*

**Scope**: **test and fixture only.** No production code change. No change to NFR-001's
50 ms or its 100 ev/s. No new lever against audit ingest latency. No ADR.

**ADRs**: [ADR-0037](../../docs/adr/0037-guided-phased-workflow.md) (the phases),
[ADR-0144](../../docs/adr/0144-an-autonomous-delivery-lane.md) (the lane),
[ADR-0124](../../docs/adr/0124-parallel-listeners-where-order-does-not-matter.md),
[ADR-0126](../../docs/adr/0126-audit-listeners-settle-at-the-broker.md),
[ADR-0127](../../docs/adr/0127-batching-audit-writes.md) (rejected — and it **is** on
`develop`),
[ADR-0135](../../docs/adr/0135-where-the-audit-span-goes.md),
[ADR-0136](../../docs/adr/0136-where-the-audit-span-goes-in-run-mode.md).

---

## 1. The diagnosis

### 1.0 The measurement this spec was written from

Taken 2026-09-09 before anything was planned, because the owner asked for a
re-measurement and because two premises in the record had already moved. Aspire test
fixture, `Where_the_ingest_span_goes`, switch exported in the launching shell,
`Logging__LogLevel__Default=Warning`. **Two runs, both passed** (26 s, then 13 s), and
they are reported unaveraged because ADR-0136 records this pipeline as bistable.

| | **Run 1** | **Run 2** |
|---|---|---|
| rate, intended → achieved | 100 → **98.6 ev/s** | 100 → **99.8 ev/s** |
| service log level | Warning | Warning |
| measurement switch | **ON** | **ON** |
| rows measured | 1000 (0 missing stamps) | 1000 (0 missing stamps) |
| per-row residual (apparatus) | 0.000 ms | 0.000 ms |
| **typical** — observed span (occurred → received) | 3101.3 ms | **15.0 ms** |
| — before handler | 3101.2 ms | 15.0 ms |
| — in handler (one clock, exact) | **0.0 ms** | **0.0 ms** |
| — write (two clocks) | 6.2 ms | 2.5 ms |
| **typical — requirement span (handover → commit)** | **between 6.2 and 3107.4 ms** | **between 2.5 and 17.5 ms** |
| **tail band** — observed span | 4601.8 ms | 530.7 ms |
| — write | 3.9 ms | 10.0 ms |
| **tail — requirement span** | **between 4.0 and 4605.7 ms** | **between 10.0 and 540.8 ms** |
| clock standing | Established, 0.75 ± 0.92 ms | Established, 0.02 ± 0.83 ms |

**Two lines are the whole feature.**

1. At a paced, achieved 98.6 ev/s — the rate NFR-001 names — **the requirement's own
   span is known only to lie between 6.2 ms and 3107.4 ms.** The 50 ms budget sits
   inside that interval. The instrument cannot say whether NFR-001 is met or missed, and
   it never has been able to.
2. **The two runs differ by 177× on the typical span at the same rate, with nothing
   changed between them.** Run 2's typical requirement span — ceiling included — is
   17.5 ms, *entirely inside* the 50 ms budget; run 1's ceiling is 3107.4 ms, sixty-two
   times outside it. ADR-0136's "bistable at the knee" is not a caveat to be noted and
   set aside. **It means any verdict taken from one run of this pipeline, in either
   direction, is a coin toss.**

The unpaced verdict fact was run in the same session for comparison: **p50 11.2 ms,
p99 4081.9 ms, max 4180.5 ms**, switch ON, 1000 of 1000 rows stamped — and it **failed**,
as it has since it was written, asserting `p99 ≤ 50 ms` against the end-to-end ceiling at
an unpaced ~15-20 ev/s. That is defect (A) and defect (B) in one line of output.

### 1.1 The issue's premise is dead, and so is the guidance built on it

The body records p50 4 800 ms rising to a 9 586 ms max and reasons from the **growth** —
a backlog forming — toward *"a drain rate, a batching boundary or a flush interval"*.
Every part of that has since been answered.

| Claim in the body | State today | Evidence |
|---|---|---|
| Latency **grows through the run** | Gone. ADR-0124 (4 listeners) and ADR-0126 (native acks) landed after the body was written and moved peak drain from ~100 to ~270 rows/s. | `AuditObservabilityInfrastructureModule.cs:57,97-98` |
| A **~5 s polling interval** | No such interval exists. Nothing in `src/` configures `DurabilitySettings`, a scheduled-job poll, a buffering limit or a flush interval. The publisher waits for no sweep — it flushes **inline in the commit**. | `OutboxTransactionalCommit.cs:30-31`, `SaveChangesAndFlushMessagesAsync` |
| A **rising backlog** shape | Replaced by a **bimodal** one. ADR-0136 calls it *"bistable at the knee"*: seven paced fixture runs give 267.4 -> 7361.9 ms with nothing changed between them. | `docs/adr/0136-…` |
| **Batching** as the untried next lever | Built, A/B-measured and **rejected** on 2026-08-28; at ~100 ev/s it made both percentiles worse. | `docs/adr/0127-batching-audit-writes.md` |

So the steer *"the interesting shape is the growth"* rests on a superseded number, and
the three mechanisms it points at have all been taken or refuted. **No lever should be
chosen from this issue as it stands.**

### 1.2 The defect is the instrument, and it is wrong at both ends

Two independent defects, each on its own sufficient to invalidate every NFR-001 verdict
this repository has recorded — in both directions.

**(A) It does not drive at the rate the requirement names.** NFR-001 is a claim about
*"sustained 100 ev/s"*. The fact that produces the verdict passes
`IngestSpanMeasurement.NoPacing` to a **single sequential writer** —
`NFR001_AuditIngestLatencyTests.cs:209-210`, driving the loop at
`IngestSpanMeasurement.cs:227-233`, which awaits each `PUT` before issuing the next.
That achieves ~15-20 ev/s. The repository already says why this is not an answer, in its
own words at `IngestRunShape.cs:55-66`:

> *"Driven flat out these same writers reached 244 ev/s and a 5.5 s span — a faithful
> measurement of overload, and no answer at all about the load the requirement
> describes."*

Only `Where_the_ingest_span_goes` paces (`IngestSpanMeasurement.cs:95-121`, 50 writers)
and guards the achieved rate (`NFR001_AuditIngestLatencyTests.cs:329-332`). The verdict
fact does neither.

**(B) It does not measure the leg the requirement names.** NFR-001's span is *"RabbitMQ
deliver-ack to audit row committed"* (`specs/009-audit-observability/spec.md:294-298`).
The verdict is computed from `received_at - occurred_at`
(`IngestSpanMeasurement.cs:351`), which **starts in a different bounded context** at
aggregate mutation, and runs through the publisher's transaction, the outbox flush, the
RabbitMQ publish, the **queue wait** and Wolverine's dispatch before the audit handler is
entered at all — then **ends at insert, not commit**.

The repository already knows this. `IngestAttribution.cs:25-33` states that the
requirement's span starts *inside* "before handler" and is therefore **bounded, not
measured**, and `:69-99` computes both bounds — `RequirementSpanFloorMs`,
`RequirementSpanCeilingMs`, and `RequirementSpanWidthMs`, documented as *"the cost of
not having a publisher-side stamp"*.

**Every figure ever quoted against NFR-001 on this issue is the ceiling.** The floor sits
at in-handler + write: 0.0 ms + 3.5-12.4 ms across ADR-0136's table, and 0.0 + 6.2 ms /
0.0 + 2.5 ms in §1.0's two runs — comfortably inside 50 ms at every load, in both
environments.

This is **not** a reason to declare NFR-001 met. A floor is not a verdict, and reading it
as one would be the same error in the opposite direction. It is a reason to refuse every
verdict recorded so far.

**(C) A third defect makes (B) unanswerable from an ordinary run.** The breakdown needs
`AuditMeasurementOptions.RecordIngestBreakdown` (`AuditMeasurementOptions.cs:16,23`). It
is off by default — **deliberately**, apparatus on a production write path, pinned by
`AuditMeasurementSwitchTests.cs:96` — and is turned on by an environment variable
exported in the shell that launches the host
(`specs/054-divide-the-recorded-85ms/quickstart.md:29-33`). **Nothing in `AppHost.cs` or
`AspireFixture.cs` sets it**, so under a plain `dotnet test` it is always OFF and
`Where_the_ingest_span_goes` refuses with *"rows arrived without the measurement
stamps"* — which is exactly what #2127 pasted. The off-by-default is right; the fixture
not exporting it is the oversight, and §1.0 shows a shell export reaches the services
unchanged.

### 1.3 Where the time can still be hiding, and the one thing nobody has asked

"Before handler" contains four things: the publisher's transaction, the outbox hop, the
**queue wait**, and Wolverine's dispatch. NFR-001's clock starts at *deliver-ack*, which
falls somewhere inside the last two — and where exactly decides the answer.

The audit listeners run `ProcessInParallelWithNativeAcks()` with `ListenerCount(4)`
(`WolverineDefaults.cs:104-130`). Under native acks a delivery stays **unacknowledged at
the broker for the whole handler** — ADR-0126 verified this by killing the service
mid-burst. That has a consequence nobody on this issue has used:

> **RabbitMQ's `messages_unacknowledged` on the audit queues is exactly the population of
> messages inside NFR-001's leg** — handed over, not yet committed.

By Little's law the mean time in that leg is `mean(messages_unacknowledged) / drain
rate`. Sampled through a paced 100 ev/s run this reads the requirement's own span from
the broker, **with no clock crossing at all** — the defect ADR-0136 could not get around
for the write leg. It answers the question in one number:

- **Near zero** — the handover is at handler entry, the requirement's span is the floor,
  and NFR-001 holds on its own leg while the *end-to-end* span, which has no budget at
  all, does not.
- **Deep** — the prefetch buffer holds the wait *inside* the leg, NFR-001 is genuinely
  missed, and the next step is a per-row transport-receipt stamp.

Either outcome is a finding. Neither is a budget change.

### 1.4 What this feature therefore is

**An instrument, and a finding.** Not a latency fix. The pipeline is not touched.

---

## 2. Out of scope, stated before the stories

- **Moving NFR-001.** Forbidden by the issue's own terms. Recorded here because the
  framing has drifted: NFR-001 lives at `specs/009-audit-observability/spec.md:294` and
  **not** in the constitution — `.specify/memory/constitution.md` contains no `NFR-001`,
  and its §IV table is the `event -> overlay` path, on which audit does not sit. Moving
  it would be a spec-009 edit, not a constitution amendment. It is out of scope either
  way.
- **Un-rejecting ADR-0127.** Measured and rejected; nothing here changes that evidence.
- **Dropping `AutoApplyTransactions` for audit endpoints** — the one untried lever, named
  in ADR-0127's Alternatives at `:173-176` (*"Not tried; arguably should have been tried
  first"*). It changes what ADR-0088 fixed, so it wants an ADR; and it attacks the
  consumer side, which every breakdown measures at **0.0 ms**. Choosing it before the
  instrument is fixed would repeat the mistake this feature exists to correct.
- **Production topology.** There is none (ADR-0130).
- **The other eight `Category=Measurement` facts.** Nine facts across six files are
  excluded from CI by `ci.yml:179`, pinned by `IntegrationTestSelectionTests.cs:81`;
  #2127 names one of them. Narrowing that exclusion is its own decision with its own CI
  cost, and this feature does not take it.

---

## 3. User stories

### US1 — P1 — An ordinary test run can divide the span

**As** the engineer holding #1956, **I want** `dotnet test` to produce the ingest
breakdown, **so that** the question of where the span goes stops depending on a
hand-launched stack and a remembered shell export.

Independently shippable and independently observable: it turns one refusal into one
answer. It blocks US2 and US3.

```gherkin
Scenario: the fixture stamps the breakdown
  Given the Aspire test fixture boots the stack
  And no environment variable is exported by the person running the test
  When "Where_the_ingest_span_goes" runs under "dotnet test"
  Then every measured row carries handler_entered_at and written_at
  And the run does not refuse with "rows arrived without the measurement stamps"

Scenario: the production default is untouched
  Given no configuration is supplied
  When AuditMeasurementOptions is constructed
  Then RecordIngestBreakdown is false
  And AuditMeasurementSwitchTests passes unmodified

Scenario: the measurement is not taken through verbose logging
  Given the fixture boots the stack
  When the breakdown reports its conditions
  Then the service log level is "Warning", reported as chosen rather than inherited

Scenario: a fixture that cannot stamp says so
  Given the switch fails to reach the audit service
  When the breakdown runs
  Then it reports "measurement switch: OFF" and the count of unstamped rows
  And it fails rather than printing zeros
```

### US2 — P2 — A verdict states the rate it was taken at, and refuses one taken at another

**As** the owner deciding about NFR-001, **I want** the fact that reports the NFR-001
verdict to drive at 100 ev/s and fail if it did not, **so that** a number taken at
15 ev/s cannot be read as an answer about 100 ev/s.

```gherkin
Scenario: the verdict is taken at the rate the requirement names
  Given the paced run shape of 50 writers targeting 100 ev/s
  When the NFR-001 verdict fact runs
  Then it reports the achieved rate
  And it fails when that rate is outside IngestRunShape.RateTolerance of the target

Scenario: an unpaced run does not claim to be a verdict
  Given the historic single-writer unpaced run, kept for comparability
  When it reports its figure
  Then it names the rate it achieved
  And it does not assert NFR-001's budget

Scenario: the verdict reports the requirement's span, not the end-to-end span
  Given a paced run with the stamps present
  When the verdict is reported
  Then it states the requirement span's floor, ceiling and width
  And it states which of the three the 50 ms budget was compared against

Scenario: a verdict that cannot be reached is refused, not guessed
  Given the requirement span's floor is below 50 ms and its ceiling above it
  When the verdict is reported
  Then the run states that the budget lies inside the interval
  And it does not report NFR-001 as met or as missed
```

### US3 — P3 — The handover is located, so the requirement's span stops being a range

**As** the owner, **I want** to know whether "deliver-ack" falls at handler entry or
behind a prefetch buffer, **so that** the choice between *"NFR-001 holds on its own leg"*
and *"NFR-001 is genuinely missed"* is made on evidence rather than on the ceiling.

```gherkin
Scenario: the leg's population is sampled from the broker
  Given a paced run at a sustained 100 ev/s
  When messages_unacknowledged on the audit queues is sampled every second
  Then the run reports the mean unacknowledged count, the achieved drain rate,
       and the implied mean time in NFR-001's leg as one divided by the other

Scenario: the assumption behind the reading is checked before it is used
  Given native acks hold a delivery unacknowledged for the whole handler
  When the audit service is quiescent
  Then messages_unacknowledged is zero
  And when 4 listeners are mid-handler it is non-zero

Scenario: the sample is taken twice, because one run is not a distribution
  Given ADR-0136 records this pipeline as bistable at the knee
  When the observation is recorded
  Then at least two runs are reported side by side, unaveraged

Scenario: an ambiguous result is reported as ambiguous
  Given the implied mean leg time and the per-row floor disagree by more than the budget
  When the finding is written
  Then it names a per-row transport-receipt stamp as the next step
  And it does not conclude that NFR-001 is met or missed

Scenario: the broker refuses the sampler
  Given the RabbitMQ management API rejects the credentials or is unreachable
  When the sampler runs
  Then the run fails naming the address and the status it received
  And it does not report a mean of zero samples
```

### Conflict, bad-request and auth scenarios on this path

They exist and are exercised, but as **preconditions of the measurement rather than
features of it**. Recorded so a later reader does not think they were skipped.

```gherkin
Scenario: a stale version is a conflict, not load
  Given two writers on one variable
  When both PUT with the same If-Match version
  Then one receives 409
  And the run shape avoids this by giving each writer its own variable
      (IngestRunShape.cs:38-48)

Scenario: an expired token is not an If-Match failure
  Given a run longer than the access token's lifetime
  When a value set is refused with 401
  Then the driver says so explicitly rather than reporting version numbers
      (IngestSpanMeasurement.cs:243-252)

Scenario: no run-mode stack configured
  Given SSE_RUNMODE_SYSTEM_VARIABLES is unset
  When the run-mode attribution fact runs
  Then it refuses, naming the three addresses it needs, and starts nothing
      (observed 2026-09-09; RunModeIngestAttributionTests.cs:68)
```

---

## 4. Independent end-to-end test procedure

Runnable by a person who has read nothing above. **Every measurement is taken at least
twice** — the first run after machine churn looks exactly like a regression, and
ADR-0136 records this pipeline as bistable.

1. Stop any AppHost —
   `Get-Process SmartSentinelEye.AppHost -ErrorAction SilentlyContinue | Stop-Process -Force`.
   A running stack holds the service binaries and the build fails MSB3027, which reads
   as a broken build. Leave the persistent containers alone.
2. With **no environment variables exported**, run
   `dotnet test tests/Integration.Tests --filter "FullyQualifiedName=…NFR001_AuditIngestLatencyTests.Where_the_ingest_span_goes"`.
   - **Before US1**: fails, *"N rows arrived without the measurement stamps"*.
   - **After US1**: passes, prints `measurement switch: ON` and `0 missing stamps`.
3. Run the NFR-001 verdict fact.
   - **Before US2**: prints a p99 and no achieved rate.
   - **After US2**: prints an achieved rate within `RateTolerance` of 100 ev/s, and the
     requirement span's floor, ceiling and width.
4. Run step 3 twice more. Record all three unaveraged.
5. **After US3**: read the reported mean `messages_unacknowledged`, drain rate and
   implied leg time. Cross-check by hand against the broker:
   `curl -su <user>:<pass> http://<rabbit-management>/api/queues` and read
   `messages_unacknowledged` for the `wolverine_audit.*` queues while a run is in
   flight.
6. Confirm nothing on the production path moved: `git diff --stat src/` is empty except
   for whatever US1 needs in `AppHost`/fixture wiring, and `AuditMeasurementSwitchTests`
   passes **unmodified**.

---

## 5. Locked tech choices

| Concern | Choice | Authority |
|---|---|---|
| Test framework | xUnit + Shouldly + Moq, hand-written fakes | ADR-0052 |
| Test naming | Sentence-style with underscores | ADR-0053 |
| Test data | Hand-written builders, no AutoFixture | ADR-0054 |
| Integration harness | The Aspire fixture; **no Testcontainers** | ADR-0103 |
| Run shape | `IngestRunShape` — one definition read by every run | spec 054 |
| Measurement switch | `AuditMeasurementOptions`, off by default on the production path | ADR-0135 |
| Listener topology | 4 listeners, native acks, audit only | ADR-0124, ADR-0126 |
| CI exclusion | `Category=Measurement` excluded by `ci.yml:179` | `IntegrationTestSelectionTests.cs:81` |
| Guards | `Ensure.That(x).IsNotNull()` | ADR-0105 |
| Async | `CancellationToken` last, no `ConfigureAwait` | ADR-0049 |
| Commits | Conventional Commits, **no `Co-Authored-By`** | ADR-0030, ADR-0086 |

**No new dependency, no new pattern, no new abstraction.** Every piece of apparatus this
feature needs — the paced shape, the stamps, the clock probe, the conditions guards, the
floor/ceiling arithmetic — already exists, built by specs 053 and 054.

---

## 6. Latency-budget impact

**N/A to constitution §IV.** Its six legs are camera->SFU, SFU->kiosk decode, the
presentation buffer, event->overlay state, composite+render, and headroom. **Audit
ingest is on none of them** — spec 009's own constitution check says *"NFR-001 / NFR-002
keep audit off every hot path; subscriber is async"*
(`specs/009-audit-observability/spec.md:335`). No leg is touched, and §VII's dashboard
obligation is not engaged.

The budget this feature is about is **spec 009's NFR-001**, and it does not move.

---

## 7. Assumptions, marked

- **A1** — `messages_unacknowledged` on the audit queues equals the in-flight population
  under `ProcessInParallelWithNativeAcks`. Inferred from ADR-0126's crash test (service
  down: 640 `messages_ready`, **0 unacked**, 0 consumers), not measured directly. **US3's
  first task verifies it before anything is concluded from it.**
- **A2** — Little's law gives a **mean**; NFR-001 is a **p99**. The reading bounds the
  leg, it does not by itself settle a p99. This is stated in the finding, not glossed.
- **A3** — The fixture propagates its own process environment into the service projects
  it launches. **Confirmed** by both of §1.0's runs: the switch was exported in the
  launching shell and the services reported `measurement switch: ON`, 1000 of 1000 rows
  stamped. US1 makes it independent of the shell.
- **A5** — Two runs are two samples, not a range. ADR-0135 recorded three clustered runs
  as though they were a spread and had to be corrected; §1.0 reports two runs that differ
  by 177× precisely so nobody reads either as the answer.
- **A4** — ADR-0136's and §1.0's write figures cross a host/container clock boundary that
  ADR-0136 declines to call established, and end at insert rather than commit. They bound
  the floor loosely, and this spec uses them only to say the floor is *far* from 50 ms —
  never to claim a value for it.

## 8. What this feature will not be able to say

- Whether NFR-001 is met **in production**. There is none (ADR-0130).
- A **p99** for the requirement's leg, unless US3's reading turns out unambiguous.
- Whether the time before handover is **reducible**, or by what. ADR-0136 refuses that
  inference and this spec does not reopen it.
- Whether **any** lever would help.
- Whether **the end-to-end span** — the thing everyone on this issue has actually been
  quoting — is acceptable. It has no budget anywhere in the repository. That gap is the
  most likely follow-up, and it is a product decision, not this feature's.

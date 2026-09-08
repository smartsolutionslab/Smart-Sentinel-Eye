# Verification — spec 103, ingest paths are countable

Phase 5 (ADR-0037), issues #622 / #619. Branch
`feat/622-619-ingest-paths-are-countable`, implementation commit `a73b1d63`.

**This file was written at phase 6, and that is itself a finding.** `tasks.md`
T007 requires phase 5's note *as a file* and T008 requires the counterfactuals
recorded in it; phase 5 observed the behaviour and reported it in prose but
committed nothing, so the artifact every later phase resumes from did not exist.
The dashboard readings below are phase 5's, transcribed. The counterfactual runs
below were **re-executed at phase 6** rather than transcribed — each one's
verbatim failure is quoted.

---

## 1. What was observed

`spec.md` §5 executed against a booted Aspire stack
(`dotnet run --project src/AppHost`, one stack per machine; token minted from
Aspire's **proxied** Keycloak endpoint, not the container's mapped port).

**Click-path:** Aspire dashboard → **Metrics** → resource `event-ingestion` →
instrument `sse.ingest.events`. The Aspire MCP tools expose logs and traces but
**no metrics tool**, so this reading is taken from the dashboard UI.

### Final readings — four series, one per source

| `source` | value |
|---|---|
| `plc` | **522** |
| `manual` | **1** |
| `webhook` | **1** |
| `inference` | **1** |
| **total** | **525** |

Four distinct series under one tag key, exactly `plc | inference | manual |
webhook` — the wire tokens `Source` already carries (FR-002, scenario 4). No
`fab` dimension, and no dimension other than `source` (FR-003).

### The controlled arithmetic — the `plc` series

The figure that matters is not the total but that each step moved it by exactly
the expected amount and nothing else did:

| step | expected | `plc` reads |
|---|---|---|
| starting point (prior run's traffic) | — | **321** |
| a drained MQTT batch of 200 commits | +200 | **521** |
| one further `plc` delivery | +1 | **522** |
| the same delivery again (re-delivery) | **+0** | **522** |

The last row is the one worth having: a re-delivered event returns
`EventAlreadyIngested` and the series does not move (scenario 5). The `manual`,
`webhook` and `inference` series stayed at 1 throughout — no step leaked into a
neighbouring bucket.

---

## 2. Limits — what this observation does not establish

Two, stated rather than left for a reader to assume verified.

- **FR-005 is not observable end-to-end.** The requirement is that a batch
  records *one measurement carrying 200*, not *200 measurements of 1*. The
  dashboard shows the accumulated total; both shapes read **521**. So the
  `+200` row above confirms the arithmetic and says nothing about the
  measurement count behind it. FR-005 rests on
  `A_batch_records_one_measurement_carrying_its_whole_count` and
  `A_stored_batch_is_counted_once_per_stored_envelope` — unit tests. `spec.md`
  FR-005 now carries this limit inline.

- **Scenarios 7 and 11 were not driven end-to-end.** Scenario 7 (an envelope
  refused for future skew does not count) and scenario 11 (a batch whose
  `SaveAsync` throws contributes nothing, and the singly-stored retry counts
  each event exactly once) both need a fault injected into a running stack.
  Neither was provoked. Both are covered by unit tests —
  `An_event_refused_for_future_skew_is_not_counted`,
  `An_envelope_refused_for_future_skew_is_not_counted`,
  `A_batch_whose_save_throws_counts_nothing`, and
  `The_retry_after_a_failed_batch_counts_each_event_exactly_once` — and
  counterfactuals 1 and 2 below show those tests bite.

**A third correction, from phase 6.** `spec.md` §5 step 6 said *"re-`POST` the
manual event with the same `eventId`"*. There is no `eventId` in
`IngestManualEventRequest` — it is `(DeviceId, Kind, OccurredAt, Payload)` and
`EventsEndpoints.Writes.cs` mints `EventIdentifier.New()` server-side. An
unkeyed retried `POST` is therefore a genuinely **new** event and is counted,
correctly; the only `POST` that does not increment is an `Idempotency-Key`
replay, and that short-circuits inside `IdempotentRequest.ExecuteCreateAsync`
**above** the handler, never reaching the `EventAlreadyIngested` branch. The
non-incrementing re-delivery observed above is the MQTT case — which is the one
scenario 5 actually names. Both sentences are fixed in `spec.md`.

---

## 3. Counterfactuals — re-run at phase 6, with their output

`plan.md` §5 lists four. All four were executed against `a73b1d63` by making the
named change, building `-c Release`, running the suite, and reverting.

### 1 — delete `IngestVolume.Record(envelope.Source);` from `IngestEventCommandHandler`

**Predicted:** the manual/webhook cases fail. **Observed:** 4 failures.

```
  Failed …IngestEventCommandHandlerTests.A_stored_event_is_counted_once_under_its_source(token: "webhook")
   Shouldly.ShouldAssertException : recorded.For(source)
  Failed …IngestEventCommandHandlerTests.A_stored_event_is_counted_once_under_its_source(token: "manual")
   Shouldly.ShouldAssertException : recorded.For(source)
  Failed …IngestEventCommandHandlerTests.A_duplicate_re_delivery_is_not_counted
   Shouldly.ShouldAssertException : recorded.TotalFor(Source.Plc)
  Failed …IngestEventBatchCommandHandlerTests.The_retry_after_a_failed_batch_counts_each_event_exactly_once
   Shouldly.ShouldAssertException : recorded.TotalFor(Source.Plc)
Failed!  - Failed: 4, Passed: 64, Skipped: 0, Total: 68
```

Two more than predicted, both correctly: `A_duplicate_re_delivery_is_not_counted`
asserts the *first* delivery counted before asserting the second did not, and the
retry test's fallback path goes through the single-event handler.

### 2 — move the batch handler's recording **before** `await events.SaveAsync(...)`

**Predicted:** *"a batch whose save throws counts nothing"* fails. This is the
assertion standing between the counter and a figure inflated by every retry.
**Observed:** 2 failures.

```
  Failed …IngestEventBatchCommandHandlerTests.The_retry_after_a_failed_batch_counts_each_event_exactly_once
   Shouldly.ShouldAssertException : recorded.TotalFor(Source.Plc)
  Failed …IngestEventBatchCommandHandlerTests.A_batch_whose_save_throws_counts_nothing
   Shouldly.ShouldAssertException : recorded.Measurements
Failed!  - Failed: 2, Passed: 66, Skipped: 0, Total: 68
```

### 3 — drop the `count <= 0` guard from `IngestVolume.Record`

**Predicted:** the "records nothing" cases fail. **Observed:** 2 failures.

```
  Failed …Ingress.IngestVolumeTests.A_negative_count_records_nothing
   Shouldly.ShouldAssertException : recorded.Measurements
  Failed …Ingress.IngestVolumeTests.A_count_of_zero_records_nothing
   Shouldly.ShouldAssertException : recorded.Measurements
Failed!  - Failed: 2, Passed: 66, Skipped: 0, Total: 68
```

### 4 — remove the `.AddMeter(IngestVolume.MeterName)` registration

**`plan.md` predicted that no unit test would fail, and that prediction was
wrong.** It is the reason this section exists in the form it does: phase 6 wrote
the test the plan said could not be written — ~50 lines, 0.7 s, no Docker, no
Aspire fixture, and **no new package**, since `MeterProvider`,
`BaseExporter<Metric>` and `BaseExportingMetricReader` are public SDK types
already flowing in through `ServiceDefaults` (FR-008 holds).

**Observed with the registration removed:**

```
  Failed SmartSentinelEye.EventIngestion.Infrastructure.Tests.IngestVolumeRegistrationTests
         .The_ingest_volume_meter_is_registered_with_the_meter_provider [394 ms]
  Error Message:
   Shouldly.ShouldAssertException : exporter.Names
    should contain
"sse.ingest.events"
    but was actually
[] (System.Collections.Generic.List`1[System.String])

Additional Info:
    SmartSentinelEye.IngestVolume is not registered, so sse.ingest.events records into nothing
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

**And green with it restored:**

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 5 s
```

**What still fails to fail** is the *export* leg. No unit test proves the OTLP
exporter ships the figure or that the dashboard renders the series with their
tag values. That is what §1's reading above establishes, and it is why phase 5
is not skippable — but the reason is now "the sink is unreachable from a unit
test", not "the meter is".

`spec.md` §5, `plan.md` §5 #4, `tasks.md` T007/T008 and the registration's own
comment all said some form of *"unit tests cannot prove the meter is
registered"*. All are corrected, because **#625 adds two more instruments** and
would have inherited the false constraint verbatim.

---

## 4. Latency-budget impact — the ≤ 200 ms `event → overlay state` leg

**Not `N/A`.** `IngestEventCommandHandler` and `IngestEventBatchCommandHandler`
sit inside that leg — an event being accepted through to its effect being
applied — so constitution §IV's citation obligation attaches, and `spec.md` §6
says so explicitly.

The cost added is **one post-commit `Counter<long>.Add` per stored event**, and
one per *source per batch* on the MQTT path:

- `Counter<T>.Add` is a lock-free interlocked update against an aggregator.
- The `TagList` is a single-entry stack struct; it allocates nothing.
- It performs no I/O and runs **outside** the transaction — the OTLP exporter
  drains on its own interval, off the ingest thread.

The addition is orders of magnitude below run-to-run variance on this leg, so
there is no figure to quote and none is claimed.

**Constitution §IV's table is unchanged, and phase 4 did not edit it.** No leg
moves state. A counter of arrivals is not a latency figure, and recording one as
though it were would be the "measured before anyone read its figure" error §IV
names.

---

## 5. Suites re-run green

| Suite | Result |
|---|---|
| `EventIngestion.Application.Tests` | 68 passed, 0 failed |
| `EventIngestion.Infrastructure.Tests` | 46 passed, 0 failed |
| `Architecture.Tests` | 353 passed, 0 failed |

All under `dotnet build -c Release` (CI uses `TreatWarningsAsErrors`), 0 errors,
0 warnings.

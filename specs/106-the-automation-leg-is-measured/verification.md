# Verification note — spec 106

**Issue:** #749 (spec 007 T099) · **Branch:** `test/749-the-automation-leg-is-measured`
**Covers:** SC-001 (two runs), SC-002 and SC-003, the two counterfactuals, and the
phase-6 remediation
**Date:** 2026-09-08 (runs) / 2026-09-09 (this note)

**Machine:** one developer laptop — Intel Core i7-6700HQ, 4 cores / 8 threads, 24 GB RAM,
Windows 11 Pro 10.0.26100, Docker Desktop 28.3.3, .NET SDK 10.0.401. **One Aspire stack at
a time**, nothing else driving the box. Everything below is `-c Release`.

---

## The figure

> **The accept→decide p95 was between 25 and 35 ms**, over three runs of 100 events each,
> on one developer machine, against a written budget of 100 ms.

**State it as that range, never as one number.** SC-001's two runs are 25.2 ms and
32.2 ms — a 28 % spread. A third run, taken after the phase-6 assertion change purely to
show the file still passes, landed at **34.5 ms** *outside* that pair's range, which is
the best argument this note can offer for its own rule: two runs did not bound the
population, and a single number would have been wrong within a day. The pass is safe
because the *margin* is 3–4×, not because any figure is precise. Quoting "25.2 ms" or "the
p95 is 28 ms" claims a precision three runs on one laptop cannot support.

| | run 1 (SC-001) | run 2 (SC-001) | run 3 (post-remediation) |
|---|---|---|---|
| n | 100 | 100 | 100 |
| min | 6.5 ms | 6.6 ms | 7.1 ms |
| p50 | 10.4 ms | 10.8 ms | 12.6 ms |
| **p95** | **25.2 ms** | **32.2 ms** | **34.5 ms** |
| p99 | 32.5 ms | 50.8 ms | 50.5 ms |
| max | 48.1 ms | 51.3 ms | 72.2 ms |
| headroom to the 100 ms budget | 4.0× | 3.1× | 2.9× |

SC-001 asks for two runs and names two; run 3 is reported beside them because leaving out
a figure that widens the range is how a range becomes a number.

### The one sentence this licenses

> Over 100 events on this machine, the interval from EventIngestion **accepting** a
> plant-floor event to Automation **deciding** its action had a p95 between 25 and 35 ms.

### What it does not license

- **Not** "NFR-001 holds". NFR-001's span is `FabEventIngestedV1` *consumed* → action V1
  *on the bus*. Neither endpoint is observable from a test process; this span overshoots at
  the head (EventIngestion's dispatch, its outbox release, one RabbitMQ hop, Wolverine's
  deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker
  send all happen after Moment B is stamped).
- **Not** "Automation's leg is within 100 ms" — a large part of the head belongs to
  EventIngestion.
- **Not** "constitution §IV's `event → overlay state` leg is measured". That leg runs to
  *effect applied*; this is a proper prefix of it. **§IV's table is unchanged by this
  branch** — see below.
- **Not** transferable off this machine. Moment A is EventIngestion's clock and Moment B is
  Automation's; they are only comparable because the Aspire fixture runs both on one OS
  clock. On a distributed deployment the difference is not meaningful.

---

## Run 1 — verbatim

`dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj -c Release
--filter "FullyQualifiedName~AcceptToDecide" --logger "console;verbosity=detailed"`

```
  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_p95_stays_within_the_automation_leg_budget [22 s]
  Standard Output Messages:
 warmed up over 20 events on warm904a2d30
 drove 100 measured events on meas904a2d30
 [accept→decide] n=100 min=6.5 p50=10.4 p95=25.2 p99=32.5 max=48.1 ms — budget 100 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_has_not_regressed_by_an_order_of_magnitude [2 s]
  Standard Output Messages:
 warmed up over 3 events on warm8cbde73b
 drove 5 measured events on meas8cbde73b
 [accept→decide] n=5 min=12.2 p50=17.2 p95=28.1 p99=29.6 max=30.0 ms — budget 400 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

Test Run Successful.
Total tests: 3
     Passed: 3
 Total time: 4,6227 Minutes
```

The third test in that run is `TrapProbeTests`, the throwaway probe described under
*Counterfactual M1*. It was never committed.

## Run 2 — verbatim

```
  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_p95_stays_within_the_automation_leg_budget [48 s]
  Standard Output Messages:
 warmed up over 20 events on warm845f4083
 drove 100 measured events on meas845f4083
 [accept→decide] n=100 min=6.6 p50=10.8 p95=32.2 p99=50.8 max=51.3 ms — budget 100 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_has_not_regressed_by_an_order_of_magnitude [1 s]
  Standard Output Messages:
 warmed up over 3 events on warm4eb53e29
 drove 5 measured events on meas4eb53e29
 [accept→decide] n=5 min=10.0 p50=11.1 p95=30.8 p99=34.4 max=35.3 ms — budget 400 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

Test Run Successful.
Total tests: 2
     Passed: 2
 Total time: 3,3626 Minutes
```

### Why these two, and not the two earlier ones

Five runs of the measurement fact exist in all. Recording which two are SC-001's, and why
the other three are not, is the point of this section — otherwise the pair is chosen after
the fact.

| run | p95 | why it is / is not one of the two |
|---|---|---|
| A | 28,6 | **Not counted.** Printed under a German locale (`28,6`), which a reader elsewhere may take for 286. The artefact line was moved to `CultureInfo.InvariantCulture` because of this run. |
| B | 30.8 | **Not counted.** The measurement fact passed but the guard fact failed in *arrange* with a 400: the run suffix was a Guid v7, whose leading hex digits are the top bits of its millisecond timestamp, so both facts minted the same rule name seconds apart. Fixed by taking the suffix from a v4. |
| **C** | **25.2** | **Run 1 above.** First clean run of the committed file. |
| **D** | **32.2** | **Run 2 above.** |
| E | 34.5 | Taken after the phase-6 assertion change, to show the file still passes rather than to measure. Reported in the table at the top and quoted verbatim at the end of this note. |

Runs A and B are reported rather than discarded silently: their figures (28.6, 30.8) sit
inside the 25–35 range and so do not change the reading, but a note that picks its two
runs without saying what else it ran is not evidence.

---

## Counterfactual M1 — the pipeline fires but skips the work

`RuleEvaluator` mutated in a dirty tree to return a hard-coded `SetVariableValue` effect
with value `"0"`, consulting neither the cache nor the predicate. Reverted immediately
after; nothing was committed.

**Predicted** (`tasks.md`, written before the run): the p95 assertion passes and the figure
*improves*; the run fails on `rowsWithExpectedValue`, and on the per-iteration poll.

**Observed — the poll caught it, ~120 s before any SQL ran.** Both facts died at
iteration 0 of the *warm-up*:

```
  Failed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_p95_stays_within_the_automation_leg_budget [2 m]
  Error Message:
   System.TimeoutException : iteration 0 on 'warm8dff9d02' never reached '98' within 120 s; a 200 carrying '0' rather than '98'. A dropped sample must not be silently excluded from the percentile, so the run fails here rather than measuring the events that did land.

  Failed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_has_not_regressed_by_an_order_of_magnitude [2 m]
  Error Message:
   System.TimeoutException : iteration 0 on 'warme9324164' never reached '98' within 120 s; a 200 carrying '0' rather than '98'. A dropped sample must not be silently excluded from the percentile, so the run fails here rather than measuring the events that did land.
```

**So the counts were never reached, and the prediction as written is not what happened.**
The claim that *the counts* catch M1 was demonstrated separately, by a throwaway
`TrapProbeTests` that drove 20 events with the poll removed and printed the span without
asserting it:

```
  Passed SmartSentinelEye.Integration.Tests.Automation.TrapProbeTests.Probe_prints_the_span_without_asserting_the_counts [1 m 23 s]
  Standard Output Messages:
 [PROBE] variable=prb16e8820e totalRows=20 rowsWithExpectedValue=0 min=7,7 p50=9,6 p95=14,4 p99=22,7 max=24,8 ms
 [PROBE] distinct payload Values observed: [0]
 [PROBE] a p95-only assertion at 100 ms would have PASSED
```

That is the finding in one line: **under M1 the latency figure gets *better* — 14.4 ms
against a healthy 25–32 — and a p95-only test passes.** `rowsWithExpectedValue` drops to
0, which is what fails the committed test once the poll is out of the way.

The probe is **not** committed and no longer exists; it is quoted here because it is the
only record of the second line of defence being exercised. The order of the two, corrected
in the class remarks and in `tasks.md`, is: the per-iteration wait first, the counts
behind it.

**Not run: the "no Active rule" counterfactual** the original SC-002 named.
`ActivateRuleAsync` reads back `state == "Active"` and fails the arrange, so that state is
unreachable from inside the test.

## Counterfactual M2 — the pipeline is genuinely slower

`await Task.Delay(150, cancellationToken)` inserted in `FabEventIngestedV1Handler.Handle`
**before** `DateTimeOffset requestedAt = clock.UtcNow;`. Placement is the point: after that
stamp it would not appear in the figure, which is the tail undershoot the spec names.

**Observed — the whole `tests/Integration.Tests/Automation/` folder run under the
mutation: 34 tests, 33 passed, 1 failed, and the one that failed is this measurement.**

```
 [accept→decide] n=100 min=157.9 p50=167.4 p95=176.9 p99=186.7 max=192.0 ms — budget 100 ms. …

        span.P95
    should be less than or equal to
100d
    but was
176,8893d

Additional Info:
    the p95 of the accept→decide span was 176.9 ms over 100 events, against a budget of 100 ms. This span is not NFR-001's — it carries EventIngestion's dispatch and a broker hop at the head and stops before the publish at the tail — so this failure does not by itself convict Automation of breaching its leg. Split the span before concluding: IngestThroughputMeasurementTests already bounds the ingest part.
```

```
Total tests: 34
     Passed: 33
     Failed: 1
```

Every prediction here held. The p95 rose by ≈150 ms exactly as the injected delay
predicts, and **no other Automation integration test reddened** —
`EventReachesItsEffectsTests`, `FirstPublishPerTypeTests`, `FirstEventSplitTests`,
`RuleLifecycleIntegrationTests`, `RuleReadIntegrationTests`,
`RuleFabResolutionIntegrationTests` and `CrossFabEvaluationIntegrationTests` all passed
with 150 ms added to every event. That gap is what #749 exists to close, and it is now an
observation rather than a claim.

**One thing M2 also shows about the guard**: under the mutation the CI guard fact
*passed*, at `n=5 p95=180.2 ms` against its 400 ms bound. That is the guard behaving as
designed — it catches an order-of-magnitude regression, not a budget breach — and it is
why the measurement fact exists separately.

---

## SC-003 — CI selection

The guard fact carries no category trait and the measurement fact carries
`[Trait("Category", "Measurement")]`, which `.github/workflows/ci.yml`'s
`Category!=Measurement&Category!=Disruptive&Category!=Maintenance` filter excludes. So CI
gains exactly one selected test (8 events) and never runs the 100-event measurement.

**A consequence worth stating, because it is where a figure would be misread.** The guard
prints an `[accept→decide …]` line too, and CI's log is the *only* place an accept→decide
line will ever appear automatically. At n=5, `percentile_cont(0.95)` is the near-maximum
of five samples against a bound four times the budget — not a measurement. The guard's
printed line was relabelled at the phase-6 gate to say so where the number is:

```
[accept→decide GUARD — not a measurement; n=5, bound is 4× the budget] n=5 min=… p95=… — budget 400 ms. Span: …
```

---

## Constitution §IV is unchanged, deliberately

`.specify/memory/constitution.md` is not touched by this branch, and in particular the
`event → overlay state` row still reads *recorded, not yet readable*.

That cell is about `EventToOverlayLatency`, the histogram covering **accept → effect
applied**. This measures **accept → decided**, a proper prefix. Producing a figure for a
prefix does not make the leg's own figure readable, and writing *measured* into that cell
on the strength of these two runs would be exactly the over-claim §IV warns about: *"a leg
recorded as measured before anyone has read its figure claims a discharge nobody earned."*

What these runs license is a **note** beside that row. Writing it is a human's decision,
not phase 4's or phase 6's.

---

## Phase-6 remediation (2026-09-09)

The review after the runs found one assertion gap that changes what the test can catch,
and it was fixed on this branch:

**`COALESCE(…, -1)` was legible but not enforcing.** The SQL coalesces an empty population
to `-1` so a reader cannot mistake it for a good figure — but `-1 ≤ 100` is *true*, so
`ShouldBeLessThanOrEqualTo(budget)` passed on the sentinel exactly as it would on `0`. The
one reachable path where that mattered: if Automation stopped forwarding `RootIngestedAt`,
every delta is NULL, `count(*)` still equals `measured` so assertion (1) passes, the
aggregates all return NULL, and only assertion (2) held the line. The p95 is now bounded
**below at zero** as well as above at the budget, and the file's remarks say the ordering
does the work rather than the sentinel.

Also corrected, all in comments and printed text: the guard's artefact label (above); the
count assertion's failure message, which sent a reader to the rule cache, the handler and
the broker when the per-iteration wait has already proved every effect landed; the claim
that the counts guard "a short-circuited predicate", which they do not — `cycleTime`
cycles `1..30` and the predicate is `<= 30`, so it is true for every event this file
publishes, and the gap is now stated; and the warm-up line, which drove 21 events (one
readiness event plus 20) and printed 20.

**The two runs above predate that last correction**, so they print `warmed up over 20
events`; the current file prints `warmed up over 21 events on … — one readiness event,
then 20`. The events driven are unchanged, and no figure moves.

### Re-run after the assertion change

The measurement was **not** re-taken — runs 1 and 2 stand as SC-001's evidence. The file
was re-run once, verbatim below, only to show that the added lower bound does not redden
it and that the guard's new label prints. Its p95 of 34.5 ms is folded into the range at
the top of this note rather than left out.

`dotnet build -c Release` first (0 warnings, 0 errors), then
`dotnet test … --no-build --filter "FullyQualifiedName~AcceptToDecideLatencyTests"
--logger "console;verbosity=detailed"`:

```
  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_p95_stays_within_the_automation_leg_budget [50 s]
  Standard Output Messages:
 warmed up over 21 events on warmd85729e0 — one readiness event, then 20
 drove 100 measured events on measd85729e0
 [accept→decide] n=100 min=7.1 p50=12.6 p95=34.5 p99=50.5 max=72.2 ms — budget 100 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

  Passed SmartSentinelEye.Integration.Tests.Automation.AcceptToDecideLatencyTests.Accept_to_decide_has_not_regressed_by_an_order_of_magnitude [1 s]
  Standard Output Messages:
 warmed up over 4 events on warm6109c9ff — one readiness event, then 3
 drove 5 measured events on meas6109c9ff
 [accept→decide GUARD — not a measurement; n=5, bound is 4× the budget] n=5 min=8.3 p50=9.3 p95=20.4 p99=22.1 max=22.5 ms — budget 400 ms. Span: EventIngestion accepting the plant-floor event (Metadata.RootIngestedAt) → Automation deciding its action (Metadata.OccurredAt). This is NOT NFR-001's consume→published span: it overshoots at the head (ingest dispatch, outbox release, one broker hop, deserialise) and undershoots at the tail (the publish, Wolverine's flush and the broker send are after the stamp).

Test Run Successful.
Total tests: 2
     Passed: 2
 Total time: 3,5107 Minutes
```

The lower bound is not exercised by a green run — nothing here produces an empty
population — so it is asserted as a guard on a state the count assertions also catch, not
as a substitute for them. What this run does show is that it costs nothing when the
population is real.

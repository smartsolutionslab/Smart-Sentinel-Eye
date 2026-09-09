# Verification note — spec 109

**Issue:** #1956 · **Branch:** `perf/1956-the-instrument-before-the-latency`
**Covers:** US1 (the fixture stamps the breakdown), US2 (a verdict states its rate and
reports an interval), US3 (the handover located from the broker) — and an **independent
re-measurement of all three phase-4b conclusions**
**Date:** 2026-09-09

**Machine:** one developer laptop — Intel Core i7-6700HQ, 4 cores / 8 threads, 24 GB RAM,
Windows 11 Pro 10.0.26100, Docker Desktop 28.3.3, .NET SDK 10.0.401. One Aspire stack at a
time, nothing else driving the box. **Fourteen fixture boots**, 2 of them `-c Debug` and
12 `-c Release`; the configuration is stated per run because it moves the figures by
roughly 2×.

**Release build before anything was run:** `dotnet build -c Release` → `Build succeeded.
0 Warning(s) 0 Error(s)` (1 m 56 s).

---

## 1. What #1956 actually said

This is the first thing the issue needs back, because every later argument on it has been
conducted against numbers nobody re-read.

| population | p50 | p99 | max | rate |
|---|---|---|---|---|
| 1 000 events | **4 800 ms** | **9 469 ms** | 9 586 ms | ~20 ev/s (the body's own figure) |
| 100 events | 4 624 ms | 5 037 ms | 5 045 ms | ~20 ev/s |

Measured as `received_at - occurred_at` in SQL, on the Aspire fixture, driving
`SystemVariableValueChangedV1` by repeated value sets. The issue is explicit that this is
a **superset** of NFR-001's leg — *"aggregate mutation → outbox → RabbitMQ → the audit
handler stamping `ReceivedAt` … a superset of the named leg, short by the final insert"* —
and then reasons from it as though it were the leg. Its three conclusions were: latency
**grows through the run** (a backlog forming), the 5 045 ms ceiling *"has the shape of a
polling interval"*, and the cause is a drain rate, a batching boundary or a flush
interval.

**None of those figures reproduces today, on the same instrument, in the same fixture.**
The historic unpaced shape — the same fact, the same generator, the same SQL — ran eight
times in this verification at 46.7–96.7 ev/s and gave **p50 7.3–12.3 ms, p99 20.7–91.2 ms,
max 38.7–216.6 ms**. That is 390–660× below the p50 the issue reports and 100–460× below
its p99 — two to three orders of magnitude, on the same fixture and the same query.
ADR-0124 (four listeners) and ADR-0126 (native acks) both landed after the issue was
written; the issue's numbers describe a pipeline that no longer exists.

---

## 2. The three phase-4b conclusions, and what independent re-measurement did to them

| # | Phase 4b's conclusion | Verdict here |
|---|---|---|
| 1 | The seconds are a **first-drive-after-boot** effect, not sustained load | **Confirmed in direction, 7 of 7 within-boot pairs — and not deterministic.** Every second paced drive was faster than the first drive of the same boot, by 3.2× to 143×. But 2 of 10 first drives were *already* fast (402.4 ms, 54.1 ms), so "the first drive is seconds, 4/4" states as a rule what is a bimodal effect with a strong bias. |
| 2 | NFR-001's own leg is **~10–20 ms**, read from broker state (T104, NEAR ZERO twice) | **Reproduced only on a warm stack, refuted on a cold one, and not safe even warm.** Warm: 16.8 and 11.7 ms, peak 4 and 2 — NEAR ZERO, matching 4b; but a third warm run read **51.8 ms**, peak 21, which the fact itself calls **DEEP**. Cold (the handover fact as the first paced drive of its boot): **237.8 / 794.1 / 402.2 / 306.6 ms, peak 64 / 115 / 64 / 115** — DEEP, four times out of four. Phase 4b ran T104 twice and both runs were warm and both landed at the bottom of the warm range; the cold half of the distribution was never sampled. |
| 3 | Every figure quoted on #1956 is the **ceiling** of a superset span | **Confirmed** — arithmetically, at `IngestSpanMeasurement.cs:367` and `:420` (`EXTRACT(EPOCH FROM (received_at - occurred_at))`) against `IngestAttribution.cs:69-99`, and in every run below, where the printed interval's width equals "before handler" exactly. |

**The correction that matters is #2.** Phase 4b concluded *"the handover is at handler
entry, the requirement's span is the floor, and the seconds seen end to end are queue wait
**before** delivery — outside NFR-001's leg"*. On a cold stack that is false: 64–115
deliveries sit **unacknowledged** at the broker, which under `ProcessInParallelWithNativeAcks`
means they have been handed over and are inside NFR-001's leg. In that mode the implied
leg is 307–794 ms against a 50 ms budget, and NFR-001 is missed **on its own terms**.

So the honest reading of US3, over seven runs rather than two, is the one the spec's own
scenario asked for and phase 4b did not reach:

> **The handover reading is bimodal, and the budget sits between the modes.** Warm, the
> population is 1–5 messages and the implied leg is 10–52 ms — inside the budget four
> times in five and outside it once. Cold, the population is 22–75 with peaks of 64–115,
> and NFR-001 is missed on its own leg by 6–16×. **Which mode a run lands in is decided
> before the measurement starts**, and no run in this repository has been taken against a
> stack up for longer than a test session. That is the ambiguous result US3's own scenario
> names, and its consequence is the one that scenario writes: **the next step is a per-row
> transport-receipt stamp**, which spec 109 does not take.

**"NFR-001 is met" is therefore not what this branch establishes**, and the finding should
not be written that way. What it establishes is that the question is now answerable, and
that answering it needs the stamp.

---

## 3. Every paced drive, unaveraged

**The commands.** `Logging__LogLevel__Default=Warning` exported in the launching shell for
every boot except 12; **nothing else exported** — in particular not the measurement switch,
which is US1's whole point.

```sh
# boots 1, 2 (Debug); 3, 4 (add -c Release)
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj \
  --filter "FullyQualifiedName~NFR001_AuditIngestLatencyTests" -l "console;verbosity=detailed"

# boots 5, 6 — the fact that is fast in second position, run first and alone
dotnet test … -c Release --filter "FullyQualifiedName~Where_the_ingest_span_goes" …

# boots 7, 8, 9, 13 — the handover fact as the first paced drive of its boot
dotnet test … -c Release --filter "FullyQualifiedName~AuditHandoverLegTests" …

# boots 10, 11, 14 — the same handover fact, fourth, on a stack three drives warm
dotnet test … -c Release \
  --filter "FullyQualifiedName~AuditHandoverLegTests|FullyQualifiedName~NFR001_AuditIngestLatencyTests" …

# boot 12 — the T013 check, with nothing exported at all
dotnet test … -c Release --filter "FullyQualifiedName~NFR001_AuditIngestLatencyTests" …
```

One `dotnet test` invocation = one fixture boot. Within a boot, xUnit runs the collection
serially in the order printed. "Position" is the position of the drive within its boot;
every drive is 1 000 measured events after a 100-event warm-up, 50 writers paced to
100 ev/s (`IngestRunShape`).

| boot | cfg | fact | pos | achieved | typical observed span | requirement interval (floor–ceiling) | 50 ms falls |
|---|---|---|---|---|---|---|---|
| 1 | Debug | Requirement_span… | 1 | 96.5 ev/s | **3 788.5 ms** | 11.7 – 3 800.1 ms | **inside** |
| 1 | Debug | Where_the_ingest… | 2 | 99.6 ev/s | 1 168.6 ms | 7.8 – 1 176.3 ms | inside |
| 2 | Debug | Requirement_span… | 1 | — | **failed cold** (Polly 10 s at `DefineAsync`) | — | — |
| 2 | Debug | Where_the_ingest… | 1* | 97.1 ev/s | **5 146.1 ms** | 10.8 – 5 156.9 ms | inside |
| 3 | Release | Requirement_span… | 1 | 99.2 ev/s | **2 674.6 ms** | 5.8 – 2 680.4 ms | **inside** |
| 3 | Release | Where_the_ingest… | 2 | 99.5 ev/s | **66.9 ms** | 6.5 – 73.4 ms | inside |
| 4 | Release | Requirement_span… | 1 | 98.7 ev/s | **5 538.4 ms** | 8.0 – 5 546.4 ms | **inside** |
| 4 | Release | Where_the_ingest… | 2 | 99.6 ev/s | **39.2 ms** | 5.1 – 44.1 ms | **above** |
| 5 | Release | Where_the_ingest… | 1 | 99.8 ev/s | 402.4 ms | −0.5 – 401.9 ms | inside |
| 6 | Release | Where_the_ingest… | 1 | 99.7 ev/s | **54.1 ms** | 5.6 – 59.7 ms | inside |
| 10 | Release | Requirement_span… | 1 | 99.7 ev/s | **1 849.9 ms** | 6.7 – 1 856.6 ms | **inside** |
| 10 | Release | Where_the_ingest… | 2 | 99.5 ev/s | **21.1 ms** | 3.3 – 24.3 ms | **above** |
| 11 | Release | Requirement_span… | 1 | 99.5 ev/s | **967.8 ms** | 6.1 – 973.9 ms | **inside** |
| 11 | Release | Where_the_ingest… | 2 | 99.9 ev/s | **12.2 ms** | 3.0 – 15.1 ms | **above** |
| 12 | Release † | Requirement_span… | 1 | 99.6 ev/s | **2 321.6 ms** | 5.5 – 2 327.1 ms | **inside** |
| 12 | Release † | Where_the_ingest… | 2 | 99.6 ev/s | 31.5 ms | 5.0 – 36.6 ms | above |
| 14 | Release | Requirement_span… | 1 | 99.6 ev/s | **2 012.0 ms** | 7.7 – 2 019.8 ms | **inside** |
| 14 | Release | Where_the_ingest… | 2 | 99.6 ev/s | **14.1 ms** | 2.0 – 16.1 ms | **above** |

\* boot 2's first drive never happened — the fact ahead of it died in *arrange*, so
`Where_the_ingest_span_goes` was the boot's first paced drive.
† boot 12 ran with **nothing exported** (the T013 check, §5) — services at `Information`.

Tail bands, same runs, in the same order: 5 219.1 / 2 281.4 / — / 6 220.2 / 3 956.1 /
882.0 / 6 215.9 / 408.8 / 2 762.1 / 1 626.7 / 3 418.3 / 129.0 / 2 786.2 / 63.9 /
3 868.2 / 184.8 / 3 062.4 / 370.4 ms observed. Boot 12's second drive printed its whole
breakdown and then failed on the log-level guard (§5), which is what "the conditions first,
before anything that can fail" is for.

**Every run:** 1 000 of 1 000 rows carried the stamps, 0 missing; per-row residual
**0.000 ms** on both bands; clock standing **Established** every time (offsets −3.20 to
+1.05 ms, worst case 4.07 ms); measurement switch **ON** in all ten boots that read it,
with nothing exported for it. (The four handover boots do not read the stamps and print no
switch line.)

### The within-boot pairs, which is where the first-drive claim lives

| boot | first drive | second drive | ratio |
|---|---|---|---|
| 1 (Debug) | 3 788.5 ms | 1 168.6 ms | 3.2× |
| 3 | 2 674.6 ms | 66.9 ms | 40× |
| 4 | 5 538.4 ms | 39.2 ms | 141× |
| 10 | 1 849.9 ms | 21.1 ms | 88× |
| 11 | 967.8 ms | 12.2 ms | 79× |
| 12 | 2 321.6 ms | 31.5 ms | 74× |
| 14 | 2 012.0 ms | 14.1 ms | 143× |

**7 of 7.** The second drive of a boot was never slower, and the slow mode never appeared
on a second drive. The two facts are the same code — both call
`IngestSpanMeasurement.RunAsync` with identical arguments — so position is the only
variable between them, and boot 2 supplies the counterfactual from the other side:
`Where_the_ingest_span_goes`, which is 12.2–66.9 ms in every second position, produced
**5 146.1 ms** when it became its boot's first paced drive.

**Where it stops being a rule.** Boots 5 and 6 ran `Where_the_ingest_span_goes` alone, as
the only paced drive of a fresh boot, and got 402.4 ms and 54.1 ms. Same configuration,
same position, same fact — and one of them is inside the range the second drives occupy.
So the effect is a **bimodal pipeline whose slow mode is strongly associated with the
first drive after a boot**, not a deterministic warm-up cost. ADR-0136's "bistable at the
knee" survives; what phase 4b added is that the modes are not equiprobable — 8 of 10 first
drives were slow, 0 of 7 second drives were.

**A 100-event warm-up does not remove it, and every run above is the evidence**: the
warm-up is inside `RunAsync`, so all eight slow first drives had already pushed 100 events
through the same handler before their first measured event.

---

## 4. The handover leg, read from the broker (T104)

`AuditHandoverLegTests` — 3 000 events paced to 100 ev/s, `messages_unacknowledged` summed
across `audit-observability.*` every 250 ms through the drive **and** the drain, quiescence
confirmed at 0 before each. Little's law over the population NFR-001's leg contains.

| boot | position | quiescent | landed | window | drain λ | samples | peak | mean L | **implied leg W** | the fact's own reading |
|---|---|---|---|---|---|---|---|---|---|---|
| 7 | 1 (cold) | 0 | 3 000 | 32.2 s | 93.3 ev/s | 94 (90 non-zero) | **64** | 22.18 | **237.8 ms** | **DEEP** |
| 8 | 1 (cold) | 0 | 3 000 | 31.8 s | 94.4 ev/s | 91 (86 non-zero) | **115** | 74.95 | **794.1 ms** | **DEEP** |
| 9 | 1 (cold) | 0 | 3 000 | 31.9 s | 94.0 ev/s | 92 (89 non-zero) | **64** | 37.79 | **402.2 ms** | **DEEP** |
| 13 | 1 (cold) | 0 | 3 000 | 32.2 s | 93.2 ev/s | 92 (80 non-zero) | **115** | 28.58 | **306.6 ms** | **DEEP** |
| 10 | 4 (warm) | 0 | 3 000 | 30.0 s | 99.9 ev/s | 88 (66 non-zero) | 4 | 1.68 | **16.8 ms** | NEAR ZERO |
| 11 | 4 (warm) | 0 | 3 000 | 30.3 s | 99.0 ev/s | 93 (70 non-zero) | 2 | 1.16 | **11.7 ms** | NEAR ZERO |
| 14 | 4 (warm) | 0 | 3 000 | 30.0 s | 99.9 ev/s | 92 (67 non-zero) | 21 | 5.17 | **51.8 ms** | **DEEP** |

Phase 4b, for comparison: mean 1.01 / 1.94, drain 99.7 / 98.9 ev/s, implied **10.1 /
19.6 ms**, peak 2 / 5 — indistinguishable from boots 10 and 11, and nothing like boots
7, 8, 9 or 13.

**Five of these seven runs print DEEP.** Four cold, and one warm: boot 14 drained at a
full 99.9 ev/s and still averaged 5.17 messages in flight, for **51.8 ms** — 1.8 ms the
wrong side of the budget. So even the warm mode is not safely inside: warm readings across
this verification and phase 4b are **10.1, 11.7, 16.8, 19.6 and 51.8 ms**, straddling 50.

**The drain rate is the tell for the cold mode.** Cold, the pipeline drains at
93.2–94.4 ev/s against an offered 100 — it cannot keep up, a backlog forms, and under
native acks that backlog is *unacknowledged*, i.e. **inside** the leg. Warm, it drains at
99.0–99.9. This is the same phenomenon §3 measures from the other end, which is why the
two instruments agree instead of contradicting each other.

**A1 is unaffected and holds**: 0 unacknowledged with the service quiescent in all seven
runs, non-zero with handlers mid-flight in all seven.

---

### Verbatim — the two handover runs that decide it

Boot 9, the handover fact as the first paced drive of its boot:

```
  Passed …AuditHandoverLegTests.Audit_handover_leg_implied_by_the_broker_population_at_100_events_per_second [42 s]
 queues sampled                        : audit-observability.*
unacknowledged before the drive       : 0
events driven, paced                  : 3000 at 100 ev/s intended
rows landed                           : 3000
window (drive + drain)                : 31.9 s
achieved drain rate                   : 94.0 ev/s
broker samples                        : 92 (89 non-zero, peak 64)
mean messages_unacknowledged (L)      : 37.79
implied mean leg, L / λ (W)           : 402.2 ms
NFR-001 budget                        : 50 ms
the reading this run supports         : DEEP — the prefetch buffer holds deliveries unacknowledged while they wait for a handler slot, that wait is inside NFR-001's leg, and the requirement is missed on its own terms; the next step would be a per-row transport-receipt stamp, which spec 109 does not take
```

Boot 11, the same fact, same command plus the three NFR-001 facts ahead of it, so it runs
fourth on a warm stack:

```
  Passed …AuditHandoverLegTests.Audit_handover_leg_implied_by_the_broker_population_at_100_events_per_second [39 s]
 queues sampled                        : audit-observability.*
unacknowledged before the drive       : 0
events driven, paced                  : 3000 at 100 ev/s intended
rows landed                           : 3000
window (drive + drain)                : 30.3 s
achieved drain rate                   : 99.0 ev/s
broker samples                        : 93 (70 non-zero, peak 2)
mean messages_unacknowledged (L)      : 1.16
implied mean leg, L / λ (W)           : 11.7 ms
NFR-001 budget                        : 50 ms
the reading this run supports         : NEAR ZERO — the handover is at handler entry, the requirement's span is the floor of the interval, and the seconds seen end to end are queue wait before delivery, which no budget in this repository covers
```

**Nothing differs between those two commands but what ran before the fact.**

### Verbatim — one within-boot pair (boot 4, Release)

```
  Passed …NFR001_AuditIngestLatencyTests.Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict [29 s]
  Standard Output Messages:
 environment                           : Aspire test fixture
endpoint reached                      : http://localhost:60386/
rate, intended → achieved             : 100 → 98.7 ev/s
service log level                     : Warning
measurement switch                    : ON
rows measured                         : 1000 (0 missing stamps)
 --- typical event (medians over every row) ---
 rows measured                         : 1000 (0 missing stamps)
observed span (occurred → received)   : 5538.4 ms
  before handler (two processes, one clock): 5538.3 ms
  in handler (one clock, exact)       : 0.0 ms
  unattributed (median arithmetic)    : 0.0 ms
  per-row residual (apparatus)        : 0.000 ms
write (two clocks — see standing)     : 8.0 ms
requirement span (handover → commit)  : between 8.0 and 5546.4 ms
  width, for want of a publisher stamp: 5538.3 ms
  front overhang, outside it          : 5538.3 ms
  back shortfall, missed by it        : 8.0 ms
   NFR-001 budget, typical              : 50 ms
  against the interval                : 8.0 to 5546.4 ms (width 5538.3 ms)
  the budget falls                    : inside the interval — this run cannot say whether NFR-001 was met or missed
```

The next fact in the same boot, on the same stack, with the same drive:

```
  Passed …NFR001_AuditIngestLatencyTests.Where_the_ingest_span_goes [14 s]
rate, intended → achieved             : 100 → 99.6 ev/s
service log level                     : Warning
measurement switch                    : ON
rows measured                         : 1000 (0 missing stamps)
 --- typical event (medians over every row) ---
observed span (occurred → received)   : 39.2 ms
  before handler (two processes, one clock): 39.1 ms
  in handler (one clock, exact)       : 0.0 ms
  unattributed (median arithmetic)    : 0.1 ms
  per-row residual (apparatus)        : 0.000 ms
write (two clocks — see standing)     : 5.0 ms
requirement span (handover → commit)  : between 5.1 and 44.1 ms
```

*(The second block drops the `endpoint reached` line and the `width / front overhang /
back shortfall` trio for length; nothing else is elided, and no figure is altered.)*

---

## 5. US1 and the T013 reading — confirmed, not fixed

Boot 12 ran the class with **no environment variable exported at all**:

```
service log level                     : Information (inherited from the appsettings; nobody chose it for this run)
measurement switch                    : ON
rows measured                         : 1000 (0 missing stamps)
```

```
  Failed …NFR001_AuditIngestLatencyTests.Where_the_ingest_span_goes [14 s]
   Shouldly.ShouldAssertException : result.Conditions.LoggingIsVerbose
    should be False
    but was True
    the services are logging at 'Information', inherited from the appsettings; … set
    Logging__LogLevel__Default=Warning for a measurement run
```

**Exactly the phase-4b reading.** US1 delivered: the stamps refusal is gone from a plain
`dotnet test` — switch ON, 0 missing stamps, without anybody remembering an export. There
were two remembered exports and US1 removed one; `LoggingIsVerbose` refuses the other one
assertion later, at `NFR001_AuditIngestLatencyTests.cs:353`. Not fixed here, and correctly
so: removing it sets the level for every integration test and #2133 is open on what
`Information` costs. **That is a decision for a person, not a phase-4b tidy-up.**

Two other refusals behaved as their scenarios say:

- **A7 is real but mis-attributed.** The cold-stack `Polly.Timeout.TimeoutRejectedException
  … '00:00:10'` at `IngestSpanMeasurement.DefineAsync` hit boot 2 — on
  `Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict`, not on
  `Where_the_ingest_span_goes`. It is a property of **the first fact to touch
  `system-variables` after a boot**, not of that one fact. 1 boot in 14.
- **The run-mode refusal** fired verbatim in boots 5 and 6: *"No run-mode stack configured.
  This run targets a stack it did not start and will not start one. Set
  SSE_RUNMODE_SYSTEM_VARIABLES, SSE_RUNMODE_KEYCLOAK and SSE_RUNMODE_AUDIT_DB …"*

---

## 6. Where the 50 ms budget falls

Per §3's table, and never asserted — the instrument places it and says so.

- **Ten first drives: `inside the interval` in every one that printed a placement.** The
  ceiling is 401.9–5 546.4 ms and the floor 5.5–11.7 ms, so the run *cannot say* whether
  NFR-001 was met or missed. That is the state every figure ever quoted on #1956 was read
  out of.
- **Second drives split.** `above the whole interval` — inside the budget on any reading —
  in boots 4, 10, 11, 12 and 14 (ceilings 44.1, 24.3, 15.1, 36.6, 16.1 ms). `inside the
  interval` in boots 1 and 3 (ceilings 1 176.3 and 73.4 ms). Phase 4b reported "above the
  whole interval every time" over its four; **that does not hold over mine** — boot 3's
  second drive has a 73.4 ms ceiling.
- **The floor never reached 50 ms in any run**: 2.0–11.7 ms across 17 drives, with two
  negative readings (−0.5, −0.8 ms) on boot 5, where the clock offset was −3.20 ms. The
  floor is at the noise level of the host↔container clock crossing, so "the floor is
  ~5 ms" is a bound, not a measurement.
- **The broker reading is the only figure that is on NFR-001's leg without a clock
  crossing, and it falls on both sides of the budget**: 11.7 and 16.8 ms (inside), 51.8,
  237.8, 306.6, 402.2 and 794.1 ms (outside).

**So the answer to #1956 is not "NFR-001 is met".** It is: *the figures on the issue are
the ceiling of a superset span and cannot decide it; the requirement's own leg has now been
read directly, and it comes out 12–52 ms on a warm pipeline and 307–794 ms on a cold one —
a mean, not a p99, either side of a 50 ms p99 budget.*

---

## 7. Latency budget

**N/A — audit ingest is not on constitution §IV's `event arrival → overlay rendered`
path.** §IV's six legs are camera→SFU, SFU→kiosk decode, the presentation buffer,
event→overlay state, composite+render and headroom; audit is on none of them, and spec
009's own constitution check says so (*"NFR-001 / NFR-002 keep audit off every hot path;
subscriber is async"*, `specs/009-audit-observability/spec.md:335`). The budget at issue is
**NFR-001, at `specs/009-audit-observability/spec.md:294`** — ≤ 50 ms p99 from RabbitMQ
deliver-ack to audit row committed under sustained 100 ev/s. It is **not** in the
constitution: `.specify/memory/constitution.md` contains no `NFR-001`, so moving it would
be a spec-009 edit. It does not move here.

*(The orchestrator's proposed line is confirmed as written.)*

---

## 8. What was NOT observed

Bluntly, because this issue exists precisely because a figure was quoted through four ADRs
without anyone checking which leg it measured.

1. **A p99 for NFR-001's own leg.** Little's law gives a **mean** (A2). Every "implied leg"
   above is a mean over a 30 s window; NFR-001 is a p99. Nothing here bounds the p99 of the
   requirement's leg in either mode.
2. **What makes a stack cold.** The slow mode is *associated* with the first paced drive
   after a boot, 8 times in 10 — but boots 5 and 6 were first drives and were fast, and
   nothing here identifies the mechanism (JIT, EF model build, Npgsql pool growth, prefetch
   credit, container CPU steal). Without a mechanism, "warm up first" is a ritual, not a
   fix — and a ritual is exactly what would let the next measurement be taken in whichever
   mode flatters it.
3. **Whether the warm mode is the production mode.** There is no production (ADR-0130). A
   service that has been up for a week is not the same as one warmed by 1 000 events 40 s
   ago, and nobody has measured the former.
4. **The end-to-end span against any budget.** `received_at - occurred_at` was 12.2 ms to
   5 538.4 ms typical across these runs — a 450× spread on one machine in one afternoon.
   **No budget in this repository covers it**, and it is the span every figure on #1956 is
   actually about. That gap is a product decision, not this branch's.
5. **Run mode.** Every figure here is the Aspire test fixture. `RunModeIngestAttributionTests`
   refused, correctly, for want of a configured stack. The issue's own step 1 ("measure run
   mode under sustained load") is still not done.
6. **Anything about a fix.** No lever was tried, and none is proposed. `AutoApplyTransactions`
   is untouched, ADR-0127 stays rejected, listener policy is unchanged, and no production
   code changed except one `isE2ETests`-guarded line in `AppHost.cs`.
7. **CI.** These facts are `Category=Measurement` and excluded by `ci.yml:179`. Nothing here
   runs in CI, and nothing here proposes that it should.

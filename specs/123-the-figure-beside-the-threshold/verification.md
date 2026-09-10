# Verification 123 — the figure beside the threshold

**Issue:** #2149 · **Branch:** `docs/2149-the-figure-beside-the-threshold`
**Where:** dev box (Windows 11, Docker Desktop), Release, `AspireFixture`
stack booted per run. **Not CI.** Every figure below is labelled with the
run it came from, and a dev-box figure is evidence about a margin, not a
substitute for CI's.

**No threshold was changed.** Not one, in either direction. Where the
arithmetic below makes a margin look vacuous, the arithmetic is the
deliverable and the edit is somebody else's decision (#2141, ADR-0144).

## How the figures were produced

One filtered `dotnet test` invocation per run, so the seven stack-dependent
budgets share a single `AspireFixture` boot (one machine, one stack):

```
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~WhepHandshakeLatencyTests|FullyQualifiedName~CommandLatencyTests|\
FullyQualifiedName~SignalRRevocationIntegrationTests|FullyQualifiedName~OverlayPushIntegrationTests|\
FullyQualifiedName~NFR002_AuditSearchLatencyTests|FullyQualifiedName~NFR002_MqttConnectAuthTests|\
FullyQualifiedName~PostgresConnectionBudgetIntegrationTests" \
  --logger "console;verbosity=detailed"
```

- **Run A** — before any edit. The phase-4a characterisation baseline.
- **Runs B and C** — after the output lines landed. The two observations
  FR-004 requires.
- `AelInterpreterBenchmarkTests` needs no stack and was run three times.

**`--logger "console;verbosity=detailed"` is load-bearing.** Without it
neither `Console.WriteLine` nor `ITestOutputHelper` reaches the console.
`ci.yml`'s integration step does not pass it — it passes `--logger trx`
— so the figures reach CI's uploaded `integration.trx` (per-test `StdOut`)
rather than the job log. Checked, not assumed: see *What CI can see*.

## §IV leg, or local SLO — per budget

Constitution §IV budgets an **asynchronous event-to-overlay path** in six
legs. Spec 116 (#2119) drew the line for `LayoutLifecycleIntegrationTests`
and the same line is drawn here. **None of the seven is one of the six
legs.** Two of them say so in their own spec, which is worth more than this
verification note saying it:

| # | Budget | What it is | Authority |
|---|---|---|---|
| 1 | WHEP auth hook p95 | **Local SLO** — stream *open* (click-to-first-frame), not the running-stream overlay path | spec 002 T086 |
| 2 | `POST /cameras` p95 | **Local SLO** — synchronous HTTP command path | spec 116's reasoning, verbatim |
| 3 | archive→push | **Local SLO** — *"an operator-action latency (≤ 1 s) — not the 800 ms event-to-overlay budget"* | `specs/003-layout-composition/plan.md:75` |
| 4 | publish→push | **Local SLO** — spec 004 is the *render substrate*; *"the budget itself starts ticking with spec 005's variable binding"* | `specs/004-overlay-designer/plan.md:73` |
| 6 | `GET /audit` p99 | **Local SLO** — a read-path query budget (spec 009 NFR-002) | spec 009 |
| 7 | MQTT CONNECT→CONNACK | **Local SLO** — connect-time auth overhead (spec 008 NFR-002) | spec 008 |
| 8 | AEL median batch | **Neither** — a component throughput expectation (ADR-0099 NFR-002). The interpreter runs *inside* leg 4's projection half, so a regression here spends that leg's 200 ms, but 100 000 evals do not occur on one event's path | ADR-0099:26 |
| 9 | Postgres connections | **Neither** — a resource budget, no latency at all (ADR-0125) | ADR-0125 |

Two of these are adjacent enough to be worth the sentence: **3 and 4 share
the transport with §IV's *Event → overlay state (RabbitMQ + projection)
≤ 200 ms***. Their own budget is **five times looser** than that leg's. If
either were a §IV leg, 1 000 ms would be a standing breach — which is the
strongest argument that reading them as legs is wrong, and the reason the
distinction is worth writing down beside the number.

## Recovered versus observed

**FR-005.** One figure of the seven was recovered from a merged PR body
rather than observed here, and the search for the others is itself a
result.

| # | Figure | Provenance |
|---|---|---|
| 2 | `POST /cameras` **median 19.7 ms, p95 98.8 ms** against a 200 ms budget | **Recovered** — PR #90 (2026-05-26), *"Latency budget impact"*, the section ADR-0031 makes mandatory. Exactly where #2149 says these figures went. |
| 1, 3, 4 | — | **Not recoverable.** Searched and read: PR #196 (WHEP, spec 002 T086) restates *"asserts p95 < 3 s"* and gives no measurement; PR #303 (spec 003 PR E) restates *"within 1 s"*; PR #417 (spec 004) leaves the box *"Integration tests … pass in the docker-backed CI job"* **unchecked**. |
| all | figures below | **Observed here**, runs B and C. |

**This sharpens #2149 rather than softening it.** The issue says the
figures "went into PR bodies, which are not in the tree". For item 2 that
is exactly right. For items 3 and 4 — the two whose specs promised the
measurement — the figure did not go into the PR body either. It was never
recorded anywhere. The gate passed on a restated budget.

## The figures

Threshold, observations, margin. **Margin is computed against the worst
observation**, because a margin computed against the best one is the
mistake this whole issue is about.

| # | Budget | Threshold | Observed | Margin |
|---|---|---|---|---|
| 1 | WHEP auth hook p95 (20 opens) | 3 000 ms | **p95 13 ms, 20 ms** (p50 6/6, max 22/26) | **115–230×** |
| 2 | `POST /cameras` p95 (100 samples) | 200 ms | **p95 219.4 ms (RED), 115.0 ms, 49.1 ms** (median 73.4 / 16.4 / 19.7) | **0.91× (breach) → 4.1×** |
| 3 | archive→push, 2 clients | 1 000 ms | **283 ms, 95 ms** | **3.5–10.5×** |
| 4 | publish→push, 2 clients (warm) | 1 000 ms | **110 ms, 57 ms** | **9–18×** |
| 6 | `GET /audit` p99, 100 k rows, 1 000 requests | 200 ms | **p99 55.8 ms, 33.3 ms** (p50 12.8/12.5, max 97.0/63.7) | **3.6–6.0×** |
| 7 | MQTT CONNECT→CONNACK (100 connects) | 15 ms p50 / 50 ms p99 — **CI only** | **p50 33.2, 27.4, 32.3 ms; p99 102.0, 102.8, 83.9 ms** | **≈ 0.5× — both exceeded ~2× off-CI** |
| 8 | AEL median batch, 10 × 10 000 evals | 500 ms median / 1 000 ms ceiling | **median 20.7, 26.7, 25.4, 23.4 ms; slowest 27.4, 39.8, 38.3, 41.2 ms** | **19× median / 24× ceiling** |
| 9 | Fixture connections vs service ceiling | < 378 | **91, 91** | **4.2×** |
| 9b | `max_connections` | ≥ 500 | **500, 500** | **1.0× — an equality, by design** |

Per-eval, item 8 reads **2.07–2.67 µs/eval** against ADR-0099's
`≤ 10 µs p99/eval` — roughly **4× inside the requirement**, and
≈ 400 000 evals/s/core against the ADR's ≥ 100 000.

## Findings — filed, not fixed

**None of these was acted on. That is the point of the pass.**

1. **#2149's headline case is worse than stated. `WhepHandshakeLatencyTests`
   is at 115–230×, not ~60×.** 3 000 ms guards an operation observed at
   6–26 ms. Nothing between 30 ms and 3 s fails this test, so it currently
   asserts that the auth hook answers *at all*.
2. **`CommandLatencyTests` is the opposite problem and the more urgent
   one.** 200 ms is the tightest budget of the seven, and the first run of
   the day **failed it at 219.4 ms** while CI's Linux runner passes. Two
   later runs on the same box came in at 115.0 ms and 49.1 ms. So the test
   is not broken and the code is not slow — but the margin is thin enough
   that a busy dev box turns it red, and the box being busy is exactly when
   someone is most likely to conclude that their change broke it. This is
   the memory-noted trap: *the first run after machine churn looks exactly
   like a regression*. **Not adjusted**; recorded so the next red is read
   correctly.
3. **`AelInterpreterBenchmarkTests`' stated arithmetic was wrong, not just
   absent.** The comment claimed "5× headroom" over an "≈ 100 ms expected"
   batch. The 100 ms was ADR-0099's *requirement* scaled to a batch, never a
   run; measured, the margin is **19×**. The threshold is unchanged and the
   arithmetic is now beside it.
4. **Off-CI, `NFR002_MqttConnectAuthTests` exceeds both withheld thresholds
   by about 2×, consistently** — p50 ≈ 27–33 ms against 15 ms, p99 ≈ 84–103 ms
   against 50 ms, three runs. #1905 already explains *why*; what was missing
   was the *how much*, which is now in the remarks.
5. **Item 9's `max_connections` assertion has a zero margin and should
   keep it.** It is an equality across a number written in two places
   (`AppHost` cannot reference `ServiceDefaults`), not a headroom check.
   Recorded because a future reader dividing 500 by 500 deserves the reason.
6. **Nothing else was red.** Runs B and C: **8 of 8 passed**, twice.

## Characterisation — phase 4a, green

**Run A, before any edit, is the baseline.** 7 of 8 passed;
`CommandLatencyTests` failed at p95 219.4 ms — recorded as finding 2 and
**not repaired**, per spec.md NFR-003. Runs B and C, after the output lines
and the doc comments, passed **8 of 8** each.

**No assertion was modified anywhere.** The diff is XML doc comments plus
one `output.WriteLine`/`Console.WriteLine` per measurement. No threshold, no
`CancellationTokenSource` window, no timeout, no filter, no trait, and no
production code.

## What CI can see

Checked rather than assumed. `--logger "console;verbosity=detailed"` is
what surfaces these lines on a console; `ci.yml`'s integration step does
not pass it — but it passes `--logger "trx;LogFileName=integration.trx"`
and uploads the result. Grepping this run's `spec123.trx` finds every one
of them, `ITestOutputHelper` and `Console.WriteLine` alike:

```
POST /cameras: n=100 median=19,7ms p95=49,1ms budget=200ms
publish-&gt;push to 2 clients (warm): 57 ms (budget 1000 ms)
pg_stat_activity = 91 connections against a service ceiling of 378 (…)
CONNECT→CONNACK over 100 connections: p50 = 32,33 ms, p99 = 83,88 ms, …
```

So a CI run now carries its own figures in its uploaded artifact, and the
next person who needs to know whether a margin moved does not have to
re-derive it from a dev box. **The CI job's own log still shows none of
them** — closing that would mean changing what CI runs, which is out of
scope here.

## Build

`dotnet build -c Release` — **Build succeeded, 0 warnings, 0 errors**
(CI treats warnings as errors). `Automation.Application.Tests` full suite:
**110 of 110 passed**.

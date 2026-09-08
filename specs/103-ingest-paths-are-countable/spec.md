# Spec 103 — Ingest paths are countable

**Issues:** #622 (`[T088]`) + #619 (`[T085]`), delivered as one slice
**Branch:** `feat/622-619-ingest-paths-are-countable`
**Worktree:** `D:/Github/wt-622`
**Phase:** 1 (Specify) · **Date:** 2026-09-08
**Feature bucket:** spec `specs/006-event-ingestion/`, story US5
**ADRs:** ADR-0118 (one telemetry sink per environment; the Prometheus/Grafana
comparison is abandoned), ADR-0117 (§VII binds *implemented* legs), ADR-0050
(`ILogger<T>` + OpenTelemetry OTLP, MEL-native), ADR-0051 (per-context
`Add<Context>{Infrastructure,Api}` DI extensions), ADR-0036 (smallest possible
change; no speculative generality), ADR-0105 (`Ensure.That`), ADR-0084 (code
metrics), ADR-0053 / ADR-0054 (test naming, hand-written builders), ADR-0109
(disjoint files for parallel slices), ADR-0139 / ADR-0144 (new behaviour starts
red; the lane may not skip phase 4a), ADR-0037 (the phased workflow).
**Constitution:** §IV (the latency budget), §VII (observability), §Testing.

---

## 0. The decision question, answered before anything else

The audit declined to decide three things. All three are disposed of by
decisions that already exist. **No new ADR is required, and this is not a
block.**

### 0.1 "Prometheus counters" — disposed of by ADR-0118

The issue title is a quotation from `specs/006-event-ingestion/plan.md:193`
("`ChannelMetrics.cs` ← Prometheus counters"), written while ADR-026's dual-sink
comparison was still live. **ADR-0118 abandoned that comparison outright**:

> *"None of it was built. Twenty-five features later there is no collector, no
> Prometheus, no Grafana, no Loki, no Tempo and no Alertmanager anywhere in the
> AppHost."*

and settled the replacement: **one sink per environment** — the Aspire dashboard
in development and CI, fed by the OTLP exporter Aspire injects; production
deferred until there is a production deployment. Constitution §VII's first and
third bullets now carry the same words.

So dropping "Prometheus" from this slice is **applying ADR-0118, not making a
decision**. The counters are `System.Diagnostics.Metrics` instruments on a
registered meter, exported over OTLP exactly as `LatencyBudget`, `WallSkew` and
`LabelDelay` already are. Nothing new is stood up and no exporter package is
added.

### 0.2 Constitution §VII does *not* oblige this work — stated rather than stretched

§VII's dashboard bullet is about **ADR-015's six latency legs**:

> *"Latency-budget dashboards (per ADR-015) are mandatory for every
> **implemented** leg."*

Ingest volume by path is **not one of the six legs**. It has no budget, no row
in §IV's table, and this spec **changes no cell of that table**. Claiming §VII
obliges it would be the mirror image of the clerical error §IV warns about — a
discharge nobody earned, arriving by a tidier route.

What §VII *does* do is constrain **how**: one sink, OTLP, the Aspire dashboard in
dev/CI. That is the second half of §0.1.

**The want therefore comes from somewhere else, and it does.** Spec 006 US5 asked
for it; `specs/006-event-ingestion/tasks.md:210,216` record T085/T088 as unbuilt;
and both issues carry `agent:ready` on Project #13 — which ADR-0144 makes the
human gate for phases 1–6. "Is this wanted" already has an answer on the board.
It is not mine to re-take, and re-taking it would be the decision I am forbidden
to make.

### 0.3 The outbox work does not supersede it — here is exactly what it covers

`tests/Integration.Tests/EventIngestion/OutboxBacklogIsVisibleTests.cs` (spec 021
T023, FR-008 / FR-010) asserts three things and no more:

| It asserts | It does not assert |
|---|---|
| `wolverine_event_ingestion.wolverine_outgoing_envelopes` exists | anything about events *arriving* |
| `wolverine_event_ingestion.wolverine_dead_letters` exists | anything about *which path* an event came from |
| the `attempts` column the health check reads exists, and `count(*)` on the outgoing table is answerable | any *rate* — it deliberately asserts only `>= 0`, because "a healthy system is usually at zero and a busy one is briefly not" |

It instruments the **outbound** side, *after* an event has already been accepted
and stored: how many integration messages wait to be delivered, and whether
delivery is stuck. It observes Wolverine's outbox. It says nothing whatever
about how many events arrived, by which ingress, or how fast. Its own comment is
explicit that the count is unassertable by design.

**Not superseded.**

---

## 1. The premise, checked

The audit's grep was re-run in this worktree:

```
grep -rnE 'new Meter\(|Counter<|Histogram<|System\.Diagnostics\.Metrics|IMeterFactory|ActivitySource|UpDownCounter' \
  src/EventIngestion --include=*.cs | grep -v /obj/
```

**Zero hits across 121 `.cs` files.** The premise holds: no meter, no counter, no
histogram, no activity source anywhere in the context.

### One correction to "unobservable", made because an unchecked premise is how this repo gets caught

Ingest volume is not *literally* invisible today. `Application/Log.cs:30-31`
emits, per accepted event:

> `"Published FabEventIngestedV1 for {Identifier} ({Source}/{Device})."`

— at **`LogLevel.Debug`**, while `src/EventIngestion/Api/appsettings.json` sets
`"Default": "Information"`. So it is off in every environment nobody has turned
up by hand, it is one line per event rather than a rate, and it is a log rather
than an aggregable series.

The audit's consequence survives that correction intact:

> *"ingestion volume by path is currently unobservable, which is precisely what
> would have made #625's backpressure question answerable."*

A Debug log you must first enable and then count by hand does not answer "is the
`plc` path running at 1 200 ev/s while `webhook` sits at zero". A counter does.

### The larger correction: the type cannot be called `ChannelMetrics`

Spec 006 put `ChannelMetrics` in `Application/Ingress/` because **all three
ingress paths went through the bounded channel**. That stopped being true at spec
020. `src/EventIngestion/Application/Ingress/IIngestChannel.cs:16-19` says so in
as many words:

> *"The HTTP write paths no longer use this channel at all — they persist before
> answering, which is a promise they can keep without one."*

Only MQTT writes to the channel now. A type named `ChannelMetrics` that counted
manual and webhook arrivals would be **named after a component it does not
observe** — precisely the mislabelling `WallSkew.cs` exists to warn about:

> *"Reusing the segment histogram would have been easy, would have compiled, and
> would have passed every test — while filing a spread under a name that means a
> journey."*

**The type is therefore `IngestVolume`**, named for the quantity it measures and
matching the `LatencyBudget` / `WallSkew` / `LabelDelay` family. The issues keep
their titles; this spec records why the name inside them was not used.

### Where the counting actually goes

Both remaining funnels are **Application command handlers**, and both already
hold the envelope's `Source`:

| Ingress | Route to storage | Handler |
|---|---|---|
| MQTT (`plc`, `inference`) | subscriber → bounded channel → persistence loop, batched | `IngestEventBatchCommandHandler` (`PersistenceLoopHostedService.cs:328`) |
| MQTT, retried after a failed batch | persistence loop, one at a time | `IngestEventCommandHandler` (`PersistenceLoopHostedService.cs:349`) |
| `POST /events/manual` | endpoint → command | `IngestEventCommandHandler` (`EventsEndpoints.Writes.cs:354`) |
| `POST /events/webhook/{name}` | endpoint → command | `IngestEventCommandHandler` (same line) |

An envelope reaches storage through exactly one of them: the batch is
all-or-nothing, and a batch that fails stores nothing before the loop retries it
singly. **Two call sites cover four sources with no double count — provided the
count is taken after the store commits**, which FR-006 requires and scenario 11
pins.

---

## 2. User story

### US1 (P1) — An operator can see how much is arriving, and by which path

An engineer looking at the `event-ingestion` service in the Aspire dashboard can
read a running count of accepted events broken down by `source`
(`plc | inference | manual | webhook`). A path that has gone silent shows a flat
line instead of being indistinguishable from a path that is merely quiet.

**Why P1, and why it is the whole slice:** it is the smallest thing that is both
independently shippable and independently observable, and #625 (the backpressure
question, T090/T091) cannot ask whether arrivals exceed drain until arrivals are
countable.

The 429 counter and the channel-depth gauge that spec 006 US5 also named
(`ingest_429_total`, `ingest_channel_depth`) belong to **#625** and are **out of
scope here** — they measure refusal and saturation, not volume, and building
them now would instrument a behaviour this slice does not touch.

**Independent test:** §5 below.

---

## 3. Acceptance scenarios

### Happy path — each ingress counts under its own source

1. **Given** the ingestion service is running,
   **when** an event is accepted and stored via `POST /events/manual`,
   **then** `sse.ingest.events` increments by exactly 1 with `source="manual"`,
   and no other `source` value changes.

2. **Given** the ingestion service is running,
   **when** an event is accepted and stored via `POST /events/webhook/{name}`,
   **then** `sse.ingest.events` increments by exactly 1 with `source="webhook"`.

3. **Given** the MQTT subscriber is connected,
   **when** a batch of 200 `plc` deliveries drains and commits,
   **then** `sse.ingest.events` increments by exactly 200 with `source="plc"` —
   **one** measurement carrying 200, not 200 measurements (FR-005).

4. **Given** an `inference` delivery arrives on MQTT,
   **then** it counts under `source="inference"`. The four sources are four tag
   values; MQTT is not collapsed into one bucket. `Source` already carries the
   same four lowercase wire tokens the MQTT topic segment and the HTTP route
   segment use, so the tag reuses that vocabulary rather than inventing a
   parallel one.

### Conflict — a duplicate is not a second event

5. **Given** event `E` has already been stored for fab `F`,
   **when** `E` is re-delivered (an MQTT redelivery after an interrupted run, or
   a retried `POST`),
   **then** `IngestEventCommandHandler` returns `EventAlreadyIngested` and
   `sse.ingest.events` **does not increment**. A redelivered event is the same
   event; counting it twice reports a burst that never happened.

6. **Given** a batch contains the same identifier twice,
   **when** the batch commits,
   **then** the counter increments by the number of envelopes **actually
   stored**, not by the batch's length.

### Bad request — a refused event is not an ingested one

7. **Given** an envelope whose `occurredAt` is too far in the future (spec 006
   FR-014),
   **when** the domain rule refuses it,
   **then** `sse.ingest.events` **does not increment**, on either handler. The
   batch handler already reports refusals separately so the loop can dead-letter
   them; a refusal is not an arrival.

8. **Given** a request that never reaches the command — malformed JSON, a payload
   over 64 KB, an unknown webhook integration —
   **then** nothing increments, because the counter sits behind the command and
   those are refused at the edge.

### Auth — a rejected caller is not ingestion volume

9. **Given** `POST /events/manual` with no bearer token, or with a token lacking
   the required scope,
   **when** the request is refused with 401/403,
   **then** `sse.ingest.events` **does not increment**. This is not a request
   counter: ASP.NET Core's own `http.server.request.duration`, already registered
   by `AddAspNetCoreInstrumentation` in `ServiceDefaults`, counts requests, and
   duplicating it here would make a rejected flood read as ingestion.

10. **Given** a revoked webhook integration presenting its old bearer token,
    **then** the same holds — refusal is not volume. (Spec 102 owns the
    revocation behaviour itself; this scenario only pins that it stays out of the
    count.)

### The write must commit before it counts

11. **Given** a batch whose `SaveAsync` throws,
    **when** the persistence loop falls back to storing one at a time,
    **then** the failed batch contributes **nothing** to the counter and the
    singly stored events contribute 1 each — exactly once per event in total. A
    count taken before the commit inflates on every retry, and the retry path is
    the ordinary way an interruption ends since spec 020.

---

## 4. Functional requirements

- **FR-001** A single counter instrument, `sse.ingest.events`, records events
  accepted **into the ingestion store**.
- **FR-002** It carries exactly one tag, `source`, whose value is `Source.Value`
  — `plc | inference | manual | webhook`.
- **FR-003** It carries **no other dimension**. In particular it carries no
  `fab`: nothing in this slice asked for per-fab volume, and `LatencyBudget`'s
  precedent is that a dimension is added when a spec names the need for it
  (`camera`, #1931) rather than because the value happens to be in scope.
- **FR-004** The instrument lives on its own meter, `SmartSentinelEye.IngestVolume`,
  registered with the OpenTelemetry meter provider. **A meter nobody registers
  records into nothing and raises no error** — all three existing instrument
  files say exactly that, and it is why §5's procedure is not optional.
- **FR-005** A batch records **one measurement carrying N**, not N measurements.
- **FR-006** The count is taken **after the store commits**, never before.
- **FR-007** A non-positive count records nothing, following `WallSkew` and
  `LabelDelay`, which drop an impossible value rather than throwing — a broken
  caller shows as missing data rather than as a fabricated figure.
- **FR-008** No new NuGet package. `System.Diagnostics.Metrics` is in the
  `Microsoft.NETCore.App` reference pack for `net10.0` (verified:
  `Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/System.Diagnostics.DiagnosticSource.dll`),
  and `OpenTelemetry.Extensions.Hosting` already flows transitively from
  `ServiceDefaults`.
- **FR-009** No configuration knob, no sampling switch, no exporter change
  (ADR-0036).

---

## 5. Independent end-to-end test procedure

Unit tests can prove the instrument records and that the handlers call it. They
**cannot** prove the meter is registered — that failure is silent by
construction. So registration is proved by asking the running system once, which
is also the only readout ADR-0118 §4 says exists:

> *"the latency histogram is emitted but cannot be read from outside the process
> that records it… there is no programmatic readout."*

1. Boot the stack: `dotnet run --project src/AppHost` (one stack per machine).
2. Mint a token from Aspire's **proxied** Keycloak endpoint — not the container's
   mapped port — and `POST /events/manual` once.
3. `POST /events/webhook/{name}` once against a registered integration.
4. Publish one `plc` event to the Mosquitto topic the subscriber wildcards.
5. In the Aspire dashboard open **Metrics → `event-ingestion`** and select
   `sse.ingest.events`. Expect three series — `source=manual`, `source=webhook`,
   `source=plc` — each at 1.
6. Re-`POST` the manual event with the **same** `eventId`. Expect `source=manual`
   to stay at 1 (scenario 5).

**If step 5 shows no instrument at all, the meter is not registered** — the
failure mode FR-004 exists for, and the one thing green unit tests cannot rule
out. Note that the Aspire MCP tools expose logs and traces but **no metrics
tool**, so this step is read from the dashboard UI.

---

## 6. Latency-budget impact — the `event → overlay state` leg, and it is not N/A

Constitution §IV requires any change on the event-to-overlay path to cite its
leg. **This change is on that path.** `IngestEventCommandHandler` and
`IngestEventBatchCommandHandler` sit inside the ≤ 200 ms `event → overlay state`
leg — an event being accepted through to its effect being applied.

The cost added is **one `Counter<long>.Add` per stored event**, and one per
*batch* on the MQTT path, taken after the transaction has already committed.
`Counter<T>.Add` is a lock-free interlocked update against an aggregator; the
`TagList` is a stack struct and allocates nothing. It is nanoseconds against a
200 ms budget and performs no I/O — the OTLP exporter drains on its own interval,
off the ingest thread.

**§IV's table is unchanged by this spec, and phase 4 must not edit it.** No leg
moves state. A counter of arrivals is not a latency figure, and recording one as
though it were would be the "measured before anyone read its figure" error §IV
names.

---

## 7. Locked tech choices applied

| Concern | Choice here | Authority |
|---|---|---|
| Metrics API | `System.Diagnostics.Metrics.Meter` + `Counter<long>`, static, `MeterName` const | ADR-0050; the `LatencyBudget` / `WallSkew` / `LabelDelay` precedent |
| Export | OTLP, one sink per environment; Aspire dashboard in dev/CI | ADR-0118, §VII |
| Registration | `.AddMeter(...)` inside the context's own `AddEventIngestionInfrastructure` | ADR-0051 |
| Guards | `Ensure.That(x).IsNotNull()` | ADR-0105 |
| Tests | xUnit + Shouldly, `MeterListener`, sentence-style names | ADR-0052, ADR-0053 |
| Phase 4a | **red** — see `plan.md` §5 | ADR-0139, ADR-0144 |

---

## 8. Assumptions marked, per ADR-0036

- **A1 (verify at phase 4/5).** `builder.Services.AddOpenTelemetry().WithMetrics(…)`
  called a *second* time — after `AddServiceDefaults()` has already called it — is
  additive and does not re-register the exporters. Believed true of
  `OpenTelemetry.Extensions.Hosting` 1.18.0.
  **Falsifier:** §5 step 5 shows duplicated series, or the service fails to boot.
  **Fallback if false:** move the single `.AddMeter` line into
  `src/EventIngestion/Api/Program.cs` immediately after `builder.AddServiceDefaults();`
  — still no shared file touched.
- **A2.** No existing dashboard, alert or query consumes a name in the
  `sse.ingest.*` space, so `sse.ingest.events` claims a free name. Grep confirms
  the only `sse.*` names in the repo are `sse.latency.segment.duration`,
  `sse.wall.skew` and `sse.overlay.label_delay`.

---

## 9. Out of scope, named so it is not quietly assumed

`ingest_channel_depth`, `ingest_429_total`, the 429 response itself (T090) and the
burst test (T091) all belong to **#625**. This slice hands #625 the arrivals
figure it needs and stops there.

---

## 10. `[NEEDS CLARIFICATION]`

None.

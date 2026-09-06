# ADR-0135: Where the audit ingest span goes

**Status:** Accepted
**Date:** 2026-08-31
**Supersedes:** (none)
**Superseded by:** (none)

## Context

NFR-001 (spec 009) requires p99 ≤ 50 ms from RabbitMQ deliver-ack to audit row
committed, under sustained 100 ev/s. The best figure ever observed is 85 ms p99,
in run mode.

Three levers have been applied or rejected against that gap, each with its own
ADR and its own measurements — parallel listeners (ADR-0124, adopted), settling
audit deliveries at the broker (ADR-0126, adopted), batching audit writes
(ADR-0127, built, measured, rejected). All three changed the **consumer** side.

Both prior conclusions ended in the same place: what remains is production
topology, which does not exist (ADR-0130), or moving the requirement. **Neither
was ever tested against a breakdown of where the time actually goes**, because no
breakdown existed. This ADR records one.

Two things made the existing figure unable to answer the question:

- **It is one number for a span nobody had divided.** `received_at - occurred_at`
  is stamped either side of the whole pipeline.
- **It is not the requirement's span.** `occurred_at` is stamped by the publisher
  as the aggregate mutates, so the figure is a **superset** of NFR-001's named leg
  at the front, and `received_at` is stamped *before* `SaveAsync`, so it is
  **short** of it at the back. Three ADRs have treated the two as
  interchangeable.

## Decision

**We record the following measurement, and nothing follows from it here.**
Whether the requirement moves, and whether a fourth lever is worth building, are
decisions with their own evidence and their own authors.

Two nullable timestamps were added to the audit row behind a switch that
**defaults to off** — handler entry, by the consumer's clock, and row **insert**,
by the database's. Together with the existing pair they divide the span into
parts that sum to it, row by row.

"Insert" rather than "commit" is deliberate and is a limitation, not a
simplification: `clock_timestamp()` evaluates as the row is written, inside a
transaction that has not committed. NFR-001's words are "audit row **committed**",
so the write leg below under-reports the requirement's back end by whatever the
commit costs.

### The breakdown

Three paced runs, 1000 events each, at an achieved 98.7 / 99.0 / 98.8 ev/s
against a target of 100. Medians. Aspire test fixture, service logging at
`Warning`.

| Part | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Observed span (occurred → received) | 1634.9 ms | 2642.5 ms | 1376.8 ms |
| — before handler entry | 1634.9 ms | 2642.4 ms | 1376.8 ms |
| — inside the handler | 0.0 ms | 0.0 ms | 0.0 ms |
| Write, insert only — **not established**, see below | 8.5 ms | 10.1 ms | 9.0 ms |
| Tail band (rows ≥ p99 of total) | 3145.7 ms | 3530.9 ms | 3026.5 ms |

**Essentially the whole span precedes the audit handler.** The handler's own work
is 0.0 ms.

**The write figure is reported and is not established**, for two independent
reasons, either of which alone would be enough:

- It subtracts a **host-process** stamp (`received_at`) from a **container**
  stamp (`clock_timestamp()` in Postgres). Those are different clocks, and the
  measured disagreement between them is ±8 ms — the same size as the leg. A
  sub-millisecond write against a container clock 8 ms behind yields a *negative*
  figure.
- It ends at insert, not commit, and so under-reports by the commit's own cost.

An earlier draft of this ADR printed 8.5–10.1 ms as a measurement and asserted
that only "before handler" crossed a clock boundary. **That was backwards**, and
is corrected below.

**This is not a theoretical objection.** The first run after the verdict was
wired in measured the host–container offset at **−21.85 ms ± 1.05 ms** and
reported the tail band's write leg as **−9.2 ms** — a negative duration, which
nothing in a pipeline can produce. The gate refused the run. Across this session
the same offset ranged from **−21.85 to +2.31 ms**, so it is not a fixed bias
that could be subtracted out; it drifts between runs on an idle machine.

The three runs tabulated above sat at 2.91 / 2.30 / 8.21 ms worst case and would
have passed the gate. That was luck rather than control, and it is why the figure
is published as *not established* rather than quietly kept.

### The two spans, stated separately

The requirement's span (broker hand-over → row committed) cannot be given as a
figure, because the hand-over falls *inside* "before handler" and no
publisher-side stamp exists to separate it. What can be stated is a range:

| | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Requirement span, floor | 8.5 ms | 10.2 ms | 9.0 ms |
| Requirement span, ceiling | 1643.4 ms | 2652.6 ms | 1385.8 ms |
| Front overhang — in the observed figure, outside the requirement | 1634.9 ms | 2642.4 ms | 1376.8 ms |
| Back shortfall — in the requirement, outside the observed figure | 8.5 ms | 10.1 ms | 9.0 ms |

**The width of that range is the cost of not having a publisher-side stamp**, and
it is wide enough that the requirement's own leg cannot be quoted as a number at
all. This feature deliberately did not add that stamp; adding one is a change to
the publish path of every context, not a measurement.

### That each row's parts cover that row's span

Measured on a **later** run than the three tabulated above, because the check did
not exist when they were taken: the median per-row residual was **0.000 ms** on
both bands, over 1000 rows with none missing stamps.

That is the claim "the parts account for the span" actually rests on. An earlier
draft rested it on the printed medians reconciling, which is a weaker and partly
accidental property — **medians do not add**, and they reconciled here only
because the in-handler part is degenerate at ~0. A pipeline that genuinely spent
time in its handler would have broken that check for reasons having nothing to do
with the stamps.

### That the breakdown is stable

The shape holds across conditions that move the span it divides by two orders of
magnitude:

| Load | Achieved | Observed span | Before handler | In handler | Write |
|---|---|---|---|---|---|
| 1 writer, unpaced | 15.6 ev/s | 11.9 ms | 11.9 ms | 0.0 ms | 3.0 ms |
| 8 writers, unpaced | 48.5 ev/s | 82.3 ms | 82.3 ms | 0.0 ms | 2.9 ms |
| 25 writers, unpaced | 60.7 ev/s | 195.2 ms | 195.2 ms | 0.0 ms | 9.2 ms |
| 50 writers, unpaced | 79.1 ev/s | 999.5 ms | 999.4 ms | 0.0 ms | 11.3 ms |
| 50 writers, unpaced, quiet logs | 244.4 ev/s | 5521.7 ms | 5521.7 ms | 0.0 ms | 9.2 ms |
| 50 writers, **paced to 100** | 98.7–99.0 ev/s | 1376.8–2642.5 ms | same | 0.0 ms | 8.5–10.1 ms |

> **Widened 2026-09-01 (spec 054).** The paced range above is three runs that
> happened to cluster, and it understates the spread. Seven paced fixture runs at
> ~99 ev/s give **267.4, 1376.8, 1414.9, 1634.9, 2642.5, 5516.0 and 7361.9 ms** —
> a 27× range, not a 2× one. The cause appears to be that 100 ev/s sits at the
> consumer's drain ceiling, so a run either keeps up or falls behind and
> accumulates backlog. **The breakdown itself is unaffected**: before-handler is
> the whole span in every one of those runs. See ADR-0136.

**The 15.6 ev/s row is the one that matters most.** There the pipeline is idle —
an 11.9 ms span, no backlog — and before-handler is still the whole of it. Without
that row the paced result would prove itself trivially: a backlogged queue spends
its time waiting, by definition.

### The clocks

**Which pair actually crosses a clock, stated correctly here after being stated
backwards.** `occurred_at` and `received_at` are stamped in different
*processes* — but on one machine those processes read one OS clock, so their
disagreement is not the risk it appears to be. Across machines it would be, and
nothing in this apparatus would notice.

The pair that genuinely crosses two clocks is `received_at` (host process) and
`written_at` (Postgres container). That is what the probe below measures, so the
probe bounds the **write leg** — not the before-handler leg the spec worried
about.

Each process's offset from the shared Postgres server was measured by bracketing
the reading and halving the round trip; the residual is reported with it.

| | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Offset from the shared server | −1.96 ms | +1.27 ms | −7.20 ms |
| Residual | ± 0.95 ms | ± 1.03 ms | ± 1.01 ms |

Worst case 8.2 ms, against a 10 ms threshold. **The run-to-run spread (8.5 ms)
is far wider than any single reading's residual (~1 ms)** — the readings are more
precise than they are accurate, and the spread is the honest figure.

**The clock question was this feature's headline risk, and the answer is split.**

Against the observed span it is immaterial: 8 ms of uncertainty on ~1500 ms is
0.3%, and the before-handler figure stands. Against the **write leg** it is
fatal: 8 ms of uncertainty on an 8.5–10.1 ms figure establishes nothing. The
spec's worry was well-founded and pointed at the wrong leg.

It would also not be immaterial against the 85 ms this work set out to divide,
which is one more reason the two must not be conflated.

**A further limit on the verdict itself.** Measuring one process against itself —
where the true skew is zero by construction — the instrument returns readings
spanning roughly 10 ms, with per-reading residuals of only 1–2 ms. So the noise
floor is the same order as the 10 ms threshold the verdict decides on. The
verdict separates a badly skewed stack from a healthy one and little finer than
that, and the residuals understate the real uncertainty.

### The apparatus' own cost

Same run shape, switch off then on. Each run reports the switch state it ran
under, read off its own rows, so the pairing is verified rather than remembered.

| | p50 | p99 | max | Rows stamped |
|---|---|---|---|---|
| Off | 17.2 ms | 4389.6 ms | 4519.7 ms | 0 of 1000 |
| On | 18.7 ms | 3691.7 ms | 3995.9 ms | 1000 of 1000 |

**About 1.5 ms at p50.** At p99 the switched-on run was faster, so the cost is
below run-to-run variance at the tail rather than negative.

## Consequences

**What this establishes.** At every load measured, from idle to saturated, the
audit pipeline's span is spent before the audit handler is entered, and the
handler's own work is unmeasurable at millisecond resolution. Each row's parts
cover that row's span exactly — checked row by row, not by comparing medians,
which do not add.

**What it does not establish, and these are not caveats but limits on use:**

- **It does not establish the write leg**, and so does not establish the
  requirement span's floor either, since that floor is built from it. The
  ceiling, which is dominated by before-handler, is unaffected.
- **It does not say which of four things spends the time.** "Before handler" is
  one interval covering the publisher's own transaction, the outbox hop, the
  broker, and Wolverine's dispatch to the handler. Separating them needs a
  publisher-side stamp, which was deliberately not added. *It isn't the handler
  or the write* is the finding; *it's the broker hop* is not.
- **It does not divide the recorded 85 ms.** These are Aspire-fixture figures. At
  a sustained 100 ev/s the fixture runs 1376–2642 ms where run mode recorded
  85–236 ms p99 — an order of magnitude worse, and in backlog where run mode was
  not. Attributing the recorded figure needs this apparatus run against run mode,
  which needs a paced load driver the repository does not have.
- **It says nothing about production.** There is no production deployment
  (ADR-0130).
- **It does not evaluate any lever**, including the obvious one this breakdown
  suggests.

**A measurement default now sits on a production write path.** It is off unless
configured, and that default is asserted on a row written through the ordinary
path rather than read back off the option.

**Debug logging is not free and is on by default in Development.** Both the
publisher and the audit service set `"Default": "Debug"`, which logs every SQL
command. Turning it down raised achieved throughput from 79 to 244 ev/s in an
otherwise identical run. Any figure taken on a dev stack without checking this
carries it.

> **Refined 2026-08-31.** That 79-to-244 pair is two real runs, but quoting it as
> *the* effect size overstates the precision. Repeated: **Debug gives 60.0 / 79.1
> / 82.5 ev/s, Warning gives 169.8 / 173.7 / 244.4** — so roughly **2–3×**, not a
> clean 3×.
>
> The spread is lopsided for a structural reason worth keeping: **at Debug the
> logging is the bottleneck, so the figure is reproducible; at Warning the
> bottleneck is the machine, so it is not.** Any single pair can therefore land
> anywhere in that range with nothing having changed.
>
> Pinning EF's SQL logging alone — the obvious narrow remedy — reaches only
> ~103 ev/s, because the cost is spread across Debug categories rather than
> concentrated in the loudest one. The measurement run now reports the service log
> level and **refuses to run at Debug**, which is the guard this warning was
> asking for.

> **Amended 2026-09-06 (spec 081, issue #1999).** The warning above is
> discharged, and two of its sentences no longer describe the repository. The
> text above is left as written; this note supersedes it on three points.
>
> **Development no longer pins `Debug`.** All eleven `appsettings.Development.json`
> now set `"Default": "Information"` and add
> `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"`. **Both edits are
> load-bearing and neither substitutes for the other.** Lowering `Default` alone
> would leave every SQL statement in the log, because EF emits `CommandExecuted`
> at `Information`; the category override is what removes that cost, and it is
> the one line a developer flips back locally when debugging a query. Moving
> `Default` is what silences the other Debug categories — the share this ADR
> measured as the larger one when it found that pinning EF alone reached only
> ~103 ev/s.
>
> **The guard now refuses an *inherited* level, not the literal `"Debug"`.** The
> sentence above — "refuses to run at Debug" — described a predicate that read
> `StartsWith("Debug")` against a fallback string holding the appsettings value
> of the day. It refused the default stack by spelling rather than by fact, so
> the eleven edits would have silently switched it off. `IngestRunConditions`
> now carries whether anyone chose the level *for this run* and refuses when
> nobody did, whatever it is spelled. The refusal this ADR asked for therefore
> survives the change that removed its original trigger.
>
> **The shipped pair has never been measured, and nothing here re-measures it.**
> The figures above stand exactly as written — Debug 60.0 / 79.1 / 82.5, Warning
> 169.8 / 173.7 / 244.4, roughly 2–3× — and none of them is a measurement of
> `Information` + `Database.Command: Warning`, which nobody has run. Between the
> ~103 ev/s of the EF-only remedy and the 169.8–244.4 of `Warning` is where the
> shipped configuration sits, against a 100 ev/s target — which is to say it is
> not known to clear it. **#2133** is filed to measure it, and until it reports,
> the inherited-level refusal above is what keeps an uncharacterised
> configuration from being certified by a run taken under it.

## Alternatives Considered

**OpenTelemetry spans read off the Aspire dashboard.** Idiomatic, and the
dashboard is already the dev sink (ADR-0118). Rejected because the trace list is
effectively unsearchable, so the approach depends on hunting history rather than
provoking a known event; and because spans do not survive a run in a form the
same SQL that produces the percentiles can read.

**Re-sourcing `occurred_at` or `received_at` from the database** to remove the
cross-clock question entirely. Rejected: it changes production behaviour to suit
a measurement, and breaks comparison with every figure already recorded. It is
worth noting what that rejection costs, now that the write leg is known to be the
cross-clock one: taking `received_at` from the database clock is precisely what
would have made the write leg measurable, and the constraint that forbids it is
the reason this ADR reports a range there instead of a number.

**A second stamp taken after commit**, which would close the gap between insert
and commit that NFR-001's wording opens. Rejected for this feature: it is a
second round trip on a path this work is only supposed to observe. It remains the
obvious way to establish the back of the span, and is not attempted here.

**Taking the p99 of each part separately.** Rejected because three independent
p99s belong to three different events and adding them divides nothing. The tail
band selects *rows* at or above the p99 of the total, so each row's parts still
sum to that row's own span.

**Reaching 100 ev/s by adding writers.** Rejected once it was measured: unpaced,
50 writers reached 244 ev/s and a 5.5 s span. "Sustained 100 ev/s" is a rate, and
running flat out measures overload. The run is now paced to the rate and asserts
it landed within ±15%.

## Implementation Notes

The measurement test stays **excluded from CI** (`Category!=Measurement`), and
NFR-001's budget stays at 50 ms. Nothing here was tuned to make anything pass.

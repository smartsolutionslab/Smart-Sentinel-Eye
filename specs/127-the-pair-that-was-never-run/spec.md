# Spec 127 — The pair that was never run

**Issues:** #2133 (measure the shipped pair), #2135 (the production half)
**Branch:** `perf/2133-the-default-that-was-never-run`
**Status:** Phase 3 complete — awaiting gate
**Lane:** autonomous (ADR-0144)
**ADRs:** 0037 (phases), 0144 (lane, 4a colour), 0036 (smallest change),
0135 (where the audit ingest span goes — the figures this refines),
0130 (no production deployment), 0103 (Aspire fixture), 0139 (red first),
0084 (code metrics), 0052/0053 (xUnit + Shouldly, test naming),
0050 (`[LoggerMessage]`, MEL), 0105 (`Ensure.That`), 0141 (NRT).
Constitution §IV (latency budget — see *What this is not*), §Testing.

## Problem — two halves of one subject

### The configuration nobody has run (#2133)

Spec 081 (#1999) ships, in eleven `appsettings.Development.json` files:

```json
"Default": "Information",
"Microsoft.EntityFrameworkCore.Database.Command": "Warning"
```

**No figure exists for that pair.** Every published figure measures a
different one. ADR-0135 records, and its 2026-08-31 refinement repeats each
arm three times:

| Arm | Runs | ADR-0135 |
|---|---|---|
| `Default: Debug`, EF inheriting it | 60.0 / 79.1 / 82.5 ev/s | the slow arm |
| `Default: Warning` | 169.8 / 173.7 / 244.4 ev/s | the fast arm |
| EF pinned alone, `Default` left at `Debug` | ~103 ev/s | "the cost is spread across Debug categories" |

So the shipped pair sits somewhere in **~103 to ~244 ev/s**, against a
**100 ev/s target** (NFR-001, spec 009). **The lower bound is uncomfortably
close to the target, and nobody knows where in that range the pair falls.**

ADR-0135's own amendment says so, and says why re-measuring the wrong pair
would be worse than measuring nothing: it "would put a confirmed-looking
figure next to a configuration it does not describe."

### The production half (#2135)

The thirteen non-Development `appsettings.json` files already set
`"Default": "Information"` and carry **no EF override**.

EF Core 10.0.11 emits `CommandExecuted` — the message carrying the **full
SQL text** — at `Information`:

```
LogExecutingCommand  EventId 20100  Level Debug
LogExecutedCommand   EventId 20101  Level Information   <- carries the SQL
LogCommandFailed     EventId 20102  Level Error
```

**So the non-Development configuration logs every SQL statement, for exactly
the reason the Development one did.** "Already `Information`" is not
"already correct" — that is the whole point spec 081's amendment makes about
the two edits being independently load-bearing.

It costs nothing today: there is no production deployment (ADR-0130;
`deploy/` holds one hand-written Mosquitto chart and no k8s package has ever
been referenced). That is why it is not urgent, and why it is cheap now.

## What this spec does

1. **Measures the three-way comparison first**, each arm repeated, matching
   ADR-0135's method closely enough that the figures sit beside the recorded
   ones honestly.
2. **Then** adds the EF override to the non-Development files, under a guard
   that fails today.

**The order is the point.** The #2135 edit is one line of JSON per file;
making it on an unmeasured premise is the thing to avoid.

## What this is not — constitution §IV

**This touches none of §IV's six legs.** The figure here is an *ingest
rate* — events per second the publish path sustains, the quantity ADR-0135
and NFR-001 are about — not a latency on the event-to-overlay path. None of
Camera→SFU, SFU→decode, presentation buffer, event→overlay state,
composite+render or headroom is implicated, and no leg's recorded state
changes.

Logging cost does affect throughput broadly, and a stack that cannot drain
100 ev/s will eventually show up as event→overlay latency under load. That
is a consequence of the ingest ceiling, not a measurement of a leg, and this
spec does not claim to have measured one.

## Non-goals — each is a way this goes wrong

- **NFR-001 The 100 ev/s target does not move.** It is a stated requirement
  (spec 009). A threshold widened to fit an observation stops being a
  threshold. If the shipped pair lands near it, that is the finding.
- **NFR-002 ADR-0135 is not edited.** Amending an ADR is a blocked outcome
  under ADR-0144. Where these figures refine it, the refinement is written
  in `verification.md` as *what it should say*, for a human to file.
- **NFR-003 The Development configuration does not change.** #1999 shipped
  it and its reasoning rests on a mechanism — EF emits SQL at
  `Information` — not on an effect size. A figure cannot un-ship it.
- **NFR-004 No arm is reported as a representative number.** If the arms
  overlap, or the run-to-run spread swamps the difference between them,
  that is what gets written down. ADR-0135 already records why: at the
  quiet arm the bottleneck is the machine, so the figure is not
  reproducible.
- **NFR-005 The measurement asserts no verdict on NFR-001.** It reports a
  rate. NFR-001's span is a p99 latency and is another question, answered
  as an interval by `NFR001_AuditIngestLatencyTests`.

## Functional requirements

- **FR-001** A measurement run reports the **achieved publish rate** for the
  unpaced fifty-writer shape — the shape ADR-0135's throughput rows were
  taken at — over `IngestRunShape.MeasuredEvents` events.
- **FR-002** The run repeats the drive **at least twice within one boot**,
  and reports each drive's figure separately together with its position in
  the boot. An average is not offered: this repository's own record is that
  the first run after machine churn looks exactly like a regression.
- **FR-003** The run reports **which arm it actually ran**, read off the
  running services rather than off the intention that launched them —
  whether `Debug`-level lines and EF's `Executed DbCommand` appear in a
  service's own log. An arm attributed from an environment variable that
  never reached the service is the failure mode that makes all three
  figures worthless.
- **FR-004** The run states both halves of the configuration — the `Default`
  level and the EF category level — each with whether anybody chose it for
  this run or it was inherited from the appsettings, mirroring
  `IngestRunConditions.LogLevelWasChosen`.
- **FR-005** Every non-Development `appsettings.json` that configures a
  `Logging:LogLevel:Default` at `Information` or lower silences
  `Microsoft.EntityFrameworkCore.Database.Command` — that category is **not
  enabled at `Information`**, the level carrying the SQL.
- **FR-006** FR-005 is enforced by a test that **binds the file as
  configuration and asks the resulting logger**, rather than matching text.
  A misspelled category key is silently inert, and a text guard comparing
  spellings cannot tell a working override from a dead one.
- **FR-007** The guard covers `appsettings.Development.json` too, by the same
  rule and in the same fact, so spec 081's eleven edits cannot regress and
  the rule stays a category rather than a list of files.

## Success criteria

- **SC-001** `dotnet build -c Release` clean — CI treats warnings as errors.
- **SC-002** The guard of FR-005/006/007 is observed **red before the JSON
  changes** and green after, with the failure quoted verbatim.
- **SC-003** Three arms measured — EF-pinned-alone (`Debug` + EF `Warning`),
  the shipped pair (`Information` + EF `Warning`), and quiet
  (`Warning` + EF `Warning`) — each driven at least twice, every figure
  recorded unaveraged in `verification.md`.
- **SC-004** `verification.md` states where the shipped pair falls in
  ADR-0135's ~103–244 range, whether it clears 100 ev/s and by how much, and
  says plainly if the spread swamps the difference.
- **SC-005** Every non-Development `appsettings.json` in scope changed, or
  the ones left out named with the reason.

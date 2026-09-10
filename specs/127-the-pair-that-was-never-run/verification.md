# Verification 127 — The pair that was never run

**Issues:** #2133 (measure), #2135 (the production half)
**Branch:** `perf/2133-the-default-that-was-never-run`
**Apparatus:** `AspireFixture` (ADR-0103), Windows 11, 2026-09-10/11.
**Fact:** `IngestThroughputTests.Publish_throughput_at_the_unpaced_fifty_writer_shape`
**Shape:** `IngestRunShape` — 50 writers, one variable each, unpaced,
1 000 events after a 100-event warm-up. ADR-0135's "50 writers, unpaced"
row. Achieved rate is events over the drive's own duration: publish-side
throughput, the quantity ADR-0135 quotes.

**Six boots, three drives each, eighteen figures. Nothing averaged.**

## The three arms, every drive

Every drive landed all 1 000 rows in the audit store before the next
began, so no drive measured its predecessor's backlog.

| Arm | Boot | drive #1 (cold) | drive #2 | drive #3 |
|---|---|---|---|---|
| **1** — `Default: Debug` + `Database.Command: Warning` | A | 98.0 | 179.3 | 184.4 |
| | B | 77.4 | 170.2 | 166.4 |
| **2** — **the shipped pair**: `Information` + `Database.Command: Warning` | A | 186.4 | 230.9 | 279.9 |
| | B | 160.9 | 214.1 | 207.0 |
| **3** — `Warning` + `Database.Command: Warning` | A | 181.9 | 258.4 | 319.8 |
| | B | 216.4 | 265.3 | 323.4 |

All figures ev/s. Cold-start cost, outside every timed span: 8.4 / 9.2 s
(arm 1), 7.9 / 10.7 s (arm 2), 5.8 / 7.1 s (arm 3) for the first create of
the boot to be answered.

**Each arm was read back off the driver's own log**, not off the shell that
launched it — three marker lines, one per boundary:

| Arm | `Executed DbCommand` (EF SQL, Information) | `Opening connection…` (EF, Debug) | `Archived variable` (our own, Information) | tail size |
|---|---|---|---|---|
| 1 | 0, 0, 0 | 13, 7, 13 | 0 (flooded out) | 400 (full) |
| 2 | 0, 0, 0 | 0, 0, 0 | 45, 49, 47 | 400 (full) |
| 3 | 0, 0, 0 | 0, 0, 0 | 0, 0, 0 | 274 |

The three patterns are distinct and each is the one its arm predicts. The
SQL text is absent from all three, which is the override doing its job in
every arm.

## Where the shipped pair falls, and whether it clears 100 ev/s

**It clears it, and not narrowly.**

- **Shipped pair, all six drives: 160.9 – 279.9 ev/s.**
- **Worst drive is 1.61× the 100 ev/s target; best is 2.80×.**
- The worst is a *cold first drive of a boot*; the worst warm drive is
  207.0 ev/s, **2.07×**.

ADR-0135's amendment placed the pair "between the ~103 ev/s of the EF-only
remedy and the 169.8–244.4 of `Warning`", and said "it is not known to
clear it". **On this apparatus it sits at or above the top of that range,
not near its floor.**

**The ~103 lower bound does not describe this machine at all**, and that is
why arm 1 was measured rather than taken from the record: the same
EF-pinned-alone configuration runs **77.4–98.0 cold and 166.4–184.4 warm**
here. A figure of ~103 is what a cold first drive of that arm looks like.

## Do the arms separate, or does the spread swamp them?

**Ignore position and the arms overlap.** Arm 1's warm drives (166.4–184.4)
sit inside arm 2's range (160.9–279.9) and overlap arm 3's cold drives
(181.9, 216.4). Quoting one number per arm from these eighteen would be
picking a position, not an arm.

**Compare at matched position and the ordering is consistent**, arm 1 <
arm 2 < arm 3 in every cell:

| Position | Arm 1 | Arm 2 (shipped) | Arm 3 |
|---|---|---|---|
| cold (#1) | 77.4, 98.0 | 160.9, 186.4 | 181.9, 216.4 |
| warm (#2, #3) | 166.4 – 184.4 | 207.0 – 279.9 | 258.4 – 323.4 |

Arm 1 and arm 2 do not overlap at either position. **Arm 2 and arm 3 do**:
182–186 at cold, 258–280 at warm. So the difference between the shipped
pair and going fully quiet is real but small enough that a single pair of
runs could show it either way — exactly the property ADR-0135 already
records, that at the quiet end the bottleneck is the machine rather than
the logging.

**Position is the largest single effect in this data.** Every boot rose
from drive #1 to drive #3 (arm 2 boot B is flat between #2 and #3). The
cold-to-warm ratio is 1.8–2.2× on arm 1, 1.3–1.5× on arm 2, 1.5–1.8× on
arm 3 — the same order as, and on arm 1 larger than, the difference between
adjacent arms.

Effect sizes at matched position, low-to-low and high-to-high:

| | cold | warm |
|---|---|---|
| Debug → Information | 2.08× / 1.90× | 1.24× / 1.52× |
| Information → Warning | 1.13× / 1.16× | 1.25× / 1.16× |
| Debug → Warning | 2.35× / 2.21× | 1.55× / 1.75× |

## What ADR-0135 should say — proposed, not applied

Amending an ADR is a blocked outcome (ADR-0144), so this is written for a
human to file. Four points, in the order the ADR raises them.

1. **The shipped pair now has a figure.** Its 2026-09-06 amendment ends
   "**#2133** is filed to measure it, and until it reports, the refusal
   above keeps a run taken at a level *nobody chose for it* from being
   certified." It has reported: **160.9–279.9 ev/s over six drives in two
   boots, worst drive 1.61× the 100 ev/s target.** The sentence "it is not
   known to clear it" is discharged for the fixture.

2. **"Pinning EF alone reaches only ~103 ev/s" needs a position beside
   it.** Re-measured here the same arm gives 77.4 / 98.0 cold and 166.4 /
   170.2 / 179.3 / 184.4 warm. ~103 is a cold figure. The clause it
   supports — "because the cost is spread across Debug categories" — still
   holds directionally: arm 1 is below arm 2 at both positions.

3. **The 2026-08-31 refinement's spread is at least partly within-boot
   position, and the ADR already knows this in another section.** Its
   "Debug gives 60.0 / 79.1 / 82.5, Warning gives 169.8 / 173.7 / 244.4"
   are three runs per arm with no position recorded, while its
   `PacedDrivesSinceBoot` note records the first drive of a boot slower
   than the second in 7 of 7 pairs. Here the Debug→Warning ratio is
   **2.2–2.35× at matched cold position and 1.55–1.75× at matched warm
   position** — so "roughly 2–3×" is the cold reading, and the warm one is
   smaller. The lopsidedness the ADR explains by "at Debug the logging is
   the bottleneck, at Warning the machine is" survives; the size does not.

4. **The gap the amendment deliberately left open does not need closing.**
   It notes that a run launched with `Logging__LogLevel__Default=Information`
   is certified under an effective configuration identical to the inherited
   one, "which is exactly the pairing #2133 says nobody has measured", and
   that refusing a chosen `Information` too "is the change #2133's figure
   exists to inform". **The figure informs it against making the change**:
   that pairing sustains 1.6–2.8× the rate the requirement names, so a run
   taken under it is fit to measure through. `IngestRunConditions` is
   therefore left exactly as it is.

**Calibration, stated because a comparison across machines is otherwise
worthless.** Arm 3 is ADR-0135's own `Warning` arm: it recorded 169.8 /
173.7 / 244.4, this machine gives 181.9 / 216.4 cold and 258.4 / 265.3 /
319.8 / 323.4 warm. The recorded range sits inside this one at the low end
and below it at the high end, so this box is **at least as fast as** the
one those figures came from — which is why arm 1 was re-measured here
rather than the recorded ~103 being used as the shipped pair's floor.

## What was not measured

- **Run mode.** These are fixture figures, like every figure in ADR-0135's
  refinement. ADR-0136 records the fixture as bistable at 100 ev/s; these
  drives are unpaced and well above that knee.
- **A p99, or any verdict on NFR-001.** This is a rate. NFR-001's span is a
  latency and remains an interval, reported by
  `NFR001_AuditIngestLatencyTests`.
- **Production.** There is none (ADR-0130).

## Constitution §IV — not touched

**None of the six event-to-overlay legs is implicated.** The figure is an
ingest rate, the quantity ADR-0135 and NFR-001 are about, not a latency on
the event-to-overlay path. No leg's recorded state changes, and this spec
claims no discharge under §VII.

Logging volume does affect throughput broadly, and a stack that could not
drain 100 ev/s would eventually surface as event→overlay latency under
load. That is a consequence of the ingest ceiling, not a measurement of a
leg.

## Phase 4a — the red, and that it discriminates

Colour: **RED**, and the red is the configuration half's. The measurement
half has no natural red: it produces a figure, and a test that failed when
the figure was low would assert the verdict this spec disclaims.

`DatabaseCommandLogLevelTests.Every_shipped_settings_file_that_logs_at_information_silences_the_sql_command_category`,
observed failing on the unmodified tree:

```
Shouldly.ShouldAssertException : noisy
    should be empty but had
13
    items and was
[Settings { Path = src/ApiGateway/appsettings.json, Default = Information, Override = , LogsSql = True }, …]

Additional Info:
    13 shipped settings file(s) log at Information without silencing
    'Microsoft.EntityFrameworkCore.Database.Command', so every SQL statement the
    process executes reaches the log — EF Core emits CommandExecuted (20101),
    which carries the statement text, at Information:
  src/ApiGateway/appsettings.json — Default 'Information', '…Database.Command' not set
  src/AppHost/appsettings.json — …
  src/AuditObservability/Api/appsettings.json — …
  src/Automation/Api/appsettings.json — …
  src/CameraCatalog/Api/appsettings.json — …
  src/EventIngestion/Api/appsettings.json — …
  src/Identity/Api/appsettings.json — …
  src/LayoutComposition/Api/appsettings.json — …
  src/MigrationRunner/appsettings.json — …
  src/OverlayDesigner/Api/appsettings.json — …
  src/ScenarioSimulator/appsettings.json — …
  src/StreamDistribution/Api/appsettings.json — …
  src/SystemVariables/Api/appsettings.json — …
```

Twenty-four files were scanned; the eleven `appsettings.Development.json`
passed, which is spec 081's edits holding. After the change: green, all
twenty-four.

**Proved by counterfactual, twice** — because a guard that has not been
made to fail on the thing it claims to catch has only been asserted to
work:

| Counterfactual, applied to `src/AppHost/appsettings.Development.json` | Result |
|---|---|
| override value changed to `"Information"` | 14 failures — that file joins the thirteen, reported as `'…Database.Command' Information` |
| override **key misspelled** `…EntityFrameworkCore.Databases.Command` | 14 failures — that file joins them, reported as `not set` |

The second is the one that matters. A misspelled category key is silently
inert and satisfies any guard that compares spellings; this one binds the
file through `ConfigurationBuilder`, builds a `LoggerFactory` from it and
asks `ILogger.IsEnabled(Information)` for EF's **own** category name. Both
files were restored and `git diff` was empty afterwards.

## The production half — files changed

**Thirteen of thirteen. None left out.**

`src/{ApiGateway, AppHost, AuditObservability/Api, Automation/Api,
CameraCatalog/Api, EventIngestion/Api, Identity/Api, LayoutComposition/Api,
MigrationRunner, OverlayDesigner/Api, ScenarioSimulator,
StreamDistribution/Api, SystemVariables/Api}/appsettings.json`

Three of them — `ApiGateway`, `AppHost`, `ScenarioSimulator` — reference no
EF Core package, and the line is inert there. Included deliberately: spec
081 set the same precedent when it put the override in the AppHost's
Development file, and a rule stated as a category needs no allow-list to
maintain. `ApiGateway` and `ScenarioSimulator` are also the two projects
with no `appsettings.Development.json`, which is why there are eleven
Development files against thirteen others.

**The Development files were not touched** (NFR-003), and the 100 ev/s
target was not touched (NFR-001).

## Build and suites

- `dotnet build -c Release` — **Build succeeded, 0 Warning(s), 0 Error(s)**
  (CI treats warnings as errors).
- `tests/Architecture.Tests` — **361 passed** after the change (360 before,
  plus the new guard).
- Solution suite, `--filter Category!=Measurement` — **2 896 passed across
  30 assemblies, 3 failed**, all three in `Integration.Tests` and **none of
  them this change**:

| Failure | Why it is not this change |
|---|---|
| `RunModeVariableResidueSweep.Archive_the_measurement_variables_a_run_mode_stack_still_holds` | Refuses in 228 ms with "No run-mode stack configured" — `SSE_RUNMODE_*` is unset on this box. A precondition of the machine. |
| `OutboxSharesTheWritesFateTests.A_write_that_cannot_commit_leaves_no_message_behind` | `TimeoutRejectedException` at the 10 s per-attempt budget, under a 463-test shared boot. **Re-run on its own: passed.** |
| `LogTailDeliversIntegrationTests.A_restarted_resource_keeps_delivering_to_its_log_tail` | Same timeout. **Re-run with `src` reverted to `origin/develop`: fails identically.** Pre-existing. |

  The third was isolated rather than argued: `git checkout origin/develop --
  <the thirteen files>`, one boot, same failure, tree restored. That is the
  only `src` change on this branch, so it cannot be the cause.

  Independently: the fixture's services read
  `appsettings.Development.json`, which already carried the override since
  spec 081, so **the effective configuration of a fixture run is unchanged
  by this commit** — its only reachable effect is on a host running outside
  Development, of which there are none (ADR-0130).

- The measurement fact carries `[Trait("Category", "Measurement")]` and is
  excluded from CI like its neighbours in `AuditObservability`.

## What the issues got wrong about the tree

Nothing material. Two small corrections:

- #2135 says "the non-Development `appsettings.json` files"; the count is
  **thirteen**, against eleven Development files — `ApiGateway` and
  `ScenarioSimulator` have no Development counterpart.
- #2133's framing that the shipped pair "sits somewhere between ~103 and
  ~170–244" is right as an inference from the record and wrong as a
  prediction for this machine: **the ~103 floor is a cold-drive figure**,
  and re-measured here the EF-pinned-alone arm reaches 166–184 warm.

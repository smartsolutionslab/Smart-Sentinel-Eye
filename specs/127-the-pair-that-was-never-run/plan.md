# Plan 127 — The pair that was never run

## Phase 4a colour: **RED**

A red honestly exists and it is not the measurement's.

- **The measurement half (#2133) has no natural red.** It produces a
  figure; a test that failed when the figure was low would be asserting a
  verdict this spec explicitly disclaims (NFR-001, NFR-005). Its output is
  the artifact, and it carries `[Trait("Category", "Measurement")]` like
  every other run in `AuditObservability` — excluded from CI by
  `Category!=Measurement`.
- **The configuration half (#2135) has one.** Thirteen non-Development
  `appsettings.json` set `Default: Information` and let
  `Microsoft.EntityFrameworkCore.Database.Command` inherit it. A fact that
  binds each file and asks the resulting logger whether that category is
  enabled at `Information` **fails on all thirteen today** and passes after
  the edit.

**Why that red discriminates**, stated because seven cannot-fail assertions
have surfaced in this session and a guard has to prove it is not the eighth:

- It fails **now**, on the unmodified tree, on thirteen named files.
- It is **not the change restated**. It binds the JSON through
  `ConfigurationBuilder` + `LoggerFactory` and asks
  `ILogger.IsEnabled(LogLevel.Information)`. A misspelled category key —
  `Microsoft.EntityFramework.Database.Command`, `Databases.Command` — is
  silently inert and passes a text comparison against nothing; it fails
  this one, because the logger it produces still logs the SQL.
- It **cannot be satisfied by the wrong value**: `Information`, `Debug` or
  `Trace` on that category all leave it enabled at `Information`.
- It **does not decay**. A new service, or a new `appsettings.json`, is in
  scope the moment it sets a `Default` at `Information` or lower.
- Counterfactually provable: setting one file's override to `Information`
  reproduces the failure for that file alone. Recorded in
  `verification.md`.

## The measurement — method, and where it differs from ADR-0135

ADR-0135's throughput figures come from the **unpaced fifty-writer** shape:
its stability table's row *"50 writers, unpaced, quiet logs — 244.4 ev/s"*
is the fast arm's best run. The new fact takes that shape from
`IngestRunShape` (`Writers = 50`, `MeasuredEvents = 1 000`,
`WarmupEvents = 100`, `NoPacing`), so the drive is the same code the paced
runs use, with the pacing gate replaced.

**Achieved rate = `MeasuredEvents / drive duration`** — publish-side
throughput, the same quantity ADR-0135 quotes, and the one logging volume
acts on.

**Three arms**, each set by environment variables the fixture's child
processes inherit:

| Arm | `Logging__LogLevel__Default` | `…__Microsoft.EntityFrameworkCore.Database.Command` | ADR-0135's figure |
|---|---|---|---|
| EF pinned alone | `Debug` | `Warning` | ~103 ev/s |
| **The shipped pair** | `Information` | `Warning` | **none — this is #2133** |
| Quiet | `Warning` | `Warning` | 169.8 / 173.7 / 244.4 ev/s |

The quiet arm is the calibration. If it lands inside ADR-0135's recorded
169.8–244.4, this machine is comparable to the one those figures came from
and the shipped pair's figure can be set beside them. If it does not, that
is the first thing `verification.md` says.

**Differences from ADR-0135's method, stated because a difference nobody
names is a figure quietly incomparable:**

1. **Three drives inside one boot**, not three boots. Cheaper, and it makes
   the within-boot warm-up effect visible rather than averaged away —
   ADR-0135 already records first-drive-of-a-boot slower than the second in
   7 of 7 pairs. Every drive reports its position.
2. **The `Default` level is *chosen* in all three arms**, never inherited.
   The shipped pair's arm therefore runs an effective configuration
   identical to the inherited one (`Information` from the env equals
   `Information` from the file; the EF override is set to the same
   `Warning` the file pins) while being attributable. ADR-0135's amendment
   describes exactly this equivalence.
3. **The arm is verified against the services' own logs.** ADR-0135's arms
   rest on which shell the run was launched from. This one reads
   `system-variables`' log tail for `Executed DbCommand` and for
   `Debug`-level lines, and reports what it found. An environment variable
   that never reached the service is the one failure that would make all
   three figures agree for the wrong reason.
4. The `deliver → row` percentiles are not re-taken. This is a rate
   measurement; the span is ADR-0135's and `NFR001_…`'s subject.

## Where the code goes

| Change | File |
|---|---|
| The measurement fact | `tests/Integration.Tests/AuditObservability/IngestThroughputTests.cs` (new) |
| The unpaced drive + arm read-back | same file; the drive reuses `IngestSpanMeasurement` and `IngestRunShape` unchanged |
| The guard | `tests/Architecture.Tests/DatabaseCommandLogLevelTests.cs` (new) |
| The production half | the 13 `src/**/appsettings.json` that set `Default` |

**Nothing in `src/` changes but JSON.** No `Shared.Kernel`,
`Shared.Contracts` or `AppHost.cs` edit — none of the contention files
(ADR-0109) is touched. `IngestRunShape`, `IngestSpanMeasurement` and
`IngestRunConditions` are **read, not edited**: the paced runs' figures stay
comparable with everything recorded against them.

## Scope of the JSON edit

All thirteen `src/**/appsettings.json` that configure `Logging:LogLevel`,
including `ApiGateway`, `AppHost` and `ScenarioSimulator`, which reference
no EF Core package.

**Why include the three with no EF.** Spec 081 set the same precedent —
`src/AppHost/appsettings.Development.json` carries the override and the
AppHost has no `Microsoft.EntityFrameworkCore` reference either. A rule
stated as a category ("a `Default` at `Information` silences the SQL
category") needs no per-file allow-list; a rule stated as a list needs one,
and a list is what decays when the fourteenth service arrives. The cost of
the three inert lines is three inert lines.

## Risks

- **The env var may not reach the service processes.** Mitigated by FR-003:
  the run reads the arm off the service's log rather than trusting the
  variable. If it did not reach, the run says so and the arms are void.
- **The quiet arm is not reproducible by construction** (ADR-0135: at
  `Warning` the bottleneck is the machine). Expected, reported, not
  smoothed.
- **C: has ~9.8 GB free.** Three fixture boots pull no new images; each boot
  regrows Docker build cache. Checked between arms.
- **One machine, one stack.** No AppHost is running (checked: run-mode
  containers up, no host process). Each arm is one foreground
  `dotnet test`; nothing is backgrounded.

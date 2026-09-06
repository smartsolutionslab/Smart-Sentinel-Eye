# Plan 081 — The SQL firehose is off by default

**Phase:** 2 (Plan) · **Date:** 2026-09-06 · **Issue:** #1999 · **Spec:** `spec.md`
**Base:** `origin/develop` @ `f25e468e`

---

## 1. Bounded context and layers

**None. This change is outside the domain model entirely.**

Recorded rather than omitted, because the plan template's shape invites padding
here and a plan that invents a context to fill a heading is worse than one that
says there isn't one.

| Plan heading | This change |
|---|---|
| Bounded context | **N/A** — host configuration plus one test-project type. |
| Domain / Application / Infrastructure / Api layers | **N/A** — no layer is touched. |
| Entities, value objects, invariants | **N/A** — no domain model changes. Constitution §II (primitives) is not engaged; `PrimitiveBoundaryTests` sees nothing new. |
| Domain event → integration event | **N/A** — no message is published, consumed or renamed. `Shared.Contracts` is untouched. |
| Boundary rules (no cross-context references) | **Trivially satisfied.** Nothing gains a project reference. NetArchTest's rule set is unaffected. |
| Persistence / migrations | **N/A** — no `DbContext`, no schema, no `MigrationRunner` change. |
| Latency budget | **N/A** — `spec.md` §11. |

The two surfaces that *are* touched:

1. **Host configuration** — 11 `appsettings.Development.json`. Pure data.
2. **The measurement test harness** —
   `tests/Integration.Tests/AuditObservability/`, three files. Test code, not
   runtime code, which keeps it inside the brief's "no runtime code" constraint.

---

## 2. Surface 1 — the configuration

### The shape every one of the 11 files lands in

Existing keys are preserved; `Default` changes value and one key is added.
Example, `src/CameraCatalog/Api/appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  }
}
```

`src/MigrationRunner/appsettings.Development.json` keeps
`"Microsoft.Hosting.Lifetime": "Information"` instead of the AspNetCore key;
`src/AppHost/appsettings.Development.json` keeps its AspNetCore key. **No file
loses a key**, and nine of the eleven end up structurally identical.

### Why the category override is required rather than merely helpful

`Default: Information` alone does not silence EF. `Microsoft.EntityFrameworkCore.Database.Command`
emits `CommandExecuted` at `Information`, so an `Information` default still logs
every statement with its text. The category override is the whole mechanism; the
`Default` move is what silences the *other* Debug categories, which ADR-0135
measured as the larger share ("pinning EF's SQL logging alone … reaches only
~103 ev/s, because the cost is spread across Debug categories"). **The two edits
do different jobs and neither substitutes for the other.**

### Ordering within the object

The new key goes **last** in each `LogLevel` object. JSON object order is not
semantic — `ConfigurationBuilder` flattens to a dictionary — but a consistent
position makes the eleven diffs read identically, which is what makes the
reviewer's `grep -c` in `spec.md` §10 step 2 a meaningful check rather than a
formality.

### Configuration precedence, so the local-flip story is checked rather than assumed

`appsettings.Development.json` is layered over `appsettings.json` by the default
host builder when `ASPNETCORE_ENVIRONMENT=Development`, and environment
variables layer over both. Therefore:

- The Development file's `Warning` wins over the base file's absence of the key —
  the firehose is off on a dev stack.
- `Logging__LogLevel__Microsoft__EntityFrameworkCore__Database__Command=Information`
  in the shell, or editing the one line, restores SQL for that service only.
  Both are the "deliberate act" the decision describes.
- Nothing in `deploy/`, CI, or any `launchSettings.json` sets a competing value —
  verified: zero hits for any `Logging`/`LogLevel` key in all eleven
  `launchSettings.json`, in `.github/workflows/`, and in `deploy/`.

---

## 3. Surface 2 — the measurement harness

### The invariant that breaks

`ServiceLogLevel` encodes an invariant nobody wrote down:

> *the environment variable is absent* ⟹ *the services are at the level the
> appsettings pin* ⟹ *that level is `Debug`*

The first implication survives this change. The second does not. The literal
`"Debug (from appsettings)"` is the second implication compiled into a string,
and editing the eleven files falsifies it.

### The design decision, per `spec.md` §6 (landing B)

Replace the conflated concept with the two it was standing in for:

| Today (one string) | After (two facts) |
|---|---|
| `LogLevel: string`, whose *value* smuggles in whether it was chosen | `LogLevel` — what the services are at · **chosen vs inherited** — whether anyone picked it for this run |

`LoggingIsVerbose` then stops being a spelling test on a label and becomes what
ADR-0135 always meant by it: *is this run's logging fit to measure through?*

```
not fit to measure  ⟸  the level was inherited (nobody chose it for this run)
                    ∨  the chosen level is Debug or Trace
```

### Recommended shape, and the alternative

**Recommended: `Option<string>` for the chosen level.** `IngestRunConditions`'s
positional parameter changes type rather than count, and `Option<T>`
(`src/Shared.Kernel/Option.cs`) is the repo's own vocabulary for a modelled
absence (ADR-0141). It is already in use in the very file that reads this
property — `RunModeIngestAttributionTests.cs:41` binds
`Option<RunModeStackAddress>` — so the pattern is local, not imported.

`Describe()` renders the inherited case explicitly, so the printed conditions
block distinguishes the two rather than hiding the difference in a parenthesis:

```
service log level                     : Information (inherited from appsettings; not chosen for this run)
```

**The threading is what settles it.** `logLevel` is not local to the record — it
arrives as `string logLevel` at `IngestSpanMeasurement.cs:80` and is passed
through to the constructor at `:145`. An added `bool` therefore adds a parameter
in **two** signatures; changing the existing parameter's type to
`Option<string>` adds none anywhere. Four construction sites must be updated
either way: `IngestSpanMeasurement.cs:140` and three in `RunModeDriverTests.cs`
(all use named arguments, so the compiler finds every one).

**Acceptable alternative: an added `bool` parameter.** Nine positional
parameters instead of eight. **S107 (too many parameters) is suppressed for test
projects** — `Directory.Build.props:108` lists it in `NoWarn` alongside S104,
S138, S1541, S134, under `Condition="'$(IsTestProject)' == 'true'"` — so
ADR-0084's four-parameter limit does not block this.

**One trap either way.** `[InlineData]` takes compile-time constants, so it
cannot carry an `Option<string>`. The theory method keeps a primitive signature
— `(string level, bool chosen, bool expected)` — and constructs the `Option`
inside its body. This is not a reason to avoid `Option<string>`; it is one line.

**Rejected: substring-sniffing the label** (`LogLevel.Contains("inherited")`).
It re-creates the defect being fixed — behaviour depending on the wording of a
human-readable string — and silently reverts the moment someone rewords the line.

### What must NOT be done here

- **Do not delete or weaken the refusal.** ADR-0144 forbids the lane from
  weakening a gate to reach green, and ADR-0135 records this refusal as the
  remedy to issue #1999 itself. Removing it while implementing #1999 would close
  the issue by discarding its own fix.
- **Do not add a test that reads an `appsettings*.json` and asserts a value.**
  It would prove the file was edited and nothing else — the
  guards-that-read-the-design-artefact defect this repository has recorded. The
  reviewer's `grep` in `spec.md` §10 is the right instrument for that question,
  because it is honest about being a file check.
- **Do not touch `IngestSpanMeasurement`, `IngestRunShape`, the percentile maths,
  or the drive loop.** Only the conditions record and the two call sites that
  populate its log-level field.

---

## 4. Surface 3 — the record

`docs/adr/0135-where-the-audit-span-goes.md` gains a dated amendment block
appended to its existing `Refined 2026-08-31` note. It states three things and
nothing more:

1. Development no longer pins `Debug`; as of 2026-09-06 the eleven Development
   files sit at `Information` with `Database.Command` at `Warning` (spec 081).
2. The guard therefore refuses an **inherited** level as well as `Debug`/`Trace`,
   because the new default's throughput has not been measured.
3. The 79→244 pair and the 2–3× range stand as written and are **not**
   re-measured by spec 081.

**The ADR's existing prose is not edited.** It records what was true when
written; an amendment supersedes, it does not rewrite (`spec.md` §3.3, and the
pattern CLAUDE.md describes for ADRs amending each other).

---

## 5. Risks

| Risk | Handling |
|---|---|
| **A developer loses a Debug line and reads it as a regression.** Twenty of twenty-two sites go dark (`spec.md` §3.1). | FR-004: the list and the one-line recovery go in the PR body. |
| **CI stays green whether or not surface 2 is done**, because both measurement tests are excluded by `Category!=Measurement`. | `tasks.md` sequences T003 before T004 and the phase-4a red is the enforcement. The reviewer's step 3 (`git diff --stat`) is the backstop. |
| **The new default may not clear 100 ev/s** — nobody has run it. | Landing (B) refuses it until someone does. A follow-up measurement issue is recommended in `spec.md` §4, deliberately not filed by this pass. |
| **The `AppHost` override is inert** (no EF package). | FR-002 records the uniformity as the requirement and the inert line as its stated price, so a later reviewer meets the reasoning instead of deleting the line as dead config. |
| **Production still has no EF override.** | Named in `spec.md` §2 as a live gap, explicitly not discharged. No production deployment exists (ADR-0130). |

---

## 6. Constitution and ADR alignment

- **§II (primitives out of the domain)** — not engaged; no domain type changes.
- **§IV (latency budget)** — N/A, with the caveat in `spec.md` §11.
- **§VII (dashboard rule for implemented legs)** — not engaged; no leg changes.
- **§Testing (two obligations)** — behaviour-changing, so 4a is red. `tasks.md` §2.
- **ADR-0036** — smallest change. The eleven files are the decision; the harness
  repair is its unavoidable consequence, not scope creep, and §3's "what must NOT
  be done" fences the boundary.
- **ADR-0050** — the 22 Debug sites are `[LoggerMessage]` output; none is
  rewritten, re-levelled, or deleted. Their *visibility* changes, not their code.
- **ADR-0084** — S107 suppressed in tests, so neither shape in §3 is blocked.
- **ADR-0144** — no ADR is authored and no gate is weakened. The ADR-0135
  amendment is a factual correction to a record, not a new decision; if a
  reviewer reads it as authoring a decision, drop it and file the correction as
  its own issue rather than expanding this one.

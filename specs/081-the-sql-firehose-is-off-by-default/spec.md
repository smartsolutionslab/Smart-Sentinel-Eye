# Spec 081 — The SQL firehose is off by default

**Issue:** #1999 · **Branch:** `chore/1999-the-sql-firehose-is-off-by-default`
**Phase:** 1 (Specify) · **Date:** 2026-09-06 · **Base:** `origin/develop` @ `f25e468e`
**ADRs:** ADR-0135 (where the audit span goes — the ADR that recorded this cost,
refined the effect size, and records the measurement guard as its remedy),
ADR-0136 (run-mode span; ran at `Warning`, so it is *not* in the position the
issue describes), ADR-0050 (`ILogger<T>` + `[LoggerMessage]`; the 23 Debug sites
below are its output), ADR-0130 (no production deployment — the dev stack is the
only stack), ADR-0036 (smallest change; no speculative generality), ADR-0037
(phased workflow; a spec may be declined), ADR-0139 (new behaviour starts red),
ADR-0144 (autonomous lane; no ADR is written here).

**No new ADR. No constitution amendment.** See `tasks.md` §3.

---

## 1. The decision being implemented

Recorded by the repository owner, 2026-09-06:

- `"Default"` becomes **`Information`** in all 11 `appsettings.Development.json`.
- `Microsoft.EntityFrameworkCore.Database.Command` becomes **`Warning`**.

The reasoning on record: EF emits command logging at `Information`, so lowering
the default alone leaves every statement in the log. The category override is
what removes the cost, and it is the one line a developer flips back locally
when debugging a query — which should be the deliberate act, not the default.

**Scope: `appsettings.Development.json` only.** Production and CI configuration
are out of scope. No runtime code.

This is the follow-through on a decision spec 054 deliberately declined to make
(`specs/054-divide-the-recorded-85ms/research.md:197`):

> **Not changed**: `appsettings.Development.json`. What Development logs at is a
> developer-experience trade-off, and a measurement should not settle it for
> everyone.

A measurement did not settle it. The owner has.

---

## 2. The premise, re-established — the issue's own count was wrong

The issue names **two** files (`AuditObservability`, `SystemVariables`) "and the
AppHost sets the same". The real number is **11**, and all 11 are identical in
the relevant key.

```sh
# the population
find . -name appsettings.Development.json -not -path '*/obj/*' -not -path '*/bin/*' | wc -l   # 11

# every one of them at Debug
grep -l '"Default": "Debug"' $(find . -name appsettings.Development.json -not -path '*/obj/*' -not -path '*/bin/*') | wc -l   # 11

# no EF override exists anywhere in src/
grep -rn 'Database.Command' --include='*.json' src/ | wc -l   # 0
```

| # | File | `"Default"` today | Other keys |
|---:|---|---|---|
| 1 | `src/AppHost/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 2 | `src/MigrationRunner/appsettings.Development.json` | `Debug` | `Microsoft.Hosting.Lifetime: Information` |
| 3 | `src/AuditObservability/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 4 | `src/Automation/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 5 | `src/CameraCatalog/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 6 | `src/EventIngestion/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 7 | `src/Identity/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 8 | `src/LayoutComposition/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 9 | `src/OverlayDesigner/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 10 | `src/StreamDistribution/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |
| 11 | `src/SystemVariables/Api/appsettings.Development.json` | `Debug` | `Microsoft.AspNetCore: Warning` |

**No file is already at `Information`.** All 11 are edits; none is a no-op.

**No `Microsoft.EntityFrameworkCore.Database.Command` entry exists anywhere** —
not in `src/`, not in `deploy/`, not in CI, not in any `launchSettings.json`
(all 11 of those carry no logging keys at all). The override is new everywhere
it lands, so the edit adds a key rather than changing one.

### Two projects have no Development file, and correctly so

`src/ApiGateway` and `src/ScenarioSimulator` ship only `appsettings.json`,
already at `"Default": "Information"`. Neither references
`Microsoft.EntityFrameworkCore` at all — the gateway is YARP, the simulator
speaks HTTP. They need no edit and are not in the count.

### The non-Development `appsettings.json` files: already correct on `Default`, out of scope on the override

All 12 non-Development `appsettings.json` files already set
`"Default": "Information"`. **They are out of scope by the owner's
instruction**, and it is worth being plain that this is not the same as "already
correct": none of them carries the EF override either, so a Production run would
log every SQL statement at `Information`. That is a live gap, not a resolved one
— but there is no production deployment (ADR-0130), so it costs nothing today.
**It is not discharged by this spec** and should be reconsidered whenever a
production deployment is first configured.

---

## 3. What actually changes — the two things nobody had counted

The issue frames this as SQL logging. It is that, plus two consequences neither
the issue nor the decision note mentions. Both were found by looking; both are
cheap to state and expensive to discover after the fact.

### 3.1 Twenty-three hand-written Debug log sites go dark in Development

`Default: Information` silences every `[LoggerMessage(Level = LogLevel.Debug)]`
in the solution, not only EF's. There are **23** (22 declarations plus one direct
`LogDebug(` call), across 11 files:

```sh
grep -rn 'Level = LogLevel.Debug' --include='*.cs' src/ | grep -v obj/ | wc -l   # 22
grep -rn 'LogDebug('             --include='*.cs' src/ | grep -v obj/ | wc -l    #  1
```

| Context | Sites | Examples a developer plausibly follows |
|---|---:|---|
| `SystemVariables/Application` | 4 | `Dedup hit for variable '{Name}' …`, `Reverse-index upserted for overlay {Overlay} …` |
| `LayoutComposition/Application` | 4 | `Broadcast OverlayHighlightChanged …`, `Broadcast ResolvedOverlayTextChanged …` |
| `AuditObservability/Application` | 2 | `Audited {EventKind} {EventIdentifier} …` |
| `EventIngestion/Application` | 2 | — |
| `Identity/Application` | 2 | `Published DeviceRegisteredV1 for {ClientId}.` |
| `ScenarioSimulator` | 2 | `Emitted {Topic} = {Value} {Unit} …` |
| `ServiceDefaults` | 2 | — |
| `Automation/Application` | 1 | `Fanned out {Count} action(s) for {EventIdentifier} …` |
| `EventIngestion/Infrastructure` | 1 | — |
| `Identity/Infrastructure` | 1 | — |
| `MigrationRunner` | 1 | `PostgreSQL {Severity}: {Message}` |

The four LayoutComposition lines and the four SystemVariables lines trace the
**variable-change → overlay push** path — the path spec 063 exists to make
observable. Losing them by default is a real cost, and it is the cost this change
buys throughput with.

**It is accepted, not overlooked.** Recovery is the same one-line local flip the
decision note names, and the messages that must survive the default filter were
already routed to `Warning` deliberately — `src/MigrationRunner/Log.cs:23`
carries the comment, from issue #1394:

> Warning, not Debug: at Debug the default filter hides it and the message may
> as well not exist (#1394).

**FR-004 requires this list to reach the PR body**, so the next developer who
notices a missing line finds an answer rather than a regression.

### 3.2 Three places in the test harness state the old value in the present tense, and become false

`Logging__LogLevel__Default` propagates from the launching shell through the
AppHost into the child services (verified in spec 054 §8). The measurement
harness reads it, and **falls back to a literal that hardcodes today's
appsettings**:

- `tests/Integration.Tests/AuditObservability/NFR001_AuditIngestLatencyTests.cs:80`
- `tests/Integration.Tests/AuditObservability/RunModeIngestAttributionTests.cs:35`

```csharp
private static string ServiceLogLevel =>
    Environment.GetEnvironmentVariable("Logging__LogLevel__Default") ?? "Debug (from appsettings)";
```

That literal feeds `IngestRunConditions.LogLevel`, and
`IngestRunConditions.LoggingIsVerbose`
(`tests/Integration.Tests/AuditObservability/IngestRunConditions.cs:43`) is
`StartsWith("Debug") || StartsWith("Trace")`. Its refusal is what ADR-0135
records as the remedy to this very issue:

> The measurement run now reports the service log level and **refuses to run at
> Debug**, which is the guard this warning was asking for.

The third place is the inline datum that pins the literal:
`tests/Integration.Tests/AuditObservability/RunModeDriverTests.cs:239` —
`[InlineData("Debug (from appsettings)", true)]`.

**Nothing breaks mechanically.** Both measurement tests are
`[Trait("Category", "Measurement")]` and excluded from CI; `RunModeDriverTests`
asserts the predicate against string literals, not against the repository. **CI
stays green if phase 4 edits only the 11 JSON files** — which is precisely the
hazard. What the repository would then contain is three present-tense statements
that are false, including two XML doc comments asserting *"Development pins
`"Default": "Debug"` in every service's appsettings"*
(`NFR001_AuditIngestLatencyTests.cs:66`, `IngestRunConditions.cs:38`).

That is the drift class CLAUDE.md's preamble catalogues — §II twice, the phase-3
board gate, §IV's leg table — a record nobody checked against what was actually
happening. **Repairing it is in scope. It is the reason this is a spec and not a
one-line chore.**

### 3.3 Historical records are NOT edited

`docs/adr/0135-…:239`, `specs/053-…/verification.md:163`,
`specs/054-…/spec.md:132` and `specs/054-…/quickstart.md:36` also say Development
is at Debug. **These are correct records of what was true when written and must
not be retro-edited.** The line is: *present-tense operational claims in live code
get corrected; historical records get a superseding note or nothing.* ADR-0135
gets a dated amendment block appended — its established pattern, it already
carries one ("Refined 2026-08-31") — not a rewrite.

---

## 4. The measurement claim — not re-measured, and the issue's figure is already superseded

**Decision: do not boot a stack. No figure in this spec is a fresh measurement.**

Four reasons, in order of weight:

1. **Neither published figure measures the configuration being shipped.** The
   issue compares `Default=Debug` against `Default=Warning`. The decision ships
   `Default=Information` **plus** `Database.Command=Warning`. That third
   configuration has never been run by anyone. A boot spent re-confirming a pair
   that is not the pair being shipped buys nothing.
2. **The repetition this repo demands has already happened, and it shrank the
   claim.** ADR-0135's refinement of 2026-08-31 repeated both arms three times —
   Debug: 60.0 / 79.1 / 82.5 ev/s; Warning: 169.8 / 173.7 / 244.4 — and concluded
   **"roughly 2–3×, not a clean 3×"**. The issue's headline "79.1 vs 244.4,
   roughly 3×" is one pair drawn from those six runs: the slowest figure of the
   slow arm against the fastest of the fast arm. It overstates the effect, and it
   is already corrected on the record.
3. **The decision does not depend on the multiple.** The owner chose on mechanism
   (EF emits at `Information`; the category override is the lever), not on effect
   size. No value of the multiple flips the choice.
4. **Disk.** `C:` is at ~12 GB and falling; each boot strands a 300–700 MB
   `aspire-dcp*` directory. The brief's floor is 6 GB.

**Therefore this spec makes no throughput claim. It does not restate 3×.** The
expected effect is *unquantified*. The one relevant published data point is
ADR-0135's note that pinning EF alone reached only **~103 ev/s** "because the cost
is spread across Debug categories rather than concentrated in the loudest one" —
which is the evidence that moving `Default` as well as the category is the right
shape, and simultaneously the evidence that the new default's throughput is
**unknown**, somewhere between ~103 and ~170–244.

**A follow-up measurement issue should be filed** (not by this pass — the brief
says do not touch the board): three paced runs at the new default, to establish
whether `Information` + `Database.Command: Warning` clears the 100 ev/s target.
Until it does, US2 keeps the measurement guard refusing it.

### The run-mode ADRs are not re-opened

The issue notes that ADR-0124, ADR-0126 and ADR-0127's run-mode figures were
taken against a stack that had this on, and is careful to say **that is not a
claim they are wrong** — the levers were compared against each other under the
same conditions. **This spec makes no claim about them either way and does not
re-open them.** Nothing here invalidates their comparisons.

For completeness, since it is the obvious fourth candidate: **ADR-0136 is not in
that position.** Its Consequences record "Service logging at `Warning`"
explicitly — it followed the runbook.

---

## 5. User stories

### US1 (P1) — A developer's dev stack does not log every SQL statement

**As** a developer running the Aspire stack, **I want** SQL command logging off
unless I ask for it, **so that** the log is readable and a figure taken on my
stack is not mostly a measurement of logging.

Independently shippable and observable: boot the stack, watch any service's
console, exercise one endpoint that touches the database, see no SQL. Flip one
line, restart that service, see SQL again.

### US2 (P1) — The measurement harness states the level truthfully and still refuses an unchosen one

**As** whoever runs a measurement, **I want** the run's reported log level to
match what the services are actually at, **and** the run to keep refusing a level
nobody chose for it, **so that** a breakdown's stated conditions are true and an
unmeasured configuration is not silently blessed.

**This is the half that carries the honest red.** See §6 for the decision it
encodes and `tasks.md` §2 for what the red asserts.

---

## 6. The one decision this spec makes beyond the owner's

Correcting §3.2's fallback literal to `"Information (from appsettings)"` has two
possible landings, and they differ in behaviour:

| | `LoggingIsVerbose` for an unoverridden run | Effect |
|---|---|---|
| **(A)** leave the predicate alone | `false` | The guard **stops refusing** the default stack. |
| **(B)** the guard refuses an *inherited* level, whatever it is | `true` | A measurement run must still set the level explicitly. |

**This spec chooses (B)**, and the reasoning is §4's: nobody has measured
`Information` + `Database.Command: Warning`, and the target is 100 ev/s. Landing
(A) would let the harness certify a run taken under a condition no one has
characterised — weakening an earned guard as a side effect of a config change,
which is the smell ADR-0144 names. (B) costs one environment variable on a
measurement run, which `specs/054-divide-the-recorded-85ms/quickstart.md:32`
already instructs.

The better spelling of the predicate is not "is this verbose" but **"was this
level chosen for this run"** — `ServiceLogLevel` already distinguishes the two
(env var present = chosen, absent = inherited). `plan.md` records the shape;
`tasks.md` records the red.

> **Overrule this and phase 4a changes colour**, so it is called out here rather
> than buried: under (A) there is no honest red left and 4a becomes
> characterisation-green. Under (B) there is one, on real logic, with no stack
> boot.

---

## 7. Acceptance scenarios

### AS1 — happy path (US1)

```gherkin
Given a developer runs the Aspire stack with no Logging__* environment override
When a request reaches any service that queries PostgreSQL
Then no Microsoft.EntityFrameworkCore.Database.Command entry appears in that
     service's log
And  the service's Information-level messages still appear
```

### AS2 — the deliberate act (US1)

```gherkin
Given a developer debugging a query
When they set Microsoft.EntityFrameworkCore.Database.Command to Information in
     that one service's appsettings.Development.json and restart it
Then the SQL statements appear again
And  no other service's log changes
```

### AS3 — the truthful report (US2)

```gherkin
Given Logging__LogLevel__Default is not set in the launching shell
When a measurement run reports its conditions
Then the "service log level" line names Information, not Debug
```

### AS4 — the conflict case: the guard still refuses (US2)

```gherkin
Given Logging__LogLevel__Default is not set in the launching shell
When a measurement run evaluates its conditions
Then the run is refused, because the level was inherited rather than chosen for
     this run, and the inherited level's throughput has never been measured
```

### AS5 — the guard accepts a chosen level (US2)

```gherkin
Given Logging__LogLevel__Default=Warning is set in the launching shell
When a measurement run evaluates its conditions
Then the run is not refused on logging grounds
```

### AS6 — bad-request / auth

**N/A.** No HTTP surface, no request, no principal. This change has no trust
boundary. Stated rather than omitted, so its absence is a finding and not a gap.

---

## 8. Functional requirements

- **FR-001** — All 11 files in §2's table set `"Default": "Information"`.
- **FR-002** — All 11 set
  `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"`. Uniform across
  all 11 including `AppHost`, which references no EF package: one grep must
  answer "is the firehose off" for the whole Development surface, and a file
  missing the key is indistinguishable from one nobody edited. **The uniformity
  is the requirement**; the inert line in `AppHost` is its price, recorded here
  so it is not read later as speculative generality (ADR-0036).
- **FR-003** — No `appsettings.json` (non-Development), CI workflow,
  `launchSettings.json`, Helm chart or runtime `.cs` file is modified.
- **FR-004** — The PR body carries §3.1's list of Debug sites that go dark, and
  the one-line recovery.
- **FR-005** — The two `ServiceLogLevel` fallbacks and the inline datum that pins
  the literal state `Information`, matching the shipped files.
- **FR-006** — A measurement run whose level was **inherited** rather than chosen
  is refused, with a message naming that the inherited default's throughput is
  unmeasured.
- **FR-007** — ADR-0135 carries a dated amendment recording that Development no
  longer pins Debug and what the guard now refuses. Its historical text is not
  edited (§3.3).

## 9. Non-functional and out of scope

- **NFR-001** — No throughput claim is made or restated (§4).
- **Out of scope**: Production and CI configuration; the missing EF override in
  the non-Development files (§2); re-measuring anything; ADR-0124 / 0126 / 0127.

---

## 10. Independent end-to-end test procedure

Runnable by a reviewer; the first four steps need no Docker.

1. **The population is complete.**
   `grep -L '"Default": "Information"' $(find . -name appsettings.Development.json -not -path '*/obj/*' -not -path '*/bin/*')`
   prints nothing.
2. **The override is everywhere.**
   `grep -c 'Database.Command' $(find . -name appsettings.Development.json -not -path '*/obj/*' -not -path '*/bin/*') | grep -v ':1'`
   prints nothing.
3. **Nothing else moved.** `git diff --stat origin/develop` lists only the 11
   JSON files, the five files under
   `tests/Integration.Tests/AuditObservability/` enumerated in `tasks.md`
   §"Files phase 4 touches", `docs/adr/0135-…`, and this spec directory.
   Nothing under `src/` other than the 11 JSON files.
4. **The unit lane is green.**
   `dotnet test -c Release --filter "Category!=Measurement"`.
5. **US1, observed** *(needs a stack; run only if disk allows — the reviewer may
   substitute steps 1–4 and must say so)*: boot the AppHost, `GET` any camera
   list, confirm the console shows no `Database.Command` line; then set that one
   key to `Information` in
   `src/CameraCatalog/Api/appsettings.Development.json`, restart, confirm SQL
   returns.

**Phase 5's verification note must state which of step 5 or its substitution was
performed.** A verification that quietly ran steps 1–4 and reported "verified" is
the guard-reads-the-artefact failure at the phase level.

---

## 11. Latency budget impact

**N/A — no leg of the event→overlay path is touched** (constitution §IV).

Stated with one caveat, because "N/A" is doing work here. The change **does**
alter the throughput of a dev stack, and every §IV figure recorded on a dev stack
was taken under the old default. It does not move a budget leg, change any code
on the path, or alter what any leg measures. What it changes is the *conditions*
under which future figures are taken — which is exactly why §4 declines to
certify the new default and §6 keeps the guard refusing it.

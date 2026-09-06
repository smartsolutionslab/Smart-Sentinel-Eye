# Tasks 081 — The SQL firehose is off by default

**Phase:** 3 (Tasks) · **Date:** 2026-09-06 · **Issue:** #1999
**Spec:** `spec.md` · **Plan:** `plan.md` · **Base:** `origin/develop` @ `f25e468e`

---

## The three declarations (ADR-0144)

### 1. Which engineer

**`infra-engineer`.**

Eleven host-configuration files plus the measurement harness under
`tests/Integration.Tests/AuditObservability/`. No aggregate, no handler, no
persistence, no endpoint, no bounded context — so `backend-engineer`'s brief
(Domain/Application/Infrastructure/Api inside a context) does not apply. Host
configuration and observability are `infra-engineer`'s named territory.

### 2. Behaviour-changing or -preserving, and what the red asserts

**Behaviour-changing → phase 4a is RED.** But the honest answer needs the
reasoning, not just the verdict, because the obvious red here is a bad one.

#### 2a. The reds that would be dishonest, and why they are rejected

- **A test that opens `appsettings.Development.json` and asserts
  `"Information"`.** This is the shape the repository has already recorded as a
  defect: *guards that read the design artefact prove the design was written
  down, not that it holds.* It would go red before the edit and green after, and
  would have demonstrated only that a file was saved. It also inverts the
  dependency — the config becomes correct *because a test says so*, when the
  authority is the owner's decision. **Rejected.** The reviewer's `grep` in
  `spec.md` §10 answers the same question and is honest about being a file check.

- **A throughput test.** The genuinely observable behaviour of the config change
  is *log volume under load*, and the only faithful assertion is a paced run
  measuring events per second. Such a test already exists — `NFR001_…` and
  `RunModeIngestAttribution…` — is `[Trait("Category", "Measurement")]`,
  excluded from CI, needs a stack boot, and is non-deterministic by ADR-0135's
  own finding ("at Warning the bottleneck is the machine, so it is not
  [reproducible]"). Building a second one would duplicate it and add a flaky
  gate. **Rejected.**

- **A test asserting a service emits no `Database.Command` entry.** Faithful, but
  it needs a booted stack and a log sink to read, i.e. the fixture, i.e. Docker
  and disk this pass explicitly declines (`spec.md` §4). **Rejected for this
  slice**; it is what `spec.md` §10 step 5 asks a human to do once, by eye.

**So for the eleven JSON files, taken alone, there is no honest automated red.**
Saying so plainly is the correct answer; manufacturing one would be worse than
having none. Their gate is the reviewer procedure in `spec.md` §10 and the
phase-5 observation, not a test.

#### 2b. The red that *is* honest, and it is real

It is not in the config. It is in the invariant the config falsifies.

`IngestRunConditions.LoggingIsVerbose` decides whether a measurement run is fit
to report. Its inputs come from `ServiceLogLevel`, whose absent-environment
fallback is the literal `"Debug (from appsettings)"` — the eleven files' current
value, compiled into a string. After T001, an unoverridden run is at
`Information`, and the harness both **reports the wrong level** and **reaches
the right refusal for a false reason**.

`spec.md` §6 resolves this to landing (B): a level nobody *chose for this run* is
not fit to measure through, whatever it is, because `Information` +
`Database.Command: Warning` has never been measured against the 100 ev/s target.

**The red, concretely — and it must be an assertion failure, not a build
failure.** This needs care, because the naive version gets it wrong.

`IngestRunConditions` carries no chosen-vs-inherited field today, so a theory
written against the final signature would fail to *compile*. **A compile error is
not an observed red.** ADR-0139 wants a failing assertion whose text goes in the
PR, and "CS1503: no overload takes 3 arguments" is not evidence about behaviour.

So T002 splits the difference precisely:

1. **Add the field, inert.** `IngestRunConditions` gains the chosen-vs-inherited
   fact (`plan.md` §3). `LoggingIsVerbose` is **not** touched. This is pure data
   — the record now carries a fact it does not yet act on — so it implements
   none of the behaviour under test.
2. **Write the failing datum**, against a signature that now compiles:

```csharp
// inherited, not chosen for this run: refused whatever the level
[InlineData("Information", /* chosen: */ false, true)]
[InlineData("Information", /* chosen: */ true,  false)]
```

3. **Observe the assertion failure.** `LoggingIsVerbose` is still
   `LogLevel.StartsWith("Debug") || LogLevel.StartsWith("Trace")`
   (`IngestRunConditions.cs:43-45`), so the inherited `"Information"` case yields
   `false` where the datum requires `true`, and Shouldly reports it. That message
   is the phase-4 evidence. The second datum passes, which is correct — it is the
   control.

**Red before green, on real logic, in a pure record, with no stack boot and no
Docker.** T003 then changes the predicate and nothing else.

**Do not collapse steps 1 and 3 into T003.** If the field and the predicate land
together there is no moment at which the test fails for the right reason, and the
red becomes unquotable.

**Why this is not "editing tests to pass".** ADR-0144 forbids the phase-4
engineer from editing tests to reach green. The edits here go the other way:
they make three present-tense statements *true again* after the change falsifies
them, and one of them turns red in the process. **Flag this in the PR body** so a
reviewer meets the reasoning rather than the diff — a test-file change inside a
config PR is exactly what a reviewer should stop on.

**Existing coverage is preserved, not replaced.** The `Warning`, `Debug` and
`Trace` cases of that theory keep their current expectations; only the datum
naming the old appsettings literal is restated. Nothing else in
`RunModeDriverTests` is touched.

### 3. Is the honest answer a new ADR?

**No new ADR. No constitution amendment.** Reasoned rather than assumed:

- **What is being decided is not architectural.** "What Development logs at" is a
  developer-experience trade-off, and spec 054 said so in as many words while
  declining to settle it (`research.md:197`). It binds no boundary, no contract,
  no dependency, no runtime behaviour in any deployed environment. ADRs record
  decisions that constrain future work; this one constrains nothing beyond eleven
  files a developer may flip back in one line.
- **The constitution is silent on logging levels.** `grep -niE 'log level|loglevel|logging|verbose'`
  over `.specify/memory/constitution.md` returns nothing, so no section is
  amended and none is contradicted.
- **The decision already has a home.** ADR-0135 raised this cost, named both
  candidate remedies, and measured them. Recording the outcome as a dated
  amendment there keeps the finding and its resolution in one place — the repo's
  established pattern, and the one CLAUDE.md describes for ADRs amending each
  other. A new ADR would split a single thread across two documents.
- **The one thing that looks architectural isn't.** Landing (B) changes what a
  measurement guard refuses. But it does not change *what the guard is for* —
  ADR-0135 already states its purpose ("a breakdown taken there measures the
  logging as much as the pipeline"); (B) restores that purpose after the config
  change breaks its spelling. Repairing an existing decision's implementation is
  not a new decision.
- **And the lane may not write one anyway** (ADR-0144). If a reviewer disagrees
  and thinks (B) is a decision rather than a repair, the correct response is to
  **park this and ask for an ADR**, not to write one inside phase 4.

---

## Parallelism (ADR-0109)

`[P]` is marked honestly where files are disjoint, with the caveat that **fan-out
is not worth it here**: T001 is one mechanical edit repeated eleven times and
T003–T004 are three files in one directory. One `infra-engineer` should take the
whole thing. The markers are recorded so the orchestrator can see the shape, not
as an instruction to split.

- **T001** (11 JSON files under `src/`) and **T003/T004** (3 `.cs` files under
  `tests/`) own disjoint files and have no ordering dependency between them.
- **T005** (ADR-0135) is disjoint from both.
- The eleven JSON files are *not* eleven tasks. They are one decision applied
  uniformly; splitting them would make an incomplete application look like a
  completed subset.

---

## Tasks

| ID | P | Story | Task | Depends on |
|---|:-:|---|---|---|
| **T001** | [P] | US1 | Set `"Default": "Information"` and add `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"` in all **11** `appsettings.Development.json` (`spec.md` §2 table). Preserve every existing key; new key last in each `LogLevel` object. FR-001, FR-002, FR-003. | — |
| **T002** | [P] | US2 | **Phase 4a RED**, in the three steps of §2b: add the chosen-vs-inherited field to `IngestRunConditions` **inert** (predicate untouched); restate the datum at `RunModeDriverTests.cs:239`; run it and **capture the verbatim assertion failure** — that text is the phase-4 evidence and goes in the PR body (ADR-0139). | — |
| **T003** | | US2 | Make T002 green by changing **`LoggingIsVerbose` only** — refuse an inherited level whatever it is — and render the inherited case in `Describe()`. Do not edit T002's datum. FR-006. | T002 |
| **T004** | | US2 | Correct the two `ServiceLogLevel` fallbacks (`NFR001_…:80`, `RunModeIngestAttribution…:35`) to report `Information` as inherited, and the two XML doc comments that assert *"Development pins `Debug`"* (`NFR001_…:66`, `IngestRunConditions.cs:38`). FR-005. | T003 |
| **T005** | [P] | US1 | Append a dated amendment to `docs/adr/0135-where-the-audit-span-goes.md` — the three points in `plan.md` §4. **Do not edit its existing prose.** FR-007. | T001 |
| **T006** | | US1 | PR body: the twenty-of-twenty-two-site list and the one-line recovery (`spec.md` §3.1), T002's verbatim failure, and a note that the test-file edits are factual corrections, not edits-to-pass (§2b). FR-004. | T001, T004 |
| **T007** | | both | **Phase 5.** Run `spec.md` §10 steps 1–4. Then either step 5 (needs a stack; disk permitting) **or** record explicitly that it was substituted and by what. A note that ran only 1–4 and says "verified" is the artefact-guard failure at phase level. | T006 |

### Dependency shape

```
T001 ──┬─→ T005 ──┐
       │          ├─→ T006 → T007
T002 → T003 → T004┘
```

T001 and T002 are the two entry points and are independent. Everything converges
on the PR body, because FR-004 is a delivery requirement rather than a code one.

---

## Files phase 4 touches

**Configuration — 11 files, one edit and one addition each:**

```
src/AppHost/appsettings.Development.json
src/MigrationRunner/appsettings.Development.json
src/AuditObservability/Api/appsettings.Development.json
src/Automation/Api/appsettings.Development.json
src/CameraCatalog/Api/appsettings.Development.json
src/EventIngestion/Api/appsettings.Development.json
src/Identity/Api/appsettings.Development.json
src/LayoutComposition/Api/appsettings.Development.json
src/OverlayDesigner/Api/appsettings.Development.json
src/StreamDistribution/Api/appsettings.Development.json
src/SystemVariables/Api/appsettings.Development.json
```

**Test harness — 5 files, all in `tests/Integration.Tests/AuditObservability/`:**

| File | Tasks | Why |
|---|---|---|
| `IngestRunConditions.cs` | T002, T003, T004 | the field (inert), then the predicate, then the stale XML doc at `:38` |
| `RunModeDriverTests.cs` | T002, T003 | the datum at `:239`; three construction sites need the new field |
| `IngestSpanMeasurement.cs` | T002 | `logLevel` is threaded through `:80` into the constructor at `:145` |
| `NFR001_AuditIngestLatencyTests.cs` | T004 | the fallback at `:80`, the XML doc at `:66` |
| `RunModeIngestAttributionTests.cs` | T004 | the fallback at `:35` |

`IngestSpanMeasurement.cs` is easy to miss — it is the only non-test file in the
list and it is why `spec.md` §10 step 3 checks the diff rather than trusting a
recollection of which files were meant to change.

**Record — 1 file:**

```
docs/adr/0135-where-the-audit-span-goes.md    (append only)
```

**Explicitly not touched** (FR-003): any non-Development `appsettings.json`, any
`launchSettings.json`, `.github/workflows/`, `deploy/`, `src/ServiceDefaults/`,
any `Log.cs`, and every other `.cs` file under `src/`.

---

## Board (ADR-0037 phase-3 gate)

The gate is **the feature's issue on Project #13** — feature-level, not per-task
(no `[TNNN]` issues since spec 028). The issue is **#1999**, which already
carries `agent:ready` and `tech-debt`.

**This pass did not touch the board**, by instruction. Whoever advances past the
gate must confirm #1999 is on Project #13 and add it if not:

```sh
gh project item-list 13 --owner smartsolutionslab --limit 2000 --format json \
  -q '.items[] | select(.content.url | endswith("/1999")) | .content.url'
# if empty:
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/1999
```

Verify by `content.url`, not by a number filter — the number filter returns zero
on a filled board, and `item-list` defaults to 30 items.

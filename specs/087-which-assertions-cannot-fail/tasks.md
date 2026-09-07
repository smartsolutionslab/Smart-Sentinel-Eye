# Spec 087 — Tasks

**Phase:** 3 (Tasks) — ADR-0037
**Spec:** `spec.md` · **Plan:** `plan.md` · **Evidence:** `census.md`
**Issue:** #2141 · **Also closes:** #2134 (its entire population — see `spec.md` §Scope)

**Engineer:** one **infra-engineer**. The work is a CI-selection guard plus test
attributes — no bounded context, no domain, no frontend. A backend-engineer
would also be competent here; the deciding factor is that the thing being
guarded is *which CI job reads a verdict*, which is infra's subject.

**Phase 4a colour: RED.** Behaviour-changing. T003 is written first and observed
failing on four classes; that verbatim failure is the phase-4 evidence and is
quoted in the PR body (ADR-0139, constitution §Testing).

---

## Parallelism (ADR-0109)

Almost none, and that is honest rather than a failure to decompose. **T003 is
the foundational task and blocks everything after it** — the four fixes exist
only to turn its red green, and applying them first would destroy the evidence
the gate requires.

The four fixes (T005) touch four disjoint files and could carry `[P]`, but they
are four one-line edits in one commit; splitting them across agents costs more
than it saves. They are one task.

The census tasks (T001, T002) are already complete — done during phase 1–3 and
delivered as `census.md`. They are listed so the record is complete, not as work
to redo.

---

## Tasks

### Census — complete, delivered as artifacts

- [X] **T001** — Run all five detections over the tree; record command,
  population counted, population excluded, and raw output for each.
  → `census.md`
- [X] **T002** — Decide per finding: deliberate or defect; assign an owner to
  every non-deliberate one. → `spec.md` §"The findings table" (15 findings)

### US-1 (P1) — A Docker-free integration test declares where it runs

- [X] **T003** [US1] **Write the guard, and observe it red.**
  New file `tests/Architecture.Tests/IntegrationTestSelectionTests.cs`.
  One `[Fact]` over the tree: every `*.cs` under `tests/Integration.Tests`
  containing a `[Fact]`/`[Theory]` and lacking `[Collection(AspireCollection.Name)]`
  must carry `[Trait("Category", …)]`. **Six were written** — the tree scan plus
  five trap and edge discriminators asserted on synthetic sources, so the
  matching keeps being checked when the tree changes shape (plan §"One assertion
  over the tree, five discriminators beside it").
  - Match an attribute **at the start of a line**, so a doc-comment naming
    `[Collection(AspireCollection.Name)]` — `RunModeDriverTests` does — is
    refused **twice over, independently**: with `StripComments` neutered the
    guard still reports 4 / 34, because the `[` sits behind `/// <c>` and the
    anchor alone refuses it; with the anchor removed instead, `StripComments`
    deletes the whole `///` line before either match runs, so stripping alone
    also refuses it. Neither mechanism is individually necessary for this
    shape — that is what makes it different from the block-comment case below,
    where stripping is the only thing that catches it. The claim that the
    guard goes *green* without stripping is true of the census's unanchored
    `grep` and **false** of the guard as built.
  - Strip `/* … */` blocks and `//` to end of line **before** matching
    (FR-003). The shape this is for is an attribute commented out inside a
    block comment: it *does* begin its own line, so only the stripping tells it
    from a live one. Assert it separately, or FR-003 is carried by a test that
    passes without it.
  - Do **not** key on "mentions `AspireFixture`" — same class, same reason.
  - Report offenders repository-relative with `/` separators (FR-004); reuse the
    `Path.GetRelativePath(...).Replace(Path.DirectorySeparatorChar, '/')` form
    from `LogTailCoverageTests:156-162`.
  - Reuse `RepositoryRoot()` as `LogTailCoverageTests` / `GuardBanWiringTests`
    define it. No project reference on `Integration.Tests` (plan §Boundaries).
  - Failure message names both legitimate declarations (FR-005).
  - **Run it. It must fail, naming exactly 4 classes.** Capture the verbatim
    output — that is the phase-4 evidence.
  - Depends on: nothing. **Blocks: T004, T005, T006.**

- [X] **T004** [US1] **Prove the guard by counterfactual.**
  Before fixing anything, confirm the guard reddens for the right reason: add a
  throwaway test class under `tests/Integration.Tests` with a `[Fact]` and
  neither attribute, confirm it is named, then remove it. Also confirm a
  fact-free helper file is **not** demanded to declare (the "no soft edge"
  scenario).
  - Depends on: T003.

- [X] **T005** [US1] **Add `[Trait("Category", "FixtureLogic")]` to the four
  classes**, turning the guard green:
  `AuditObservability/AttributionVerdictTests.cs` (7 facts),
  `AuditObservability/IngestAttributionTests.cs` (13),
  `AuditObservability/RunModeDriverTests.cs` (11),
  `EventIngestion/ListEventsTranslationTests.cs` (3).
  - Depends on: T003 (the red must be captured first).

- [X] **T006** [US1] **Observe the end-to-end effect, without Docker.**
  `dotnet test tests/Integration.Tests -c Release --filter "Category=FixtureLogic"`
  → the selected population rises from 3 classes to 7, and the newly-selected
  tests execute and pass **with Docker not running**. Record the before/after
  counts. This is the phase-5 observation: the cheap step now reads verdicts it
  previously skipped.
  - **34 is the count of `[Fact]`/`[Theory]` sites, not of executed cases**, and
    the two are the same number only where no theory carries `[InlineData]`.
    Here they do not: the four classes' 34 sites run as **50** cases, so the
    step goes 30 → 80. Recorded because "34 tests execute" is the kind of
    figure this spec exists to catch.
  - Depends on: T005.

### Follow-up issues — filed, not fixed

- [ ] **T007** File the findings this spec deliberately does not fix. One issue
  each, or grouped where the fix is shared; each cites `specs/087-…/spec.md`'s
  findings table. **No code changes.**
  - **F1** `WhepHandshakeLatencyTests` ~60× — *still live*, contrary to #2141;
    spec 077 already filed it as a P2 human decision (`specs/077-…/spec.md:252-262`).
  - **F2** `RunModeVariableResidueSweep` — excluded from CI *and* read nowhere;
    the only one of 13.
  - **F7/F8/F9** the `IRuleCache` keyed-seam tests (×4), `KioskPrivilegeSweepTests:104`,
    `SystemVariableValueRequestedV1HandlerTests:108`. F7 is an interface-shape
    question, not a test edit — group it.
  - **F10** `A_reconnect_after_a_success_does_not_inherit_the_previous_backoff`,
    4 ms cap against a 500 ms window.
  - **F11** `ReconnectReconcileIntegrationTests` — a timing tripwire wired to its
    own timeout. **New; not on #2141's list.**
  - **F12** `NFR_VariableResolutionLatencyTests` 133× — looser than the case
    #2141 leads with.
  - **F13** nine budgets with no recorded observation; three of them
    (`CommandLatencyTests`, `SignalRRevocationIntegrationTests`,
    `OverlayPushIntegrationTests`) sit under a spec that *promised* the figure.
  - **F14** `NFR002_MqttConnectAuthTests` p50 — 15 ms threshold, 17.58 ms
    observed; the inverse defect.
  - **F15** `SfuLatencyIsReadableTests` maps to a §IV leg and reads no latency
    figure.
  - Depends on: nothing. May run in parallel with T003–T006 **[P]** — it touches
    no file in the repository.

- [ ] **T008** Add the feature issue (#2141) to Project #13 — the Phase 3 gate.
  ```sh
  gh project item-add 13 --owner smartsolutionslab --url <issue-url>
  ```
  Needs the `project` scope. `item-add` prints nothing on success; verify with
  `--limit 2000`. **No per-task issues** — feature-level only, per the workflow
  table.

---

## Explicitly not tasks

Listed because a class-level issue sprawls, and #2141's own strongest
constraint is *do not bulk-fix*. Of 15 findings, **one** is fixed here.

| Not done | Owner |
|---|---|
| Fix NFR-001's excluded failing budget | #2127 |
| Add CI coverage for NFR-005 | #2128 |
| Measure the logging default | #2133 — needs a booted stack; this spec took none |
| The Docker-free-class omission | **subsumed here**; #2134 closes with this PR |
| Any threshold raised, lowered or tightened | **nobody, here** |
| Rename the `FixtureLogic` category | separate trivial PR if review wants it (plan §A1) |
| A second guard for detections 2, 3 or 4 | not built — none is reliably derivable |
| Any ADR | **blocked outcome** (ADR-0144); none needed — ADR-0139 already governs |

---

## Gate (Phase 3)

Tasks atomic and dependency-ordered. The feature issue (#2141) goes on Project
#13 via T008. No per-task issues are created.

**One thing for the reviewer to confirm before phase 4:** assumption A1 —
reusing `FixtureLogic` for four classes, one of which (`AttributionVerdictTests`)
is pure arithmetic and touches no fixture. The alternative is a category rename,
which is a `ci.yml` edit and is declined here on ADR-0036 grounds.

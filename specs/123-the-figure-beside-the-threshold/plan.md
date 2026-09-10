# Plan 123 — the figure beside the threshold

## What was surveyed before choosing

- All nine test files named by #2149, read end to end. Two facts changed the
  shape of the work:
  - **Seven of the nine print nothing on success.** The figure exists only
    inside a Shouldly `customMessage`, which is evaluated *only when the
    assertion fails*. So the measurement a green run performs is discarded
    by the green run. Only `CommandLatencyTests` prints unconditionally, and
    it is the one whose comment already says why.
  - `NFR002_MqttConnectAuthTests.BudgetsApplyHere` (line ~80) withholds both
    thresholds off CI, and its `<remarks>` asserts a **relation without a
    number**: *"the observed local p99 exceeds even the gross-regression
    ceiling"*. That sentence is the exact defect #2149 describes, in one
    line, and it is fixable by writing the figure down.
- `ResolvedTextReachesItsFabTests.cs:114-131` — the template. Copied in
  shape, not in wording.
- `specs/116-the-budget-times-what-it-names/spec.md` — for the §IV-leg vs
  local-SLO wording. Its formulation is reused verbatim in substance: §IV
  budgets an **asynchronous event-to-overlay path**; a synchronous HTTP
  command budget is a **local SLO** and is not one of the six legs.
- `PostgresConnectionBudget` — already the best-documented number in the
  repo (97 of 100 held; 22 connections at 100 ev/s; pools plateauing at 6).
  What is missing is only the *fixture-side* count against
  `ServiceCeiling`, which is the half the integration test asserts.
- `.github/workflows/ci.yml:179` — none of the nine carries a
  `[Trait("Category", …)]`, so **all nine run in the blocking `integration`
  job on every PR**. These are gates, not scripts, which is why a vacuous
  margin matters and why a flaky one would be worse.

## The decision

**Record in three places, adjust in none.**

1. **Beside the constant** — an XML doc comment on each threshold carrying:
   the observed figure(s), what was observed (which leg of which path), the
   ratio, and the reason the margin is that size. FR-001/FR-002.
2. **On the success path** — one line of output per measured test, so the
   recorded figure is re-observable by running the test rather than by
   trusting the comment. FR-003.
3. **In `verification.md`** — the raw runs, twice each, with the arithmetic
   and the vacuous margins named as findings.

**Why (2) is in scope and not scope creep.** A comment recording a number
nobody can reproduce is the same artifact as a threshold with no number
beside it — one indirection better, but still unfalsifiable from a clone.
The suite already contains the pattern and the justification for it
(`CommandLatencyTests:49`), so this mirrors an existing pattern rather than
inventing one, per CLAUDE.md "read before write". It adds no assertion, no
wait and no branch: `Console.WriteLine` where the class has no
`ITestOutputHelper`, `output.WriteLine` where it has one.

**What is deliberately not done:**

- **No threshold moves.** Several margins are already visibly enormous. The
  arithmetic goes in `verification.md` for a human to file; #2141 forbids
  the bulk-fix and ADR-0144 makes the edit a blocked outcome.
- **No new trait, no new filter, no timeout change.** Each of these would
  change what CI enforces.
- **`ReconnectReconcileIntegrationTests` untouched** — out of scope per
  spec.md, and its defect is structural rather than clerical.

## Phase 4a — GREEN, behaviour-preserving (characterisation)

**Declared at phase 3, per ADR-0144.** This change alters comments and adds
output lines. It changes no assertion, no threshold, no wait, no production
code. The obligation is therefore *characterisation observed green*, not red:

1. The seven in-scope tests are run **before any edit** and captured green.
   That output is the transported artifact and it is quoted in the PR.
2. After the edit the **same tests pass with assertions unmodified**. An
   assertion that had to be edited would be evidence the change moved
   behaviour — block, do not adjust (and here it would also be the exact
   forbidden outcome).

The measurement itself is the second and third runs, post-edit, since the
figures only become visible once (2) is in place. FR-004's "at least twice"
is satisfied by those two runs plus, for the pre-existing printer
(`CommandLatencyTests`), the baseline run as well.

## Risks

- **One machine, one stack.** Every stack-dependent figure comes from
  sequential runs of one filtered `dotnet test` invocation sharing a single
  `AspireFixture` boot. A second concurrent boot produces `FailedToStart`
  that reads like a code defect.
- **A cold first run reads as a regression.** Hence FR-004. Where two runs
  disagree materially, both figures are recorded rather than averaged — the
  spread *is* the information.
- **The Aspire fixture is not CI.** Every figure is labelled with where it
  was taken. A dev-box number is evidence about the margin, not a
  substitute for CI's.

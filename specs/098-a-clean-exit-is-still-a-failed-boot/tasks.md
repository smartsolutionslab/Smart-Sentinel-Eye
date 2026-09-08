# Tasks: A clean exit is still a failed boot

**Feature**: 098 | **Issue**: #1930 | **Spec**: `./spec.md` | **Plan**: `./plan.md`
**Branch**: `fix/1930-a-service-that-exits-says-why`
**Worktree**: `D:/Github/wt-1930` (cut from `origin/develop` at `4a0bae62`)

---

## The three declarations (ADR-0144)

### 1. Engineer

**`infra-engineer`.**

The entire change is `tests/Integration.Tests/Fixtures/AspireFixture.cs` and its
Docker-free companion test class — the Aspire test fixture and how it reports a
boot failure. That is the same file and the same brief as specs 062 and 084, both
delivered by `infra-engineer`.

`backend-engineer` is the wrong choice on both halves of its brief: there is no
bounded context, no domain model, no value object, no EF or Wolverine code, and
no migration. The one `src/` file this spec discusses —
`RuleCacheSeederHostedService` — is **not edited** (spec §3d).

### 2. Behaviour-changing or -preserving

**Behaviour-changing. Phase 4a is RED, and the red exists and is cheap.**

`FormatLikelyCause` returns `string.Empty` today for both new inputs, so a test
asserting it names `automation` fails on the merge base. No Docker, no Aspire
boot, no container — `[Trait("Category", "FixtureLogic")]` runs in the `backend`
job in seconds.

**Ambiguity resolves to red** and there is none here: the report says nothing
before and names a resource after. This is not a refactor and no characterisation
test applies.

**One existing assertion is inverted**, by design and declared here rather than
in a diff — see T003. Every other cause-line test must pass **unmodified**;
editing one is the signal that the change over-reached (plan §"The one existing
assertion that changes").

### 3. Board

#1930 is already on Project #13 (status *In Progress*), so no `item-add` is
needed. No per-task issues — feature-level tracking since spec 028.

**#1930 is not closed by this PR.** Reference it, do not use a closing keyword
(spec §7 US3).

---

## Dependencies and parallelism

```
T001 ──┬── T002 ─── T003 ─── T004 ─┬── T007 ─── T008 ─── T009
       └── T005 ─── T006 ──────────┘
```

- **T001 is foundational and blocks everything.** It is the red observation; the
  engineer's brief is its verbatim output.
- **T002–T004 (US1) and T005–T006 (US2) touch the same two files**, so they are
  **not** `[P]`. Marking them parallel would produce the merge conflicts ADR-0109
  exists to avoid.
- **T007 and T008 are `[P]` against each other** — different GitHub issues,
  no shared file, no shared index.

There is no foundational `Shared.Kernel` / `Shared.Contracts` / `AppHost` /
Aspire-resource work in this feature, so there is nothing for the orchestrator to
fan out. **This is a one-engineer, one-file slice by construction.** Say so in
the PR rather than implying parallelism that does not exist.

---

## Phase 4a — the red (test-writer)

### T001 [US1] Write the two failing cause-line tests and observe them red

**File**: `tests/Integration.Tests/Fixtures/AspireFixtureReportSelectionTests.cs`

Add two `[Fact]`s beside the existing cause-line tests, sentence-style names
(ADR-0053), Shouldly (ADR-0052):

- `A_long_running_resource_that_finished_with_exit_code_zero_is_named_as_a_cause`
  — `["automation"] = "Finished"`, `["automation"] = 0`, plus two `Running`
  siblings so the ordinary case sits alongside it. Assert the returned string
  contains `automation`, contains `Finished`, contains `exit code 0`, and does
  **not** contain the phrase `a non-zero exit`.
- `A_long_running_resource_that_ended_with_no_exit_code_is_named_as_a_cause`
  — the same states with `["automation"] = null`. Assert it contains `automation`
  and says no exit code was recorded, and assert
  `result.ShouldNotContain("exited with code \n")` — the empty-code rendering the
  test being replaced in T003 was written to prevent.

Run **only** the trait, capture the output **verbatim**, do not fix anything:

```sh
cd D:/Github/wt-1930
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj \
  -c Release --filter "Category=FixtureLogic"
```

**Done when**: both new tests fail with a Shouldly message showing an empty
actual string, and the rest of the `FixtureLogic` trait is green. The verbatim
failure block is the engineer's brief and goes at the top of the PR body
(ADR-0139).

**Do not** write US2's header tests yet — T005 covers them, after US1 is green,
so the two reds do not blur into one.

---

## Phase 4b — US1: the cause line names a resource that ended (infra-engineer)

### T002 [US1] Add `EndedStates` and `EndedDuringStartup`

**File**: `tests/Integration.Tests/Fixtures/AspireFixture.cs`

A `private static readonly string[] EndedStates = [Finished, Exited, "Terminated"];`
placed immediately after `FatalStartupStates`, with a doc comment stating it is
that array minus `FailedToStart` (never ran) and `RuntimeUnhealthy` (still
running), and why `"Terminated"` is a literal — cross-reference
`FatalStartupStates`' existing explanation rather than restating it.

`private static bool EndedDuringStartup(string? state)` using
`StringComparer.OrdinalIgnoreCase`, matching every other state comparison in this
file.

**Done when**: it compiles and `Category=FixtureLogic` is unchanged (still two
red, rest green). Nothing calls the new member yet — this commit builds on its
own (ADR-0087).

### T003 [US1] Rewrite the one assertion that pins the old silence

**File**: `tests/Integration.Tests/Fixtures/AspireFixtureReportSelectionTests.cs`

`A_resource_with_a_captured_null_exit_code_is_not_named_as_a_cause` asserts
`FormatLikelyCause(...).ShouldBeEmpty()` on `["automation"] = "Finished"` /
`["automation"] = null` — **#1930's observation with an assertion that the report
stays quiet about it.**

- Keep `SelectResourcesToReport(states, exitCodes).ShouldBe(["automation"])` as-is.
- Replace the `ShouldBeEmpty()` line with the naming assertion.
- Rename to `A_resource_with_a_captured_null_exit_code_is_named_without_inventing_one`.
- Rewrite the comment to keep the finding it protected — *never render "exited
  with code " with nothing after it* — and say the new sentence avoids the phrase
  entirely.

**Done when**: the rewritten test is red for the same reason T001's two are (the
production change lands in T004). The diff of this test goes in the PR body under
its own heading; a reviewer must be able to see the inversion without reading the
full diff.

### T004 [US1] Add the second population to `FormatLikelyCause`

**File**: `tests/Integration.Tests/Fixtures/AspireFixture.cs`

Per plan §1. Population A's predicate, wording and joining are **byte-identical**.
Population B is `!IsHealthy(…) && !ExitedNonZero(…) && EndedDuringStartup(state)`,
with its own sentence and its own closing clause. A's sentence first where both
are non-empty. Extract the two sentence builders as private statics if
`FormatLikelyCause` crosses 30 LOC or SonarAnalyzer's complexity limit (ADR-0084).

**Done when**:

- The three tests from T001 and T003 are green.
- **All five of these pass unmodified** — check by `git diff --stat` showing no
  change to their bodies:
  `No_cause_is_claimed_when_the_one_shot_exited_cleanly`,
  `A_resource_that_crashed_and_came_back_is_not_named_as_a_cause`,
  `The_report_names_a_likely_cause_when_a_resource_exited_non_zero`,
  `The_timeout_message_names_the_cause_before_it_lists_the_states`,
  `No_cause_is_claimed_when_no_resource_states_were_captured`.
- The whole `Category=FixtureLogic` trait is green.
- `dotnet build -c Release` is clean — analyzers included.

**If any of those five needed editing, stop and report.** That is the
over-reach signal, not a step to work through.

---

## Phase 4c — US2: the section header agrees with the cause line (infra-engineer)

### T005 [US2] Write the two failing header tests and observe them red

**File**: `AspireFixtureReportSelectionTests.cs`

Two `[Fact]`s over `FormatFailedResourceReport` (the public route to
`DescribeWhySelected`, as
`A_resource_that_ran_and_died_is_distinguishable_from_one_that_never_launched`
already does):

- `Finished` + exit `0` → the header says the process ran and stopped.
- `Finished` + exit `null` → the header says the process ended with no exit code
  recorded.

Run the trait; capture the two failures verbatim.

**Done when**: both fail showing the current bare `Finished` header.

### T006 [US2] Add the two `DescribeWhySelected` arms

**File**: `AspireFixture.cs`

Two arms above the `_ => state` fallback, matching an ended state with `0` and
with `null` respectively. The two existing arms are untouched; the fallback stays
for `Waiting`, `Starting`, `RuntimeUnhealthy` and anything unforeseen.

**Done when**: T005's two are green,
`A_resource_that_ran_and_died_is_distinguishable_from_one_that_never_launched`
passes unmodified, and the trait is green.

---

## Phase 5 — verification (infra-engineer, `/verify`)

### T007 [P] [US1] Reconstruct #1930's report and read it

Not "tests green". **Print the report a human would have read** on the observed
run and quote its first three lines.

A scratch `[Fact]` — written, run, and **deleted before the PR** — calling
`FormatTimeoutMessage(TimeSpan.FromMinutes(8), states, exitCodes, "…", "…")` over
the #1930 shape: `automation = "Finished"` with exit code `0`, eleven `Running`
siblings, `migrations = "Finished"` with `0`. Run it, copy the output.

**Done when** `verification.md` records:

- the first three lines of the report, verbatim, showing `Likely cause:` naming
  `automation` where the merge base printed the state list immediately;
- the same three lines from the merge base, for contrast — `git stash`, run,
  `git stash pop`;
- the `Category=FixtureLogic` run, green, with its count;
- an explicit line: **"No Aspire boot. No Docker. No latency measurement — spec
  §10 records this path as N/A."**

**Ask the user before booting Aspire.** Nothing in this feature needs it, and the
verification above is stronger than a boot would be: a boot cannot produce the
`Finished` state on demand without the provocation spec 084's phase 4a used, and
this change does not depend on provoking it.

### T008 [P] [US3] Comment on #1930 and #2146, and leave both open

Two comments, two different issues, no shared file — genuinely parallel with
T007.

**On #1930** — record what source reading ruled out on 2026-09-08:

- Lead 1 cannot be a *cause*: the seeder's token is linked only to
  `ApplicationStopping` (no `HostOptions.StartupTimeout` anywhere in `src/`), so
  cancelling it requires something already stopping the process; and the same
  `catch (… is not OperationCanceledException)` shape sits in
  `ReverseIndexSeederHostedService` and `KioskPrivilegeSweepHostedService`, so it
  singles out nothing. Quote spec §3.
- Lead 2 has no support beyond a merge date; `AddWolverineForContext` is shared by
  all nine contexts. Quote spec §4.
- The `.trx` artifact already uploads `if: always()` with 14-day retention, so a
  re-run erases the *conclusion*, not the *evidence*. Quote spec §5.
- The fast fail belongs to #2146 and is blocked on an ADR. Quote spec §6.
- **The issue stays open** as an unreproduced observation with a better report
  waiting for it.

**On #2146** — its body opens by saying #2066 made each of the twelve waits stop
on its own resource's fatal state. Spec 084 **FR-010** says the waits were left
exactly as they are, and what shipped is `ThrowIfAnyGatedResourceDiedAsync`, a
single post-wait sample. Correct the premise, leave `agent:blocked` in place, and
note that the correction *strengthens* #2146's case rather than weakening it —
the gap is larger than its body claims.

**Done when**: both comments are posted and both issues are still `OPEN` with
their labels unchanged. Verify the state after posting — MEMORY.md's *"A PR
mention rarely auto-closes the issue"*.

---

## Phase 6 / 7 — review and PR

### T009 [US1] Review and open the PR

`/code-review`. **Not** `/security-review`: nothing crosses a trust boundary, no
auth, no scope, no secret, no persisted state, and no production assembly is
recompiled.

PR body must carry, in this order:

1. T001's verbatim red block, and T005's.
2. The T003 assertion inversion, called out under its own heading, with the
   sentence *"this is a behaviour-changing slice; the assertion moved because the
   behaviour did"*.
3. `Phase 4a: red (behaviour-changing).`
4. The verification note from T007.
5. **What this is**: a diagnostic. `Refs #1930` — **no closing keyword**.
6. The two out-of-scope statements a reviewer will otherwise ask for: the fast
   fail is #2146's and needs an ADR (ADR-0144 forbids writing one here), and
   neither of #1930's two leads was changed because neither is supportable.

`gh pr create --base develop`. Conventional Commits (ADR-0030). **No
`Co-Authored-By` footer and no session trailer** (ADR-0086) — the PR *body* still
carries the Claude Code line.

Each commit builds on its own (ADR-0087): T002 adds an unused member, T003 and
T005 add failing tests, T004 and T006 make them pass. Verify with
`git rebase --exec 'dotnet build -c Release' origin/develop` before pushing —
four specs broke this rule in the last day.

**Do not push** without the user's go-ahead; phases 1–3 stop at the gate.

---

## Gate

**Phase 3 gate**: tasks atomic; #1930 already on Project #13. Hand back for
review before T001.

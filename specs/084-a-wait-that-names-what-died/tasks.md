# Tasks: A boot that does not pass over a corpse

**Feature**: 084 | **Issue**: #2066 (re-scoped) | **Spec**: `./spec.md` | **Plan**: `./plan.md`
**Branch**: `fix/2066-a-wait-asks-how-it-finished`
**Re-tasked**: 2026-09-07, after phase 4a disproved the previous mechanism.

---

## The three declarations (ADR-0144)

### 1. Engineer

**`infra-engineer`.** The whole change is the Aspire test fixture and its CI
behaviour. The phase-5 provocation touches two `Program.cs` files in production
projects, but only as reverted scratch lines whose purpose is to make Aspire
publish a state — infrastructure work in shape and in intent. No domain,
application, contract or frontend file is touched. `backend-engineer` would be
reading a decompile of `Aspire.Hosting.dll`, which is not its brief.

### 2. Behaviour-changing or -preserving

**Behaviour-changing. Phase 4a is RED — and the red already exists.**

It changes what `InitializeAsync` does when a resource it waited for is dead:
today it returns successfully and hands the tests a broken stack; after, it
throws naming the dead resource.

**The gate is a runtime observation, and phase 4a already made it.** Two
provocations, five boots:

> With `camera-catalog` provoked to die on startup, `InitializeAsync` completed
> normally and the run failed inside the *test body* at `00:02:51.92` with
> `Polly.Timeout.TimeoutRejectedException` → `TaskCanceledException` →
> `IOException`, on the `POST /cameras` that only runs after the fixture declared
> the stack ready.
>
> With `identity` provoked to die on startup, the filtered test **passed, twice.**
> A fifth boot, filtered onto a test that talks to identity, confirmed identity
> really was dead.

**The second block is the stronger red and belongs at the top of the PR body:** a
green test over a dead service is the most damning evidence this defect can
produce, and no unit test can express it. T001 retrieves those verbatim blocks
from the phase-4a agent; T002 re-provokes only if they were not retained.

The `Category=FixtureLogic` unit tests over `IsFatalStartupState` and
`FormatResourceDeathMessage` are **supporting evidence, not the gate** —
`plan.md` §"The honest limits" item 1 records that deleting the single new call
site leaves them all green.

**Does the healthy path need new evidence? YES.** The check is fail-open by
construction (`IsFatalStartupState` matches five states and nothing else), but
"the twelve are all `Running` at the end of a healthy boot" is a claim about the
boot, not about the predicate. It is discharged by **one** clean full `-c Release`
integration run, green (T009).

### 3. Does this need a new ADR?

**No.**

ADR-0103 decided that integration tests boot the real Aspire stack rather than
Testcontainers; ADR-0068 decided the fixture. Neither is touched. The decision
this change embodies — *the fixture refuses to proceed on a state that means
failure, naming the resource* — was taken and shipped by spec 062 / #2064 /
PR #2065, which wrote no ADR because it was applying ADR-0103's fixture rather
than amending it. Reading one more snapshot at one more point does not create a
new decision, and the fatal-state set is a *reading* of Aspire 13.5.3, not a
choice this repository gets to make.

**Its sibling is one, and is already filed.** #2146 — the fatal-state watchdog —
would turn the fixture from *a sequence of waits* into *a supervised boot*, with a
background subscription that can cancel the shared token. It carries
`agent:blocked` for exactly that reason. **Mechanism A, the per-wait predicate
this spec no longer ships, now belongs to that ADR's question 2** and must not be
re-filed separately (`spec.md` §8).

---

## Parallelism (ADR-0109)

**Almost none, and honestly so.** Every implementation task touches
**`tests/Integration.Tests/Fixtures/AspireFixture.cs`**, a single file. `[P]` is
only available where files are genuinely disjoint — here, the test file against
the fixture file.

**Foundational, blocks the rest:** T003 and T004 (the two pure functions). The
sweep cannot be written before the helpers it calls.

Phases 5 onward are strictly sequential because every runtime step is a Docker
boot on one machine with a finite disk.

---

## Phase 4a — the red (no boot needed if the evidence was kept)

- **[T001] [US1]** — **Retrieve the phase-4a output verbatim** from the agent
  that ran it: the `camera-catalog` `[FAIL]` block with its `[xUnit.net
  00:02:51.92]` marker and full exception chain, and the `identity` run's
  **passing** output with the fifth boot's confirmation that identity was dead.
  These are the PR body's red. No boot.

- **[T002] [US1]** — *Only if T001 comes back empty.* Re-provoke the `identity`
  case: scratch line in `src/Identity/Api/Program.cs` guarded by
  `Environment.GetEnvironmentVariable("PATH") is not null` (never
  `args.Length >= 0` — `S3981` under `-c Release` with `TreatWarningsAsErrors`;
  062 paid for this twice), run the filtered test, capture the **pass**. One boot.
  *Check `C:` free space first. Do not boot below 8 GB; abort below 5 GB.*

- **[T003] [US1]** — Amend
  `tests/Integration.Tests/Fixtures/AspireFixtureStartupGateTests.cs`. It stood
  at **sixteen cases naming the two members twenty times**; the amendment makes
  it **seventeen cases and twenty-two names** — the figure the build confirms
  (15 on `IsFatalStartupState`, 7 on `FormatResourceDeathMessage`). **Fifteen of
  the sixteen keep their assertions**; what changes is one case, one case added,
  and the comments throughout.

  **The CS0117 count is not a red, and this task no longer claims one.** An
  earlier draft recorded the file as "red as 20 × CS0117" and told the engineer
  to observe it. `develop` already carries the correction — spec 061's
  `24e6fc4c`: *at that tree the tests do not fail, they fail to compile, which
  is not a red test; a prelude is required wherever a test names a symbol the
  previous commit lacks*. So the branch is committed as a **prelude first**:
  `refactor(integration)` introducing `FatalStartupStates`,
  `IsFatalStartupState` and `FormatResourceDeathMessage` with no call site, then
  this file **green on arrival**, then `fix(integration)` with the sweep and the
  call site. Each of the three builds on its own — ADR-0087 lands them
  individually on `develop`, so one that compiles only with its successor breaks
  `git bisect` forever.

  **Unchanged assertions (11) — the fatal-state vocabulary survives intact:**
  `Finished_is_a_fatal_state_for_a_long_running_resource`,
  `Exited_is_a_fatal_state`, `FailedToStart_is_a_fatal_state`,
  `RuntimeUnhealthy_is_a_fatal_state`,
  `Terminated_is_a_fatal_state_although_Aspire_has_no_constant_for_it`,
  `Running_is_not_a_fatal_state`, `Unknown_is_not_a_fatal_state`,
  `NotStarted_is_not_a_fatal_state`, `Waiting_is_not_a_fatal_state`,
  `A_null_state_is_not_a_fatal_state`, `State_matching_ignores_case`.

  **Unchanged assertions (4) — the message shape survives:**
  `The_message_names_the_resource_the_state_and_the_exit_code`,
  `The_message_says_so_when_no_exit_code_was_recorded`,
  `The_message_puts_the_cause_before_the_log`,
  `The_message_does_not_list_every_resource`.

  **One case is rewritten**, because its claim is now false:
  `The_message_says_the_wait_stopped_rather_than_spending_the_budget` →
  `The_message_says_the_fixture_refused_to_report_a_healthy_boot`, asserting
  `"rather than reporting a healthy boot over a dead resource"`. The check spends
  the whole boot and then refuses to return; it saves no budget, and the message
  must not say it does.

  **Comments need rewriting throughout.** Several cite "the twelve waits" or
  "would fire on every healthy boot", both of which described the withdrawn
  mechanism. `Waiting_is_not_a_fatal_state` in particular: the reason it must not
  match is no longer that it is common mid-boot, but that at the sample a
  `Waiting` resource is unreachable — its wait would still be blocked — and
  fail-open is the correct default for a state we did not expect.

  Also add one case the sweep needs and the predicate did not:
  `Two_dead_resources_are_both_reported` (FR-008), over the joined message.

  **Green on arrival, and the subject line must say so.** These cases hold the
  vocabulary and the message shape; neither can be observed failing for the
  right reason, because nothing in this file exercises the sweep. `plan.md`
  §"The honest limits" ¶1 concedes exactly that. The red→green that discharges
  §Testing is the **runtime provocation** (T001/T002 → T009), which is on the
  behaviour and is the stronger evidence.

  *Note: supporting evidence. T001/T002 is the gate.*

## Phase 4b — implementation (GREEN)

- **[T004] [US1]** — `AspireFixture.IsFatalStartupState(string?)`, `internal
  static`, beside `ExitedNonZero`. Five states, `OrdinalIgnoreCase`, fail-open on
  everything else. The `"Terminated"` literal carries the comment naming
  `KnownResourceStates.TerminalStates` (`{ Finished, FailedToStart, Exited }`)
  and `Aspire.Hosting.Dcp.Model.ExecutableState.Terminated` (`internal`). Blocks
  T006.

- **[T005] [US1]** — `AspireFixture.FormatResourceDeathMessage(string, string,
  int?, string)`, `internal static`. `int?` — see `plan.md`. Wording mirrors
  `FormatMigrationFailureMessage`, with the *"rather than reporting a healthy boot
  over a dead resource"* sentence. Blocks T006. *May be written in the same edit
  as T004.*

- **[T006] [US1]** — `private static readonly string[] GatedResources = [...]`
  (the twelve; `migrations` excluded with the comment saying why), and
  `ThrowIfAnyGatedResourceDiedAsync()` — **no `CancellationToken`**, and
  `plan.md` §4 says why it must not have one: loop, `TryGetCurrentState`,
  `IsFatalStartupState`, collect **all** dead, fetch each one's log only then, throw
  one `InvalidOperationException` carrying every `FormatResourceDeathMessage`.
  Depends on T004, T005.

- **[T007] [US1]** — The call site: one `await ThrowIfAnyGatedResourceDiedAsync()`
  at the end of the `try` block in `InitializeAsync`, after
  `WaitForServiceHealthAsync("overlay-designer")`, with the comment from
  `plan.md` §5. **No existing wait, comment or line order is touched** (FR-010).
  Depends on T006.

- **[T008] [US1]** — Re-run T003's Docker-free suite. Green, **unmodified**. If
  any assertion has to be *edited* to pass, stop — that is evidence the behaviour
  moved somewhere other than intended.

## Phase 5 — verification (two boots, after T010's withdrawal)

- **[T009] [US1]** — **The `identity` proof, and the headline.** Scratch line in
  `src/Identity/Api/Program.cs`; run the same filter that **passed** in phase 4a.
  Expect `InvalidOperationException` naming `identity`, its state, its exit code
  (or "no exit code recorded"), and its log. A pass here is a failure of the
  change. Depends on T007.

- **[T010] [US1]** — ~~**The `camera-catalog` proof.**~~ **Withdrawn at phase 6,
  with the argument, rather than skipped.** It would have moved the scratch line
  to `src/CameraCatalog/Api/Program.cs` to show the check is not hard-wired to
  one resource. It is not, and no boot is needed to see it: the sweep has **no
  name-dependent branch** — `name` is the loop variable over a `static readonly
  string[]`, passed unmodified to `TryGetCurrentState`, to
  `CaptureOneResourceLogAsync` and into the message; `IsOneShot` is deliberately
  never consulted, because FR-003 is discharged by omitting `migrations` from
  the list, not by a branch on its name. And phase 4a's *pre-change*
  camera-catalog provocation already observed that a dead camera-catalog reaches
  this point. A second .NET project dying on the same DCP path covers nothing
  the identity run does not. **A boot is not spent on it.**

- **[T011] [US1]** — Revert the scratch line — **both**, had T010 run. `git
  status` clean under `src/`. Depends on T009.

- **[T012] [US1]** — **The healthy-path evidence.** One full clean
  `dotnet test … -c Release` integration run, green, with **CI's filter**:
  `Category!=Measurement&Category!=Disruptive&Category!=Maintenance`. **Not
  "no filter"**, which an earlier draft said and which is not green on this
  machine and never was: `RunModeIngestAttributionTests` is `Category=Measurement`
  and needs `SSE_RUNMODE_*` pointing at a long-lived run-mode stack. This is the
  discharge for the one risk with suite-wide blast radius. Depends on T011.

- **[T013] [US1]** — Write
  `specs/084-a-wait-that-names-what-died/verification.md`: the verbatim phase-4a
  red (including the **passing** identity run), the post-change block for
  `identity` (T010 withdrawn, so there is no second one), the clean-run result,
  and the machine state. **Written at phase 6, not phase 4b** — it was left
  unwritten and the three runs survived only in a handover narrative, which on a
  spec about a green signal that proved nothing is the one artifact that must
  not be missing.
  **No timing claim** unless a figure was actually read off an
  `[xUnit.net HH:MM:SS.ss]` marker — never `Duration:`, which read `8 ms` for a
  nine-minute boot in 062. Depends on T012.

  *This spec makes no minutes-saved claim (`spec.md` §10). Do not reintroduce one.*

## Phase 6 — QA

- **[T014] [US1]** — `/code-review`. Each item has a known prior failure behind it:
  - Any restatement of `is null or 0` outside `ExitedNonZero` (#2061's blocker).
  - The exit code being used as a **gate** rather than as detail (FR-005) — it is
    `null` for all three containers.
  - `==` instead of `OrdinalIgnoreCase` on state text.
  - `Unknown` or `NotStarted` having crept into the fatal set.
  - `migrations` having crept into `GatedResources` — `Finished` is its success.
  - Logs fetched on the healthy branch (FR-009), or a `WatchAsync` where
    `TryGetCurrentState` belongs (NFR-003).
  - Only the first dead resource reported (FR-008).
  - Any edit to the twelve waits, their order or their comments (FR-010).
  - The new exception being a `TimeoutException` or an OCE subtype (FR-006).
  - A scratch line surviving into the diff.

---

## Board (ADR-0037 phase-3 gate)

Feature-level only — no per-task issues since spec 028. **#2066 already exists**,
so the gate is that it is on Project #13 with status Todo. Verify with
`--limit 2000`; `item-list` defaults to 30 and a full board reads as empty
otherwise.

**Two board edits are recommended and neither is made here:**

1. **Re-title and re-body #2066** to the re-scoped defect — suggested title
   *"The fixture reports a healthy boot with a dead service"* — recording the
   disproof. No new issue: `spec.md` §"What the board should say".
2. **Update #2146** with the corrected coverage table (`spec.md` §3) and with
   mechanism A, which its ADR must now decide on. Its current body inherits the
   false premise.

**This agent does not touch the board.**

---

## Files phase 4 will touch

| File | Phase | Committed |
|---|---|---|
| `D:\Github\wt-2066\tests\Integration.Tests\Fixtures\AspireFixtureStartupGateTests.cs` | 4a (amend) | yes |
| `D:\Github\wt-2066\tests\Integration.Tests\Fixtures\AspireFixture.cs` | 4b | yes |
| `D:\Github\wt-2066\specs\084-a-wait-that-names-what-died\verification.md` | 5 (new) | yes |
| `D:\Github\wt-2066\src\Identity\Api\Program.cs` | 5 scratch | **no — reverted** |
| `D:\Github\wt-2066\src\CameraCatalog\Api\Program.cs` | 5 scratch | **no — reverted** |

`tests/Integration.Tests/Fixtures/AspireFixtureMigrationGateTests.cs` is **not**
expected to change: `FormatMigrationFailureMessage` and `ExitedNonZero` keep
their signatures and behaviour, and the `migrations` wait is not touched at all
by this re-scope. If anything forces an edit there, stop and re-read — do not
adjust the assertion.

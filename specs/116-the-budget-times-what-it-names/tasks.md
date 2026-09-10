# Tasks — spec 116

**Phase 4a colour: RED (behaviour-changing).** Production code is
untouched, but the covering test *is itself the thing being edited*, so
ADR-0144's characterisation path — "the same tests must pass
**unmodified**" — cannot hold. ADR-0144 resolves that ambiguity to
behaviour-changing, because that path fails loudly. The red artifact is
the guard (T003), not the layout test.

| ID | P? | Story | Task | Status |
|---|---|---|---|---|
| T001 | | US1 | **Baseline measurement.** Run the CI-filtered integration suite (`Category!=Measurement&Category!=Disruptive&Category!=Maintenance`) **twice** on the unfixed tree; record the layout test's timed section from each run. Keep the temp-file instrumentation for this task only. | |
| T002 | | US1 | Remove the uncommitted instrumentation block (`LayoutLifecycleIntegrationTests.cs:46-48`, the `%TEMP%/sse-2119-budget.txt` append). The measurement is reported in `verification.md`, not written by the test. | |
| T003 | | US1 | **(4a, RED)** New `tests/Architecture.Tests/StopwatchWindowScopeTests.cs`: `StripComments` / `ClosedWindows` / `ForbiddenCalls` + three `[Fact]`s per plan. Rule: a closed `Stopwatch <v> = Stopwatch.StartNew();` … `<v>.Stop();` window must not contain `CreateAdminClientAsync`, `CreateAuthenticatedClientAsync` or `RegisterCameraAsync`. Normalise separators; exclude `/obj/` and `/bin/`. **Observe it red**, quote the verbatim failure, and confirm it names `LayoutLifecycleIntegrationTests.cs` **and no other file**. Commit red. | |
| T004 | | US1 | **(4b)** The fix: hoist `await LayoutRequests.RegisterCameraAsync(aspire)` to a local declared before `Stopwatch.StartNew()`. Two lines. No other statement, and not the 500 ms figure, changes. | |
| T005 | | US1 | **(4b)** Rewrite the class doc comment: the 500 ms is a **local SLO for the layout-composition synchronous command path**, explicitly **not** one of constitution §IV's six event-to-overlay legs (§IV budgets an event path; this is a synchronous HTTP command), and the camera registration is a precondition kept outside the clock. | |
| T006 | | US1 | Run T003 green. Run the full `Category=FixtureLogic` step and `coverage-check.ps1` to confirm no collateral. | |
| T007 | [P] | US1 | Prove the guard by **counterfactual**: feed `ClosedWindows`/`ForbiddenCalls` a snippet that provisions inside a closed window and confirm it is reported; and a snippet with an unclosed clock and a bare unrelated `.Stop();` and confirm it is not. Disjoint file from T008's work. | |
| T008 | | US1 | **After measurement.** Re-run the CI-filtered suite **twice**; report the layout test's timed section and the delta against T001. Two runs because the first after machine churn looks like a regression. | |
| T009 | | US1 | `verification.md`: the four figures, the delta, the guard's red-then-green transcript, the counterfactual, and one line stating this touches **none** of §IV's six legs so §VII is not engaged. | |

## Ordering and dependencies

```
T001 → T002 → T003 (red, committed) → T004 → T005 → T006 → T008 → T009
                                                      └→ T007 [P]
```

- **T001 blocks everything.** A baseline taken after the fix is not a
  baseline.
- **T003 must be committed red before T004.** ADR-0139: the failure has
  to be in the history, not only in a transcript. That commit is red by
  design; T004+T005 restore green.
- **T004 and T005 land together** — the hoist without the sentence leaves
  the figure meaningless again, which is the issue's actual complaint.
- **T007 is the only `[P]`.** It owns assertions inside the guard file
  written at T003 and touches nothing T008 measures; everything else is
  strictly sequential on one Aspire stack (two concurrent boots produce a
  `FailedToStart` that reads like a code defect).

## Blocked outcomes — do not take them to reach green

- Raising the 500 ms budget (ADR-0144: no gate weakening).
- Adding a `Category` trait to the layout test — it would leave
  `ci.yml:179`'s filter and stop gating PRs.
- Broadening the guard's ban list to make an unrelated failure go away,
  or narrowing it to the one filed call site.

## Gate

Tasks atomic; the **feature-level** issue #2119 on Project #13 (per-task
issues stopped after spec 028):

```sh
gh project item-add 13 --owner smartsolutionslab --url <issue-url>
```

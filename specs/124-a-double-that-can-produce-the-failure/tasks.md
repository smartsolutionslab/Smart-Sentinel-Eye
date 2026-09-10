# Tasks — 124 A double that can produce the failure

Feature-level issue: **#2151** (on Project #13). No per-task issues — the
convention since spec 028 (CLAUDE.md §Workflow).

| # | Task | FR | File | Done when |
|---|---|---|---|---|
| T001 | Baseline the five affected test projects | — | — | 110 / 9 / 61 / 12 / 81 green |
| T002 | Apply counterfactuals M1–M4, M6; run the six candidates unmodified | — | `src/` (reverted) | all six green, output quoted |
| T003 | Give `Automation.Application.Tests`' `InMemoryRuleCache` a lookup-key log | FR-001 | `tests/Automation.Application.Tests/Fakes/InMemoryRuleCache.cs` | `Lookups` returns an ordered snapshot under the same lock as the buckets |
| T004 | Assert the looked-up key in tests 1 and 2 | FR-001 | `…/Evaluation/RuleEvaluatorTests.cs` | each asserts exactly one lookup, for the fab it passed |
| T005 | Assert the looked-up key in test 3, and its absence in test 4 | FR-001 | `…/EventHandlers/FabEventIngestedV1HandlerTests.cs` | test 4 asserts `Lookups` is empty for all three unusable-fab cases |
| T006 | Assert the enrolled-kiosk filter against the shipped client | FR-002 | `tests/Identity.Infrastructure.Tests/KeycloakAdmin/EnrolledKioskQueryTests.cs` (new) | a realm holding a kiosk, an operator console and an attribute-less client yields the kiosk alone |
| T007 | Assert the second removal sends nothing, plus its control | FR-002 | same file | no `DELETE` when no realm role is held; a `DELETE` carrying the role when one is |
| T008 | Record repeats in `FakeKeycloakAdminClient`; assert them in test 7 | FR-003 | `tests/Identity.Application.Tests/Fakes/FakeKeycloakAdminClient.cs`, `…/KioskPrivilegeSweepTests.cs` | two passes over one kiosk are two removals, not a set of one |
| T009 | Point tests 5 and 7 at where their property is proved | FR-002 | `…/KioskPrivilegeSweepTests.cs` | both docstrings name the Infrastructure test; no assertion removed |
| T010 | Capture the log in test 6 and assert the rejected name | FR-004 | `tests/SystemVariables.Application.Tests/EventHandlers/SystemVariableValueRequestedV1HandlerTests.cs` | single Warning entry, containing `1bad` |
| T011 | Label `repo.Variables.ShouldBeEmpty()` as the forward guard it is | FR-005 | same file | assertion kept, `because` written, remarks explain why no double can redden it |
| T012 | M5 pass: swap the pre-change sweep tests in, observe green; swap back, observe red | FR-003 | — | spec 092's `Passed: 5` reproduced, then red |
| T013 | Revert every counterfactual, `touch`, re-run | — | `src/` | five projects green; `git status` shows no `src/` change |
| T014 | `dotnet build -c Release`; full unit-test count | — | — | 0 warnings, 0 errors; count recorded |
| T015 | Write `verification.md` with all three observations per test | — | `specs/124-*/verification.md` | every quote is output this branch produced |

## Dependency order

T001 → T002 → (T003 → T004, T005) ‖ (T006, T007) ‖ (T008 → T009) ‖ (T010 →
T011) → T012 → T013 → T014 → T015.

T003 gates T004 and T005 (they read `Lookups`). T008 gates T009 only for the
file's coherence, not for compilation. Everything else is independent.

# Tasks 082 — An audit row names its fab

**Spec:** `spec.md` · **Plan:** `plan.md`
**Issues:** #2068 (delivering), #2071 (folded in)
**Engineer:** backend
**Phase 4a colour:** **red** — behaviour-changing (see §Phase 4a below)

## Phase 4a declaration — what the red asserts

Three distinct reds. They are not interchangeable and all three are required.

| Red | Asserts | Why it alone is not enough |
|---|---|---|
| **Guard red** (T001) | `EventMetadataFabDeclarationTests` names exactly 3 sites against unmodified `src/` | Proves the population, not that the right fab is stamped |
| **Handler red** (T002) | each of the 3 published events' `Metadata.Fab` is the expected fab | Producer-side only; the filed symptom is on the read surface |
| **Symptom red** (T003) | a munich archive row is not returned to a berlin-only caller's audit search | The symptom; but would pass on an empty page without T002's precision |

The guard's red is the load-bearing one for the *class*; the handler and symptom reds are
load-bearing for the *instance*. The guard red must be captured **before** any handler is
touched — a guard first run after the fix proves only that a file was edited.

## User story US1 — an operator sees only their own fab's history

| ID | P | Story | Task | Files | Depends on |
|---|---|---|---|---|---|
| T001 | | US2 | Write `EventMetadataFabDeclarationTests` per plan §The guard. Run against **unmodified** `src/`. **Capture the verbatim failure listing all 3 sites** — it is the PR's evidence and the proof the population is 3, not 4. | `tests/Architecture.Tests/EventMetadataFabDeclarationTests.cs` | — |
| T002 | [P] | US1 | Add a producer assertion on `SystemVariableArchivedV1.Metadata.Fab`. Observe red. Note: this file already asserts `Metadata.Fab` on the *other* event it publishes — assert the archived event specifically. | `tests/SystemVariables.Application.Tests/EventHandlers/VariableArchivedDomainEventHandlerTests.cs` | T001 |
| T003 | [P] | US1 | Add producer assertions on `LayoutRevisionArchivedV1.Metadata.Fab` and `LayoutRevisionPublishedV2.Metadata.Fab`. Observe red. | `tests/LayoutComposition.Application.Tests/EventHandlers/LayoutRevisionArchivedDomainEventHandlerTests.cs`, `…/LayoutRevisionPublishedDomainEventHandlerTests.cs` | T001 |
| T004 | | US1 | Add the symptom test: seed/produce a munich archive row, search as a berlin-only operator, assert absent; and assert a genuinely fab-less row is still returned (the #1300 non-regression). Observe red on the first half. | `tests/Integration.Tests/AuditObservability/CrossFabReadGuardIntegrationTests.cs` | T001 |
| T005 | [P] | US1 | FR-001: pass `fab.Value` in the `EventMetadata` fab position for `SystemVariableArchivedV1`. **One argument. Do not touch line 101** — that is #2012's fix and is already correct. | `src/SystemVariables/Application/EventHandlers/VariableArchivedDomainEventHandler.cs` | T002 |
| T006 | [P] | US1 | FR-002 + FR-003: pass `fab.Value` in both LayoutComposition handlers. One argument each. | `src/LayoutComposition/Application/EventHandlers/LayoutRevisionArchivedDomainEventHandler.cs`, `…/LayoutRevisionPublishedDomainEventHandler.cs` | T003 |
| T007 | | US1 | FR-006: correct the stale "camera, stream, layout, overlay and variable" enumeration to what is true after T005/T006 — camera already stamps, layout and variable now do. Comment only, no behaviour. | `src/AuditObservability/Application/Queries/Handlers/SearchAuditQueryHandler.cs`, `tests/Integration.Tests/AuditObservability/CrossFabReadGuardIntegrationTests.cs` | T005, T006 |
| T008 | | US2 | FR-007: the guard's doc comment states what green does **not** prove — not the right fab, not a runtime-null nullable (#2076), not non-handler publishers. | `tests/Architecture.Tests/EventMetadataFabDeclarationTests.cs` | T001 |
| T009 | | — | Full `-c Release` build + test. Guard green, all three producer assertions green, symptom test green, `HandlerDeconstructionTests` and `BoundaryTests` unaffected. | — | T005–T008 |

## Parallelism

- **T002 ∥ T003** — different test projects.
- **T005 ∥ T006** — different bounded contexts, no shared file (ADR-0109).
- **T001 blocks everything.** Not because of a file conflict but because its red run
  against unmodified source is the evidence; run it after a fix and it proves nothing.
- **T007 must follow T005/T006** — it describes the state they create.
- No foundational task: no `Shared.Kernel`, `Shared.Contracts`, `AppHost` or Aspire change.

## Phase 5 (verify)

The §Independent end-to-end test procedure in `spec.md`. Requires the Aspire stack —
**check C: headroom first (≥ 8 GB), stop the AppHost process directly afterwards.**
Latency: **N/A**, nothing on the event→overlay path (spec §Latency-budget impact).

## Phase 7

One PR closing **both** #2068 and #2071 with closing keywords, `--base develop`. Body
carries T001's verbatim guard failure (all three sites) and the producer/symptom reds.
Per memory: a mention rarely auto-closes — check both issues' state after the merge.

## Board

The feature issue is #2068; #2071 is folded in. **This pass does not touch the board.**

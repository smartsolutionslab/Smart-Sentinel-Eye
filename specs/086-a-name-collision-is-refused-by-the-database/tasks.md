# Tasks 086 — A name collision is refused by the database

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2115
**Status:** Phase 3 (Tasks) — awaiting gate review
**Engineer:** backend (one agent per story; both stories are backend)
**Phase 4a colour:** **red** overall — see `plan.md` §7 for the per-part split

---

## Legend and rules

`[ID] [P?] [Story]` — `[P]` means the task owns files disjoint from every other
`[P]` task at the same point and may run in parallel (ADR-0109).

**No foundational task exists.** Nothing in `Shared.Kernel`, `Shared.Contracts`,
`AppHost`, or the Aspire resource graph changes, so there is nothing to land
before the fan-out. **US1 and US2 are fully parallel across two bounded
contexts** — the file sets do not intersect at any point.

The only sequencing preference: **US1 lands the pattern.** If both stories run at
once, whoever takes US2 should read the US1 diff rather than re-derive the
design.

**Every commit must build on its own** (ADR-0087). The natural per-context
commit boundary is: tests → aggregate → configuration + migration + repository +
comments as one commit (the configuration and the migration cannot compile-and-
pass apart from each other, and the repository predicate depends on the column).

---

## US1 (P1) — Two concurrent overlay creates cannot both succeed

**Context:** `OverlayDesigner`. Ships and is observable alone.

### Phase 4a — tests first

| ID | P | Task | Depends on |
|---|---|---|---|
| **T001** | [P] | **Characterisation baseline.** Run `tests/OverlayDesigner.Domain.Tests`, `tests/OverlayDesigner.Application.Tests` and `tests/Integration.Tests/OverlayDesigner/` **unchanged** and capture the verbatim green output. These cover the create/branch/archive name rules that must not move. They must pass **unmodified** after every later task in this story; an assertion needing an edit is evidence the predicate moved — block, do not adjust. | — |
| **T002** | | **Red domain tests for the chain marker** in `tests/OverlayDesigner.Domain.Tests/Overlay/OverlayTests.cs`: a new chain is not archived; archiving the last live revision sets the marker; publishing (which archives the prior Published) leaves it unset; branching a fully-archived chain clears it; reverting in place sets it when it strands the chain. Observe failing (the property does not exist) and quote the output. | T001 |
| **T003** | | **Red concurrency integration test** in `tests/Integration.Tests/OverlayDesigner/` — two `POST /overlays` with an identical name issued from two tasks started together; assert exactly one `201`, one `409`, and `count(*) = 1`. **Must be observed failing against the current schema** (both requests answer `201`) and the failure quoted in the PR. A test first run after the index exists proves the index is present, not that it changed anything. | T001 |
| **T004** | | **Red integration tests for the preserved rules** in the same file: an archived chain releases its name (`201`, two rows, exactly one with an unset marker); resurrecting a chain whose name was taken is refused `409 OVERLAY_NAME_TAKEN`. The second is existing behaviour — it may arrive green; the *first* asserts the new column and will not. | T001 |

### Phase 4b — implementation

| ID | P | Task | Depends on |
|---|---|---|---|
| **T005** | | **`Overlay.ArchivedAt`** (`ArchivedAt?`) on `src/OverlayDesigner/Domain/Overlay/Overlay.cs`. Recompute in one private call at the end of every mutator — `CreateDraft`, `BranchDraft`, `EditDraft`, `Publish`, `Revert`, `ArchiveRevision`. Invariant: set **iff** every revision is `Archived`. Reuse the existing predicate shape from the chain's own archived-revision logic; do not duplicate it. | T002 |
| **T006** | | **`OverlayConfiguration.cs`**: map `archived_at` (nullable, same `HasConversion` shape as the revision-level `ArchivedAt` at lines 145-148); replace the plain index at lines 68-69 with `ux_overlays_name_active` — `.IsUnique().HasFilter("archived_at IS NULL")`. Rewrite the class doc so it describes what the file now does. | T005 |
| **T007** | | **Migration** (ADR-0067, `OverlayDesignerDbContext`): `AddColumn` → backfill `archived_at` from `overlay_revisions` for chains with no non-`Archived` revision → **pre-flight duplicate check** (`DO $$ … RAISE EXCEPTION … USING HINT`, modelled on `20260823194632_CaseInsensitiveCameraNames.cs:38-61`, listing the colliding names; **not** auto-reconciled) → `DropIndex ix_overlays_name` → `CreateIndex ux_overlays_name_active`. `Down` reverses all four. Regenerate `OverlayDesignerDbContextModelSnapshot.cs`. The pre-flight check runs **after** the backfill — its predicate needs the new column. | T006 |
| **T008** | | **`OverlayRepository.GetByNameAsync`**: `.Where(candidate => candidate.ArchivedAt == null)` in place of the `Revisions.Any(...)` predicate, so the handler's check and the index evaluate the same column. Delete the stale comment claiming the index is permissive. | T007 |

### Gate

| ID | P | Task | Depends on |
|---|---|---|---|
| **T009** | | **Re-run T001's suites unmodified** and confirm green; then T002–T004 green. Quote both. | T008 |

---

## US2 (P2) — Two concurrent layout creates in one fab cannot both succeed

**Context:** `LayoutComposition`. Same shape, fab-scoped. Every task below is
`[P]` against its US1 twin — disjoint files, different context.

### Phase 4a — tests first

| ID | P | Task | Depends on |
|---|---|---|---|
| **T010** | [P] | **Characterisation baseline** — `tests/LayoutComposition.Domain.Tests`, `tests/LayoutComposition.Application.Tests` and `tests/Integration.Tests/LayoutComposition/` run unchanged, output captured. Includes the fab-scoping suites, which must not move. | — |
| **T011** | [P] | **Red domain tests for the chain marker** in `tests/LayoutComposition.Domain.Tests/Layout/LayoutTests.cs` — same five cases as T002. Observe failing; quote. | T010 |
| **T012** | [P] | **Red concurrency integration test** in `tests/Integration.Tests/LayoutComposition/` — two `POST /layouts` with an identical name **in one fab**, concurrent; one `201`, one `409`, `count(*) = 1`. Observed failing on the current schema; failure quoted. | T010 |
| **T013** | [P] | **Red/green integration tests for the preserved rules**: the same name in a **second fab** is still accepted; an archived chain releases its name within its fab; the cross-fab tile refusal still precedes the name check (`CreateLayoutDraftCommandHandler` ordering unchanged); resurrecting a chain whose name was taken is refused `409 LAYOUT_NAME_TAKEN`. | T010 |

### Phase 4b — implementation

| ID | P | Task | Depends on |
|---|---|---|---|
| **T014** | [P] | **`Layout.ArchivedAt`** (`ArchivedAt?`) on `src/LayoutComposition/Domain/Layout/Layout.cs`, recomputed at the end of every mutator. The predicate already exists as `NewestWhenFullyArchivedOrNull` (`Layout.cs:227-228`) — express the marker in terms of the same rule rather than a second copy of it. | T011 |
| **T015** | [P] | **`LayoutConfiguration.cs`**: map `archived_at`; replace the plain index at lines 83-84 with `ux_layouts_fab_name_active` on `(Fab, Name)` — `.IsUnique().HasFilter("archived_at IS NULL")`. **Keep `ix_layouts_fab`** (listing filter, not a uniqueness index). Rewrite the class doc at lines 20-26 and the inline comment at lines 76-84 — both currently say the SQL-side index is deferred and that the application check is authoritative for v1. | T014 |
| **T016** | [P] | **Migration** (`LayoutCompositionDbContext`): same four steps as T007, keyed on `(fab, name)`, backfilling from `layout_revisions`, pre-flight check listing collisions **per fab**. Regenerate `LayoutCompositionDbContextModelSnapshot.cs`. | T015 |
| **T017** | [P] | **`LayoutRepository.GetByNameAsync`**: `.Where(candidate => candidate.ArchivedAt == null)`; delete the stale comment at lines 33-39. Leave the fab predicate and its comment alone. | T016 |

### Gate

| ID | P | Task | Depends on |
|---|---|---|---|
| **T018** | [P] | **Re-run T010's suites unmodified** and confirm green; then T011–T013 green. Quote both. | T017 |

---

## Cross-story verification (phase 5)

| ID | P | Task | Depends on |
|---|---|---|---|
| **T019** | | **Observe it end to end** per `spec.md` §5 against the Aspire fixture. The verification note must carry two things a green suite does not prove: (a) the `pg_indexes.indexdef` text for **both** new indexes, read from a live database after `MigrationRunner` ran, showing `UNIQUE` and the partial predicate; (b) the observed `201`/`409` split from two genuinely concurrent requests, with the post-condition row count. **Latency: N/A** — no leg of constitution §IV is on this path. | T009, T018 |

---

## Board (phase 3 gate)

Issue **#2115** must be on Project #13 — added by hand; `/speckit-tasks` adds
nothing. Per-task issues are **not** created (the repo stopped at spec 028).

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/2115
```

Verify with `--limit 2000`; `item-list` defaults to 30 and a filled board looks
empty without it.

---

## Not tasks

Listed so nobody adds them mid-flight:

- No `Api` or `Application` change. Both endpoints already declare `409`, and
  `UniqueConstraintExceptionHandler` matches SQLSTATE `23505` generically
  (verified, `UniqueConstraintExceptionHandler.cs:118-124`) — the new indexes
  need no registration.
- No `Shared.Contracts`, no domain or integration event, no Wolverine change.
- No OpenAPI change.
- No `name_normalized` column — these names are case-sensitive by design and the
  handler and index agree (`spec.md` §1.3).
- No fab dimension for `OverlayDesigner`.
- No frontend change.
- **No ADR** (`plan.md` §1). The follow-up ADR recording the double-enforcement
  posture is a separate issue for a human to open.

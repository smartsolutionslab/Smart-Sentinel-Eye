# Plan 086 — A name collision is refused by the database

**Spec:** `spec.md` (issue #2115)
**Status:** Phase 2 (Plan) — awaiting gate review
**Engineer:** backend
**Change class:** see §7 — split per part, and the slice as a whole is
**behaviour-changing (red)**

---

## 1. ADR position

**No new ADR is required. This implements a decision already recorded; it does
not make one.** The reasoning, since the brief asked for it explicitly:

The decision at stake is *"a uniqueness rule is enforced twice — an application
check for a usable answer, and a unique index for the guarantee."* That is not a
proposal; it is the product's stated posture, written down in the XML doc on
`src/ServiceDefaults/Persistence/UniqueConstraintExceptionHandler.cs:16-22` and
instantiated by twelve `.IsUnique()` indexes across **nine** bounded contexts
(re-measured 2026-09-07; the "six" this line carried was wrong, and so was the
reading of it — see spec §1). The handler exists *specifically* to give the
loser of an index race a `409` instead of a `500`. Layouts and overlays own four
of those twelve, all of them structural and on the revisions table; what neither
has is a unique index on the **operator-chosen name**, which is the row the
handler was built to serve and which makes that doc comment's word "every" false
— a defect in the record, not an open architectural question.

The comment at `LayoutConfiguration.cs:80-82` does not say otherwise. Read
closely, it is a **deferral with a stated blocker** — *"promoting it to the
database is a behaviour change on data that may already violate it — a separate
decision from fab-scoping"* — not a recorded refusal. The blocker was existing
data. That has now been measured: zero duplicates, on both tables, under both
the total and the non-archived predicate (spec §6). The stated reason to defer
no longer holds.

ADR-0113 fixes the concurrency posture (two-layer optimistic; no
retry-on-conflict). A unique index that refuses a duplicate outright is that
posture applied at the storage layer, not an amendment to it — nothing about
expected-version checking, `If-Match`, or the EF token changes.

The one thing worth naming honestly is that this needs **more than an index**:
the chain-archival marker (§3) is a change to two aggregate roots. That is a
modelling decision *inside* two bounded contexts. It crosses no boundary, alters
no `Shared.Contracts` message, changes no HTTP contract, and introduces no new
pattern — a partial unique index over a same-row state column is the
Camera/Rule/Variable shape verbatim. ADRs in this repo record cross-cutting and
locked choices; a per-context modelling detail is what `plan.md` and its gate
are for. **This is the load-bearing choice in the plan and should be reviewed as
such at the phase-2 gate.**

Follow-up, not a blocker: the double-enforcement posture deserves an ADR of its
own, because it currently lives only in a doc comment. That is a separate issue
and this lane may not write it (ADR-0144).

---

## 2. Bounded contexts and layers touched

Two contexts, disjoint file sets, **no cross-context references** (the boundary
rule is untouched — nothing here goes near `Shared.Contracts`).

| Context | Domain | Infrastructure | Api | Application |
|---|---|---|---|---|
| `OverlayDesigner` | `Overlay` aggregate: archival marker | `OverlayConfiguration`, one migration, `OverlayRepository` | — | — |
| `LayoutComposition` | `Layout` aggregate: archival marker | `LayoutConfiguration`, one migration, `LayoutRepository` | — | — |

**`Api` and `Application` are deliberately empty.** The endpoints already declare
`409`; `UniqueConstraintExceptionHandler` is registered centrally in
`ServiceDefaults` and matches on SQLSTATE `23505` generically
(`UniqueConstraintExceptionHandler.cs:124-130`), **not** on index names — so the
two new indexes need no registration anywhere. This was verified, not assumed:
the issue asserted it and the assertion holds.

The two create handlers and the two branch handlers keep their existing
application-level checks unchanged. They are the common path and produce the
specific error code; the index is the backstop for the race (§1).

---

## 3. Entities, value objects, invariants

### 3.1 The chain-archival marker

Both aggregates gain one property:

- `Layout.ArchivedAt` — `ArchivedAt?` (the value object already exists at
  `src/LayoutComposition/Domain/Layout/ArchivedAt.cs`)
- `Overlay.ArchivedAt` — `ArchivedAt?`
  (`src/OverlayDesigner/Domain/Overlay/ArchivedAt.cs`)

A **nullable value-object reference**, not `Option<T>`: this is persisted state,
and EF maps a nullable VO reference while it does not map `Option<T>` (CLAUDE.md
house rule; ADR-0141's scope note). It is not a primitive, so
`PrimitiveBoundaryTests` is satisfied.

Chosen over a chain-level `State` string (spec §9 A2) because the value object
already exists in both contexts — no new domain vocabulary — and because
`WHERE archived_at IS NULL` is a cheaper and less ambiguous index predicate than
a string comparison. The Rule/Variable spelling remains a defensible alternative
if the gate prefers literal symmetry.

### 3.2 The invariant

> `ArchivedAt` is set **iff** every revision in the chain is `Archived`; its
> value is the instant the chain became fully archived.

It must be recomputed inside the aggregate, after the mutation, in every method
that can move a revision's state — `CreateDraft` (always unset), `BranchDraft`
(always clears — the chain has a Draft again), `Publish`, `Revert`,
`ArchiveRevision`. `EditDraft` cannot change state; recomputing there anyway is
harmless and keeps the rule "one private call at the end of every mutator", which
is the form least likely to be forgotten by the next change.

The aggregate already computes exactly this predicate for
`NewestWhenFullyArchivedOrNull` (`Layout.cs:227-228`), so the marker names
something the aggregate already knew and never wrote down.

**Drift is the risk this design carries.** It is answered by a domain test that
asserts the marker against `Revisions` after every mutator (spec §3, last
scenario), not by a comment.

### 3.3 Aggregate boundary and transaction

Unchanged. Revisions stay an owned collection inside the chain, so the marker and
the revision states it summarises are written in one `SaveChanges`. There is no
window in which the column and the child rows disagree, and no second aggregate
is involved.

### 3.4 Concurrency (ADR-0113)

Unchanged, and worth stating so the gate can check it: the marker is **not** a
concurrency token. `Version` remains the EF token; `If-Match` remains layer 1.
The unique index adds a third, different refusal — a name collision, answered
`409 RESOURCE_ALREADY_EXISTS` — which ADR-0119's vocabulary rule already
separates from a stale-version `409`
(`UniqueConstraintExceptionHandler.cs:44-55`).

---

## 4. Persistence

### 4.1 EF configuration

`OverlayConfiguration.cs` — replace the plain index at lines 68-69:

- map `overlay.ArchivedAt` to `archived_at`, nullable, with the same
  `HasConversion` shape the revision-level `ArchivedAt` already uses (lines
  145-148);
- `HasIndex(overlay => overlay.Name)` → `.HasDatabaseName("ux_overlays_name_active")`,
  `.IsUnique()`, `.HasFilter("archived_at IS NULL")`.

`LayoutConfiguration.cs` — replace the plain index at lines 83-84 identically,
keyed on `(Fab, Name)`, named `ux_layouts_fab_name_active`. **`ix_layouts_fab`
stays** — it backs the listing filter and is not a uniqueness index.

Both class-level doc comments must be rewritten. `LayoutConfiguration.cs:20-26`
currently says a SQL-side index is deferred and that "the application check is
authoritative for v1"; leaving that in place after this change reproduces
precisely the defect the repo has recorded three times — a comment that outlives
what it describes.

### 4.2 Migrations (ADR-0067)

**Two per DbContext, four in all — corrected during phase 4b; this section
predicted one per context.** They are applied by `MigrationRunner`; no AppHost or
Aspire resource change.

The split is forced by the red-first sequencing, not chosen for tidiness. The
property has to exist before the domain tests that name it can fail for a reason
other than `CS1061`, and an `ArchivedAt?` property that EF does not map does not
merely go unindexed — it breaks the model outright with *"The entity type
'ArchivedAt' requires a primary key"*. So the column is mapped and added in the
prelude commit, and the constraint follows in the commit that earns it:

- `*ChainArchivalMarker` — `AddColumn` only.
- `*NameUniquePerLiveChain` — backfill, pre-flight check, index swap.

Their `Up` steps, in order across the pair:

1. `AddColumn` `archived_at` (`timestamp with time zone`, nullable).
2. **Backfill** from the child table:
   `UPDATE overlays o SET archived_at = (SELECT max(r.archived_at) FROM overlay_revisions r WHERE r.overlay_id = o.overlay_id) WHERE NOT EXISTS (SELECT 1 FROM overlay_revisions r WHERE r.overlay_id = o.overlay_id AND r.state <> 'Archived');`
   — and the layouts equivalent. A chain with **no** revisions cannot exist
   (`CreateDraft` always makes one), so the `NOT EXISTS` form does not need an
   extra guard against the empty chain; state that in the migration comment
   rather than leaving the reader to work it out.
3. **Pre-flight duplicate check**, modelled directly on
   `20260823194632_CaseInsensitiveCameraNames.cs:38-61`: a `DO $$ … RAISE
   EXCEPTION … USING HINT … $$` that names the colliding `(fab, name)` /
   `(name)` groups among rows where `archived_at IS NULL`. It must run **after**
   the backfill, because the predicate depends on the new column.
   **Deliberately not auto-reconciled** — the only fixes available to a
   migration are renaming or archiving somebody's live wall, which is an
   operator's decision. The camera migration's comment says this well and the
   reasoning should be restated, not cross-referenced.
4. `DropIndex` the old plain index, then `CreateIndex` the new partial unique
   one.

`Down` reverses across the pair: drop the unique index, recreate the plain one,
drop the column. The backfill is not undone — its values stay correct while the
column exists, and the column goes with the migration that added it.

Both `*DbContextModelSnapshot.cs` files regenerate.

**Generated migration bodies are exempt from the `Ensure.That` rule** (ADR-0105);
the hand-written `Sql` blocks inside them are still ordinary code and should read
as such.

### 4.3 Repository

`GetByNameAsync` in both repositories currently derives active-ness with
`.Where(candidate => candidate.Revisions.Any(revision => revision.State != Archived))`,
which EF renders as an `EXISTS` over the child table. It should switch to
`.Where(candidate => candidate.ArchivedAt == null)`.

Two reasons, and the second is the important one: the query stops touching the
child table (the new index can serve it), and — decisively — **the handler's
check and the index then evaluate the same predicate over the same column**. If
they are allowed to differ, the failure mode is a create the handler admits and
the database refuses, surfacing as the generic code where the specific one was
expected. Making them identical is not an optimisation.

The stale comment at `LayoutRepository.cs:33-36` ("the DB index is permissive")
goes with the change. Lines 37-39 are the fab comment and stay — this section
and `tasks.md` T017 both said "33-39", which would have deleted a correct
comment along with the wrong one.

**Three copies of this predicate exist, not two.** The two in-memory fakes —
`tests/OverlayDesigner.Application.Tests/Fakes/InMemoryOverlayRepository.cs` and
`tests/LayoutComposition.Application.Tests/Fakes/InMemoryLayoutRepository.cs` —
each duplicate it, and switching only the real repositories would leave both
Application suites green against a rule production no longer applies. They are
scaffolding standing in for the repository, so updating them is not editing a
test's assertion; the handler tests above them are untouched and must stay
untouched. Added phase 4b: neither this section nor T008/T017 mentioned them.

---

## 5. Messaging

**None.** No domain event is added, no integration event changes, no
`Shared.Contracts` type is touched, and no Wolverine handler, queue or outbox
behaviour changes. The existing `LayoutRevisionArchivedDomainEvent` /
`OverlayRevisionArchivedDomainEvent` continue to be raised exactly where they are
today — the marker is state the same mutator sets, not a new announcement.

This is worth writing down because "chain became archived" reads like an event
worth publishing. Nothing consumes it, so publishing it would be speculative
generality (ADR-0036).

---

## 6. Boundary rules

- No cross-context project reference is added; `NetArchTest`'s `BoundaryTests`
  are unaffected.
- The `ArchivedAt` value objects are per-context types that already exist in
  each context's own `Domain/<Aggregate>/` folder (ADR-0092). Neither is
  promoted to `Shared.Kernel`, and they must not be — two contexts owning a
  same-named concept is the boundary working, not duplication to remove.
- Nothing enters `Shared.Contracts`.

---

## 7. Change classification (phase 4a colour)

Declared per part, as the brief requires, rather than collapsed into one label.

| Part | Class | Colour |
|---|---|---|
| The chain-archival marker on both aggregates | **behaviour-changing** — the aggregate acquires state it did not have and a new invariant | **red**: new domain tests for the marker, observed failing first |
| `GetByNameAsync` switching predicate | **behaviour-preserving** — same set of chains, different SQL | **characterisation**: the existing create/branch/lifecycle suites captured green *before*, and passing **unmodified** after. An assertion that has to be edited is evidence the predicate moved — block, do not adjust |
| The partial unique indexes + migrations | **behaviour-changing** — a concurrent create that previously produced two rows now produces one row and a `409` | **red**: an integration test observed failing against the current schema. Predicted to be the concurrency one; it was the schema-level one — see below |

**The slice as a whole is behaviour-changing and its phase-4a colour is red.**
Ambiguity resolves to red (CLAUDE.md), and here there is no ambiguity: closing
the race is the point of the work.

This section predicted that the red test that mattered most would be the
concurrency one: two concurrent creates both returning `201` on the current
schema, because a test that only ever ran after the index exists proves the
index is present, not that it changed anything.

**That is not what happened, and it is recorded rather than quietly dropped
(observed phase 4b, written down phase 6).** Twelve unawaited writers were
dispatched twice on a clean box; `CreateOverlayDraftCommandHandler`'s and
`CreateLayoutDraftCommandHandler`'s own read-then-insert check won every time.
The concurrency test was **green before the change and green after**. It adds
**no** phase-4a evidence. It becomes an invariant once the index exists —
"exactly one 201" then holds however the race resolves — which is precisely
what `ServiceDefaults.UniquenessRaceIntegrationTests` has always been for
CameraCatalog, and it fails if the index regresses.

**The phase-4a evidence for the index is the schema-level test instead.**
`A_second_live_chain_with_the_same_name_is_refused_by_the_database` and its
layout twin insert a second live chain straight through a `DbContext`, so
nothing depends on scheduling: they failed deterministically on the current
schema and pass once the index exists. That is why they were written alongside
the concurrency test rather than instead of it — and it is why the requirement
above, "it **must** be observed failing", could not be met by the test the
requirement named. The obligation is discharged; the test that discharged it is
not the one predicted.

---

## 8. Sequencing and parallelism (ADR-0109)

There is **no foundational task**: nothing in `Shared.Kernel`,
`Shared.Contracts`, `AppHost`, or the Aspire resource graph changes, so nothing
blocks a fan-out.

The two contexts own **entirely disjoint files** and are genuinely parallel. The
only ordering worth keeping is that **US1 (overlays) lands the pattern first** —
it is the simpler shape, and reviewing it once is cheaper than reviewing the
same design twice in one PR. If both are given out at once, the layouts
engineer should read the overlays diff rather than re-derive the approach.

Within each context the order is fixed by dependency: marker → configuration →
migration → repository → integration test.

---

## 9. Verification (phase 5)

The end-to-end procedure is spec §5. Two things must appear in the verification
note and neither is satisfied by a green test run:

- the `pg_indexes.indexdef` text for both new indexes, read out of a live
  database after `MigrationRunner` has run — proving the migration produced a
  **partial unique** index and not merely that EF's model says so;
- the observed `201` / `409` split from two genuinely concurrent requests, with
  the post-condition row count.

A guard that reads the EF configuration would prove the design was written down,
not that the database holds it.

**Latency: N/A** (spec §8). No leg of constitution §IV is on this path, so no
figure is cited and none is discharged.

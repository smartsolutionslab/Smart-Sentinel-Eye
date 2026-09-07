# Spec 086 — A name collision is refused by the database

**Issue:** #2115
**Branch:** `fix/2115-a-name-collision-is-refused-by-the-database`
**Status:** Phase 1 (Specify) — awaiting gate review
**ADRs:** ADR-0113 (two-layer optimistic concurrency), ADR-0067 (MigrationRunner),
ADR-0121 (branch from a fully-archived chain), ADR-0139 + constitution §Testing
(red-first for new behaviour), ADR-0109 (parallel decomposition), ADR-0142/0143
(idempotency and retry safety)
**Related specs:** 003 (LayoutComposition), 004 (OverlayDesigner), 037
(branch-from-archived), 075 (endpoint 409 classification — where this was found)

---

## 1. Problem

`ix_layouts_fab_name` and `ix_overlays_name` look like uniqueness constraints
and are not. Both are plain btrees — in the EF configuration and in the shipped
schema, verified against the persistent dev database (§6).

So `POST /layouts` and `POST /overlays` guard their name rule in application
code only: the handler reads through `GetByNameAsync`, finds nothing, inserts.
Two concurrent creates of the same name both read nothing and both insert. Two
same-named rows result, and neither caller is told.

`UniqueConstraintExceptionHandler` already turns SQLSTATE 23505 into
`409 RESOURCE_ALREADY_EXISTS`, and its own doc comment asserts the product-wide
posture: *"Every uniqueness rule in this product is enforced twice: an
application-level check that produces an answer an operator can act on, and a
unique index that guarantees the invariant."* Twelve `.IsUnique()` indexes across
**nine** contexts instantiate that posture.

**LayoutComposition and OverlayDesigner are not absent from that twelve — they
own four of it**, and the first draft of this section said otherwise. Each has
`ux_*_revisions_number` and `ux_*_revisions_one_published`, both on the
revisions table. What neither has is a unique index on the **operator-chosen
name**: their only name index is a plain btree. So the narrower true claim, and
the one this spec rests on, is that these two contexts enforce their
*structural* rules twice and their *name* rule once — which is what makes the
word "every" in that comment false today.

Re-measured 2026-09-07, and the command is written down because the earlier
figure was not:

```sh
grep -rn "\.IsUnique()" src --include=*.cs | grep -v /Migrations/ | grep -v /obj/
```

### 1.1 Why this is not "just add `unique: true`"

**This is the load-bearing finding, and it contradicts the issue's cost
estimate.**

The rule the handlers enforce is not "name is unique". It is FR-006 in both
specs 003 and 004:

> Layout names MUST be unique across the set of **non-Archived chains**. (Two
> archived chains can share a name; an archived chain doesn't block a new chain
> reusing its name.)

A chain is archived when **every one of its revisions** is `Archived`
(`Layout.NewestWhenFullyArchivedOrNull`, and the
`.Any(revision => revision.State != Archived)` predicate in both repositories).
That predicate lives on the child table — `layout_revisions` /
`overlay_revisions` — while `Name` lives on the parent.

Postgres cannot express that as a partial index predicate. An index predicate
must be immutable and may not contain a subquery; a function that reads another
table is not immutable, and marking one so corrupts the index. There is no
generated-column route either — a generated column may not reference another
table. (The deferral comment at `LayoutConfiguration.cs:23-25` proposes "a
function-backed partial index"; that specific remedy is not available.)

The precedent indexes do not have this problem because their state column sits
on the same row as the name: `cameras.status <> 'Decommissioned'`,
`rules.state <> 'Archived'`, `system_variables.state <> 'Archived'`. Layout and
Overlay are the only revision-chain aggregates in the product, which is exactly
why they are the two left out.

**Therefore the database can only enforce FR-006 once the chain's own archival
state is materialised onto the parent row.** That is a domain change plus a
schema change plus a backfill, in two contexts — not a one-line migration.

### 1.2 Two options, and why one is rejected

| Option | Cost | Verdict |
|---|---|---|
| **A. Total unique index** on `(fab, name)` / `(name)` | one migration per context | **Rejected.** It deletes FR-006's reuse clause: a name would never be released by archival. The repo's stated posture is the opposite — `RuleConfiguration` and `VariableConfiguration` each carry a comment saying the partial filter "is preserved deliberately … must not quietly take that away". It would also break spec 037 / ADR-0121: a fully-archived chain resurrected by `POST /{id}/draft` would be refused by the index even when its name is genuinely free. |
| **B. Chain-archival marker on the parent row + partial unique index** | domain + config + migration + backfill, ×2 contexts | **Chosen.** Reduces both aggregates to the shape the other six contexts already have, and preserves FR-006 exactly. |

### 1.3 Deliberately *not* in scope

- **Case-insensitivity.** `LayoutName` and `OverlayName` are plain
  `StringValueObject`s: trimmed, ordinal equality, no `NormalizedValue`. The
  handler compares raw trimmed text and a btree unique index on the raw column
  compares the same thing — **they agree**, so no `name_normalized` column is
  needed here. Making these names case-insensitive (as `CameraName` became in
  #1434) is a separate, user-visible behaviour change and a separate issue.
- **A fab scope for overlays.** `OverlayDesigner` has **zero** occurrences of
  `Fab` in its entire source tree. Overlays are deliberately global; fab
  visibility is derived through the layouts that carry them (spec 017's
  overlay-frame scoping). `(name)` alone is the correct scope, and adding a fab
  dimension to OverlayDesigner is not this spec's business.
- **Any change to the two endpoints' request or response shapes.** See §4.

---

## 2. User stories

### US1 (P1) — Two concurrent overlay creates cannot both succeed

**As** an operator console (or two operators at once),
**when** two `POST /overlays` requests carrying the same name are in flight
simultaneously,
**then** exactly one is created and the other is refused with `409`.

Overlays first: no fab dimension, the smaller aggregate, and the one whose
missing `.IsUnique()` has no rationale recorded at the configuration. It
establishes the chain-marker pattern that US2 copies.

### US2 (P2) — Two concurrent layout creates in one fab cannot both succeed

**As** an operator console,
**when** two `POST /layouts` requests carrying the same name **in the same fab**
are in flight simultaneously,
**then** exactly one is created and the other is refused with `409`; and the same
name in a *different* fab is still accepted.

### US3 (P2, ships inside US1 and US2) — An archived name is still reusable

**As** an operator,
**when** a chain has been fully archived,
**then** its name is free for a new chain, and the archived chain can still be
resurrected by branching a draft **unless** another chain has taken its name in
the meantime — behaviour unchanged from today, now also guaranteed by the
database rather than only by the handler.

---

## 3. Acceptance scenarios (Gherkin)

### US1 — Overlays

```gherkin
Scenario: Concurrent creates with the same overlay name
  Given no overlay is named "Line 4 Banner"
  When two POST /overlays requests naming "Line 4 Banner" are issued concurrently
  Then exactly one responds 201 Created
  And the other responds 409
  And exactly one row exists in overlays with name = 'Line 4 Banner'

Scenario: The happy path is unchanged
  Given no overlay is named "Line 4 Banner"
  When a single POST /overlays names "Line 4 Banner"
  Then it responds 201 Created
  And the overlay's archived marker is unset

Scenario: The sequential collision still answers specifically
  Given an overlay named "Line 4 Banner" with a non-Archived revision
  When POST /overlays names "Line 4 Banner"
  Then it responds 409 with code OVERLAY_NAME_TAKEN
  # unchanged: the application check still fires first on the common path

Scenario: An archived chain releases its name
  Given an overlay named "Line 4 Banner" whose every revision is Archived
  When POST /overlays names "Line 4 Banner"
  Then it responds 201 Created
  And two rows exist in overlays with name = 'Line 4 Banner'
  And exactly one of them has an unset archived marker

Scenario: Resurrecting a chain whose name was taken is refused
  Given an overlay A named "Line 4 Banner" whose every revision is Archived
  And an overlay B named "Line 4 Banner" with a Draft revision
  When POST /overlays/A/draft is issued
  Then it responds 409 with code OVERLAY_NAME_TAKEN
  # unchanged behaviour (spec 037 FR-009); now also refused by the index

Scenario: A bad request is unaffected
  When POST /overlays is issued with an empty name
  Then it responds 400
  And no row is written

Scenario: Auth is unaffected
  When POST /overlays is issued without the scope the endpoint declares
  Then it responds 403
  And no row is written
```

### US2 — Layouts (fab-scoped)

```gherkin
Scenario: Concurrent creates with the same layout name in one fab
  Given no layout in fab "FAB-A" is named "North Wall"
  When two POST /layouts requests naming "North Wall" in fab "FAB-A" are issued concurrently
  Then exactly one responds 201 Created
  And the other responds 409
  And exactly one row exists in layouts with fab = 'FAB-A' and name = 'North Wall'

Scenario: The same name in a second fab is still accepted
  Given a layout named "North Wall" in fab "FAB-A"
  When POST /layouts names "North Wall" in fab "FAB-B"
  Then it responds 201 Created

Scenario: An archived layout chain releases its name within its fab
  Given a layout in fab "FAB-A" named "North Wall" whose every revision is Archived
  When POST /layouts names "North Wall" in fab "FAB-A"
  Then it responds 201 Created

Scenario: The cross-fab tile refusal still precedes the name check
  When POST /layouts names a camera outside the requested fab
  Then it responds with the cross-fab refusal, not a name refusal
  # the ordering in CreateLayoutDraftCommandHandler is unchanged
```

### The marker itself (domain, both contexts)

```gherkin
Scenario: A new chain is not archived
  When a draft chain is created
  Then its archived marker is unset

Scenario: Archiving the last live revision archives the chain
  Given a chain whose only revision is a Draft
  When that revision is archived
  Then the chain's archived marker is set

Scenario: Publishing does not archive the chain
  Given a chain with a Published revision and a Draft
  When the Draft is published, archiving the prior Published
  Then the chain's archived marker stays unset

Scenario: Branching a fully-archived chain un-archives it
  Given a chain whose every revision is Archived
  When a Draft is branched from it
  Then the chain's archived marker is unset

Scenario: The marker always agrees with the revisions
  For every mutation of revision state
  The marker is set exactly when every revision is Archived
```

---

## 4. Contract impact

**The HTTP status does not change.** Both endpoints already declare `409`,
earned today by the handler's own refusal and by the idempotency key (ADR-0142).

**The error *code* seen by the loser of a race is new**, and this spec states it
rather than implying nothing changes: the application check answers
`LAYOUT_NAME_TAKEN` / `OVERLAY_NAME_TAKEN`; the index race answers the generic
`RESOURCE_ALREADY_EXISTS`. A client switching on the code will see a value it
has not previously seen from these two endpoints. This is the same split every
other context already exposes, and `UniqueConstraintExceptionHandler`'s doc
explains why the shared layer stays generic. **No OpenAPI change** — the status
is unchanged and problem-details bodies are not enumerated per code.

**Four endpoints change their error surface, not two.** The paragraph above was
written about the two creates and missed the two branch endpoints until phase 6.
`BranchDraft` is the only mutator that **clears** the marker, so branching a
fully-archived chain updates its row *into* the partial index — and
`BranchDraftRevisionCommandHandler` runs its own read-then-write name check on
exactly that path, because an archived chain's name goes free while it is
stranded and may have been taken (FR-009). The same race therefore exists at
`POST /overlays/{id}/draft` and `POST /layouts/{id}/draft`. A caller who loses it
now gets `409 RESOURCE_ALREADY_EXISTS` where it previously got `201` — having
silently branched a chain onto a name another chain already holds. Still no
OpenAPI change: both mappings already declare `409`.

**A retried request is unaffected.** `POST` is not retried by default
(ADR-0143), and an `Idempotency-Key` replay returns the original answer
(ADR-0142). The race this closes is two *distinct* callers, not a retry.

---

## 5. Independent end-to-end test procedure

Run against the Aspire fixture (ADR-0103); no Testcontainers.

1. Boot the stack. `MigrationRunner` applies all four new migrations. Confirm in
   the migration log that each ran and that neither pre-flight check raised the
   duplicate exception.
2. `psql` the two databases and assert the index definitions:
   - `overlay-designer-db`: `ux_overlays_name_active` exists, its `indexdef`
     contains `UNIQUE` and the partial predicate; `ix_overlays_name` is gone.
   - `layout-composition-db`: `ux_layouts_fab_name_active` exists, ditto;
     `ix_layouts_fab_name` is gone.
3. Fire two `POST /overlays` with an identical name from two tasks started
   together. Assert one `201`, one `409`; then
   `SELECT count(*) FROM overlays WHERE name = …` returns 1.
4. Repeat for `POST /layouts` within one fab; then repeat across two fabs and
   assert both succeed.
5. Archive every revision of one chain, create a new chain with the same name,
   assert `201`, then assert the row count is 2 and exactly one row has an unset
   marker.
6. Attempt `POST /{id}/draft` on the archived chain; assert `409`.

---

## 6. Measured starting state — no duplicates exist

Run 2026-09-07 against the persistent run-mode volume
`smartsentineleye.apphost-0d1b3f6bcf-postgres-data`, copied to a throwaway
volume and booted on `timescale/timescaledb:2.27.1-pg17`. The original volume
was not written to.

```sql
-- layout-composition-db
SELECT count(*) AS layouts FROM layouts;                       --> 3
SELECT fab, name, count(*) FROM layouts
  GROUP BY fab, name HAVING count(*) > 1;                      --> 0 rows
SELECT l.fab, l.name, count(*) FROM layouts l
  WHERE EXISTS (SELECT 1 FROM layout_revisions r
                WHERE r.layout_id = l.layout_id AND r.state <> 'Archived')
  GROUP BY l.fab, l.name HAVING count(*) > 1;                  --> 0 rows

-- overlay-designer-db
SELECT count(*) AS overlays FROM overlays;                     --> 12
SELECT name, count(*) FROM overlays
  GROUP BY name HAVING count(*) > 1;                           --> 0 rows
SELECT o.name, count(*) FROM overlays o
  WHERE EXISTS (SELECT 1 FROM overlay_revisions r
                WHERE r.overlay_id = o.overlay_id AND r.state <> 'Archived')
  GROUP BY o.name HAVING count(*) > 1;                         --> 0 rows
```

A case-insensitive grouping (`upper(name)`, and `upper(fab), upper(name)`) also
returned 0 rows in both databases. The live `pg_indexes` output confirms both
indexes are non-unique btrees today.

**This measures one dev database and proves nothing about any other
environment.** The migration therefore carries the same pre-flight duplicate
check that `20260823194632_CaseInsensitiveCameraNames` carries: refuse with the
colliding names listed, and do **not** auto-reconcile.

---

## 7. Locked tech choices

Postgres partial unique index; EF Core `HasIndex(...).IsUnique().HasFilter(...)`;
migrations applied by `MigrationRunner` (ADR-0067) — **four** migrations, two per
DbContext (this line said two, one per context; `plan.md` §4.2 records why the
column has to land before the constraint); xUnit + Shouldly + hand-written fakes (ADR-0052/0054); integration via
`AspireFixture` (ADR-0103); nullable value-object reference for the persisted
absence (constitution §II and the CLAUDE.md house rule that persisted absences
use nullable value-object references, not `Option<T>`).

---

## 8. Latency budget

**N/A.** Both endpoints are management-plane creates. Nothing on the
`event arrival → overlay rendered` path is touched: no leg of constitution §IV
is affected, and no leg's figure is claimed, discharged, or re-measured by this
work.

---

## 9. Assumptions and open questions

- **A1 (assumption, marked):** the chain-archival marker is maintained by the
  aggregate, not by a database trigger. The repo has no trigger precedent and
  the constitution puts invariants in the domain. The drift risk is real and is
  answered by the last scenario in §3 — a domain test asserting the marker
  against the revisions after every mutation.
- **A2 (assumption, marked):** the marker is spelled as a nullable `ArchivedAt`
  reference rather than a chain-level `State` string. `ArchivedAt` already exists
  as a value object in both contexts, so this adds no new domain vocabulary;
  `state <> 'Archived'` would mirror Rule/Variable more literally. Either
  satisfies this spec — `plan.md` picks one, and the phase-2 gate is the place to
  overrule it.
- **Q1:** should the double-enforcement posture — currently stated only in an XML
  doc comment on `UniqueConstraintExceptionHandler`, and instantiated twelve
  times — be recorded as an ADR? Recommended as a **follow-up issue**, not a
  blocker for this work. This lane may not write ADRs (ADR-0144).

---

## 10. Out of scope

Case-insensitive layout/overlay names; a fab dimension for OverlayDesigner;
retro-fitting the generic-versus-specific 409 code split; any change to the two
endpoints' request or response shapes; any frontend change.

---

## 11. Delivery notes (recorded phase 6)

Facts about the commit series rather than about the feature. They are here
because they outlive a PR body.

**Two commits are red on their own, and that is the sequencing working.** The
red-first order (`tasks.md`, the prelude above the task tables) puts the marker's
domain tests in their own commit ahead of the mutator change that satisfies them,
so `test(overlay): the chain marker does not follow its revisions` and
`test(layout): the chain marker does not follow its revisions` each land on
`develop` with four failing tests in that context's domain suite, made green by
the `feat` commit immediately after. ADR-0087 requires every commit to **build**,
and both do; it does not require every commit to be green, and red-first could
not be honoured if it did. Named by subject rather than by SHA because
rebase-merge renames them. Anyone bisecting a domain-suite failure into either
commit has found the sequencing, not a regression.

**Why the rebases replay cleanly — for a different reason than the one first
given.** The premise recorded during phase 4b was that the newer `develop`
"touches nothing under `src/LayoutComposition` or `src/OverlayDesigner`". That is
**false**: `git diff 921dde95 4a514e81` shows
`src/LayoutComposition/Api/LayoutEndpoints.cs +7` and
`src/OverlayDesigner/Api/OverlayEndpoints.cs +31`, from spec 085. The conclusion
survives on the reason that actually holds — both are purely additive
`.ProducesProblem(StatusCodes.Status403Forbidden)` calls plus comments, nothing
removed or renamed, and **no commit on this branch touches either `*.Api`
project**. The branch's files are Domain, Infrastructure, the two
Application-test fakes, `ServiceDefaults`, the two new integration test files and
these three artifacts; verified with `git log --name-only`, not inferred from
directory names.

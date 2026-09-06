# Tasks 083 — A decommissioned camera still names its fab

**Spec:** `spec.md` · **Plan:** `plan.md`
**Issue:** #2076 (delivering)
**Engineer:** **backend** — one file in `StreamDistribution/Infrastructure`, one integration
test against the Aspire fixture. No frontend, no infra, no AppHost, no Aspire resource.
**Phase 4a colour:** **red** — behaviour-changing. §Phase 4a below says what the red asserts.

## Phase 4a declaration — what an honest red looks like here

The failure mode this task must not fall into is named in the issue and it is the whole
risk of a one-argument fix: **a test that asserts the request URL contains
`includeRetired=true` proves a string, not a behaviour.** It would pass against a
CameraCatalog that filtered the row out anyway, against a handler that ignored the
parameter, and against a lookup that built the URL correctly and then dropped the row.

**The red is a decommissioned camera's stream acquiring its fab, driven end to end.**

| | |
|---|---|
| **Where** | `tests/Integration.Tests/StreamDistribution/StreamFabAttributionIntegrationTests.cs` — the file that already holds `RealLookup()`, a real `client_credentials` token for `stream-distribution-attribution`, and the real `GET /cameras` |
| **Arrange** | provision a camera in munich → its stream appears with `fab = "munich"`; retire the camera (`POST /cameras/{camera}/retire`) → `Decommissioned`; **then** blank the stream's fab in SQL |
| **Act** | `RealLookup().FabsByCameraAsync(...)`, then one `AttributePassAsync` |
| **Assert** | the map contains that camera identifier → `"munich"`; the pass attributes 1; re-read from the database, the stream's fab is `"munich"` |
| **Red before the fix** | the map has no key for the camera. The map assertion fails first, and the attribution assertion would fail too. Both are about resolution, neither mentions a URL. |
| **Not asserted anywhere** | the request string |

**Order matters in the arrange.** Retire first, blank second: `CameraRetiredV1` retires the
stream, `Stream` carries an EF concurrency token, and blanking before the retirement lands
races the handler. `AttributePassAsync` already retries `DbUpdateConcurrencyException`
because `StreamHealthWatcher` writes to these same rows every two seconds — reuse it, do
not write a second loop.

**Capture the verbatim failure.** It goes in the PR body (ADR-0139); it is the only form of
the evidence a later reader can check.

## User story US1 — a stream whose camera was retired acquires its fab

| ID | P | Story | Task | Files | Depends on |
|---|---|---|---|---|---|
| T001 | | US1 | Write `A_stream_whose_camera_was_decommissioned_reacquires_its_fab` per §Phase 4a. Run against **unmodified** `src/`. **Capture the verbatim failure** — it is the PR's evidence, and a test first run after the fix proves only that a file was edited. | `tests/Integration.Tests/StreamDistribution/StreamFabAttributionIntegrationTests.cs` | — |
| T002 | | US1 | FR-001: add `&includeRetired=true` to the request in `ReadPageAsync`. **One argument.** Comment says *why* (spec 028 hid retired rows after ADR-0116 established this call; a stream's fab is its camera's whether or not the hardware is still on the wall) — not what the URL now says. | `src/StreamDistribution/Infrastructure/Attribution/CameraCatalogFabLookup.cs` | T001 |
| T003 | | US1 | FR-003 + SC-003: run the file's **four** existing tests **unmodified** and confirm green — `A_stream_with_no_fab_is_returned_to_nobody`, `The_same_stream_is_visible_while_it_still_has_its_fab`, `Blanked_streams_reacquire_the_fab_of_their_own_camera`, `A_stream_whose_camera_the_catalogue_does_not_know_stays_unattributed`. The third is the two-fab test and the load-bearing one — the only one that rules out a "fix" filling every stream with a single fab. The fourth is FR-010's test; it must still pass, because FR-010 is unchanged. **An assertion that has to be edited is evidence the behaviour moved — block, do not adjust.** | `tests/Integration.Tests/StreamDistribution/StreamFabAttributionIntegrationTests.cs` (read only) | T002 |
| T004 | | US1 | Update `CameraCatalogFabLookup`'s class doc and `StreamFabAttributionOptions.PageSize`'s comment: the read now spans retired rows too, so "one or two requests" is approximate and the population grows over a deployment's life. Comment only, no behaviour. | `src/StreamDistribution/Infrastructure/Attribution/CameraCatalogFabLookup.cs`, `…/StreamFabAttributionOptions.cs` | T002 |
| T005 | | — | Full `-c Release` build + test. `BoundaryTests`, `HandlerDeconstructionTests` and `PrimitiveBoundaryTests` unaffected. Format and analyzers clean. | — | T002–T004 |

## Parallelism (ADR-0109)

**There is none worth marking, and that is the honest answer for a one-file fix.** No task
carries `[P]`:

- T001 blocks T002 — not for a file conflict but because its red against unmodified source
  *is* the evidence.
- T002, T003 and T004 all touch or read the same two files in the same context.
- **No foundational task.** Nothing in `Shared.Kernel`, `Shared.Contracts`, `AppHost` or
  Aspire resources changes, so there is nothing for the orchestrator to fan out behind.

## Out of scope — named so they are excluded rather than forgotten

| Thing | Why not here | Where it goes |
|---|---|---|
| Paging tie-break: `(Name, Fab)` is not unique once retired rows are included, so a page boundary inside a tie can skip a row | Different context (CameraCatalog), different defect, and a shared read path the console uses. Does not arise below 200 catalogue rows; measured today at 19. | **File a new issue** (spec §Risk) |
| Per-stream identification of unresolved streams | The count is already logged, the pass retries every restart, and #1467 removes the state entirely | plan §VII |
| `StreamHealthChangedDomainEventHandler`'s `domainEvent.Fab?.Value` | Not a defect. Same null seen from the other end; this spec removes its cause | spec §The audit row |
| `streams.fab SET NOT NULL` | #1467, still `agent:blocked` | plan §VIII |
| Correcting #1467's third precondition | A sentence in another issue, and this pass does not touch the board | plan §VIII |

## Phase 5 (verify)

The §Independent end-to-end test procedure in `spec.md`. Requires the Aspire stack —
**check `C:` headroom first (≥ 8 GB, abort below 5 GB), prefer E2E mode, and stop the
AppHost process directly afterwards.** Latency: **N/A**, startup only, off every leg of
constitution §IV (spec §Latency-budget impact).

The step that carries the verification note is step 7: the same `GET /streams/{camera}`
returning 404 before the pass and 200 after it, on a stream whose camera is
`Decommissioned`.

## Phase 7

One PR closing **#2076** with a closing keyword, `--base develop`. The body carries T001's
verbatim red, states that no FR was amended and why (spec §The FR-010 claim), and records
the two follow-ups: the paging tie-break issue and the #1467 comment. Per memory, a mention
rarely auto-closes — check the issue's state after the merge.

## Board

The feature issue is **#2076**, labelled `bug` + `agent:ready`. Its Project #13 membership
was **not checked** — this pass does not touch the board, and reading it costs against
Projects v2's separate rate limit. Confirm it before phase 3's gate is called met.

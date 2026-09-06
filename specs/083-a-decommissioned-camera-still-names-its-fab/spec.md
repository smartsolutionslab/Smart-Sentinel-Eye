# Spec 083 — A decommissioned camera still names its fab

**Issue:** #2076 · **Branch:** `fix/2076-a-decommissioned-camera-still-names-its-fab`
**ADRs:** ADR-0116 (the cross-fab attribution lookup — this sits inside its Decision item 4),
ADR-0036 (smallest change), ADR-0037 (phased workflow), ADR-0139 (new behaviour starts red),
ADR-0109 (parallel markers), ADR-0113 (no retry-on-conflict — unchanged here)
**Related, not folded in:** #1467 (blocked by this, and carries the misquote — §The FR-010 claim),
#2068/#2071 → spec 082 (the other end of the same null — §The audit row)
**New ADR needed:** **no.** See §Why this is not an ADR.

## Problem

`src/StreamDistribution/Infrastructure/Attribution/CameraCatalogFabLookup.cs:44-45`
requests `/cameras?offset={offset}&limit={pageSize}` and never passes
`includeRetired=true`. `ListCamerasQueryHandler:55-57` guards on
`if (!includeRetired)` and filters
`camera.Status != CameraStatus.Decommissioned` unless asked otherwise
(spec 028 FR-007).

So `StreamFabAttributionService` cannot see a decommissioned camera. A stream
with a null fab whose camera was later decommissioned is attributed on no pass,
ever — not because the camera is gone, but because a default on a listing hid it.
FR-009 then makes that stream visible to nobody, permanently, and correctly.

Spec 028 added the filter *after* ADR-0116 established the lookup. Neither change
was wrong on its own.

## The FR-010 claim — verified, and it does not hold

#2076 says spec 016's FR-010 "describes a case that does not exist", quoting it as
reserving a permanent null for a stream *"whose camera genuinely no longer exists
in CameraCatalog"*. **That phrase is not in FR-010, and not in spec 016 at all.**

| Source | Actual text |
|---|---|
| `specs/016-stream-fab-scoping/spec.md:140` (FR-010) | "A stream whose camera **cannot be resolved** MUST remain unattributed, and that MUST be recorded rather than treated as success." |
| `docs/adr/0116-….md`, Decision item 4 | "A stream whose camera **the catalogue does not return** keeps its null fab and is counted as unresolved (FR-010)." |
| **#1467, third precondition bullet** | "Any stream whose camera **genuinely no longer exists in CameraCatalog** … those stay null forever by design (FR-010)." |

The gloss is **#1467's**, and #2076 inherited it as if it were the requirement.
`grep -rn "genuinely" --include=*.md .` returns hits across the repo and **none**
in `specs/016-stream-fab-scoping/`.

Both authoritative texts are condition-neutral. They describe the *lookup's* answer,
not a fact about the camera's existence — which is exactly right, because a lookup
that cannot resolve a camera has no way to tell "deleted" from "hidden by a query
default" from "CameraCatalog is down". FR-010 is met today and stays met after this
change; the fix narrows the population that trips it to what it was always meant to be.

**Consequence: this is one act, not two.** No functional requirement is amended, no
spec is corrected, nothing is a human's call, and no part of this is blocked. The
record does not contradict the code either before or after.

**#1467's bullet is wrong and should be corrected** — but it is a sentence in
someone else's open issue, not an artifact of this spec. Recorded here; not done here
(this pass does not touch the board).

### And the premise is corroborated empirically

There is no hard delete of a camera. Searched, several ways:

- `\.Remove(|RemoveRange|ExecuteDelete` across `src/` — 6 hits, none on cameras
  (in-memory caches and dictionaries in Automation, EventIngestion, StreamDistribution's
  health watcher, SystemVariables' reverse index).
- `MapDelete` across `src/` — 3 hits, all in EventIngestion/Identity, none on `/cameras`.
- `DELETE FROM` in `.cs`/`.sql` — 2 hits, both StreamDistribution/SystemVariables
  migrations, neither on cameras.
- `retention|purge|hard.?delete|cleanup` — no sweep over cameras.
- `ICameraRepository` exposes `GetByIdentifierAsync`, `GetWithinFabAsync`,
  `ExistsByNameAsync`, `Add`, `SaveAsync`. **No delete.**
- `CameraStatus` doc comment: "Terminal — nothing leaves Decommissioned."

And in the measured database (§Measurement): **0 of 19 streams reference a camera
absent from the catalogue.** Every stream's camera row is still there.

## Measurement — re-measured, not inherited

#2076's figures (15 of 27 cameras, 15 of 27 streams) are **not reproducible and are
dropped.** Re-measured 2026-09-06 against the run-mode volume
`smartsentineleye.apphost-0d1b3f6bcf-postgres-data` (last written 19 h earlier), read by
starting a throwaway `timescaledb:2.27.1-pg17` against the volume — no Aspire boot.

| Quantity | #2076 | Measured |
|---|---|---|
| Cameras total | 27 | **19** |
| Cameras `Decommissioned` | 15 | **7** (12 `Registered`) |
| Streams total | 27 | **19** |
| Streams `Retired` | 15 | **7** (12 `Healthy`) |
| Streams with `fab IS NULL` | — | **0** |
| Streams whose camera is `Decommissioned` | — | **7** |
| Streams whose camera row is absent from the catalogue | — | **0** |
| Catalogue rows sharing `(fab, name_normalized)` | — | **0** |

**Read the last four rows together.** 7 streams — 37% — are served by a camera the
lookup cannot see. All 7 already carry a fab, because they were provisioned after
spec 016 and `Stream.Provision` requires one. So the defect is **real in code and
latent in this data**: nothing here is currently broken, and the first pre-016 stream
in any long-lived deployment lands in that 37%. That is the honest shape of it, and it
is why the red test has to *manufacture* the null rather than find one.

## User stories

### US1 (P1) — a stream whose camera was retired acquires its fab

**As** the attribution pass, **I want** the camera catalogue to include
decommissioned cameras, **so that** a stream provisioned before spec 016 whose
camera has since been pulled off the wall is attributed to the fab it has always
belonged to, instead of being invisible forever.

This is the whole feature. There is no US2.

## Acceptance scenarios

**Happy path**

```gherkin
Given a camera registered in munich, and a stream serving it whose fab is null
  And that camera has been retired, so its status is Decommissioned
When the attribution pass reads the camera catalogue and runs
Then the lookup's map contains that camera identifier mapped to "munich"
  And the stream's fab is "munich"
  And the pass reports 1 attributed, 0 unresolved
```

**Non-regression — the live camera still works**

```gherkin
Given a camera registered in dresden that has not been retired
  And a stream serving it whose fab is null
When the attribution pass runs
Then the stream's fab is "dresden"
```

Both fabs appear across the two scenarios deliberately, for ADR-0116's reason: a pass
that filled everything with "munich" would satisfy a single-fab assertion.

**Conflict — a stream that already has a fab is not reassigned**

```gherkin
Given a stream whose fab is already "munich"
When the attribution pass runs and the map says "munich"
Then the stream is not in the pass's population at all
  And Stream.AttributeToFab is not called on it
```

`Stream.AttributeToFab` throws `InvalidOperationException` on a second call by design
(`Stream.cs:121`). The pass selects only `fab == null`, so this is structural, not a
new check.

**FR-010 still bites where it should**

```gherkin
Given a stream whose camera identifier is in no catalogue row
When the attribution pass runs
Then the stream's fab stays null
  And it is counted as unresolved rather than defaulted
```

**Bad request / auth**

```gherkin
Given the attribution service account, which holds sse.cameras.read and is a member
      of every fab group (ADR-0116)
When it requests GET /cameras?offset=0&limit=200&includeRetired=true
Then the response is 200
  And no additional scope is required
```

`includeRetired` is `[FromQuery] bool includeRetired = false` on the same `reads`
group under the same `sse.cameras.read` scope (`CameraEndpoints.cs:118, 379`). A
malformed value is model-binding's 400, unchanged.

## Independent end-to-end test procedure

Real Aspire stack. Nothing here is a mock.

1. Boot the stack. Check `C:` headroom ≥ 8 GB first.
2. As the multi-fab operator, `POST /cameras` in munich. Wait for the stream to be
   provisioned (`GET /streams/{camera}` → 200, `fab` = `"munich"`).
3. `POST /cameras/{camera}/retire`. Confirm `GET /cameras?limit=200` omits it and
   `GET /cameras?limit=200&includeRetired=true` returns it with
   `"status":"Decommissioned"`.
4. `UPDATE streams SET fab = NULL WHERE camera_id = …` — recreate the pre-016 state.
   (Retire **before** blanking: `CameraRetiredV1` writes to the stream row, and the
   aggregate carries an EF concurrency token.)
5. Confirm the stream is now visible to nobody: `GET /streams/{camera}` → 404 for both
   its own fab's operator and a multi-fab one (FR-009).
6. Run one attribution pass through the **real** `CameraCatalogFabLookup` with a real
   `client_credentials` token for `stream-distribution-attribution`.
7. **Before the fix:** the map has no key for that camera; the pass attributes 0; the
   stream stays null and stays a 404.
   **After the fix:** the map maps it to `"munich"`; the pass attributes 1; the stream's
   fab is `"munich"` and `GET /streams/{camera}` is 200 again.
8. Stop the AppHost **process directly**.

Step 7's two halves are the same commands run on either side of a one-argument change.
That is what makes this observable rather than asserted.

## Functional requirements

- **FR-001**: The catalogue read that feeds stream fab attribution MUST include
  decommissioned cameras.
- **FR-002**: A stream with no fab whose camera is decommissioned MUST acquire that
  camera's fab on the next attribution pass.
- **FR-003**: Nothing else about the pass changes — a camera the catalogue does not
  return still leaves its stream null and counted as unresolved (spec 016 FR-010,
  ADR-0116 item 4); an unreachable catalogue is still swallowed and retried at the
  next start (item 5); no stream is ever defaulted (FR-009).
- **FR-004**: The change MUST be proved by a test that observes a decommissioned
  camera's stream *acquiring its fab*, end to end through the real HTTP lookup —
  **not** by asserting the request URL contains a string.

## Success criteria

- **SC-001**: A stream whose camera is `Decommissioned` and whose fab is null ends
  one attribution pass carrying that camera's fab. Observed, not inferred.
- **SC-002**: The stream is a 404 to every operator before the pass and a 200 to its
  own fab's operator after it.
- **SC-003**: `StreamFabAttributionIntegrationTests`' **four** existing tests pass
  **unmodified** — `A_stream_with_no_fab_is_returned_to_nobody`,
  `The_same_stream_is_visible_while_it_still_has_its_fab`,
  `Blanked_streams_reacquire_the_fab_of_their_own_camera` and
  `A_stream_whose_camera_the_catalogue_does_not_know_stays_unattributed`. FR-009 and
  FR-010 are untouched by this change and must be seen to be. The two-fab test is the
  load-bearing one: it is the only one that rules out a "fix" that fills every stream
  with a single fab.

## Latency-budget impact

**N/A.** `StreamFabAttributionService` is an `IHostedService` that runs once at host
start, only while unattributed streams exist, and makes no HTTP call at all in the
steady state. It is on no leg of `event arrival → overlay rendered` (constitution §IV).
It does not touch `StreamHealthWatcher`, the SFU, or any render path.

The one cost is at startup: the catalogue read now returns retired rows too, so the
population grows monotonically over a deployment's life. The retired rows are the
smaller of the two factors, and saying so matters. This listing spans **every** fab the
service account holds, and the constitution's target is 250 concurrent cameras *per
fab* (§Scale) — so at `PageSize = 200` the pass is already several requests at target
scale with zero retired rows, and this change adds each fab's history on top of that.
It happens once, at a start where unattributed streams exist.
`ICameraFabLookup`'s "one or two requests" comment was already an under-count at target
scale and this change widens the gap; §Risk records the one way that matters.

## Risk — the fix's own second-order edge, and why it is not folded in

`ListCamerasQueryHandler.SortBy` orders by `(Name, Fab)` — a tie-break added
deliberately, its comment says, so that "a page boundary could not show one row twice
and the other never". **`(fab, name_normalized)` is unique only for live rows**: the
partial index is `WHERE status <> 'Decommissioned'`, which exists precisely so a name
can be reused. So with `includeRetired=true` the sort key is no longer unique, the
order within a tie is the database's choice, and a page boundary falling inside a tie
can skip a row.

A duplicate is harmless here — the lookup accumulates into a dictionary keyed by
camera identifier, so a repeat overwrites with the same fab. **A skip is not**: the
skipped camera's stream stays unattributed, which is this very defect returning
through the fix's own door.

Three things bound it, and none of them is "it cannot happen":

- It needs the catalogue to exceed one page (> 200 rows) **and** a tie to straddle a
  boundary. **Only the tie is genuinely contingent.** More than 200 rows is not a
  coincidence to wait for: it is the documented production target — 250 concurrent
  cameras *per fab*, constitution §Scale — reached by **one** fab, and this listing
  spans every fab the service account holds, so it arrives sooner still. Measured
  today: 19 rows, 0 ties, one request. It does not arise **yet**.
- It is not created by this change **for the endpoint** — any caller passing
  `includeRetired=true`, the management console included, could already meet it. It
  **is** created by this change **for this lookup**. Before this commit this lookup's
  page could not contain a tie at all: live rows are unique on `(fab,
  name_normalized)` by the partial index (`CameraConfiguration.cs:132-136`). The gate
  moved from *impossible for this caller* to *one name reuse away, past 200 rows*.
- The fix for it is a stable tie-break on `camera_id` in `SortBy`, in **CameraCatalog**,
  not in this lookup. Nothing StreamDistribution can do reaches it.

**Not folded in** (ADR-0036): a different context, a different defect, and a change to
a shared read path used by the UI. It is filed as **#2144**. Recorded here so that the
next person to page a catalogue over 200 rows meets it rather than discovers it.

**#2144 should be re-read in light of the two corrections above**, because both make it
closer than a first reading of this section suggested: the row count is a target the
product is designed to reach rather than an unlikely coincidence, and the tie became
reachable *in this lookup's own page* only with this change. It is still latent rather
than live — §Measurement earns that: 19 rows, one request, **0** sharing
`(fab, name_normalized)` — but the distance is one name reuse, not two coincidences.

## The audit row — #2068/#2071's guard, and why it does not cover this

`StreamHealthChangedDomainEventHandler` passes `domainEvent.Fab?.Value` into
`EventMetadata`'s fab position. Spec 082's guard scans for a **literal `null`**, and
this is a nullable propagation of a `FabIdentifier?` — so the guard does not see it,
correctly, and spec 082 listed #2076 as "Related, not folded in".

**That is the same defect seen from the other end, not a second one.** The audit row's
fab is null *because* `streams.fab` is null, and `streams.fab` is null because the
attribution pass could not resolve the camera. The publisher is not defective: it
faithfully forwards what the aggregate holds, and its comment already says "Null only
when the stream itself has no fab yet (spec 016)". There is nothing to fix at that
call site. This spec removes the cause of a whole class of those rows; #1467's
`NOT NULL` removes the possibility. **No change to that handler is in scope.**

## Why this is not an ADR

ADR-0116 Decision item 4 reads: *"A stream whose camera the catalogue does not return
keeps its null fab and is counted as unresolved (FR-010)."* This change makes the
catalogue return a camera it should always have returned. The item's rule is untouched,
item 5's fail-open is untouched, the service account's scope is unchanged
(`sse.cameras.read`), and its fab-group membership is unchanged. No decision moves.

**No security widening either.** The listing's retired-row exclusion is a default about
usefulness, not an authorization boundary — `GET /cameras/{camera}` already "returns
retired cameras too, with their status" under the same scope. This principal could
already read every retired camera, one at a time.

## Assumptions, stated

- **A decommissioned camera's fab is still that stream's fab.** The stream's history
  belongs to the plant the hardware stood in; `Stream` is retired but its row is kept
  because "retirement records that hardware *was* there" (`Stream.cs:132`). Attributing
  it records a fact; it resurrects nothing. The alternative — leaving it null — makes it
  invisible to the operators whose plant it documents, which is FR-009 misfiring rather
  than working.
- **Name reuse does not confuse the map.** The lookup keys on `cameraIdentifier`. A
  reused name is a *new* row with a new Guid v7, so a retired row and its live namesake
  are two distinct keys, never a collision.
- **The retired row always carries a fab.** `cameras.fab` is
  `character varying(32) NOT NULL`, verified against the live schema. There is no window
  where a decommissioned camera answers with a blank fab that the lookup would skip.

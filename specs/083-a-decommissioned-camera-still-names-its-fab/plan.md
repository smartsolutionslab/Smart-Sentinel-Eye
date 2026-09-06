# Plan 083 — A decommissioned camera still names its fab

**Spec:** `spec.md` · **Issue:** #2076
**ADRs:** ADR-0116, ADR-0036, ADR-0037, ADR-0139, ADR-0109, ADR-0113, ADR-0049, ADR-0143

## I. Bounded context and layers

One context, one layer, one file.

| Context | Layer | File | Change |
|---|---|---|---|
| StreamDistribution | Infrastructure | `Attribution/CameraCatalogFabLookup.cs` | one query-string argument on the request in `ReadPageAsync` |

**CameraCatalog is not touched.** It already supports the parameter, on the route the
lookup already calls, under the scope the lookup already holds. That is the whole reason
this is a one-argument change and not a contract change.

- **Domain** — untouched. `Stream.AttributeToFab` and `CameraStatus` are unchanged.
- **Application** — untouched. `StreamFabAttributionService.Attribute` is unchanged; it
  already does the right thing with whatever the map contains.
- **Api** — untouched.
- **Shared.Contracts / Shared.Kernel** — untouched. No integration event, no DTO, no
  value object. **There is no foundational task**, so nothing blocks anything else here.

## II. Entities, value objects, invariants

Nothing new. The invariants this change must not disturb, and where each is enforced:

| Invariant | Enforced at | Effect of this change |
|---|---|---|
| A stream's fab is its camera's and cannot be reassigned | `Stream.AttributeToFab` guards `Fab is not null` | none — the pass still selects only `fab == null` |
| Nothing is ever defaulted | `Attribute` skips a camera absent from the map | none — the map only gains keys, never a placeholder |
| An unattributed stream is visible to nobody | spec 016 FR-009, the listing's fab filter | none — this reduces how many rows are in that state |
| A camera's status is terminal | `CameraStatus` | read only |
| `cameras.fab` is `NOT NULL` | schema | relied on: a retired row still names a fab |

## III. Messaging

**None.** No domain event, no integration event, no Wolverine handler, no outbox row.
The attribution pass is a startup `IHostedService` reading over HTTP and writing through
EF. This is the one cross-context HTTP call StreamDistribution makes, and it is already a
recorded bounded exception to constitution §III (spec 016 `plan.md` §III, ADR-0116).

Its shape does not change: same route, same client, same delegating handler attaching the
bearer, same `client_credentials` token provider, same scope, same fab-group membership.
Only the query string differs.

## IV. Boundary rules

- **No new cross-context project reference.** `CameraCatalogFabLookup` speaks HTTP and
  parses `JsonElement`; it does not reference `CameraCatalog`. Unchanged.
- **NetArchTest `BoundaryTests` must stay green** without modification. If it goes red,
  something structural was done that this plan does not describe.
- `Shared.Contracts` is not involved — the catalogue listing is an HTTP DTO read
  loosely, deliberately, so that adding a field to `CameraSummaryDto` cannot break the
  attribution pass. That stays true.

## V. The change

In `ReadPageAsync`, the request becomes:

```
/cameras?offset={offset}&limit={pageSize}&includeRetired=true
```

with a comment saying **why**, not what — that spec 028 hid retired cameras from the
default listing after ADR-0116 established this call, and that a stream's fab is its
camera's whether or not the hardware is still on the wall. A comment restating the URL
would be a drive-by comment.

**Retry safety (ADR-0143):** `GetAsync` is a `GET`; the standard resilience handler
already retries it, and this stays true. No `RetryEveryMethod()`, no `Idempotency-Key` —
neither applies to a read.

**Cancellation (ADR-0049):** `cancellationToken` is already the last parameter and is
already threaded. Unchanged.

## VI. Alternatives considered

- **Per-camera `GET /cameras/{camera:guid}`.** This route now exists (`CameraEndpoints.cs:108`)
  and already returns retired cameras — #1435 has been closed since ADR-0116 was written.
  Rejected: it turns one-to-a-few requests into one per unattributed stream, and ADR-0116
  chose the whole-catalogue read for a stated reason (the stream's fab is precisely what is
  unknown, so the read cannot be narrowed). Changing that is an ADR-scale decision for a
  defect that needs a query parameter.
- **Filter in StreamDistribution instead.** There is nothing to filter — the rows never
  arrive.
- **Change CameraCatalog's default to include retired.** Rejected outright: it changes a
  read surface the management console depends on, to fix a caller that can simply ask.
- **Also fix the paging tie-break.** Rejected — see spec §Risk. Different context,
  different defect, separate issue.

## VII. Observability

**Nothing new, and that is a deliberate call — but the gap is real and named.**

`StreamFabAttributionService` already logs
`"Attributed {Attributed} stream(s) to a fab; {Unresolved} could not be resolved."` at
Information, on every pass that had work, and nothing at all in the steady state. That
line is spec 016 FR-008 + FR-010's discharge and ADR-0116's stated way of noticing a fab
whose group membership was never granted.

**What it does not give you is *which* streams.** An operator reading `unresolved=3` cannot
find those three without a SQL query, and nothing alerts on a non-zero count. Since
`unresolved` was, until this change, guaranteed non-zero for a whole class of streams, the
signal was there and meant nothing — which is how this sat undetected.

Adding per-stream identification is a real improvement and it is **not in this slice**
(ADR-0036). What makes deferring it defensible rather than lazy:

- The count is not silent — it is Information-level on every non-trivial pass.
- The pass is **not a give-up**. It selects `fab IS NULL` afresh at every host start, so
  a stream stays a candidate forever. Once the parameter is passed, the next restart
  attributes it. There is nothing to un-stick by hand.
- #1467's `SET NOT NULL` is the permanent answer: after it, `unresolved` cannot be
  non-zero because the state cannot exist. Building a diagnostic for a state that is
  being eliminated is the speculative generality the guidelines forbid.

Constitution §VII's dashboard rule binds implemented latency legs; this touches none
(spec §Latency-budget impact), so it does not attach here.

## VIII. Relationship to #1467 — what this unblocks, and what it does not

#1467 (`agent:blocked`) tightens `streams.fab` to `NOT NULL`. Its preconditions:

| Precondition | Effect of this spec |
|---|---|
| `SELECT count(*) FROM streams WHERE fab IS NULL` → 0 in every environment | **Makes it reachable.** Before: a stream whose camera is decommissioned could never reach 0, in any environment, by any amount of restarting. After: a restart resolves it. |
| The pass has logged `0 could not be resolved`, or logged nothing | **Same.** This was the unreachable one. |
| Streams whose camera "genuinely no longer exists" resolved by hand | **Dissolved.** Spec §The FR-010 claim: there is no hard delete, this bullet describes nothing, and it should be struck from #1467 rather than satisfied. |

**So this removes the blocker in principle but does not clear the label.** Three things
still stand between #1467 and delivery, none of them this spec's:

1. Someone must **run** an attribution pass on each environment after this ships and
   confirm the count is 0. A code change cannot assert that about a deployment.
2. #1467's own body must be corrected — the third bullet is wrong (see above), and a
   precondition nobody can satisfy will read as a permanent hold.
3. #1467 also proposes retiring `Stream.AttributeToFab`, `StreamFabAttributionService`
   and ADR-0116's cross-fab service account. That is the point of it, and it is an
   ADR-scale change (superseding ADR-0116), not a migration.

**Recommended, not done here:** after this merges, comment on #1467 recording that its
third precondition describes nothing and that the first two are now reachable, and leave
`agent:blocked` in place until an operator has run and read the pass. **This pass does not
touch the board.**

## IX. Constitution check

| Principle | Verdict |
|---|---|
| I. DDD / no primitives on the domain | ✅ no domain change; the lookup is Infrastructure and legitimately speaks `Guid`/`string` at an HTTP boundary |
| II. Value objects | ✅ untouched |
| III. Bounded contexts, no cross-context references | ✅ the existing, recorded HTTP exception, unchanged in shape |
| IV. Latency budget | ✅ N/A — startup only, off every leg |
| VII. Observability | ⚠️ existing signal preserved; the "which streams" gap is named and deferred with reasons (§VII) |
| IX. No speculative generality | ✅ one argument; the diagnostic is not built for a state #1467 removes |
| Testing (red for new behaviour) | ✅ red, and the red asserts resolution rather than a URL (§tasks Phase 4a) |
| ADR-0036 smallest change | ✅ the paging tie-break and the audit-row question are both named and excluded |

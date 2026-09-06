# Spec 082 — An audit row names its fab

**Issues:** #2068 (delivered), #2071 (folded in — see §Scope)
**Branch:** `fix/2068-an-audit-row-names-its-fab`
**ADRs:** ADR-0102 (common metadata envelope), ADR-0115 (overlays are fab-neutral),
ADR-0036 (smallest change), ADR-0037 (phased workflow), ADR-0139 (new behaviour starts red)
**Related, not folded in:** #2076, #1300 (closed)

## Problem

Three integration-event publishers pass a literal `null` into `EventMetadata`'s
`Fab` position while the fab sits in a local variable in the same method.

`SearchAuditQueryHandler` treats a null-fab audit row as **cross-fab and readable
by every caller** — deliberately, and correctly (see §What the query really does).
So each of the three publishes rows that any operator of any fab can read.

The symptom recorded on a long-lived run-mode Postgres (#2068): three
`SystemVariableArchivedV1` rows, zero carrying a fab, against
`SystemVariableDefinedV1`'s three of three.

## Measured population — every `EventMetadata` construction in `src/`

**21 construction sites**, counted two ways that agree: 21 `Metadata:` named-argument
sites, and 19 `EventMetadata(` spellings plus 2 target-typed `Metadata: new(`.

**6 pass a literal `null` in the fab position. Exactly 3 of those 6 have a fab on
their source record — those 3 are the defect.**

**What the guard actually sees is 20 of those 21 sites, 5 of the 6 literal nulls,
and all 3 defects.** `AuditRetentionHostedService` publishes from a private
`ArchiveAndDropAsync` and its file declares no `Handle`/`HandleAsync`, so the scan
never reaches it. Its exemption is therefore **structural** — out of scope by
construction — and not derived from `AuditChunk` carrying no `Fab`. Recorded because
"the guard derives all six verdicts" is a stronger claim than what runs, and this
repository has had to correct a summary that outran its evidence before.

| Site | Source record | Record carries `Fab`? | Fab argument | Verdict |
|---|---|---|---|---|
| `SystemVariables/…/VariableArchivedDomainEventHandler.cs:39` | `VariableArchivedDomainEvent` | **yes** | **`null`** | **defect — #2068** |
| `LayoutComposition/…/LayoutRevisionArchivedDomainEventHandler.cs:33` | `LayoutRevisionArchivedDomainEvent` | **yes** | **`null`** | **defect — #2071** |
| `LayoutComposition/…/LayoutRevisionPublishedDomainEventHandler.cs:54` | `LayoutRevisionPublishedDomainEvent` | **yes** | **`null`** | **defect — #2071** |
| `OverlayDesigner/…/OverlayRevisionArchivedDomainEventHandler.cs:31` | `OverlayRevisionArchivedDomainEvent` | **no** | `null` | correct (ADR-0115) |
| `OverlayDesigner/…/OverlayRevisionPublishedDomainEventHandler.cs:44` | `OverlayRevisionPublishedDomainEvent` | **no** | `null` | correct (ADR-0115) |
| `AuditObservability/…/AuditRetentionHostedService.cs:145` | `AuditChunk` | **no** | `null` | correct — a Timescale chunk spans fabs |
| `StreamDistribution/…/StreamHealthChangedDomainEventHandler.cs:56` | `StreamHealthChangedDomainEvent` | yes, but `FabIdentifier?` | `Fab?.Value` | not a literal null — **#2076** |
| `Automation/…/FabEventIngestedV1Handler.cs:91, :101` | `FabEventIngestedV1` | yes | `fab` | correct |
| `CameraCatalog/…` × 4 (Registered, Renamed, Retired, AddressChanged) | each domain event | yes | `.Fab.Value` | correct |
| `EventIngestion/…/EventIngestedDomainEventHandler.cs:58` | `EventIngestedDomainEvent` | yes | `fab.Value` | correct |
| `Identity/…/ClientRegisteredDomainEventHandler.cs:49, :63` | `ClientRegisteredDomainEvent` | yes | `fab.Value` | correct |
| `Identity/…/RotateWebhookClientCommandHandler.cs:147` | (command, not an event) | — | `fab.Value` | correct |
| `SystemVariables/…/VariableArchivedDomainEventHandler.cs:101` | `VariableArchivedDomainEvent` | yes | `fab.Value` | correct — fixed by #2012 |
| `SystemVariables/…/VariableDefinedDomainEventHandler.cs:34` | `VariableDefinedDomainEvent` | yes | `fab.Value` | correct |
| `SystemVariables/…/VariableValueChangedDomainEventHandler.cs:42, :69` | `VariableValueChangedDomainEvent` | yes | `fab.Value` | correct |

**The exemptions are derived, not registered.** An overlay revision genuinely has
no fab — `OverlayRevisionPublishedDomainEvent` and `OverlayRevisionArchivedDomainEvent`
have no `Fab` component at all, which is ADR-0115 expressed in the type. `AuditChunk`
carries no fab either, but its site is exempt for the structural reason above rather
than by that derivation — the guard never reads it. So the rule needs no hand-typed
exemption list, and that is the whole reason a guard is worth building here rather
than a pinned register (contrast spec 075).

**A per-handler test register would not have caught this, and demonstrably did not.**
`VariableArchivedDomainEventHandlerTests` already asserts `push.Metadata.Fab.ShouldBe("munich")`
— twice — but on the `ResolvedOverlayTextChangedV1` push, never on `SystemVariableArchivedV1`.
The file has the right assertion on the wrong event, and the defect survived it.

**Stale record found while measuring.** `SearchAuditQueryHandler.cs:62-68` and
`CrossFabReadGuardIntegrationTests` both name "camera, stream, layout, overlay and
variable events" as publishing with no fab. **Camera is no longer in that set** — all
four CameraCatalog handlers stamp the fab. The comment is a record nobody re-checked;
FR-006 corrects it.

## What `SearchAuditQueryHandler` really does — verified, not assumed

`src/AuditObservability/Application/Queries/Handlers/SearchAuditQueryHandler.cs:57-72`:

- explicit `fabId` filter → rows of that fab only;
- no filter, caller has fab memberships → `auditEvent.Fab == null || allowed.Contains(auditEvent.Fab)`;
- no filter, caller has no membership → `Fab == null` only.

So a null-fab row **is** returned to every fab-assigned caller. The issue's wording
"treats a null fab as unscoped" is accurate.

**But it is deliberate and it is right.** The behaviour was introduced by #1300
(closed): excluding null-fab rows made a whole class of history readable by *nobody*.
`CrossFabReadGuardIntegrationTests.Search_without_a_fab_filter_returns_the_callers_fabs_and_cross_fab_rows`
asserts it as intended behaviour today.

**Therefore the fix cannot belong at the query.** A stored row records `fab = null`
and nothing else; the query has no way to tell "legitimately cross-fab" from "the
publisher forgot". That information exists only at the publisher. The query stays
untouched — and after this spec its rule is *true* rather than merely tolerated,
because the remaining null-fab publishers are the ones that genuinely have no fab.

## Scope — why #2071 is folded in and #2076 is not

**#2071 folds in.** The deliverable is the guard, and the guard cannot land green
while any of the three sites is unfixed. Splitting leaves two options, both worse:
land the guard failing, or land it with a hand-typed exemption for LayoutComposition
that a follow-up deletes — which is precisely the register this spec exists to avoid.
ADR-0036's "a fix changes the bug and nothing else" is satisfied: **one bug**, one
line's shape, three instances, one guard. The trade is a PR touching two bounded
contexts instead of one; the alternative is a guard whose first act is to exempt a
known defect.

**#2076 is not folded in.** `StreamHealthChangedDomainEvent.Fab` is `FabIdentifier?`
and the handler passes `Fab?.Value` with a stated reason. It is not a literal null and
this guard will not catch it — a nullable that is null at *runtime* is a different
defect with a different cause (attribution cannot see decommissioned cameras). The
analysis touches it only to say the guard does not cover it. FR-007 records that
honestly rather than letting a green guard imply coverage it does not have.

## User stories

### US1 (P1) — An operator sees only their own fab's archive history

An operator of one fab searching the audit surface does not see that another fab's
system variable or layout revision was archived or published.

**This is the shippable slice.** It is observable end to end: archive a variable in
munich, search as a berlin operator, the row is absent.

### US2 (P1) — The next handler cannot reintroduce it

A handler whose source event carries a fab cannot pass `null` in `EventMetadata`'s
fab position. The build fails.

US2 is not separable from US1 here: the guard's red *is* the proof that the population
in §Measured population is complete.

## Acceptance scenarios

### Happy path
```gherkin
Given a system variable "oeeLine1" in fab "munich"
When an operator archives it
Then the SystemVariableArchivedV1 event's metadata carries fab "munich"
And the audit row recorded for it carries fab "munich"
```

### The leak (the symptom under test)
```gherkin
Given a system variable archived in fab "munich"
And a layout revision published and archived in fab "munich"
When an operator whose only membership is fab "berlin" searches the audit surface
Then none of those three rows is returned
```

### Cross-fab rows stay readable (the regression this must not cause)
```gherkin
Given an audit row that genuinely has no fab
When any fab-assigned operator searches the audit surface
Then the row is returned
```
The #1300 behaviour is preserved. Overlay-revision and audit-chunk rows keep their
null fab and keep being readable by everyone.

### Bad request
```gherkin
Given an audit search
When it is called with a fabId the caller does not hold
Then it is refused 403 RESOURCE_FAB_NOT_AUTHORIZED
```
Unchanged; asserted so the fix is shown not to have moved it.

### Auth
```gherkin
Given an unauthenticated caller
When it searches the audit surface
Then it is refused 401
```

### The guard
```gherkin
Given a handler whose first parameter's record type has a Fab component
When its body constructs an EventMetadata passing a literal null in the fab position
Then the build fails naming the file, the record and the position
```
```gherkin
Given a handler whose first parameter's record type has no Fab component
When its body passes a literal null in the fab position
Then the build passes
```

## Functional requirements

- **FR-001** — `VariableArchivedDomainEventHandler` stamps `fab.Value` on `SystemVariableArchivedV1`.
- **FR-002** — `LayoutRevisionArchivedDomainEventHandler` stamps `fab.Value` on `LayoutRevisionArchivedV1`.
- **FR-003** — `LayoutRevisionPublishedDomainEventHandler` stamps `fab.Value` on `LayoutRevisionPublishedV2`.
- **FR-004** — An architecture guard derives the rule from source: for every `Handle`/`HandleAsync`
  whose first parameter's record type declares a `Fab` component, no `EventMetadata`
  constructed in that body may pass a literal `null` in the fab position. No exemption list.
- **FR-005** — The guard **fails loudly on anything it cannot parse** — an unresolvable record,
  a named-argument form, an arity it cannot map — rather than skipping it. A guard that
  silently passes what it did not understand is the failure mode this repo has recorded before.
  It also asserts a non-zero checked count, as `HandlerDeconstructionTests` does.
- **FR-006** — The stale "camera, stream, layout, overlay and variable" enumeration in
  `SearchAuditQueryHandler` and `CrossFabReadGuardIntegrationTests` is corrected to what
  remains true after this change.
- **FR-007** — The guard's own doc comment states what it does **not** prove: it does not
  prove the *right* fab was stamped, and it does not cover a nullable fab that is null at
  runtime (#2076).

## Independent end-to-end test procedure

1. Boot the Aspire stack.
2. As `admin@munich.test`, define and then archive a system variable; publish and archive a layout revision.
3. As a berlin-only operator, `GET /audit?pageSize=200`. **Expect:** no row for any of step 2.
4. As `admin@munich.test`, `GET /audit?pageSize=200`. **Expect:** all three rows present, each with `fab: "munich"`.
5. Query `audit_events` grouping by `event_kind` with `count(*)` against `count(fab_id)` for the
   three kinds. **Expect:** the two counts equal for each — the shape the issue's evidence table
   showed as 3 rows / 0 with a fab.
6. Confirm the overlay-revision rows still carry a null fab and are still returned in step 3.

## Locked tech choices

xUnit + Shouldly (ADR-0052); integration via `AspireFixture`, no Testcontainers (ADR-0103);
the guard lives in `tests/Architecture.Tests` alongside `HandlerDeconstructionTests`, whose
source-reading machinery it reuses; `Ensure.That` guards (ADR-0105); no new packages.

## Latency-budget impact

**N/A.** Nothing here is on the event→overlay path. The three handlers publish to the
Wolverine outbox exactly as before; one string argument changes from `null` to a value
already in scope. `ResolvedOverlayTextChangedV1` — the one message in these files that
*is* on the path — is untouched.

## Out of scope

- #2076 — nullable stream fab attribution.
- Any change to `SearchAuditQueryHandler`'s null-fab semantics.
- Backfilling the existing unscoped rows in the run-mode database.

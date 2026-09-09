# Feature Specification: What "applied together" means for a camera

**Feature Branch**: `feat/1975-camera-edits-staged-together`

**Created**: 2026-09-09

**Status**: **Phase 1 complete — the deliverable is a finding, not a feature.** Phases 2
and 3 are deliberately not run. See §6.

**Input**: #1975 — *"Camera Catalog has no staged-config workflow, unlike the other three
contexts in decision 022."* Raised by spec 047's audit
(`specs/047-the-decisions-we-made/audit.md:491`), labelled `agent:ready`, and queued into
the ADR-0144 autonomous lane. The issue itself records the change as *"Small, and low
urgency — single-field edits are the common case and work correctly."*

**Scope**: **investigation only.** No production code. No test. No ADR — writing one is
what this spec concludes is needed, and ADR-0144 forbids this lane from writing it.

**ADRs**:
[ADR-0037](../../docs/adr/0037-guided-phased-development.md) (the phases),
[ADR-0144](../../docs/adr/0144-an-autonomous-delivery-lane.md) (the lane and its three
prohibitions),
[decision 022](../../docs/adr/0000-initial-decisions.md) (the locked decision under
audit),
[ADR-0043](../../docs/adr/0043-optimistic-concurrency.md) /
[ADR-0113](../../docs/adr/0113-optimistic-concurrency-two-layer.md) (the two-layer
concurrency this would have to interact with),
[ADR-0120](../../docs/adr/0120-name-mutability.md) (why a camera name is editable at
all),
[ADR-0142](../../docs/adr/0142-idempotency-keys-for-non-idempotent-posts.md) (caller-supplied idempotency),
[ADR-0036](../../docs/adr/0036-karpathy-coding-guidelines.md) (no speculative generality).

---

## 1. The claim under test

Decision 022, verbatim (`docs/adr/0000-initial-decisions.md:36`, status **Locked**):

> Draft → preview → publish workflow applies to **Overlays**, **Layouts** (with
> assignment guard + render-error fallback), **Automation rules** (with dry-run mode),
> and **Camera Catalog** (staged config changes applied together). All other contexts
> edit live with audit log. Common publish pipeline: draft → validate → preview/dry-run →
> publish → atomic activate → audit trail.

Three of four are built. The fourth is not. The issue offers two routes — build it, or
amend 022 to drop the clause — and notes the second *"is defensible."*

**The lane may take only the first**, because the second is an ADR amendment. This spec
therefore set out to establish that the first is the right thing to build. **It is not**,
and the reason is not the one the issue anticipated.

---

## 2. Finding 1 — the absence is real

The audit's own grep, re-run:

```sh
$ grep -rilE "staged|pendingConfig|applyTogether" src/CameraCatalog/ --include=*.cs
(no output; exit 1)
```

Searched again for the *job* rather than one spelling, per the audit's own method
(`audit.md:466`) — `revision|draft|publish|pending|preview|stage|batch|proposed|apply`
across `src/CameraCatalog`. Every hit is one of three unrelated things: `PendingEvents`
(the domain-event outbox, `CameraRepository.cs:92,107`), the word "publishes" in
integration-event doc comments, and EF's "applying migrations"
(`CameraCatalogMigrator.cs:9`).

**Verdict: absent, under any name.** Nothing is half-built, so nothing shrinks the issue
that way.

`src/CameraCatalog/Domain/Camera/` holds `Camera`, `CameraIdentifier`, `CameraName`,
`CameraStatus`, `FabIdentifier`, `Registration`, `RegisteredAt`, `RtspUrl`,
`ICameraRepository` — and no revision, draft or staging type. Four commands exist:
`RegisterCamera`, `RenameCamera`, `ChangeCameraAddress`, `RetireCamera`.

---

## 3. Finding 2 — the issue's motivating case cannot be built as described

The issue names the case decision 022 had in mind:

> a camera whose **address, name and fab** all change together — a re-cabling, or a move
> between lines.

**The fab cannot change.** Not "is not currently changeable" — cannot, by design, in three
independent places:

- `Camera.cs:13-24` — *"Fixed at registration: a camera is bolted to a wall in one
  building, so relocating the device means registering it afresh rather than moving the
  record."* `Fab` has a private setter and **no behaviour method sets it**; the aggregate
  offers `Retire`, `ChangeAddress`, `Rename`, and nothing else.
- `PatchCameraRequest.cs:22-26` — *"The fab and the identifier remain absent, and immutably
  so (spec 015 FR-004, spec 029 FR-008) — there is nothing here that could express changing
  them, which is a stronger guarantee than validating them away."*
- `CameraEndpoints.cs:88-90` — the endpoint's own OpenAPI summary: *"The fab and identifier
  are immutable."*

The fab is also load-bearing for authorization, not merely for naming — a camera's record
carries its RTSP address, so reaching another plant's camera is reaching its video
(`Camera.cs:18-22`, #1397). Making it mutable is a security change, not a config change,
and is nothing decision 022 asks for.

**So the case reduces to two fields: address and name.** That is materially smaller than
the case the issue argues from, and the reduction was not visible when the issue was
filed.

---

## 4. Finding 3 — the one-field limit is a recorded deferral, not an oversight

`PatchCameraRequest.cs:15-21`:

> Both properties are optional and **exactly one** must be present. Not because PATCH
> forbids more — it does not — but because each attribute has its own command and its own
> `If-Match` check, and a request changing both would need the second command to see a
> version the first had already advanced. Supporting that means **one combined command
> with one version check**, which is a larger change than either correction is worth
> today.

Enforced at `CameraEndpoints.cs:262-275`, which answers 400 with
*"Send either rtspUrl or name, not both: each is applied under its own version."*

Two consequences worth stating plainly:

1. Somebody already identified the exact defect the issue describes, named the exact fix
   ("one combined command with one version check"), and deferred it in writing. The gap is
   known, bounded and cheap — **and it is not a draft/preview/publish workflow.**
2. "Each step is separately audited as if it were an intentional state", concretely: the
   audit bus writes **one row per integration-event delivery**
   (`IntegrationEventAuditHandler.cs:22,36-39,57`). Changing name then address produces a
   `CameraRenamedV1` row and a `CameraAddressChangedV1` row. Between the two requests the
   catalog really does describe a camera that never existed — and
   `CameraAddressChangedV1` is not audit-only: `StreamDistribution` turns it into a
   `RepointStreamCommand`
   (`StreamDistribution/Application/EventHandlers/CameraAddressChangedIntegrationEventHandler.cs:12,27`),
   so the intermediate state reaches the media path, not just the log.
   `CameraRenamedV1` has no consumer but audit.

---

## 5. Finding 4 — the three built contexts are **two** patterns, and neither transplants

The brief asked whether the three share one design. They do not.

| | Overlays | Layouts | Automation |
|---|---|---|---|
| Staged state | `Revision` **child entity** (`OwnsMany`), own table | same, own table | **status field on the root** (`RuleState.cs:12-18`: Draft / Active / Archived) |
| Edit-a-draft | `PATCH …/revisions/{n}` | same | **does not exist** — no `EditRule*` command |
| Server preview | **none** — preview is client-side (`apps/shared/src/ui/composites/OverlayEditor.tsx`) | **none** | `POST /rules/{name}/dry-run`, a read-scoped query |
| Publish | `POST …/revisions/{n}/publish` | same | `POST /rules/{name}/publish` (one-way flag) |
| Revert | `POST …/revisions/{n}/revert` | same | **does not exist** |
| Integration event | `OverlayRevisionPublishedV1` + `…ArchivedV1` | `LayoutRevisionPublishedV2` + `…ArchivedV1` | **none — and therefore no audit row at all** |

Overlays and Layouts are near-verbatim twins (their XML doc comments are identical prose).
Automation is a two-state flag that predates the chain pattern.

Two observations decide this spec:

**(a) The twins' "preview" is client-side because something renders them.** An overlay and
a layout are drawn on a wall of up to 250 displays; a draft exists so an operator can look
at it before 250 screens do. There is no server preview endpoint in either context — the
preview step of decision 022 is discharged by a React editor. **Nothing renders a camera
record.** Automation's dry-run is the only server-side preview in the repo, and it exists
because a rule *evaluates* — you can feed it a sample event and see what it would do. A
camera record does neither.

**(b) The cost of mirroring Overlays is out of proportion to two fields.** Mirroring means:
a `Revision` child entity, `CameraRevisionIdentifier` / `CameraRevisionNumber` /
`CameraRevisionState` / `PublishedAt` / `ArchivedAt` value objects, an `OwnsMany` mapping
with a partial unique index, a migration, **six** command + error + handler triples
(create-draft, branch-draft, edit-draft, publish, archive, revert — the shape at
`src/OverlayDesigner/Application/Commands/`, 12 files), **seven** routes
(`OverlayEndpoints.cs:37,48,57,65,78,89,102,115`), two new `*V1` contracts with their
audit-handler registrations, and management-web UI to drive it — replacing two dialogs
(`EditCameraAddressDialog.tsx`, `RenameCameraDialog.tsx`) that work correctly today.

That is the machinery of a versioned, publishable artefact, built so an operator can change
a name and an address in one request instead of two.

---

## 6. The conclusion, and why this spec stops

**Only two shapes are actually available, and neither is deliverable by this lane.**

**Shape A — mirror Overlays literally.** Honours decision 022 word for word. Rejected: it
is speculative generality of the exact kind ADR-0036 and CLAUDE.md forbid — a draft that
nothing previews, a revert nothing needs, an archive chain for a record that has one live
version by definition. The issue itself supplies the argument against it: *"the first three
have a publish/preview audience and a camera arguably does not."* §5(a) turns that "arguably"
into evidence: the twins' preview is a renderer, and a camera has no renderer.

**Shape B — build only "applied together":** one `ChangeCameraConfigurationCommand`, one
load, one `If-Match` check, both aggregate methods, one `SaveAsync`. Right-sized, ~a day,
fixes the real defect in §4. **But it honours one half of decision 022 and silently leaves
the other half unhonoured** — no draft, no preview, no publish. Decision 022 would stay
recorded as Locked-and-holding while one of its four clauses was met in half. That is
precisely the drift spec 047's audit exists to find, and precisely the failure mode CLAUDE.md
records three times over (§II twice, the phase-3 board gate, §IV's leg table): *a record
nobody checked against what was actually happening.* Shipping Shape B without recording the
scoping decision would seed the next audit's finding.

**So the honest answer is an ADR**, and it is the same ADR either way — one that says what
decision 022's Camera Catalog clause *means*. It has three candidate resolutions, and this
spec does not choose between them:

1. **Drop the clause.** A camera has no publish/preview audience; the Camera Catalog edits
   live with an audit log like every other context. Shape B then becomes an ordinary,
   ADR-free improvement to `PATCH /cameras/{camera}` — not a 022 obligation at all.
2. **Narrow the clause to atomicity.** "Applied together" means one transaction and one
   version check, not draft → preview → publish. Shape B discharges it, and 022's own
   parenthetical for Camera Catalog — which is *not* the same parenthetical the other three
   carry — is the textual hook.
3. **Keep it whole and accept Shape A.** Defensible only if someone wants *deferred
   application* — staging a re-cabling now and applying it at a maintenance window. Nothing
   in the issue, the audit, or the code says an operator has asked for that, and inventing
   the requirement to justify the machinery is the wrong order.

**ADR-0144 forbids this lane from writing an ADR or amending the constitution: "it
implements decisions, it does not make them."** Phases 2–7 are therefore not run, and this
is a blocked-with-a-finding outcome, not a failure.

---

## 7. Answers the ADR author will need

Collected here so the decision can be written without repeating this investigation.

**Concurrency (ADR-0043 / 0113).** No new mechanism is required by either shape.

- Today: `ConcurrencyHeaders.TryReadExpectedVersion`
  (`src/ServiceDefaults/ConcurrencyHeaders.cs:123`) — 428 with no `If-Match`, wildcard and
  weak tags rejected; handlers compare `Version != expectedVersion` before mutating
  (`RenameCameraCommandHandler.cs:40-45`) and the EF token on the root row catches the
  in-transaction race.
- **Shape B** *removes* a concurrency hazard rather than adding one: the whole reason both
  fields cannot be sent today is that the second command would see a version the first had
  advanced. One command, one check, one bump — strictly simpler.
- **Shape A** also needs nothing new:
  `AggregateVersionInterceptor.HasDirtyOwnedDescendant`
  (`src/ServiceDefaults/Persistence/AggregateVersionInterceptor.cs:62,89`) already makes a
  touched owned child bump the *root's* version, which is how Overlays and Layouts guard a
  draft edit. The risk in Shape A is not concurrency; it is that a long-lived draft goes
  stale against a camera edited live in the meantime — a policy question the ADR must
  answer, and one the twins answer with `revert` and a partial unique index.

**Idempotency (ADR-0142).** Publish endpoints in all three built contexts carry **no**
`Idempotency-Key`; only the three creates do (`POST /overlays`, `POST /layouts`,
`POST /rules`), and `POST /cameras` already does too (`CameraEndpoints.cs:145,177`).

- **Shape B** needs none. It is a `PATCH` guarded by `If-Match`: a retry either matches the
  version and applies once, or is 412. `PATCH` is not retried by default (ADR-0143), and
  conditional-request semantics already give at-most-once.
- **Shape A**'s `POST …/draft` is create-shaped and **should** carry a key, matching the
  four creates; its `…/publish` should not, matching the six existing publish routes. Note
  the scope key must include the caller — `IdempotencyScope.For(key, endpoint,
  actingOperator)` — as every existing call site does.

**Latency (constitution §IV).** **N/A — no leg.** This is operator-facing catalog editing
over HTTP; it is not on the `event arrival → overlay rendered` path. The one adjacency
worth naming and dismissing: `CameraAddressChangedV1` reaches `StreamDistribution`, which
repoints a MediaMTX path. That is media-path *setup*, not the event→overlay leg, and it is
unmeasured by §IV in either shape. Shape B would make the repoint fire once with a correct
sibling name instead of once against a stale one — a correctness improvement, not a budget
change.

**§II (no primitive-typed domain state).** Not reached, because no domain state is
proposed. Recorded for whichever shape is chosen: Shape A's `CameraRevisionNumber`,
`CameraRevisionState`, `PublishedAt` and `ArchivedAt` must all be value objects, as the
twins' equivalents are — `PrimitiveBoundaryTests` fails the build otherwise.

**Phase 4a colour, if the ADR sends this back.** **Behaviour-changing → red**, for either
shape. Both add a capability that does not exist. The red for Shape B is the sharpest and
worth writing down: `PATCH /cameras/{camera}` with **both** `rtspUrl` and `name` and a
valid `If-Match` currently answers **400 CAMERA_INVALID_REQUEST** — a test asserting 204,
one version bump, both values stored, and the two integration events on one commit fails
on that 400 before a line is written.

---

## 8. What could not be verified

- **No build or test run.** Nothing here needs one — every claim is a file:line reading —
  but the "one combined command" cost estimate in §6 is a reading of the twins' file
  counts, not a measured implementation.
- **Whether an operator wants deferred application.** Resolution 3 in §6 stands or falls on
  a requirement nobody has stated. This investigation found no evidence for it and did not
  look for it outside the repository.
- **The other three contexts' preview claim is a code reading.** Overlays and Layouts have
  no server preview route; the client-side editor at
  `apps/shared/src/ui/composites/OverlayEditor.tsx` was located but not run.

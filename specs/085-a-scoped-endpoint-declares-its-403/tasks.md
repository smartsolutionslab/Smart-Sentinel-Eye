# Tasks — Spec 085, seventeen endpoints, not eight

**Phase:** 3 (Tasks) — ADR-0037. Gate: tasks atomic; the feature's issue (#2113)
on Project #13.

**Engineer:** backend, throughout. Every file is C#; nothing frontend, infra or
Aspire.

**Phase 4a colour:** **red** (behaviour-changing — the generated OpenAPI document
gains a `403` on 17 operations).

`[P]` marks tasks owning disjoint files (ADR-0109).

---

## Foundational — blocks everything

The guard must exist and must fail before a single declaration is added. A
declaration added first makes the red unobtainable, and an unobserved red is a
phase-4 failure, not a shortcut.

### T001 — Widen the guard's class doc to hold the second sentence

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

The file currently guards *"an endpoint names the scope it needs"*. It will also
guard *"an endpoint that needs a scope declares the refusal that scope
produces"*. Record, in the class doc:

- the measured population — 56 mappings, 54 scoped, **28 of them scoped only
  through a `MapGroup`**, 17 undeclared across 5 files in 3 contexts;
- the three producers of 403 (scope policy, fab guard, `ApiError.Forbidden`) and
  that **only the first is this rule's antecedent**;
- that the rule runs **one way** — `POST /streams/authorize` is anonymous and
  declares a 403 correctly, so a mirror would fail on correct code;
- that `RequireScope` is used by no endpoint in `src/`, so a rule written to its
  name would pass vacuously. This is the near-miss the issue itself set up and
  it belongs in the file that avoids it.

**Done when:** the doc states the population and the one-way direction, and
carries no figure that was not measured on this branch.

### T002 — Carry the declared status out of the chain read

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

`ReadMapping` already takes the mapping's `[start..end]` span with comments
blanked and literals masked, and reads the summary and the authorization from
it. Add `DeclaresForbidden` to `EndpointMapping`, filled from that same span by
looking for `.ProducesProblem(StatusCodes.Status403Forbidden)`.

Do the same for `RouteGroup`, from the group span `Read` already computes.

**Done when:** both records carry the flag and nothing else about the reading
changed. No new regex machinery beyond a call-site search of the shape
`IndexOfCall` already provides.

**Depends on:** nothing. **Blocks:** T003, T004, T005.

### T003 — G1: every scoped mapping declares the refusal

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

A per-file `[Theory]` over `EndpointFiles()`, population from
`ScopedWithLiteral(file)` — which already filters to `AuthorizationKind.Scoped`
and to constants that resolve in the catalogue, so an unresolvable constant stays
A3's failure and does not fail twice for one cause.

Failure message names file, line, verb, full route, the scope literal, and what
the document currently asserts.

**Done when:** run against unmodified `src/`, it fails naming **17** mappings in
**5** files. See T006 — that number is the gate, not a detail.

**Depends on:** T002.

### T004 — G2: no route group declares the refusal

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

`[Fact]`. Zero of the 17 groups declare a 403 today. The assertion exists because
OpenAPI **inherits** group metadata: a group-level declaration would make T003's
per-chain demand unsound and fail correct code. The failure message must say the
shape is refused and why — not that an endpoint is wrong.

**Done when:** green on unmodified `src/`; and, checked by hand, red when a 403
is temporarily added to the `/overlays` group.

**Depends on:** T002.

### T005 — G3: the walk's count agrees with an independent sweep

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

`[Fact]`. Mapping-walk declarations + group-walk declarations must equal a flat
sweep of `.ProducesProblem(StatusCodes.Status403Forbidden)` over
`ApiSourceFiles()` (the wider `src/*/Api/**/*.cs` glob, deliberately). Both sides
read **38** before the change and **55** after.

This is what stops a declaration hoisted somewhere the walk cannot see from
reading as "declares nothing" — the property `PaginatedConsumerTests` established
and `PreconditionDeclarationTests` borrowed.

**Done when:** green on unmodified `src/` at 38 = 38.

**Depends on:** T002.

### T006 — G4: the hub is excluded by name, with the reason

`tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`

Add to `MappedOutsideTheChain`'s doc that `/hubs/layouts` is outside this rule
too, and why: a SignalR hub has no OpenAPI operation, so there is nothing for
`ProducesProblem` to write into — the same reason it cannot carry a
`.WithSummary`. Comment only; the row and both existing directional checks are
unchanged, and they already make a *second* outside-the-chain route fail rather
than inherit the exclusion.

**Done when:** a reader of the register can tell why the hub needs no 403 without
opening another file.

**Depends on:** nothing. **May run alongside** T002–T005 (same file, so not `[P]`).

---

## The red run — the gate between the halves

### T007 — Observe the red, and check it is the *right* red

```sh
dotnet test tests/Architecture.Tests -c Release \
  --filter EndpointScopeDeclarationTests
```

Run **before any `src/` edit**. Capture the output verbatim; it goes in the PR
body (ADR-0139).

**The failure must name 17 mappings across 5 files.** Three wrong reds to
recognise, because each looks like success:

| Wrong red | What it means |
|---|---|
| **8 mappings, all `OverlayEndpoints.cs`** | the population narrowed to own-chain mappings; 28 of the 54 are missing. *This is the dangerous one* — 8 is also the issue's headline number, so a wrong red reads as the right one. |
| **8 mappings, all Identity** | the population narrowed to Identity, whatever the mechanism. |
| **green** | the antecedent matched nothing — almost certainly written against `RequireScope`, which no endpoint uses. |

Any of these: stop, fix the guard, re-run. Do **not** proceed to T008–T012.

**Depends on:** T001–T006.

---

## The declarations — five disjoint files

Each adds one line per listed mapping, placed among that chain's existing
`ProducesProblem` calls:

```csharp
.ProducesProblem(StatusCodes.Status403Forbidden)
```

`StatusCodes` is already imported in all five. **No comment beside the new
lines** — the reason now lives in a guard that fails the build (ADR-0036,
ADR-0139). No other change to these files: no summary, no scope, no handler.

### T008 [P] — `src/Identity/Api/DevicesEndpoints.cs`
Lines 33 (`POST /devices/register`), 43 (`DELETE /devices/{clientId}`),
54 (`GET /devices`). **3 added.**

### T009 [P] — `src/Identity/Api/KiosksEndpoints.cs`
Lines 33 (`POST /kiosks/enroll`), 43 (`DELETE /kiosks/{clientId}`),
54 (`GET /kiosks`). **3 added.**

### T010 [P] — `src/Identity/Api/WebhookRotationEndpoints.cs`
Lines 39 (`POST /webhook-integrations/{name}/rotate`), 54
(`GET /webhook-integrations`). **2 added.**

### T011 [P] — `src/OverlayDesigner/Api/OverlayEndpoints.cs`
Lines 30, 40, 47, 54, 66, 76, 88, 100 — the whole overlay surface. **8 added.**
The only file in the seventeen whose scopes are all declared in-chain, and the
only context with no fab guard at all: producer 1 is the sole reason these
eight can answer 403.

### T012 [P] — `src/AuditObservability/Api/AuditEndpoints.cs`
Line 46 (`GET /audit/{auditIdentifier:guid}`). **1 added.** Its two siblings at
lines 32 and 39 already declare it; this one was missed when they were written.

**Depends on:** T007. **Mutually disjoint** — five files, three contexts.

---

## Green, and what the green is worth

### T013 — Green run

```sh
dotnet test tests/Architecture.Tests -c Release \
  --filter EndpointScopeDeclarationTests
```

**Done when:** all assertions pass, and T005's two sides both read **55**.

**Depends on:** T008–T012.

### T014 — Prove the antecedent is derived, by counterfactual

A green G1 does not distinguish a derivation from a checklist — both go green
once the lines are added. This is the task that does.

1. Delete `.RequireAuthorization(Scope.Sse.Overlays.Read)` **and** the 403 from
   `GET /overlays`. Run the guard.
   - **Expect:** G1 says nothing about that mapping (it left the population),
     while `Every_endpoint_that_enforces_no_scope_is_registered_against_an_open_issue`
     fails it for enforcing no scope.
   - **If G1 still demands a 403** from a route that now requires none, the
     population is hard-coded somewhere and the derivation claim in the spec is
     false. That is a finding worth more than the fix (#2142 rests on it) —
     report it, do not paper over it.
2. Revert.
3. Move one 403 declaration out of a chain into a private helper the walk cannot
   see. **Expect:** T005 fails on the disagreement, not T003 alone. Revert.

**Done when:** both counterfactuals behaved as stated, and the outcome is
recorded in the PR body — including if it did not.

**Depends on:** T013.

### T015 — Full fast lane

```sh
dotnet build -c Release
dotnet test tests/Architecture.Tests -c Release
```

Docker-free (ADR-0103). Confirms the five `src/` edits did not disturb
`PreconditionDeclarationTests`, `ConcurrencyConflictDeclarationTests` or
`PrimitiveBoundaryTests`.

**Done when:** build and the whole Architecture.Tests project are green.

**Depends on:** T013.

### T016 — Verification note (phase 5)

Write what was observed and, explicitly, what was not:

- the red (17/5) and the green (55 = 55), quoted;
- T014's counterfactual results;
- **that no 403 was observed on the wire by this change.** The behaviour is
  observed on three `/system-variables` reads by
  `tests/Integration.Tests/SystemVariables/VariableReadScopeIntegrationTests.cs`
  and derived for the other 51 from the single `AddScopePolicies(Scope.All)`
  registration all nine APIs share;
- **that no member of this guard family reads the emitted OpenAPI document.**
  If the Aspire stack is available, do step 6 of the spec's procedure — boot
  OverlayDesigner, `GET /openapi/v1.json`, confirm `POST /overlays` now carries a
  `403`. If it is not, say so; do not imply it ran. Another issue holds the stack
  as of 2026-09-07.

**Latency:** N/A — no leg of the event-to-overlay path is touched (constitution
§IV).

**Depends on:** T014, T015.

---

## Not in scope

Named so they are refused deliberately rather than forgotten.

- **A mirror rule** (unscoped ⇒ must not declare 403). Fails on
  `POST /streams/authorize`, which is anonymous and declares one correctly via
  `AuthorizeWhepErrors.Forbidden`. A mirror carved to exempt it is a register.
- **Extracting the shared endpoint reader** across the four guards. Three copies
  of `MaskLiterals`/`StatementEnd` exist; this spec adds a fourth *consumer* and
  no fourth copy. The extraction is a behaviour-preserving refactor of test
  infrastructure — a separate issue, and one whose phase 4a colour would be
  characterisation, not red.
- **A guard that reads the generated OpenAPI JSON.** The gap all four guards
  share. Belongs with #2142.
- **Producer 2's own population** — which routes call `IFabAuthorizationGuard`
  and whether each declares 403. Nine of the 17 have it and are fixed here
  incidentally; whether a route has producer 2 *without* a scope is unasked and
  is register-shaped.
- **Any change to which scope a route requires**, any new scope, any realm edit,
  any 401 declaration.
- **A new ADR.** Constitution §VIII already locks scope checks at every endpoint
  plus fab-group membership; ADR-0070 locks the surface. Declaring a status the
  locked model already produces is implementation, not decision — the same call
  specs 072 and 075 recorded. ADR-0144 bars the lane from writing one in any
  case; the point is that nothing here is being decided.

---

## Gate (Phase 3)

Tasks are atomic and each names its own "done". **The board is not touched by
this phase** — #2113 is a feature-level issue and belongs on Project #13, added
by hand:

```sh
gh project item-add 13 --owner smartsolutionslab \
  --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/2113
```

No per-task issues (the practice stopped after spec 028).

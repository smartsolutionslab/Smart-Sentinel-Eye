# Spec 085 — Seventeen endpoints, not eight

**Issue:** #2113 — *Every scoped endpoint can return 403 and none declares it*
**Branch:** `fix/2113-a-scoped-endpoint-declares-its-403`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0070 (Minimal APIs only — the surface the declaration lives on),
ADR-0089 (`ApiError` carries the HTTP status, which is how the third 403
producer reaches the wire), ADR-0114 (fab inference; the only ADR in the
repository that mentions 403 at all), ADR-0139 (rules that fail the build rather
than conventions a reviewer remembers), ADR-0084 (code metrics), ADR-0052 /
ADR-0103 (xUnit + Shouldly; no Docker in the fast lane), ADR-0065 (coverage
gates), ADR-0109 (parallel markers), ADR-0036 (smallest change, no drive-by),
ADR-0037 (phased workflow), ADR-0144 (autonomous lane — no ADR is written here).

**Constitution:** §VIII *Safe by Default at Trust Boundaries* —
"**Authorization** is enforced by scope checks at every endpoint, plus fab-group
membership." That sentence is the decision this spec implements the *declaration*
of. §Testing — new behaviour starts red.

---

## The issue as filed, and what survives contact with the repository

The issue says three things. Two are right and one is wrong in a way that would
have produced a guard that passes without checking anything.

**Right: Identity is eight, and none of the eight declares 403.** Measured, not
inherited — `DevicesEndpoints` 3, `KiosksEndpoints` 3, `WebhookRotationEndpoints`
2. The issue marked its own figure unverified; it holds.

**Right, and understated: the population is larger.** Identity is 8 of **17**.

**Wrong: "`RequireScope` is right there in the mapping's own chain."** Two
separate errors in one clause, and each on its own is fatal to a guard written
from the sentence:

1. **`RequireScope` is used by no endpoint in `src/`.** It exists —
   `src/ServiceDefaults/Authorization/RequireScopeExtensions.cs:76` — and it
   forwards to `RequireAuthorization`. Every one of the 56 mappings spells it
   `.RequireAuthorization(Scope.…)`. A guard matching `RequireScope` literally
   would match zero mappings and pass green on day one, for ever.
   `EndpointScopeDeclarationTests` already knows this and reads both spellings;
   its own doc says so in as many words.
2. **More than half the scoped population does not carry authorization in its
   own chain at all.** **28 of the 54** inherit it from a `MapGroup` — every
   mapping in Identity, Audit, Automation, CameraCatalog and
   WebhookIntegrations, plus three of EventIngestion's. A per-chain grep finds
   26 of 54 and calls the other 28 unscoped.

The rule is still derivable. It is derivable **because
`EndpointScopeDeclarationTests` already resolves group inheritance by receiver
name**, not because the antecedent sits in the chain. That distinction is the
whole reason this spec puts the new assertions in that file rather than a new
one, and it is the reason the derivation is cheap here and would not be cheap
anywhere else.

---

## The census, measured

Worktree `D:\Github\wt-2113` at `921dde95`, 2026-09-07. Counted by walking every
`.Map(Get|Post|Put|Patch|Delete)(` site under `src/*/Api/**/*Endpoints.cs` with
comments blanked and string literals masked — the same reading
`EndpointScopeDeclarationTests` performs — and cross-checked against a flat
`grep` sweep.

| Fact | Count |
|---|---|
| Endpoint files (`src/*/Api/**/*Endpoints.cs`) | **12** |
| Route-handler mappings in them | **56** |
| Routes mapped outside the chain (`MapHub`) | **1** (`/hubs/layouts`) |
| Mappings whose effective authorization is a **scope** | **54** |
| Mappings that are **`AllowAnonymous`** | **2** |
| Mappings with a **bare** `RequireAuthorization()` or unresolved | **0** |
| Scope taken from the mapping's **own chain** | 26 |
| Scope **inherited from a `MapGroup`** | 28 |
| `.ProducesProblem(StatusCodes.Status403Forbidden)` sites under `src/*/Api` | **38** |
| … on a **scoped** mapping | 37 |
| … on an **anonymous** mapping | 1 (`POST /streams/authorize`, correctly) |
| **Scoped mappings that do not declare 403** | **17** |
| `MapGroup` chains declaring **any** 403 | **0** (of 17 groups) |

Both 56 and 38 were obtained twice by different means and agreed: the mapping
walk and an independent flat sweep. 56 also equals the figure
`EndpointScopeDeclarationTests` has pinned since spec 070, which is a third
witness written by someone else.

**This census is the state before the change, and stays that way** — it is what
motivated the work, pinned to the commit named above. After phase 4b the last
three rows read **55**, **0** and **0**: the seventeen declarations were added,
by both witnesses again. The rows above them do not move, because this change
adds no mapping and no file — `56` and `12` are still asserted exactly.

### The seventeen

| File | Line | Route | Scope, and where it comes from |
|---|---|---|---|
| `src/AuditObservability/Api/AuditEndpoints.cs` | 46 | `GET /audit/{auditIdentifier:guid}` | `Scope.Sse.Audit.Read` — group |
| `src/Identity/Api/DevicesEndpoints.cs` | 33 | `POST /devices/register` | `…DeviceClients.Write` — group |
| `src/Identity/Api/DevicesEndpoints.cs` | 43 | `DELETE /devices/{clientId}` | `…DeviceClients.Write` — group |
| `src/Identity/Api/DevicesEndpoints.cs` | 54 | `GET /devices` | `…DeviceClients.Read` — group |
| `src/Identity/Api/KiosksEndpoints.cs` | 33 | `POST /kiosks/enroll` | `…KioskClients.Write` — group |
| `src/Identity/Api/KiosksEndpoints.cs` | 43 | `DELETE /kiosks/{clientId}` | `…KioskClients.Write` — group |
| `src/Identity/Api/KiosksEndpoints.cs` | 54 | `GET /kiosks` | `…KioskClients.Read` — group |
| `src/Identity/Api/WebhookRotationEndpoints.cs` | 39 | `POST /webhook-integrations/{name}/rotate` | `Scope.Sse.Webhooks.Write` — group |
| `src/Identity/Api/WebhookRotationEndpoints.cs` | 54 | `GET /webhook-integrations` | `Scope.Sse.Webhooks.Write` — group |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 30 | `POST /overlays` | `Scope.Sse.Overlays.Write` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 40 | `GET /overlays/{overlayIdentifier:guid}` | `Scope.Sse.Overlays.Read` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 47 | `GET /overlays` | `Scope.Sse.Overlays.Read` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 54 | `POST …/revisions/{n}/publish` | `Scope.Sse.Overlays.Write` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 66 | `POST …/revisions/{n}/archive` | `Scope.Sse.Overlays.Write` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 76 | `POST …/draft` | `Scope.Sse.Overlays.Write` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 88 | `PATCH …/revisions/{n}` | `Scope.Sse.Overlays.Write` — own chain |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 100 | `POST …/revisions/{n}/revert` | `Scope.Sse.Overlays.Write` — own chain |

Three contexts, five files. Identity is 8, OverlayDesigner is 8, Audit is 1.

---

## Three producers of 403, not one

Spec 075's first lesson: *"which statuses can this route return" is not one
question*. Its 409 had four producers and a register naming one of them
misclassified nine routes. So: established before counting.

1. **Scope policy failure.** `.RequireAuthorization(<Scope constant>)` selects a
   policy registered by `RequireScopeExtensions.AddScopePolicies`, which is
   `RequireAuthenticatedUser()` plus an assertion that the `scope` claim
   contains the target token (or the `sse.management` legacy bundle, for every
   scope but `sse.events.publish`). An authenticated principal failing that
   assertion is **forbidden, not challenged** — 403. **Reaches all 54.**
2. **Fab-group refusal.** `IFabAuthorizationGuard.EnsureAccessAsync` throws
   `FabAuthorizationException`; `FabAuthorizationExceptionHandler` writes
   `403 RESOURCE_FAB_NOT_AUTHORIZED`. Registered unconditionally in
   `AddBearerAuthentication`, which all nine `Api/Program.cs` files call.
3. **Handler refusal carrying the status.** A `Result` failure whose
   `ApiError.Status` is `HttpStatusCode.Forbidden` (ADR-0089) rendered onto the
   wire. One route today: `POST /streams/authorize`, via
   `AuthorizeWhepErrors.Forbidden` — and it is `AllowAnonymous`, so producer 1
   cannot reach it and producer 3 is the only reason it declares 403.

### The mechanism of the omission, which is worth writing down

Every existing 403 declaration in `src/` is justified, in a comment beside it, by
producer **2**:

> `src/CameraCatalog/Api/CameraEndpoints.cs:36` — *"403 became reachable when fab
> resolution landed (spec 015 T024)"*

and the same sentence, with a different spec number, in EventIngestion,
LayoutComposition, StreamDistribution, SystemVariables and WebhookIntegrations.
Producer 1 has been reachable on every scoped route since spec 008 and is named
in none of them.

So the gap falls out exactly where producer 2 is absent — **OverlayDesigner's 8,
which use no fab guard anywhere in `src/OverlayDesigner/Api`** — plus the nine
places where producer 2 *is* present and the declaration still never followed:
Identity's 8 and Audit's 1. `src/Identity/Api/WebhookRotationEndpoints.cs:136`
even carries the sentence

> *"…instead of the 403 they have earned."*

six lines from a chain that declares 400, 409, 412, 428 and 502, and no 403.
Nothing was reasoning about producer 1; the declarations propagated by imitation
from the specs that introduced fab resolution, and stopped where those specs
stopped.

### Does every scoped route *necessarily* produce 403?

Spec 075's second lesson: a false declaration is the same defect with the sign
flipped, so *"if every caller necessarily holds the scope, does the route still
need the declaration?"* must be answered rather than assumed.

**Yes, all 54 can produce it.** The nearest thing to a universal scope is the
`sse.management` legacy bundle, which satisfies every policy but
`sse.events.publish`. No principal necessarily holds it:
`tests/Integration.Tests/SystemVariables/VariableReadScopeIntegrationTests.cs`
mints a real `scenario-simulator` service account holding eleven scopes, neither
the bundle nor `sse.variables.read`, and observes 403. Scopes are granted
per-client in the realm; there is no fallback policy and no default policy
anywhere in `src/`. Declaring 403 on all 54 makes the document **true**, not
merely fuller.

---

## 403, not 401 — verified, not assumed

Declaring the wrong one is worse than declaring neither, so this is settled from
two directions.

**Mechanism.** ASP.NET's `PolicyEvaluator` challenges (401) only when the
*authentication* result did not succeed; an authenticated principal whose policy
requirements fail is forbidden (403). Both policy requirements here —
`RequireAuthenticatedUser()` and the scope assertion — sit behind a successful
bearer authentication, so a wrong-scope caller lands on the forbid branch.

**Observation.** `VariableReadScopeIntegrationTests` asserts
`HttpStatusCode.Forbidden` **exactly**, on the real Aspire stack, and pairs it
with a 200 from the admin client so that a stack refusing everybody cannot pass
as a successful refusal. Its own doc states the discrimination this spec needs:
a mis-minted token 401s, a wrong-scope token 403s.

**Generalisation.** That observation is on three `/system-variables` reads. It
generalises to all 54 as a *registration* fact rather than an extrapolation: one
`AddScopePolicies(Scope.All)` call in
`src/ServiceDefaults/AuthenticationDefaults.cs:90`, reached by all nine APIs
through `AddBearerAuthentication`, builds every policy the same way. There is no
second policy shape to behave differently.

**So:** the antecedent is a scope, the consequent is **403 only**. No 401 is
added by this spec; the two anonymous routes keep the declarations they have.

---

## `/hubs/layouts`

`app.MapHub<LayoutLifecycleHub>("/hubs/layouts")` in
`src/LayoutComposition/Api/Program.cs`, already the single row in
`EndpointScopeDeclarationTests`' `MappedOutsideTheChain` register, authorized by
`[Authorize(Policy = Scope.Sse.Layouts.Read)]` on the hub class.

**Does it need a 403 declaration? No, and it cannot have one.** It is a SignalR
hub, not a route handler: it has no fluent chain, no `.WithSummary`, and — the
decisive part — **no OpenAPI operation**. `ProducesProblem` writes into an
operation's responses; there is no operation to write into. The register already
records that a hub cannot carry a summary for the same reason.

**Can the guard see it? Yes, and it must say so out loud.** Silence would be the
`/snapshot` failure again — a route walked past because the reader did not parse
its shape. So the hub is excluded **by name and with the reason**, in the same
register that already holds it, rather than by falling outside a glob. The
register is read in both directions today; the new rule inherits that.

---

## User stories

### US-1 (P1) — A scoped endpoint declares the refusal its scope produces

**As** a client author generating code from the OpenAPI document,
**I want** every operation that sits behind a scope to declare `403`,
**so that** my generated client has a branch for the answer a wrong-scope token
actually gets, instead of an assertion that it cannot happen.

Independently shippable and observable end to end: build the document, look at
`POST /overlays`, see the `403`. One story, one slice.

**Out of scope, deliberately:** any change to *which* scope a route requires,
any change to fab authorization, any new scope, any 401 declaration, and the
register-only half of the declaration family (#2142 / spec 075's 409).

---

## Functional requirements

- **FR-001.** Every route-handler mapping under `src/*/Api/**/*Endpoints.cs`
  whose **effective** authorization resolves to a scope constant in the `Scope`
  catalogue declares `.ProducesProblem(StatusCodes.Status403Forbidden)` in its
  own fluent chain. Effective means: the mapping's own chain if it declares
  authorization, otherwise its `MapGroup`'s, bound by receiver name — the
  resolution `EndpointScopeDeclarationTests` already performs.
- **FR-002.** The rule runs **one way only**. A mapping that is not scoped is
  not forbidden to declare 403. `POST /streams/authorize` is `AllowAnonymous`
  and declares 403 correctly, from producer 3; a mirror would fail on it, and a
  mirror carved to exempt it would be a register. Same reasoning
  `PreconditionDeclarationTests` records for its 400.
- **FR-003.** **No `MapGroup` chain declares a 403.** Asserted separately and
  independently, because OpenAPI *does* inherit group metadata: if a group ever
  declared one, FR-001's demand for a per-chain line would be wrong, and the
  guard would fail correct code. Zero of 17 groups declare one today; the
  assertion keeps FR-001 sound rather than merely true.
- **FR-004.** The mapping walk's count of 403 declarations agrees with an
  **independent flat sweep** of `.ProducesProblem(StatusCodes.Status403Forbidden)`
  over the same directories. Both read 38 before the `src/` edits and 55 after
  — the assertion is the agreement, not either number. This is what keeps the
  walk
  honest — a declaration hoisted somewhere the walk cannot see makes the two
  disagree and fails the build, rather than quietly reading as "declares
  nothing". Property established by `PaginatedConsumerTests`, borrowed by
  `PreconditionDeclarationTests`, borrowed again here.
- **FR-005.** The route mapped outside the chain is excluded **by name, with the
  reason**, in the existing `MappedOutsideTheChain` register — which is already
  read in both directions, so a second such route fails the build rather than
  inheriting the exclusion.
- **FR-006.** The 17 declarations are added. Nothing else in those five files
  changes: no summary, no scope, no handler, no ordering beyond placing the new
  line among the existing `ProducesProblem` calls.
- **FR-007.** No new excuse mechanism. The existing A9 self-scan
  (`The_guard_offers_no_way_to_excuse_an_endpoint`) covers the new assertions for
  free, because they live in the file it scans.

---

## What the guard cannot do, stated before it is built

Written here so a green run is not read as more than it is. Each of these is a
real gap, not a hedge.

1. **It does not observe a 403.** It compares source against source. The only
   observation of the behaviour in the repository is
   `VariableReadScopeIntegrationTests`, on three routes. After this spec, 54
   routes declare a status that has been *observed* on 3 of them and *derived*
   for the other 51 from a shared policy registration.
2. **It does not read the emitted OpenAPI document.** `.ProducesProblem` in
   source is asserted; the generated JSON is never opened — by this guard or by
   either of its two siblings. If ASP.NET stopped honouring the call, all three
   would stay green. Naming this is the price of not building a document-reading
   guard in a slice this size; it is a candidate follow-up, not a defect of this
   one.
3. **It sees only the population it can parse.** 56 chain mappings plus one
   registered hub. The 56-pin and the outside-the-chain register bound that
   number; they do not remove the limit.
4. **It is single-producer.** Only producer 1 is an antecedent. A future
   anonymous route refusing with an `ApiError.Forbidden` and declaring nothing is
   invisible to it — exactly the `/snapshot` shape of #91, one producer along.
   Producer 2's population is a superset question that belongs to #2142's
   register half, not here.

---

## Acceptance scenarios

### Happy — the derived population agrees with the declarations

```gherkin
Given the 12 endpoint files under src/*/Api
When the guard resolves each of the 56 mappings' effective authorization
Then 54 resolve to a scope constant in the Scope catalogue
And every one of those 54 declares ProducesProblem(Status403Forbidden) in its own chain
And the flat sweep of that call finds the same number of sites the walk did
```

### Conflict — a scope inherited from a group is still a scope

```gherkin
Given GET /devices declares no authorization in its own chain
And the MapGroup it is written on declares Scope.Sse.Identity.DeviceClients.Read
When the guard resolves its effective authorization
Then the mapping is in the population
And omitting its 403 fails the build naming the file and line
```

*This is the scenario a guard written from the issue's own sentence would miss —
28 of the 54 are in the population only through it.*

### Bad request — a declaration in the group instead of the chain

```gherkin
Given someone moves the 403 onto the /overlays MapGroup to save eight lines
When the guard runs
Then FR-003 fails naming the group
And FR-001 fails naming all eight mappings
```

*Two failures, not one, and neither says "fixed". The document would in fact be
correct; the guard refuses the shape because a group-level declaration makes
FR-001 unsound for every future mapping added under it.*

### Auth — the population moves with the authorization, not with a list

```gherkin
Given GET /overlays has its .RequireAuthorization(Scope.Sse.Overlays.Read) removed
When the guard runs
Then the mapping leaves the 403 population in the same diff
And EndpointScopeDeclarationTests' existing completeness rule fails it for enforcing no scope
```

*The antecedent is read, not typed in. This is the scenario that separates a
derivation from a checklist, and it is the one to run by hand at phase 5.*

### No soft edge — a commented-out declaration is not credited

```gherkin
Given a chain whose ProducesProblem(Status403Forbidden) line is commented out
When the guard runs
Then the mapping is reported as undeclared
```

*Comments are blanked before the walk, as they already are for the scope read.*

### No soft edge — a new scoped endpoint cannot slip past

```gherkin
Given a new mapping is added under an existing scoped group with no 403
When the guard runs
Then the 56-pin fails, and FR-001 fails naming the new mapping
```

---

## Independent end-to-end test procedure

Docker-free throughout (ADR-0103); the fast lane is enough for every step but
the last, which is optional and needs the stack another issue is holding.

1. **Measure the gap without the guard.** From the worktree:
   ```sh
   grep -rc "ProducesProblem(StatusCodes.Status403Forbidden)" \
     src/Identity/Api/*Endpoints.cs src/OverlayDesigner/Api/OverlayEndpoints.cs
   ```
   Expect `0` for all four files before phase 4, and `3 3 2 8` after.
2. **Observe the red.** `dotnet test tests/Architecture.Tests -c Release
   --filter EndpointScopeDeclarationTests` before touching `src/`. Expect a
   failure naming **17** mappings across **5** files. Quote it in the PR.
3. **Green.** Same command after the 17 declarations. Expect pass.
4. **Prove the antecedent is derived, not listed** — the counterfactual
   (MEMORY: *prove a guard by counterfactual*). Delete one
   `.RequireAuthorization(Scope.Sse.Overlays.Read)` **and** its 403, run the
   guard. If FR-001 stays silent about that mapping — while the file's existing
   completeness rule fails it for enforcing no scope — the antecedent is read.
   If FR-001 still demands a 403 from a route that now requires no scope, the
   population is hard-coded somewhere and the derivation claim is false. Revert.
5. **Prove the flat cross-check bites.** Move one 403 declaration into a helper
   method the walk cannot see. Expect FR-004 to fail on the disagreement, not
   FR-001 alone. Revert.
6. **Build the document (optional, needs the stack).** Boot the OverlayDesigner
   API, `GET /openapi/v1.json`, confirm `POST /overlays` now lists a `403`
   response and did not before. This is the only step that reads the emitted
   document and the only one that closes gap 2 above; if the stack is
   unavailable, say so in the verification note rather than implying it ran.

---

## Phase 4a — how the colour is obtained

**Behaviour-changing → red.** The generated OpenAPI document gains a `403`
response on 17 operations. That is observable output changing, and §Testing
gives it the red obligation; ambiguity resolves to red anyway.

**The red is step 2 above**, run before any `src/` edit, quoted verbatim into
the PR body per ADR-0139.

**The trap this class keeps setting, and why this instance escapes it.** A guard
that reads the source and asserts a `.ProducesProblem(403)` line exists proves
only that a file was edited — it is a diff-checker wearing a build failure's
clothes. What makes this one a derivation is that **the antecedent is read too**:
which mappings are in the population is resolved from the source, chain then
group, and the constant is resolved against `Scope`'s members by reflection. No
route, no scope literal and no count of 17 is typed into the test. Scenario
*Auth* above and step 4 of the procedure are the checks that this is so, and they
are the two that must actually be run — a green FR-001 alone does not
distinguish the two kinds of guard.

---

## Latency budget

**N/A.** No leg of the event-to-overlay path is touched. `ProducesProblem` is
build-time OpenAPI metadata; it adds no work to any request, and the routes
involved are not on the streaming or overlay-state path. Constitution §IV
unaffected.

---

## Non-functional

- **Coverage (ADR-0065).** Five `src/` edits, each a single added chained call
  with no branch. Domain/Application/Shared ratios unmoved.
- **Code metrics (ADR-0084).** Each edited file grows by 1–8 lines and all five
  stay well under the 300-LOC limit — measured today: `OverlayEndpoints.cs` 124
  (+8 → 132), `DevicesEndpoints.cs` 213 (+3), `KiosksEndpoints.cs` 209 (+3),
  `WebhookRotationEndpoints.cs` 212 (+2), `AuditEndpoints.cs` 183 (+1). `S104` is
  in the test projects' `NoWarn` list, so extending the 1825-line guard is not a
  new suppression.
- **Boundaries (ADR-0044, NetArchTest).** No project reference changes. The
  guard lives in `Architecture.Tests`, which already references the Api
  assemblies it reflects over.
- **No Docker (ADR-0103).** Steps 1–5 run in the fast lane.

---

## Assumptions, marked

- **A1.** The new assertions extend `EndpointScopeDeclarationTests` rather than
  opening a fourth file. *Reason:* the antecedent resolution — including group
  inheritance, which is 28 of the 54 — already exists there and nowhere else,
  and three files in `tests/Architecture.Tests/` already carry their own copy of
  `MaskLiterals`/`StatementEnd`. A fourth copy to re-derive a fact one file
  already has is the wrong trade. *Tension, stated so a reviewer can overrule:*
  the file's stated rule is "an endpoint names the scope it needs", and this is
  a different sentence about the same antecedent — the precedent for grouping by
  antecedent rather than by status is `PreconditionDeclarationTests`, which holds
  both 428 and 400 because both come off one call site.
- **A2.** Extracting a shared endpoint reader across the four guards is **not**
  done here. It is a real duplication (three copies today, and this spec adds a
  fourth consumer without adding a fourth copy) and it is a behaviour-preserving
  refactor of test infrastructure — a separate issue, not a drive-by (ADR-0036).
- **A3.** The 17 declarations are placed among each chain's existing
  `ProducesProblem` calls in status order where one exists. Cosmetic; no rule
  requires it.
- **A4.** Producer 2's own population — which routes call the fab guard, and
  whether each declares 403 — is **not** measured or fixed here. Nine of the 17
  have it and are fixed by producer 1's rule incidentally; whether any route has
  producer 2 *without* a scope is unasked. That question is register-shaped and
  belongs to #2142.

---

## What this leaves #2142

#2142 exists to draw the line between derivable and register-only. This spec is
the evidence that the line is real, and it moves it.

**Derivable — resolvable from `src/*/Api` source alone:**

| Status | Antecedent | How far the reader must go | Guard |
|---|---|---|---|
| **403** | effective authorization is a scope | chain, then its `MapGroup` — **no handler body** | this spec |
| **428** | handler reads `If-Match` | chain → mapped handler's body | `PreconditionDeclarationTests` |
| **400** (malformed precondition) | same call site as the 428 | chain → mapped handler's body | same file |

403 sits **furthest inside** the line: it needs no body scan at all, which is
why the rule is one grep-shaped fact per mapping once inheritance is resolved.

**Register-only — the antecedent crosses the Application boundary:**

| Status | Why it cannot be derived | Guard |
|---|---|---|
| **409** | four producers, discriminated by an `ApiError` status, an `Add`-vs-mutate call, an `.IsUnique()` index and idempotency wiring, three to four hops away | `ConcurrencyConflictDeclarationTests` (typed in, 2026-09-05) |

**So #2142's remaining job is the register half plus the meta-rule**: state the
test — *is the antecedent decidable from the mapping's chain, its group, and the
body of the handler it names?* — and, for statuses that fail it, standardise the
register shape the 409 guard invented (both directions, a named mechanism per
row, an open issue per excuse). It should also inherit gap 2 above: **no member
of this family reads the emitted OpenAPI document**, and until one does, the
whole family asserts that source says a thing, not that the document does.

**And one correction #2142 should carry forward:** the derivable/register-only
split is not the same as "visible in the mapping's own chain". 403 is derivable
and 52% of its population is not in its own chain. The right test is *resolvable
by the reader*, and what makes it resolvable here is that a guard already
resolves it.

---

## Gate (Phase 1)

Spec reviewed; no `[NEEDS CLARIFICATION]` remains. The one figure the issue asked
to be re-counted has been: **Identity is 8, the population is 17, of 54 scoped
mappings, of 56.**

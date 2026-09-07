# Spec 091 — Six routes, not two

**Issue:** #2114 — *Two Identity DELETE endpoints answer a 400 their contract denies*
**Branch:** `fix/2114-a-malformed-route-value-declares-its-400`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0070 (Minimal APIs only — the surface the declaration lives on),
ADR-0089 (`ApiError` carries the HTTP status, which is how a *handler-returned*
status differs from a *declared* one), ADR-0038 / ADR-0046 / ADR-0066
(hand-written value objects whose `.From(...)` throws — the producer in three of
the six), ADR-0105 (`Ensure.That` is what throws inside `.From`), ADR-0139 (rules
that fail the build rather than conventions a reviewer remembers), ADR-0084 (code
metrics), ADR-0052 / ADR-0103 (xUnit + Shouldly; no Docker in the fast lane),
ADR-0065 (coverage gates), ADR-0109 (parallel markers), ADR-0036 (smallest
change, no drive-by), ADR-0037 (phased workflow), ADR-0087 (each commit builds on
its own), ADR-0144 (autonomous lane — no ADR is written here).

**Constitution:** §VIII *Safe by Default at Trust Boundaries* — the section that
makes the endpoint the place a malformed value is refused. §Testing — "the test
is written first, is **observed failing**, and that failure is quoted in the PR
body."

**No ADR is written or amended by this spec.** Declaring a status the locked
model already produces is implementation, not decision — the same call specs 072,
075 and 085 recorded. ADR-0144 bars the lane from writing one in any case.

---

## The issue as filed, and what survives contact with the repository

The issue makes three claims. The substance is right, the arithmetic is wrong in
the direction it warned about, and **both of its line ranges point at the wrong
code**.

**Right: the two named endpoints answer an undeclared 400.** `DELETE
/devices/{clientId}` and `DELETE /kiosks/{clientId}` declare 200 / 403 / 404 /
409 and no 400, and both handlers return one for a malformed client id. Verified
on this branch.

**Wrong: both line ranges.** The issue cites `DevicesEndpoints.cs:43-48` and
`KiosksEndpoints.cs:43-48` as "declares". Lines 43–48 in both files are the
**`POST /register` / `POST /enroll` chain**, which already declares its 400 on
line 43. A reader who followed the citation would find the endpoint correct and
close the issue. The declarations that are actually missing are at **50–56** in
both files. The "returns" ranges are off too — `199-204` and `196-201` against
actual `202-213` (Devices) and `198-209` (Kiosks). The right lines, measured:

| Claim | Filed | Actual |
|---|---|---|
| Devices — chain that omits 400 | `:43-48` | **`:50-56`** (`MapDelete` at 50) |
| Devices — the 400 return | `:199-204` | **`:202-213`** (`Disable` starts at 198) |
| Kiosks — chain that omits 400 | `:43-48` | **`:50-56`** (`MapDelete` at 50) |
| Kiosks — the 400 return | `:196-201` | **`:198-209`** (`Disable` starts at 194) |

**Wrong, and the issue said to expect it: two is not the population.** It is
**six**, across **six files in five bounded contexts**. The issue's own warning —
"two endpoints is what this review happened to look at, not a census" — holds
against itself.

**Right, and this spec's spine: the rule is derivable.** The issue guessed that
"a 400 raised in the endpoint's own file is a much shorter hop" than spec 075's
409. It is, and the measurements below say so rather than assuming it.

---

## The census, measured

Worktree `D:\Github\wt-2114` at `45be837d`, 2026-09-07. Every
`.Map(Get|Post|Put|Patch|Delete)("route", Handler)` site under `src/*/Api/**`,
with the handler body resolved by method-group name within the same Api project
(partial classes span three files in EventIngestion, LayoutComposition and
OverlayDesigner), cross-checked against a flat `grep` sweep.

| Fact | Count |
|---|---|
| Files under `src/*/Api` containing route mappings | **12** |
| Route-handler mappings in them | **56** |
| Mappings using a **bare method-group** reference (not a lambda) | **56 of 56** |
| Handler bodies the reader failed to resolve | **0** |
| Routes mapped outside the chain (`MapHub`) | **1** (`/hubs/layouts`, Program.cs) |
| Mappings whose **own handler body** can return 400 | **43** |
| … of which **declare** a 400 | 37 |
| … of which **do not** — **the population** | **6** |
| Mappings that declare a 400 | 49 |
| Mappings that declare none | 7 (the six, plus `POST /streams/authorize`, which produces none) |
| Mappings reaching a 400 **only** through an Api-project helper, undeclared | **0** |
| Mappings that declare a 400 with no producer visible even one hop out | **1** (`GET /audit`) |
| `.ProducesProblem(400)` sites under `src/*/Api` | 42 |
| `.ProducesValidationProblem(400)` sites under `src/*/Api` | 7 |
| Both spellings together — the sweep total | **49** (→ **55** after this change) |
| Route **groups** declaring any status | **0** of 17 |

### The six

| # | Route | File : line | Producer |
|---|---|---|---|
| 1 | `GET /audit/{auditIdentifier:guid}` | `AuditObservability/Api/AuditEndpoints.cs:46` | `AuditEventIdentifier.From` throws → `AUDIT_INVALID_INPUT` |
| 2 | `DELETE /devices/{clientId}` | `Identity/Api/DevicesEndpoints.cs:50` | `ClientId.From` throws → `DEVICE_INVALID_INPUT` |
| 3 | `DELETE /kiosks/{clientId}` | `Identity/Api/KiosksEndpoints.cs:50` | `ClientId.From` throws → `KIOSK_INVALID_INPUT` |
| 4 | `GET /layouts/{layoutIdentifier:guid}` | `LayoutComposition/Api/LayoutEndpoints.cs:55` | `== Guid.Empty` → `LAYOUT_INVALID_INPUT` |
| 5 | `GET /overlays/{overlayIdentifier:guid}` | `OverlayDesigner/Api/OverlayEndpoints.cs:48` | `== Guid.Empty` → `OVERLAY_INVALID_INPUT` |
| 6 | `GET /streams/{cameraIdentifier:guid}` | `StreamDistribution/Api/StreamEndpoints.cs:34` | `== Guid.Empty` → `STREAM_INVALID_CAMERA` |

Every one of the six refuses **the route value**, in the handler's first
statement block, before anything else runs.

### Two producer shapes, and why that matters

The six split evenly, and a guard keyed on either shape finds three:

```csharp
// Shape A — 1, 2, 3.  The value object's own guard throws (ADR-0105).
try { parsed = ClientId.From(clientId); }
catch (ArgumentException ex)
{ return Results.Problem(title: "DEVICE_INVALID_INPUT", detail: ex.Message,
      statusCode: StatusCodes.Status400BadRequest); }

// Shape B — 4, 5, 6.  A hand-written check ahead of a .From that would throw.
if (overlayIdentifier == Guid.Empty)
{ return Results.Problem(title: "OVERLAY_INVALID_INPUT",
      detail: "overlayIdentifier must be a non-empty Guid.",
      statusCode: StatusCodes.Status400BadRequest); }
```

A guard written from the issue's sentence — "`ClientId.From(clientId)` throws" —
matches shape A only and reports **three**. A guard written from
`catch (ArgumentException)` also matches three. The antecedent that matches all
six is neither: it is the **status token in the handler body**.

### The `:guid` constraint is not a defence

Four of the six carry a `:guid` route constraint, and it is tempting to read that
as "malformed values never reach the handler". It is wrong.
`00000000-0000-0000-0000-000000000000` **satisfies** the constraint and **fails**
the domain check, so it reaches the handler and earns the 400. The constraint
filters non-Guid text (which becomes a routing 404); it does not filter the empty
Guid.

This is also why the constraint route is closed to us as a fix: typing
`{clientId}` would turn a 400 into a 404 and change the product to make the
contract true. Refused (see *Not in scope*).

---

## Derivable, not register-only

**Verdict: derivable.** Four measurements carry it, and each would have to fail
for the answer to be "register".

1. **The handler is always findable.** 56 of 56 mappings pass a bare
   method-group name; there are **zero inline lambdas** in `src/*/Api`, and the
   reader resolved **56 of 56** bodies. Spec 075's 409 could not be derived
   because the antecedent lay three or four hops *inside Application*; here it
   lies zero hops away, in a method in the same project as its own `MapDelete`.
2. **The reader already exists and already does exactly this.**
   `tests/Architecture.Tests/PreconditionDeclarationTests.cs` (spec 072, #2101)
   resolves a mapping's handler body by method-group name across an Api
   project's partial classes and scans that body for an antecedent. It even
   already owns the 400 *declaration* regex, in both spellings. Nothing new has
   to be invented; a second antecedent has to be read.
3. **One hop adds nothing.** Following calls from the handler into other methods
   of the same Api project (depth 3, qualified calls included) adds **zero**
   routes to the population. The direct-body rule and the one-hop rule return
   the same six, so the guard need not decide a hop depth to be correct on
   today's corpus. That question belongs to #2142; it is not forced here.
4. **No group declares anything.** All 17 `MapGroup` chains carry only
   `RequireAuthorization` / `WithTags`. OpenAPI inherits group metadata, so a
   group-level declaration would make a per-chain demand unsound; there is none,
   and the guard must assert that rather than assume it.

**The exact antecedent a guard keys on**, stated so #2142 can inherit it verbatim:

> Within the mapping's resolved handler body — comments blanked, string and char
> literals masked — the token `StatusCodes.Status400BadRequest`, or a call to
> `Results.BadRequest(` or `.ValidationProblem(`.

**The consequent:** within the mapping's own chain span (from `.MapX(` to the
statement's terminating `;`), a call matching
`\.Produces(?:Validation)?Problem\(\s*StatusCodes\.Status400BadRequest\s*\)`.

### The rule runs one way only

`GET /audit` declares a 400 and its handler body produces none. That is
**correct**: its signature takes `[FromQuery] Guid? actor`, `DateTimeOffset?
since`, `DateTimeOffset? until` and `int? pageSize`, and ASP.NET's own model
binding answers 400 for a malformed one — a producer no source scan of
`src/*/Api` can see. A mirror rule (*declares ⇒ must produce*) would fail correct
code on day one. Same structural lesson as spec 085's `POST /streams/authorize`,
different cause.

### Both declaration spellings count

Seven chains declare their 400 as `.ProducesValidationProblem(...)` rather than
`.ProducesProblem(...)`. A guard matching only the latter reports **13**, not 6 —
seven of them correct code. `PreconditionDeclarationTests` learned this already
and says so in its own doc; this spec does not get to relearn it.

---

## User stories

### US-1 (P1) — An integrator's client is generated from a contract that is true

**As** an integrator generating a client from `openapi/v1.json`,
**I want** every status a route can answer to appear in its operation,
**so that** a malformed identifier surfaces as a typed 400 rather than an
unhandled response my generated client has no branch for.

Today the generated client for `DELETE /devices/{clientId}` has branches for 200,
403, 404 and 409. A typo in the client id returns 400 — a shape it cannot
represent — so it falls through as an unexpected error, and the message that
would have told the caller what was wrong is thrown away.

**This is the whole slice.** One user story, six one-line declarations, one
guard. Nothing smaller is observable end to end; nothing larger belongs here.

### US-2 (P2) — The next endpoint author cannot repeat it

**As** the author of the seventh route that refuses a route value,
**I want** the build to fail if I omit the declaration,
**so that** the omission is caught in seconds rather than in a phase-6 review of
an unrelated issue — which is how all six of these were found, one at a time,
across five specs.

US-2 is delivered by the same guard that produces US-1's red. It is not separable
work.

---

## Acceptance scenarios (Gherkin)

### Happy — the status appears in the contract

```gherkin
Scenario: The generated document declares the refusal the handler can answer
  Given the OverlayDesigner API is running
  When I GET /openapi/v1.json
  Then the operation for "GET /overlays/{overlayIdentifier}" lists responses
       200, 400, 403 and 404
```

### Conflict — the guard refuses an omission

```gherkin
Scenario: A new route that refuses its route value and does not say so
  Given a handler whose body returns Results.Problem(statusCode: 400)
    And its mapping chain declares 200 and 404 only
  When the Architecture.Tests project runs
  Then the run fails
   And the message names the file, the line, the verb and the full route
   And the message names what the chain declares today
```

### Bad request — the behaviour that is being declared, unchanged

```gherkin
Scenario: A malformed client id is refused, exactly as before
  Given a caller holding sse.identity.devices.write
  When it sends DELETE /devices/-bad
  Then the response is 400
   And the problem title is "DEVICE_INVALID_INPUT"
   And no device was disabled
```

`-bad` is malformed because `ClientId` requires a leading letter or digit
(`src/Identity/Domain/RegisteredClient/ClientId.cs`).

```gherkin
Scenario: The empty Guid passes the route constraint and fails the domain check
  Given a caller holding sse.overlays.read
  When it sends GET /overlays/00000000-0000-0000-0000-000000000000
  Then the response is 400
   And the problem title is "OVERLAY_INVALID_INPUT"
```

```gherkin
Scenario: A non-Guid route value is still a routing 404, not a 400
  Given a caller holding sse.overlays.read
  When it sends GET /overlays/not-a-guid
  Then the response is 404
   And no handler ran
```

The third scenario is the one that keeps the fix honest: it is the behaviour a
typed `{clientId}` parameter would impose on the *other* two routes, and the
reason that fix is refused.

### Auth — the refusal order is unchanged

```gherkin
Scenario: An unscoped caller is forbidden before the value is parsed
  Given a caller without sse.identity.devices.write
  When it sends DELETE /devices/-bad
  Then the response is 403
   And the malformed value was never parsed
```

The scope policy runs in the pipeline, ahead of the handler. This change adds a
declaration and moves nothing, so 403 still precedes 400. If a phase-5 run
observes 400 here instead, the change did more than it claims.

---

## Independent end-to-end test procedure

Steps 1–4 are Docker-free and are the gate. Steps 5–6 need the Aspire stack,
which **another track holds as of 2026-09-07**; if it is unavailable, say so in
the verification note rather than implying it ran.

1. On unmodified `src/`, run the new guard. It must fail, naming **exactly six**
   mappings across six files in five contexts (see *Phase 4a*).
2. Add the six declarations. Re-run: green, and the sweep cross-check reads
   **55 = 55**.
3. `dotnet build -c Release` — clean, analyzers included.
4. `dotnet test tests/Architecture.Tests -c Release` — the whole project,
   confirming `PreconditionDeclarationTests`,
   `ConcurrencyConflictDeclarationTests`, `EndpointScopeDeclarationTests` and
   `PrimitiveBoundaryTests` are undisturbed.
5. Boot Identity via the AppHost. Mint a token with
   `sse.identity.devices.write`. `DELETE /devices/-bad` → **400**,
   `DEVICE_INVALID_INPUT`. `GET /openapi/v1.json` → the `delete` operation on
   `/devices/{clientId}` now carries a `400` response.
6. Boot OverlayDesigner. `GET /overlays/00000000-0000-0000-0000-000000000000` →
   **400**; `GET /overlays/not-a-guid` → **404**. The document carries `400`.

**No member of this guard family reads the emitted OpenAPI document.** All four
read `src/*/Api` text. Step 5–6's document check is the only thing that closes
that gap, and it is manual. Recorded, not fixed — it belongs with #2142.

---

## Locked tech choices this spec uses, unchanged

Minimal APIs and their fluent metadata (ADR-0070); `Results.Problem` with an
`ApiError`-shaped title/detail (ADR-0089); value objects whose `.From` guards with
`Ensure.That` (ADR-0038, ADR-0046, ADR-0066, ADR-0105); xUnit + Shouldly with no
Docker in the fast lane (ADR-0052, ADR-0103); a rule that fails the build rather
than a convention a reviewer remembers (ADR-0139).

## Latency budget

**N/A.** No leg of the event-to-overlay path is touched (constitution §IV). Six
`.ProducesProblem` calls run once at startup, write into a document, and are not
on any request path — `GET /overlays/{id}` is an operator-console read, not the
overlay-state leg.

---

## Not in scope

Named so they are refused deliberately rather than forgotten.

- **Typing the route parameter.** `{clientId:guid}`, or a `TryParse`-bound
  `ClientId`, would make the 400 unreachable — by turning it into a routing 404.
  That fixes the contract by changing the product, and it would silently change
  the answer four callers already receive. Explicitly refused by the brief and
  by ADR-0036.
- **The mirror rule** (*declares 400 ⇒ must produce one*). Fails `GET /audit`,
  which correctly declares the 400 its `[FromQuery] Guid?` binding produces.
- **A general rule for the whole defect class.** #2142 owns the line between
  derivable and register-only across all five instances. This spec measures one
  instance and hands #2142 the antecedent, the one-hop-adds-zero result and the
  one-way constraint. It does not draw the line.
- **Extracting the shared endpoint reader.** Four guards now carry their own
  `RepositoryRoot` / `MaskLiterals` / `StatementEnd`; this spec adds a fifth
  consumer. Spec 085 deferred the extraction as a behaviour-preserving refactor
  of test infrastructure with a characterisation colour, and that judgement
  stands.
- **A guard that reads the generated OpenAPI JSON.** The gap all five guards
  share. #2142.
- **The three EventIngestion / WebhookIntegrations reads** that reach a 400
  through `EventIngestionFabResolution.ResolveReadFabsAsync`. They all declare
  it; there is nothing to fix, and their producer is the #2101 shape, already
  covered.
- **Any change to a handler, a status, an error code, a scope or a realm.**
  Six added lines in `src/`, and nothing else.
- **A new ADR.** Nothing is decided here.

---

## Gate (Phase 1)

No `[NEEDS CLARIFICATION]` remains. Two assumptions are marked rather than
buried:

- **Assumption 1.** "Can return a 400 for a malformed route value" is read as
  "the handler body returns a 400 before doing anything else". All six do, so the
  narrower and wider readings coincide today. If a seventh route returns a 400
  from deep in its body for an unrelated cause, the guard demands a declaration
  it also deserves — a wider net, not a wrong one.
- **Assumption 2.** The framework's own model-binding 400 is out of the guard's
  sight and stays there. This is what forces the one-way rule, and it is the
  single largest residual: a route with *only* a framework producer and no
  declaration would pass this guard. `GET /audit` is the one route that would
  have exposed that, and it already declares. Recorded for #2142.

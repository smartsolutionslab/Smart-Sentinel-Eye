# Tasks — Spec 091, six routes, not two

**Phase:** 3 (Tasks) — ADR-0037. Gate: tasks atomic; the feature's issue (#2114)
on Project #13.

**Engineer:** **backend**, throughout. Every file is C#. Nothing frontend,
nothing infra, nothing Aspire, no migration, no realm edit.

**Phase 4a colour:** **red** (behaviour-changing — the generated OpenAPI document
gains a `400` on six operations, and the guard that demands it does not exist
yet). The concrete red is T005; read it before writing a line of the guard.

**Docker:** not needed for T001–T009. T010 needs the Aspire stack and is
explicitly allowed to report "not run".

`[P]` marks tasks owning disjoint files (ADR-0109).

---

## Foundational — blocks everything

The guard must exist and must fail before a single declaration is added. A
declaration added first makes the red unobtainable, and an unobserved red is a
phase-4 failure, not a shortcut.

### T001 — New guard file with a class doc that states its own population

`tests/Architecture.Tests/RouteValueRefusalDeclarationTests.cs` (new)

Record, in the class doc, and measure nothing that is not measured on this
branch:

- **the antecedent, in words:** within the mapping's resolved handler body —
  comments blanked, literals masked — the token `StatusCodes.Status400BadRequest`,
  or a call to `Results.BadRequest(` or `.ValidationProblem(`;
- **the consequent:** the mapping's own chain span declares 400 in **either**
  spelling — `.ProducesProblem(...)` (42 sites) or `.ProducesValidationProblem(...)`
  (7 sites). A guard matching only the first reports 13, seven of them correct
  code;
- **the measured population** — 56 mappings in 12 files; 43 whose handler body
  can return 400; **6 undeclared, across 6 files in 5 contexts**;
- **the two producer shapes**, and that a guard keyed on either finds three:
  shape A is `try { X.From(routeValue) } catch (ArgumentException) { … 400 }`
  (Audit, Devices, Kiosks); shape B is `if (routeValue == Guid.Empty) { … 400 }`
  (Layout, Overlay, Stream);
- **that the rule runs one way.** `GET /audit` declares a 400 its
  `[FromQuery] Guid? actor` / `DateTimeOffset?` / `int?` binding produces inside
  ASP.NET, where no source scan can see it. A mirror rule fails correct code;
- **that the `:guid` constraint is not a defence** —
  `00000000-0000-0000-0000-000000000000` satisfies it and still earns the 400,
  which is why four of the six carry a constraint and are still in the
  population;
- **why this is a new file and not an addition to `PreconditionDeclarationTests`**
  — the populations are disjoint (none of the six touches `ConcurrencyHeaders`),
  and a rule whose subject its filename denies is a rule nobody finds. Name the
  cost: a fifth copy of the reader helpers, and that the extraction is deferred
  with spec 085's reasoning.

**Done when:** the doc states the population and the one-way direction, and
carries no figure that was not measured on this branch.

**Depends on:** nothing. **Blocks:** T002–T005.

### T002 — Copy the reader from `PreconditionDeclarationTests`

`tests/Architecture.Tests/RouteValueRefusalDeclarationTests.cs`

Copy — do not reinvent (ADR-0036) — `RepositoryRoot()`, `ApiSourceFiles()`,
`MaskComments`, `MaskLiterals`, `StatementEnd`, and the handler-body resolution
that maps a bare method-group name to exactly one method body within the same Api
project's partial classes. That resolution is the load-bearing part and it is
already proven against this corpus.

Two things the copy must keep:

- **the glob is `src/*/Api/**/*.cs`**, wider than `*Endpoints*.cs`. Handlers live
  in `EventsEndpoints.Reads.cs`, `LayoutEndpoints.Queries.cs`,
  `OverlayEndpoints.Queries.cs`, and a helper producer lives in
  `EventIngestionFabResolution.cs`. A narrower glob drops bodies and G4 fires;
- **the chain span** is `[.MapX( … terminating ;]`, bracket-depth aware, with
  comments blanked and literals masked. `StreamEndpoints.cs:34` has a
  three-line concatenated `WithSummary` inside its chain; a line-based reader
  gets it wrong.

**Done when:** the file compiles and the reader returns **56** mappings across
**12** files, with **0** unresolved handler bodies.

**Depends on:** T001. **Blocks:** T003–T005.

### T003 — G1: a route that refuses its route value declares the refusal

`tests/Architecture.Tests/RouteValueRefusalDeclarationTests.cs`

A per-file `[Theory]` over the endpoint files. Population: mappings whose
resolved handler body matches the antecedent. Demand: the chain span matches

```
\.Produces(?:Validation)?Problem\(\s*StatusCodes\.Status400BadRequest\s*\)
```

Failure message names file, line, verb, the full route (group prefix + own
route), and what the chain declares today.

**Done when:** run against unmodified `src/`, it fails naming **exactly six**
mappings in **six** files. See T005 — that number is the gate, not a detail.

**Depends on:** T002.

### T004 — G2, G3, G4: the three assertions that stop a false green

`tests/Architecture.Tests/RouteValueRefusalDeclarationTests.cs`

Three `[Fact]`s. Each exists because without it G1 can pass while checking
nothing.

**G2 — no route group declares a 400.** Zero of the 17 `MapGroup` chains declare
any status today. OpenAPI *inherits* group metadata, so a group-level declaration
would make G1's per-chain demand unsound and fail correct code. The failure
message must say the shape is refused and why — not that an endpoint is wrong.

- **Done when:** green on unmodified `src/`; and, checked by hand, red when a
  `.ProducesProblem(StatusCodes.Status400BadRequest)` is temporarily added to the
  `/overlays` group at `OverlayEndpoints.cs:27`.

**G3 — the walk agrees with an independent flat sweep.** Mapping-walk
declarations plus group-walk declarations must equal a flat sweep of both
spellings over `ApiSourceFiles()`. Both sides read **49** before the change and
**55** after. This is what stops a declaration hoisted somewhere the walk cannot
see from reading as "declares nothing".

- **Done when:** green on unmodified `src/` at 49 = 49.

**G4 — every mapping is read rather than skipped.** Every `.MapX("route", X)`
site resolves to exactly one handler body. **56 of 56** today; there are zero
inline lambdas in `src/*/Api`. Without this, a mapping the reader drops leaves
the population silently and G1 goes green over it.

- **Done when:** green on unmodified `src/` at 56 = 56, and the failure message
  names the mapping it could not resolve.

**Depends on:** T002. **May run alongside** T003 (same file, so not `[P]`).

---

## The red run — the gate between the halves

### T005 — Observe the red, and check it is the *right* red

```sh
dotnet test tests/Architecture.Tests -c Release \
  --filter RouteValueRefusalDeclarationTests
```

Run **before any `src/` edit**. Capture the output verbatim; it goes in the PR
body (ADR-0139), and it is the only form of this evidence a later reader can
check.

**The failure must name six mappings across six files in five contexts:**

```
GET    /audit/{auditIdentifier:guid}        AuditEndpoints.cs:46
DELETE /devices/{clientId}                  DevicesEndpoints.cs:50
DELETE /kiosks/{clientId}                   KiosksEndpoints.cs:50
GET    /layouts/{layoutIdentifier:guid}     LayoutEndpoints.cs:55
GET    /overlays/{overlayIdentifier:guid}   OverlayEndpoints.cs:48
GET    /streams/{cameraIdentifier:guid}     StreamEndpoints.cs:34
```

Four wrong reds to recognise, because each looks like success:

| Wrong red | What it means |
|---|---|
| **2 mappings, both Identity** | the antecedent narrowed to `ClientId.From` or to the issue's own two files. *This is the dangerous one* — 2 is the issue's headline number, so a wrong red reads as the right one. |
| **3 mappings** | the antecedent is one producer shape. `catch (ArgumentException)` finds A (Audit, Devices, Kiosks); `== Guid.Empty` finds B (Layout, Overlay, Stream). Both are half the population. |
| **13 mappings** | the declaration regex matched `.ProducesProblem` only and called the seven `.ProducesValidationProblem` chains undeclared. Seven of the thirteen are correct code. |
| **green**, or **49 mappings** | the antecedent matched nothing, or it was inverted. |

Any of these: stop, fix the guard, re-run. Do **not** proceed to T006.

**Depends on:** T001–T004.

---

## The declarations — six disjoint files, one commit

Each adds exactly one line to one chain, placed among that chain's existing
`ProducesProblem` calls:

```csharp
.ProducesProblem(StatusCodes.Status400BadRequest)
```

`StatusCodes` is already imported in all six. **No comment beside the new lines**
— the reason now lives in a guard that fails the build (ADR-0036, ADR-0139). No
other change to these files: no summary, no scope, no handler, and **no change to
the route parameter's type or constraint** (see *Not in scope*).

### T006 [P] — `src/AuditObservability/Api/AuditEndpoints.cs`
`GET /audit/{auditIdentifier:guid}`, chain at 46–51. **1 added.** Its two
siblings at 32 and 39 already declare it.

### T007 [P] — `src/Identity/Api/DevicesEndpoints.cs`
`DELETE /devices/{clientId}`, chain at 50–56. **1 added.** Note that lines 43–48
— the range the issue cites — are `POST /devices/register`, which already
declares its 400 and must not be touched.

### T008 [P] — `src/Identity/Api/KiosksEndpoints.cs`
`DELETE /kiosks/{clientId}`, chain at 50–56. **1 added.** Same caution: 43–48 is
`POST /kiosks/enroll`, already correct.

### T009 [P] — the three read routes
Three disjoint files, so three parallel edits, grouped here because they are one
shape:

- `src/LayoutComposition/Api/LayoutEndpoints.cs` — `GET /layouts/{layoutIdentifier:guid}`, chain at 55–63. **1 added.**
- `src/OverlayDesigner/Api/OverlayEndpoints.cs` — `GET /overlays/{overlayIdentifier:guid}`, chain at 48–54. **1 added.**
- `src/StreamDistribution/Api/StreamEndpoints.cs` — `GET /streams/{cameraIdentifier:guid}`, chain at 34–42. **1 added.**

**Depends on:** T005. **Mutually disjoint** — six files, five contexts.

### Commit shape — read this before committing

**The guard (T001–T004) and all six declarations (T006–T009) go in one commit.**

- A guard-only commit compiles but leaves `dotnet test tests/Architecture.Tests`
  **red on `develop`**, and rebase-merge lands it individually (ADR-0087). Spec
  085 did exactly this at `a7a2f00f`; anyone bisecting that range meets a failing
  architecture suite unrelated to their search. ADR-0139 requires the red be
  *observed and quoted*, not committed — T005 satisfies it.
- **Six commits, one per context, is worse**: five intermediate commits, each red
  on a `[Theory]` naming the files not yet fixed.

Suggested message (Conventional Commits, ADR-0030; **no `Co-Authored-By` and no
session trailer** — ADR-0086):

```
fix(api): six routes declare the 400 their own handler answers
```

---

## Green, and what the green is worth

### T010 — Green run

```sh
dotnet test tests/Architecture.Tests -c Release \
  --filter RouteValueRefusalDeclarationTests
```

**Done when:** all four assertions pass, and G3's two sides both read **55**.

**Depends on:** T006–T009.

### T011 — Prove the antecedent is derived, by counterfactual

A green G1 does not distinguish a derivation from a checklist — both go green
once the six lines are added. This is the task that does. Nothing here is
committed; each step is reverted.

1. **Delete the `if (overlayIdentifier == Guid.Empty)` block** from
   `OverlayEndpoints.Queries.cs:21-27` **and** the 400 just added to its chain.
   Run the guard.
   - **Expect:** G1 says nothing about that mapping — it left the population,
     because its handler no longer produces a 400. G3 reads 54 = 54.
   - **If G1 still demands a 400** from a route that can no longer answer one,
     the population is hard-coded somewhere and this spec's derivation claim is
     **false**. That finding is worth more than the fix — #2142 rests on it.
     Report it; do not paper over it. Revert.
2. **Move one declaration out of a chain** into a private helper the walk cannot
   see. **Expect:** G3 fails on the disagreement, **and G1 fails too** — the
   hoisted chain now reads as declaring nothing, so the mapping is reported as an
   omission at the same time. A run showing only G3 red means the declaration did
   not actually leave the chain span. Revert.
3. **Add `.ProducesProblem(StatusCodes.Status400BadRequest)` to the `/overlays`
   group.** **Expect:** G2 fails. Revert.

**Done when:** all three counterfactuals behaved as stated, and the outcome is
recorded in the PR body — including if one did not.

**Depends on:** T010.

### T012 — Full fast lane

```sh
dotnet build -c Release
dotnet test tests/Architecture.Tests -c Release
```

Docker-free (ADR-0103). Confirms the six `src/` edits did not disturb
`PreconditionDeclarationTests` (whose 400 rule overlaps this one's consequent
without overlapping its population), `ConcurrencyConflictDeclarationTests`,
`EndpointScopeDeclarationTests` or `PrimitiveBoundaryTests`.

**Done when:** the Release build and the whole `Architecture.Tests` project are
green.

**Depends on:** T010.

### T013 — Verification note (phase 5)

Write what was observed and, explicitly, what was not:

- the red (6 mappings / 6 files / 5 contexts) and the green (55 = 55), quoted
  verbatim;
- T011's three counterfactual results;
- **that no 400 was observed on the wire by this change**, unless step 5–6 below
  ran. The six behaviours predate this spec by many commits and are unchanged;
- if the Aspire stack is available, run the spec's procedure steps 5–6:
  `DELETE /devices/-bad` → 400 `DEVICE_INVALID_INPUT`;
  `GET /overlays/00000000-0000-0000-0000-000000000000` → 400;
  `GET /overlays/not-a-guid` → **404**, unchanged; and
  `GET /openapi/v1.json` shows the new `400` on both operations. **If the stack
  is unavailable, say so; do not imply it ran.** Another track holds it as of
  2026-09-07;
- **that no member of this guard family reads the emitted OpenAPI document.** All
  five read `src/*/Api` text. Recorded, owned by #2142.

**Latency:** N/A — no leg of the event-to-overlay path is touched (constitution
§IV).

**Depends on:** T011, T012.

---

## Not in scope

Named so they are refused deliberately rather than forgotten.

- **Typing the route parameter** — `{clientId:guid}`, a `TryParse`-bound
  `ClientId`, or any route constraint. It would make the 400 unreachable by
  turning it into a routing 404: fixing the contract by changing the product, and
  silently changing an answer four callers already receive.
- **A mirror rule** (declares 400 ⇒ must produce one). Fails `GET /audit`, which
  correctly declares the 400 its query-parameter binding produces inside ASP.NET.
- **The general rule for the class.** #2142 owns the line. This spec hands it the
  antecedent, the one-hop-adds-zero measurement, and the one-way constraint.
- **Extracting the shared endpoint reader** across the five guards. Deferred with
  spec 085's reasoning: behaviour-preserving test-infrastructure refactor, its
  colour would be characterisation, and it is a separate issue.
- **A guard that reads the generated OpenAPI JSON.** The gap all five share.
  #2142.
- **The 400s produced by ASP.NET model binding** on `[FromQuery]` parameters —
  invisible to every source scan, and the largest residual this guard leaves.
  Recorded for #2142.
- **Any change to a handler, a status code, an error code, a scope, or a realm.**
  Six added lines in `src/`.
- **A new ADR.** Nothing is decided here — and ADR-0144 bars the lane from
  writing one in any case.

---

## Gate (Phase 3)

Tasks are atomic and each names its own "done".

**The board gate is already satisfied — verified, not assumed.** #2114 is on
Project #13 with status *In Progress*:

```sh
gh project item-list 13 --owner smartsolutionslab --limit 2000 --format json \
  -q '.items[] | select(.content.url != null)
      | select(.content.url | endswith("/2114"))
      | "\(.content.number) \(.status) \(.content.title)"'
# 2114 In Progress Two Identity DELETE endpoints answer a 400 their contract denies
```

`--limit 2000` matters: `item-list` defaults to 30 and a filled board reads as
empty without it. No `item-add` is needed, and **no per-task issues** are created
(the practice stopped after spec 028).

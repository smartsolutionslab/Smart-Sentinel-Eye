# Spec 075 — Three endpoints, not twenty-eight

**Issue:** #2096 · **Branch:** `fix/2096-every-mutating-endpoint-declares-its-409`
**Phase:** 1 (Specify) · **Date:** 2026-09-05
**ADRs:** ADR-0113 (two-layer optimistic concurrency — Layer 2 is the handler in
question), ADR-0119 (the `_STALE` vocabulary, and the source of the sentence
this issue is built on), ADR-0070 (Minimal APIs — the surface), ADR-0139 (rules
that fail the build where they can be made to), ADR-0084 (code metrics),
ADR-0052 / ADR-0103 (xUnit + Shouldly; no Docker in the fast lane), ADR-0065
(coverage gate), ADR-0109 (parallel markers), ADR-0037 (phased workflow),
ADR-0144 (autonomous lane — no ADR is written here).

## The issue as filed, and what survives contact with the repository

> `ConcurrencyConflictExceptionHandler` turns EF Core's
> `DbUpdateConcurrencyException` into … **HTTP 409** — on **every mutating
> endpoint in every context**. **No context declares it.** … the generated
> OpenAPI asserts a status code roughly **28 endpoints** can return cannot
> happen.

**The mechanism is real. The population is not.** Measured on `414568bb`:

| Claim as filed | Measured |
|---|---|
| ~28 mutating endpoints | **33** (26 `POST`, 1 `PUT`, 3 `PATCH`, 3 `DELETE`), in **11 files** across **8 contexts** |
| the handler fires on every one of them | **28 at most**; 5 cannot reach it at all |
| no context declares it | **25 of the 33 already declare `409`** in their own `Produces` chain |
| ~28 endpoints whose document denies a reachable status | **3** |

**Three endpoints, not twenty-eight.** The fix is three lines, one comment
correction, and a guard whose honest description is narrower than the issue
hoped for. Everything below is the evidence for that.

### Why "no context declares it" is true of the code and false of the document

ADR-0119 §*An eighth site, which no context declares* is about **error codes**:
its survey table lists where `_STALE`-suffixed codes are *declared in source*,
and `AGGREGATE_VERSION_STALE` is declared in `ServiceDefaults` rather than in a
context. That sentence is correct and this spec does not disturb it.

**OpenAPI does not declare error codes. It declares statuses**, one slot per
status per operation. `.ProducesProblem(StatusCodes.Status409Conflict)` says
"this operation can answer 409"; it says nothing about which of the causes did.
So on the 25 mutating endpoints that already declare `409` — for a name
collision, a stale version, a terminal state — the generated document **already
tells the truth about the Layer-2 race**. There is nothing to add there and
nothing that was denied.

Reading ADR-0119's sentence as an OpenAPI claim is the whole of the ~28. It is
the mistake this repository keeps finding in its own records, arriving from the
other side: a true sentence carried into a context where it stops being true.

### The census, measured

```sh
# 33 mutating mappings, 11 files, 8 contexts
grep -rhoE "\.Map(Post|Put|Patch|Delete)\(" --include=*.cs src/*/Api | wc -l   # 33
grep -rlE  "\.Map(Post|Put|Patch|Delete)\(" --include=*.cs src/*/Api | wc -l   # 11

# each mutating mapping, and whether its own chain declares 409
awk '/\.Map(Post|Put|Patch|Delete)\(/ {c=1; b=""; s=FNR; v=$0}
     c {b=b $0 " "}
     c && /;[ \t]*$/ {printf "%s %s:%d %s\n", (b~/Status409Conflict/?"409":"---"), FILENAME, s, v; c=0}' \
  src/*/Api/*Endpoints*.cs
```

**25 print `409`. Eight print `---`:**

| # | Verb + route | Handler path | Can EF raise `DbUpdateConcurrencyException`? |
|---:|---|---|---|
| 1 | `POST /rules/{name}/dry-run` | no persistence — mapped on the **read** group, "nothing is persisted" | **no** |
| 2 | `POST /events/manual` | `IngestEventCommandHandler`: `events.Add(@event)` then `SaveAsync` | **no** — insert only |
| 3 | `POST /events/webhook/{integrationName}` | reads the integration, then the same insert-only path | **no** — insert only |
| 4 | `POST /streams/authorize` | `AuthorizeWhepCommandHandler`: validates a forwarded token against a read-only stream lookup; no `SaveAsync` | **no** |
| 5 | `POST /streams/kiosk-latency` | records a meter value; nothing enters a domain model | **no** |
| 6 | `POST /cameras/{camera}/retire` | `RetireCameraCommandHandler`: load, `retiring.Retire(...)`, `SaveAsync` | **yes** |
| 7 | `DELETE /devices/{clientId}` | `DisableDeviceCommandHandler`: load, `client.Disable(clock)`, `SaveAsync` | **yes** |
| 8 | `DELETE /kiosks/{clientId}` | `DisableKioskCommandHandler`: load, `client.Disable(clock)`, `SaveAsync` | **yes** |

**Rows 6–8 are the defect.** Each loads a tracked aggregate, mutates it and
saves; each aggregate's configuration marks `Version` `.IsConcurrencyToken()`
(`CameraConfiguration.cs:105`, `RegisteredClientConfiguration.cs:76`), so a
concurrent write — or a concurrent delete — makes EF's affected-row count
disagree and the shared handler answers `409 AGGREGATE_VERSION_STALE`. None of
the three declares it.

**Rows 1–5 must stay undeclared.** An insert cannot raise
`DbUpdateConcurrencyException` — the exception is EF's affected-row check on an
`UPDATE` or `DELETE`, and a fresh row has no prior version to disagree with.
One of the five touches no database at all, and two more only read one.
**Declaring 409 on these five would be a new false claim of exactly the kind
being fixed.**

### The mechanism, verified rather than assumed

- **Registered once, globally.** `AuthenticationDefaults.cs:80` adds
  `ConcurrencyConflictExceptionHandler` inside `AddBearerAuthentication`, which
  all **9** Api `Program.cs` files call, and all **9** call
  `app.UseExceptionHandler()`. So it is live everywhere; the limit on its reach
  is EF, not registration.
- **It answers 409 unconditionally** for `DbUpdateConcurrencyException` and
  defers everything else (`ConcurrencyConflictExceptionHandler.cs:44-56`).
- **A configured concurrency token is not the gate.** All 9 write contexts
  configure one, but even without one EF raises the same exception when an
  `UPDATE`'s row has been deleted underneath it. The gate is "does this endpoint
  cause EF to `UPDATE` or `DELETE` a row that already exists", which is what the
  table above answers per endpoint.
- **No route group declares `Produces`.** Verified by scanning every `MapGroup`
  chain in `src/*/Api`: zero contain `Produces`. So a mapping's own chain is the
  whole of its response metadata, and reading the chain is reading the document.

### #2088 has landed, and it changed the surface

`PreconditionDeclarationTests` exists and now guards two statuses on the 17
`If-Match` endpoints (the 428 from #2088, the 400 from #2101). Its own doc
comment already names this spec's territory as the residual it will not take:

> The stale answer is produced four hops away, in a command handler across the
> Application boundary, at a status ADR-0119 leaves legally variable — which is
> why it stays out, and stays named as a residual below.

That judgement stands and this spec does not overturn it. **All 17 `If-Match`
endpoints already declare `409` or `412`**, so nothing in that population is
missing anything. The three offenders here are outside it — none of the three
requires `If-Match`. **This is a different register, not an extension of that
guard**, and the plan says where the new one lives and why.

### Found alongside, and deliberately not fixed here

Both Identity `Disable` mappings declare **only** `200` and `404`. Their groups
require `sse.identity.devices.write` and `sse.identity.kiosks.write`
respectively, so a **403** is reachable on both and neither declares it — the
same defect class as this spec's, at a different status, and not the one #2096
was filed about. It is not confined to those two: **none of Identity's eight
mappings declares `403`, and every one of them sits behind a scope** — measured,
not assumed. **Left for its own issue**, because a spec that quietly widened from one
status to three would be unreviewable against its own title, and because the
403 population needs the same per-endpoint reachability audit this one got
rather than an assumption that a scoped group implies a declaration.

## The three options in the issue, decided

**Chosen: option 2 — per-endpoint declarations**, on a population of **3**.

**Option 1 (a filter, convention or operation transformer) is rejected on the
corrected numbers, not on taste.** Attaching 409 "wherever the central handler
can fire" requires knowing where it can fire, and that is invisible at the place
a convention attaches: the endpoint knows nothing about whether its command
handler inserts or updates. A convention that attached to *every* mutating
mapping would change nothing on 25, correct 3, and **make the document wrong on
5** — the same defect, in the opposite direction, produced by a mechanism that
hides it. The issue's instinct ("28 hand-written lines would be the wrong
shape") was priced against 28. At 3, the arithmetic reverses.

**Option 3 (leave it, record why) is rejected because there is something to
fix.** It would also be an ADR by construction, which this lane may not write
(ADR-0144). It is the right answer only if the three are not real, and they are:
`RetireCamera`'s chain carries a comment saying 409 is absent *because retiring
is idempotent* — true of the domain, false of the race, and the reason the
omission survived review.

**No new ADR is needed, and the run is not blocked.** ADR-0113 decides the
concurrency model; ADR-0119 decides the vocabulary and records the shared
handler. Whether a status a route can return appears in that route's `Produces`
chain is not an architectural choice — it is the obligation CameraCatalog wrote
out at `CameraEndpoints.cs:68-70` and spec 072 enforced for the 428. Declining
to build a general guard is likewise not a decision being made here for the
first time: `PreconditionDeclarationTests` already declined the same inference,
in writing, for the same stated reason.

## User stories

### US-1 (P1) — An endpoint that can lose a race says so, and the five that cannot are not made to

**As** a caller reading the generated OpenAPI, or an operator whose retire raced
another writer,
**I want** `POST /cameras/{camera}/retire`, `DELETE /devices/{clientId}` and
`DELETE /kiosks/{clientId}` to declare the `409` their write path can produce,
**and** the five endpoints that cannot produce one to keep saying so,
**so that** a generated client has a branch for the lost update, and so that the
next reader of #2096 does not add 409 to all thirty-three.

This is the whole shippable slice. There is no US-2: the guard and the three
declarations are one change — the guard's red run is what proves the three were
missing, and the three are what turn it green.

## Functional requirements

- **FR-001** — `POST /cameras/{camera}/retire`, `DELETE /devices/{clientId}` and
  `DELETE /kiosks/{clientId}` each gain
  `.ProducesProblem(StatusCodes.Status409Conflict)` in their own fluent chain.
  Nothing else in `src/` changes behaviour: no route, no policy, no handler
  body, no status any handler returns.

- **FR-002** — `CameraEndpoints.cs`'s comment on the retire mapping — *"409 is
  absent because retiring is idempotent"* — is corrected in the same diff to say
  what the new line is for. A stale justification sitting beside a contradicting
  line is how the next author deletes the line.

- **FR-003** — A guard in `tests/Architecture.Tests` asserts that **the set of
  mutating mappings under `src/*/Api` whose chain declares `Status409Conflict`
  equals a pinned set of 28 routes**, and that the complement is the pinned set
  of 5 that cannot produce one. One set equality over the whole census, not spot
  checks on eight.

- **FR-004** — The census is pinned exactly: **33 mutating mappings in 11 files
  across 8 contexts.** A mapping added, moved or removed fails the count in the
  same diff that adds it.

- **FR-005** — Nothing resolves to a pass by default. A mapping whose route
  literal cannot be read, or that appears in neither pinned set, fails naming
  the file, line, verb and route — never "skipped", never counted as compliant.

- **FR-006** — The guard's doc comment states, in the manner of
  `PreconditionDeclarationTests` and `PaginatedConsumerTests`, **what it does not
  prove** — the list under *What the guard cannot do* — and states the question a
  new endpoint's author must answer to place it in one set or the other: *does
  this endpoint's command handler mutate or delete an aggregate that already
  exists?*

- **FR-007** — There is no exemption mechanism, register file or attribute. The
  two pinned sets **are** the classification; the diff that adds an endpoint says
  which set it joins.

## What the guard cannot do, stated before it is built

This repository has a recorded failure mode — *a guard that reads the design
artefact proves the design was written down, not that it holds*. **This guard is
one of those, and pretending otherwise would be worse than not building it.**

- **It is a register, not a derivation.** It cannot decide whether a *new*
  endpoint can produce the conflict; that needs the endpoint → command handler →
  repository → EF chain, three or four hops across the Application boundary, and
  the discriminating fact — `Add` versus mutate-then-save — is not visible at the
  Api layer. Its value is that the classification **cannot be skipped**: a new
  mutating endpoint fails FR-004's count and lands its author in the file that
  states the rule.
- **It cannot tell why a 409 is declared.** OpenAPI has one 409 slot per
  operation. On 25 endpoints the declaration is already there for a domain
  conflict; the guard is green on them whether or not anyone considered the race.
  **A green run is not evidence that the rule was applied** — only that nobody
  removed a line or added a mutating endpoint unclassified.
- **It does not prove reachability.** That `DbUpdateConcurrencyException` can
  actually be raised on the three is argued in this spec from the handler bodies
  and the EF configurations; no test asserts it. Provoking a true database race
  needs two overlapping transactions against real Postgres — Docker, CI-only, and
  a race to arrange. **Not attempted, and not claimed.**
- **It reads the fluent chain, not the generated document.** Safe *today* because
  no route group declares `Produces` (verified, zero). If one ever does, the
  guard under-reads and nothing says so.
- **It is rooted at `src/*/Api`.** A mapping that leaves those directories is
  invisible to it; FR-004's pinned count, not the sweep, is what catches that.

**A rule with no honest enforcement is still worth landing. It just must not be
sold as enforced** — neither the doc comment nor the PR body may say it is.

## Acceptance scenarios

### Happy — the census agrees with the classification

```gherkin
Given every mutating mapping under src/*/Api resolves to a route the guard can read
  And 28 of them declare Status409Conflict in their own chain
  And the other 5 are the endpoints whose write path performs no EF update or delete
When the Architecture.Tests suite runs
Then every assertion passes
  And the pinned census reports 33 mutating mappings in 11 files
```

### Conflict — a lost update the document denies

```gherkin
Given POST /cameras/{camera}/retire loads a Camera, mutates it and saves
  And CameraConfiguration marks Version as a concurrency token
  And its Produces chain declares 400, 403 and 404 but not 409
When the guard runs
Then it fails naming CameraEndpoints.cs, the line and POST /cameras/{camera}/retire
  And the message says the generated OpenAPI denies a status the shared
      concurrency handler can produce on this route
```

### Bad request — a bulk fix in the wrong direction

```gherkin
Given someone adds .ProducesProblem(StatusCodes.Status409Conflict) to
      POST /streams/kiosk-latency, which touches no database
When the guard runs
Then it fails with a message distinct from the missing-declaration message
  And that message says a declared conflict no write path can produce is the
      same defect as an undeclared one, pointing the other way
```

### Auth — the classification does not move with authorization

```gherkin
Given DELETE /devices/{clientId} answers 403 for a caller without
      sse.identity.devices.write
  And answers 404 for a client that is not a device, before any save
When the guard runs
Then it still requires the 409 declaration
  And it makes no claim about the order of the authorization and the save
```

### No soft edge — a new mutating endpoint cannot slip past unclassified

```gherkin
Given an author adds a new MapPost under src/*/Api
When they run the suite without touching the guard
Then FR-004's census fails on the count
  And the failure names the file that states how to classify the new endpoint
```

## Independent end-to-end test procedure

Docker-free; no Aspire stack, no Postgres.

1. **Rebuild the census independently.** Run the three commands in *The census,
   measured*. Expect **33**, **11**, and 25 `409` rows before the change, **28**
   after.
2. **Confirm the eight non-declarers are the eight in the table**, and open the
   three handlers named in rows 6–8 to see `load → mutate → SaveAsync`; open
   `IngestEventCommandHandler.cs:61-62` to see `events.Add(@event)` immediately
   before its save, which is why row 2 is not a fourth offender.
3. **Break it in the omission direction.** Delete the new
   `.ProducesProblem(StatusCodes.Status409Conflict)` from `DevicesEndpoints.cs`.
   Re-run: exactly one failure, naming that file, line and route.
4. **Break it in the mirror direction.** Restore, then add a 409 to
   `POST /streams/kiosk-latency`. Re-run: exactly one failure, with a message
   different from step 3's.
5. **Break the census.** Restore, then add a throwaway `MapPost` to any
   `*Endpoints.cs`. Re-run: FR-004 fails on the count. If it stays green, the
   census is decorative.
6. **Confirm no runtime behaviour moved.** `git diff src/` contains only three
   added `.ProducesProblem` lines and comment text — no `Produces<T>` change, no
   route, no `RequireAuthorization`, no handler body, no `WithSummary`.
7. **Optional, and the strongest observation available without Docker (phase
   5).** In a throwaway project that is not committed, map
   `MapCameraCatalogEndpoints`, `MapDevicesEndpoints` and `MapKiosksEndpoints`
   onto a bare `WebApplication`, enumerate `EndpointDataSource.Endpoints`, and
   print each endpoint's `IProducesResponseTypeMetadata` statuses. That observes
   the metadata the OpenAPI generator actually reads rather than the source text.
   Serving `/openapi/v1.json` would need the service running, and `MapOpenApi` is
   development-only, so it belongs to CI if anywhere.

## Phase 4a — how the colour is obtained

**Colour: red**, and the artefact is the guard's own first run.

**The honest statement of what changes is: the document is the behaviour.** No
request gets a different answer — the 409 was always produced and always will be.
What changes is the contract the product publishes about itself, and what
observes it is the endpoint's `Produces` metadata, read by the guard.

Characterisation is the wrong instrument for the same reason it was in spec 072:
it pins the current declarations, and the current declarations are the thing
being corrected. A characterisation test over today's chains would encode the
three omissions as the safety net.

**The red, exactly.** `test-writer` writes the guard, runs
`dotnet test tests/Architecture.Tests` against unmodified `src/`, and must
observe FR-003 **red naming exactly three routes** — `POST
/cameras/{camera}/retire`, `DELETE /devices/{clientId}`, `DELETE
/kiosks/{clientId}`. Three things must hold on that same first run, each the
cheapest available check that the guard discriminates rather than failing
everything:

- The **census (FR-004) is green**: 33 mappings, 11 files. A red census on an
  unmodified tree means the mapping reader is wrong, not the source.
- The **mirror is green**: the five must already be reported as not declaring
  409.
- **No fourth route appears.** A fourth means the reader is not seeing a chain
  that is there — most likely a multi-line chain terminated early.

That verbatim output is the phase-4 brief and is quoted in the PR body. The
engineer adds the three declarations and the comment correction; it **may not
edit the guard to pass**.

**A green first run is a phase-4 failure**, with a specific diagnosis: either the
pinned set was written from today's source instead of from the intended
classification, or the mapping reader matched nothing.

**Docker-free throughout** (ADR-0103). The neighbouring guards that read the same
files — `PreconditionDeclarationTests`, `EndpointScopeDeclarationTests`,
`StaleCodeConventionTests`, `HandlerDeconstructionTests` — must pass
**unmodified** afterwards.

## Latency budget

**N/A.** No leg of the 800 ms event→overlay path is touched. Nothing here runs in
a request path: the guard is a build-time scan, and `Produces` metadata is read
when the OpenAPI document is generated, which happens only in development and
only on request. Constitution §VII's dashboard obligation does not attach.

## Non-functional

- **Fast lane.** File reads, regex and brace matching; well under a second added
  to `Architecture.Tests`. No Docker, no fixture, no database.
- **Deterministic and platform-neutral.** Paths normalise to `/` before
  comparison or reporting and `\r` is stripped before matching — a backslash
  literal is green on Windows and red on Linux CI, and this repository has been
  bitten by exactly that.
- **Coverage.** `Architecture.Tests` is a guard project, not a covered assembly;
  ADR-0065's 90/80/90 gates are unaffected. The three `src/` edits are single
  fluent lines inside already-covered registration methods.
- **Code metrics (ADR-0084).** `S104` is in the test projects' `NoWarn` list, so
  guard-file length is not a build argument; the guard is a new file for
  cohesion, as the plan sets out.

## Assumptions, marked

1. **"Can raise `DbUpdateConcurrencyException`" is read as "the endpoint's write
   path causes EF to `UPDATE` or `DELETE` a row that already exists".** Inserts
   are excluded because EF's affected-row check has nothing to compare against; a
   duplicate key is a `DbUpdateException` and is answered by
   `UniqueConstraintExceptionHandler` instead. If a future handler inserts and
   then updates within one `SaveChanges`, its endpoint changes sets and the guard
   will not notice — the classification is a human judgement the guard records.
2. **`IdempotencyStore` cannot contribute one.** Its three writes are
   `ExecuteSqlRawAsync`, outside the change tracker, so an endpoint carrying an
   `Idempotency-Key` gains no additional path to this 409.
3. **`414568bb` is the measurement base**, on the worktree at
   `D:\Github\wt-2096`. Re-run the census commands before phase 4 if anything
   touching `src/*/Api` lands first; FR-004's pinned numbers are what would move.
4. **The spec directory is named for the finding, not for the branch.** The
   branch is `fix/2096-every-mutating-endpoint-declares-its-409`, and that name
   asserts what the audit disproved. Nothing depends on the mismatch; it is
   recorded so a later reader is not confused by it.

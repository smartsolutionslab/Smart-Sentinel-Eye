# Spec 075 — Four endpoints, not twenty-eight

**Issue:** #2096 · **Branch:** `fix/2096-every-mutating-endpoint-declares-its-409`
**Phase:** 1 (Specify), corrected at phase 6 · **Date:** 2026-09-05
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
| the handler fires on every one of them | **20**; the other 13 never make EF `UPDATE` or `DELETE` an existing row |
| no context declares it | **25 of the 33 already declare `409`** in their own `Produces` chain |
| ~28 endpoints whose document denies a reachable status | **4** |

**Four endpoints, not twenty-eight.** The fix is four lines, one comment
correction, and a guard whose honest description is narrower than the issue
hoped for. Everything below is the evidence for that.

> **Corrected at phase 6, and the correction is the same defect as the issue's.**
> This spec first said **three**, and partitioned the census on
> `DbUpdateConcurrencyException` alone. That is a true sentence about one
> mechanism carried into a claim about a status that has **four** —
> indistinguishable in shape from reading ADR-0119's error-code sentence as an
> OpenAPI claim, which is the mistake this document was written to correct.
> `POST /events/manual` was recorded as unable to answer `409` because its
> handler inserts. The premise is true; it does not settle the question, because
> that route's `409` comes from `IdempotentRequest`, not from EF. See *Four
> mechanisms, not one* below.

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

| # | Verb + route | Handler path | Can this route answer `409`, and by what? |
|---:|---|---|---|
| 1 | `POST /rules/{name}/dry-run` | no persistence — mapped on the **read** group, "nothing is persisted"; `DryRunRuleErrors` is 404/400 only | **no** |
| 2 | `POST /events/manual` | `IngestEventCommandHandler`: `events.Add(@event)` then `SaveAsync` — but the endpoint wraps it in `IdempotentRequest.ExecuteCreateAsync` | **yes** — idempotency in progress |
| 3 | `POST /events/webhook/{integrationName}` | reads the integration to authenticate, then the same insert-only path; no `Conflict` error, **no `Idempotency-Key`**, and the one unique constraint on `events` — its composite key `(Fab, Id, IngestedAt)` — is unreachable on a fresh Guid v7 and moot besides, since `StoreOrRefuseAsync` answers 503 to every non-cancel exception | **no** |
| 4 | `POST /streams/authorize` | `AuthorizeWhepCommandHandler`: validates a forwarded token against a read-only stream lookup; no `SaveAsync`; `AuthorizeWhepErrors` is 401/403 only | **no** |
| 5 | `POST /streams/kiosk-latency` | records a meter value; nothing enters a domain model | **no** |
| 6 | `POST /cameras/{camera}/retire` | `RetireCameraCommandHandler`: load, `retiring.Retire(...)`, `SaveAsync` | **yes** — lost update |
| 7 | `DELETE /devices/{clientId}` | `DisableDeviceCommandHandler`: load, `client.Disable(clock)`, `SaveAsync` | **yes** — lost update |
| 8 | `DELETE /kiosks/{clientId}` | `DisableKioskCommandHandler`: load, `client.Disable(clock)`, `SaveAsync` | **yes** — lost update |

**Rows 6–8 are the defect the issue was about.** Each loads a tracked aggregate,
mutates it and saves; each aggregate's configuration marks `Version`
`.IsConcurrencyToken()` (`CameraConfiguration.cs:105`,
`RegisteredClientConfiguration.cs:76`), so a concurrent write — or a concurrent
delete — makes EF's affected-row count disagree and the shared handler answers
`409 AGGREGATE_VERSION_STALE`. None of the three declares it.

**And these three are the only routes that reach the lost update *alone*.** That
is why they were the omissions rather than a random three: none of
`RetireCameraErrors`, `DisableDeviceError` or `DisableKioskError` carries
`HttpStatusCode.Conflict`. **The evidence is the absent status, not an absent
file** — an earlier draft of this section argued that "there is no
`DisableDeviceErrors` file at all", which is true and misleading: the two
`Disable` error hierarchies exist, inside `DisableDeviceCommand.cs` and
`DisableKioskCommand.cs`, declaring only `NotFound` and `BadGateway`. Anyone who
later moved them into files of those names would have read the argument as
falsified when nothing had changed. Every other mutating route already had a
second, deterministic reason to declare `409` — so its author declared it
without ever needing to think about the race, and these three had only the race.

**Row 2 is the fourth**, and it is the one this spec first got wrong. See below.

**Rows 1, 3, 4 and 5 must stay undeclared.** Each has to clear all four
mechanisms, not just the lost update: no error on the path carries
`HttpStatusCode.Conflict`, EF is never made to `UPDATE` or `DELETE` an existing
row, no unique index is written through, and no `Idempotency-Key` is read.
**Declaring 409 on these four would be a new false claim of exactly the kind
being fixed.**

### Four mechanisms, not one

`.ProducesProblem(StatusCodes.Status409Conflict)` says only *this operation can
answer 409*. So the question a `Produces` chain can be judged against is
whether **anything** on the route produces the status. Four things do:

| # | Mechanism | Produced by | Code | Rows citing it |
|---:|---|---|---|---:|
| 1 | **Handler refusal** | a command handler returns a `Result` failure whose `ApiError.Status` is `HttpStatusCode.Conflict`; `ApiErrorResults.ToProblem` renders it (`ApiErrorResults.cs:19`) | the handler's own | **25** |
| 2 | **Lost update** | `ConcurrencyConflictExceptionHandler` on `DbUpdateConcurrencyException` | `AGGREGATE_VERSION_STALE` | **20** |
| 3 | **Unique-index race** | `UniqueConstraintExceptionHandler` on a unique violation (`AuthenticationDefaults.cs:87`) | `RESOURCE_ALREADY_EXISTS` | **11** |
| 4 | **Idempotency in progress** | `IdempotentRequest` **returns** `Results.Problem(… 409)` (`IdempotentRequest.cs:94`) — not an exception, so nothing catches it | `IDEMPOTENT_REQUEST_IN_PROGRESS` | **9** |

**These are counts of rows that *cite* the mechanism, not of routes it can
reach** — a register row names mechanisms sufficient to produce the status, not
every mechanism that produces it, so rows 2 and 3 here are lower bounds. (Row 1
is not: the four register entries lacking `refusal` each say so explicitly with
`ONLY`, and the four routes that cannot answer at all clear all four
mechanisms.) Two of these counts were wrong until 2026-09-05 and are corrected
here, re-measured against `CanAnswerConflict`:

- **Refusal was recorded as 26; it is 25.** Counted three ways: the word
  `refusal` occurs 25 times in the register; the four rows that lack it are
  exactly the four whose mechanism string says `ONLY` — retire, `events/manual`,
  `DELETE /devices/{clientId}`, `DELETE /kiosks/{clientId}` — and 29 − 4 = 25;
  and 25 `.ProducesProblem(StatusCodes.Status409Conflict)` call sites stood under
  `src/*/Api` before this branch, a set that coincides with the refusal rows
  exactly, because the four declarations this branch adds are those same four
  `ONLY` routes. The same wrong 26 is in the body of commit `316de843` — the
  commit whose message announces that the class doc's false figures were
  corrected against a re-measurement. Left standing rather than reworded, so
  that this correction is a record rather than a quiet rewrite of one.
- **Unique race was recorded as 7; the register as written said 9, and it is now
  11.** 9 is the measurement: `unique race` occurs 11 times, of which 2 are `NOT
  a unique race`. 11 is after review completed the column — `POST
  /layouts/{…}/draft` and `POST /overlays/{…}/draft` race on
  `ux_layout_revisions_number` / `ux_overlay_revisions_number`
  (`LayoutConfiguration.cs:190`, `OverlayConfiguration.cs:150`), because
  `BranchDraft` adds a revision numbered `MaxRevisionNumber().Next()` and two
  concurrent branches compute the same number. No classification moves: both
  routes were already in the can-answer set on refusal and lost update.

The rows overlap: most routes carry two or three. **Mechanism 1 is the largest
and was the one nobody named** — including this spec's first draft and the
review that corrected it. It needs no race at all: a name already taken, a stale
`If-Match` version (ADR-0113 Layer 1), a terminal state.

**Mechanism 4 is what makes `POST /events/manual` row 2's `yes`.** The endpoint
routes its write through `IdempotentRequest.ExecuteCreateAsync`
(`EventsEndpoints.Writes.cs:98`), `IdempotencyStore<EventIngestionDbContext>` is
registered (`EventIngestionInfrastructureModule.cs:50`) and migration
`20260903094940_AddIdempotencyKey` exists. Two concurrent calls sharing a key
and a caller, where the first outlives the five-second poll window, make the
second answer `409`. It is the only one of the **nine** keyed creates and
rotations whose chain does not say so.

**Two rows the review got wrong in the other direction, corrected here.**
`POST /layouts/` and `POST /overlays/` were described as insert-with-collision.
They are not: `ix_layouts_fab_name` (`LayoutConfiguration.cs:83`) and
`ix_overlays_name` (`OverlayConfiguration.cs:68`) are **not unique**, so a
concurrent create there produces two same-named rows silently rather than a
`409`. Their declaration is earned by mechanisms 1 and 4. Recorded because this
spec exists over a number taken on trust, and a corrected figure adopted on
trust is the same failure wearing the reviewer's coat.

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

**Chosen: option 2 — per-endpoint declarations**, on a population of **4**.

**Option 1 (a filter, convention or operation transformer) is rejected on the
corrected numbers, not on taste.** Attaching 409 "wherever the central handler
can fire" requires knowing where it can fire, and that is invisible at the place
a convention attaches: the endpoint knows nothing about whether its command
handler refuses, inserts or updates, nor whether it is wrapped in
`IdempotentRequest`. A convention that attached to *every* mutating mapping
would change nothing on 25, correct 4, and **make the document wrong on 4** —
the same defect, in the opposite direction, produced by a mechanism that hides
it. The issue's instinct ("28 hand-written lines would be the wrong shape") was
priced against 28. At 4, the arithmetic reverses.

**And the widened predicate strengthens this rather than weakening it.** With
four mechanisms rather than one, a convention would have to know about all four
to attach correctly — including one that lives in the endpoint's own wrapper and
one that lives in an EF index configuration two projects away. That the
narrowest possible version of this inference was got wrong once, in this very
spec, is the argument against automating it.

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

### US-1 (P1) — An endpoint that can answer 409 says so, and the four that cannot are not made to

**As** a caller reading the generated OpenAPI, or an operator whose retire raced
another writer,
**I want** `POST /cameras/{camera}/retire`, `DELETE /devices/{clientId}`,
`DELETE /kiosks/{clientId}` and `POST /events/manual` to declare the `409` their
path can answer,
**and** the four endpoints that cannot answer one to keep saying so,
**so that** a generated client has a branch for the lost update and for the
idempotency replay, and so that the next reader of #2096 does not add 409 to all
thirty-three.

This is the whole shippable slice. There is no US-2: the guard and the four
declarations are one change — the guard's red run is what proves each was
missing, and the declarations are what turn it green.

## Functional requirements

- **FR-001** — `POST /cameras/{camera}/retire`, `DELETE /devices/{clientId}`,
  `DELETE /kiosks/{clientId}` and `POST /events/manual` each gain
  `.ProducesProblem(StatusCodes.Status409Conflict)` in their own fluent chain.
  Nothing else in `src/` changes behaviour: no route, no policy, no handler
  body, no status any handler returns.

- **FR-002** — `CameraEndpoints.cs`'s comment on the retire mapping — *"409 is
  absent because retiring is idempotent"* — is corrected in the same diff to say
  what the new line is for. A stale justification sitting beside a contradicting
  line is how the next author deletes the line.

- **FR-003** — A guard in `tests/Architecture.Tests` asserts that **the set of
  mutating mappings under `src/*/Api` whose chain declares `Status409Conflict`
  equals a pinned set of 29 routes**, and that the complement is the pinned set
  of 4 that cannot answer one. One set equality over the whole census, not spot
  checks on eight. **Each row of the 29 carries the mechanism** that produces
  its `409`, so a wrong row is visible rather than plausible — a reader can open
  the named errors file, index or handler and disagree.

- **FR-003a** — Route identity is **context-qualified**. The prefix alone is not
  unique: EventIngestion and Identity both map `/webhook-integrations`. Without
  the context, two colliding rows merge into one and the only failure is the
  census arithmetic — *"32 is not 33"*, the one message that names no route.

- **FR-004** — The census is pinned exactly: **33 mutating mappings in 11 files
  across 8 contexts.** A mapping added, moved or removed fails the count in the
  same diff that adds it.

- **FR-005** — Nothing resolves to a pass by default. A mapping whose route
  literal cannot be read, or that appears in neither pinned set, fails naming
  the file, line, verb and route — never "skipped", never counted as compliant.

- **FR-006** — The guard's doc comment states, in the manner of
  `PreconditionDeclarationTests` and `PaginatedConsumerTests`, **what it does not
  prove** — the list under *What the guard cannot do* — and states the question a
  new endpoint's author must answer to place it in one set or the other: *can any
  of the four mechanisms produce a 409 on this route — a handler refusal, a lost
  update, a unique-index race, or an idempotency replay?* **This requirement used
  to name the narrow predicate** — *does this endpoint's command handler mutate or
  delete an aggregate that already exists?* — which is mechanism 2 alone, the
  question that classified eight routes by a mechanism it never mentioned and
  `POST /events/manual` wrongly. A requirement stating a predicate the guard has
  replaced judges the guard against the defect.

- **FR-007** — There is no exemption mechanism, register file or attribute. The
  two pinned sets **are** the classification; the diff that adds an endpoint says
  which set it joins.

## What the guard cannot do, stated before it is built

This repository has a recorded failure mode — *a guard that reads the design
artefact proves the design was written down, not that it holds*. **This guard is
one of those, and pretending otherwise would be worse than not building it.**

- **It is a register, not a derivation.** It cannot decide whether a *new*
  endpoint can answer the conflict; that needs the endpoint → command handler →
  repository → EF chain, three or four hops across the Application boundary,
  plus the endpoint's own idempotency wiring, and the discriminating facts — an
  `ApiError` carrying `HttpStatusCode.Conflict`, `Add` versus mutate-then-save,
  an `.IsUnique()` index, an `IdempotentRequest.Execute…` call — are not all
  visible at the Api layer. Its value is that the classification **cannot be
  skipped**: a new mutating endpoint fails FR-004's count and lands its author
  in the file that states the rule.
- **It cannot tell which mechanism a declared 409 was written for, and does not
  read the problem code.** OpenAPI has one slot; most of the 29 carry two or
  three mechanisms, and the guard checks only that the slot is present. A route
  whose declaration was added for a name collision is green whether or not
  anyone considered the race, and one that answers
  `IDEMPOTENT_REQUEST_IN_PROGRESS` where the register records
  `AGGREGATE_VERSION_STALE` is green too. **A green run is not evidence that the
  rule was applied** — only that nobody removed a line or added a mutating
  endpoint unclassified.
- **It does not prove reachability.** That a lost update or a unique-index race
  can actually be provoked is argued in this spec from the handler bodies and
  the EF configurations; no test asserts it. Provoking a true database race
  needs two overlapping transactions against real Postgres — Docker, CI-only,
  and a race to arrange. **Not attempted, and not claimed.** The two
  deterministic mechanisms — a handler refusal and an idempotency replay — need
  only one request each and are still only argued, not exercised.
- **It reads the fluent chain, not the generated document.** Safe *today* because
  no route group declares `Produces` (verified, zero). If one ever does, the
  guard under-reads — and an assertion now says so rather than nothing saying so.
  It reads **masked** source: comments are blanked before anything is matched,
  so a commented-out declaration is not credited, and string literals are
  stepped over when a chain's end is found, so an unbalanced bracket inside a
  `WithSummary` cannot run one chain into the next and let a route borrow its
  neighbour's 409. Both defects were real in the first version, both are proved
  fixed by constructed counterexamples rather than argued, and the masker's own
  assumption — no verbatim, raw or escaped literals in these files — is asserted
  too.
- **It is rooted at `src/*/Api`.** A mapping that leaves those directories is
  invisible to it; FR-004's pinned count, not the sweep, is what catches that.

**A rule with no honest enforcement is still worth landing. It just must not be
sold as enforced** — neither the doc comment nor the PR body may say it is.

## Acceptance scenarios

### Happy — the census agrees with the classification

```gherkin
Given every mutating mapping under src/*/Api resolves to a route the guard can read
  And 29 of them declare Status409Conflict in their own chain
  And the other 4 are the endpoints no mechanism can make answer 409
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
  And it names the mechanism — lost update, and only that one, because
      RetireCameraErrors declares no Conflict
  And the message tells the author to add .ProducesProblem(Status409Conflict)
```

### Conflict — a 409 that never touches EF at all

```gherkin
Given POST /events/manual inserts an event and refuses nothing with a Conflict
  And the only unique constraint on events is its composite key (Fab, Id,
      IngestedAt), unreachable on a fresh Guid v7 and moot anyway because
      StoreOrRefuseAsync answers 503 to every non-cancel exception
  And the endpoint wraps its write in IdempotentRequest.ExecuteCreateAsync
  And its Produces chain declares 201, 400, 403, 429 and 503 but not 409
When the guard runs
Then it fails naming EventsEndpoints.cs, the line and POST /events/manual
  And it names idempotency in progress as the sole mechanism, so the reader is
      not sent looking for a database race that is not there
```

### Bad request — a bulk fix in the wrong direction

```gherkin
Given someone adds .ProducesProblem(StatusCodes.Status409Conflict) to
      POST /streams/kiosk-latency, which touches no database
When the guard runs
Then it fails with a message distinct from the missing-declaration message
  And that message says a declared conflict nothing on the path can answer is
      the same defect as an undeclared one, pointing the other way
  And the reason it prints clears all four mechanisms, not just the lost update
```

### No soft edge — a declaration commented out inside a chain is not credited

```gherkin
Given the .ProducesProblem(Status409Conflict) inside the retire chain is
      commented out rather than deleted
When the guard runs
Then the omission assertion fails naming that route
  And the flat sweep does not quietly count the commented line as a declaration
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
   measured*. Expect **33**, **11**, and 25 `409` rows before the change, **29**
   after.
2. **Confirm the eight non-declarers are the eight in the table**, and open the
   three handlers named in rows 6–8 to see `load → mutate → SaveAsync`. Then
   check row 2 in **both** directions, because that is where this spec was
   wrong: `IngestEventCommandHandler.cs:61-62` shows `events.Add(@event)`
   immediately before its save — so EF cannot raise the conflict — while
   `EventsEndpoints.Writes.cs:98` shows the write wrapped in
   `IdempotentRequest.ExecuteCreateAsync`, which can return one directly. The
   first fact alone looks decisive and is not.
2a. **Re-derive the mechanism column rather than reading it.** For each of the
   29, the claim is checkable at one file:
   `grep -rn "HttpStatusCode.Conflict" src/*/Application` for mechanism 1,
   the handler body for mechanism 2, `grep -rn "IsUnique" src/*/Infrastructure`
   for mechanism 3, and `grep -rn "IdempotentRequest\." src/*/Api` for
   mechanism 4. Two rows in the phase-6 review's own list were wrong — see
   *Four mechanisms, not one* — so this step is not ceremonial.
3. **Break it in the omission direction.** Delete the new
   `.ProducesProblem(StatusCodes.Status409Conflict)` from `DevicesEndpoints.cs`.
   Re-run: exactly one failure, naming that file, line and route.
4. **Break it in the mirror direction.** Restore, then add a 409 to
   `POST /streams/kiosk-latency`. Re-run: exactly one failure, with a message
   different from step 3's.
5. **Break the census.** Restore, then add a throwaway `MapPost` to any
   `*Endpoints.cs`. Re-run: FR-004 fails on the count. If it stays green, the
   census is decorative.
5a. **Break it the way a guard is usually broken — silently.** Restore, then
   *comment out* rather than delete a live `.ProducesProblem(Status409Conflict)`
   inside a chain. Re-run: exactly one failure, naming that route. Before the
   phase-6 rework this stayed green on all eight assertions while the generated
   document lost the declaration, which is the one omission the guard exists to
   catch.
6. **Confirm no runtime behaviour moved.** `git diff src/` contains only four
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
omissions as the safety net.

**The red, exactly — first run.** `test-writer` writes the guard, runs
`dotnet test tests/Architecture.Tests` against unmodified `src/`, and observes
FR-003 **red naming exactly three routes** — `POST /cameras/{camera}/retire`,
`DELETE /devices/{clientId}`, `DELETE /kiosks/{clientId}`. Three things must
hold on that same first run, each the cheapest available check that the guard
discriminates rather than failing everything:

- The **census (FR-004) is green**: 33 mappings, 11 files. A red census on an
  unmodified tree means the mapping reader is wrong, not the source.
- The **mirror is green**: the endpoints that cannot answer 409 must already be
  reported as not declaring it.
- **No extra route appears.** One more means the reader is not seeing a chain
  that is there — most likely a multi-line chain terminated early.

**The red, exactly — after the phase-6 rework.** The predicate widened from one
mechanism to four, so the partition moved and the guard was re-run against a
`src/` already carrying the first three declarations. It must go **red naming
exactly one route** — `src/EventIngestion/Api/EventsEndpoints.cs:30
EventIngestion POST /events/manual` — with the mechanism named as idempotency
in progress and the message telling the author to add the line. Everything else
green, including the two new constructed counterexamples that prove the masker
does what it claims.

That verbatim output is the phase-4 brief and is quoted in the PR body. The
engineer adds the declarations and the comment correction; it **may not edit the
guard to pass**.

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
  ADR-0065's 90/80/90 gates are unaffected. The four `src/` edits are single
  fluent lines inside already-covered registration methods.
- **Code metrics (ADR-0084).** `S104` is in the test projects' `NoWarn` list, so
  guard-file length is not a build argument; the guard is a new file for
  cohesion, as the plan sets out.

## Assumptions, marked

1. **"Can answer 409" is read as "any of the four mechanisms produces the status
   on this route".** The narrower reading — *EF `UPDATE`s or `DELETE`s an
   existing row* — is the one this spec shipped first, and it is the source of
   the phase-6 blocker. It is still the right test for **mechanism 2** and
   nothing else. If a future handler inserts and then updates within one
   `SaveChanges`, its endpoint changes sets and the guard will not notice — the
   classification is a human judgement the guard records.
2. **`IdempotencyStore` cannot contribute a `DbUpdateConcurrencyException`,
   which is not the same as contributing no 409.** Its three writes are
   `ExecuteSqlRawAsync` and its insert is `ON CONFLICT`, so it reaches neither
   the change tracker nor `UniqueConstraintExceptionHandler`. **This spec then
   drew the wrong conclusion from a correct premise**: `IdempotentRequest`
   returns `Results.Problem(… Status409Conflict)` *itself*
   (`IdempotentRequest.cs:94`), so an endpoint carrying an `Idempotency-Key`
   gains a 409 that no exception-handler survey can find. Nine endpoints do.
   The mistake was looking for the mechanism where the previous one lived —
   which is the failure mode this repository keeps recording, one level up.
3. **`414568bb` is the measurement base**, on the worktree at
   `D:\Github\wt-2096`. Re-run the census commands before phase 4 if anything
   touching `src/*/Api` lands first; FR-004's pinned numbers are what would move.
4. **The spec directory is named for the finding, not for the branch, and the
   finding has since moved.** The branch is
   `fix/2096-every-mutating-endpoint-declares-its-409`, and that name asserts
   what the audit disproved. The directory is
   `075-three-endpoints-not-twenty-eight`, and the audited number is now
   **four** — the heading says so, the directory keeps its original name, and
   renaming it would break every existing link for a digit. Nothing depends on
   either mismatch; both are recorded so a later reader is not confused, and so
   that the directory name is not read as a third independent measurement.

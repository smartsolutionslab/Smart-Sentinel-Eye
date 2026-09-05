# Tasks — Spec 075, four endpoints, not twenty-eight

**Phase:** 3 (Tasks) · **Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2096

**Engineer:** `backend-engineer` throughout. One C# guard in
`tests/Architecture.Tests` plus four fluent lines and two comment corrections
in four `src/*/Api` files. No frontend, no infrastructure, no Aspire wiring, no
migration, no domain or application code. `test-writer` owns T001–T006 per
ADR-0144's phase-4 split; the engineer receives the verbatim red output as its
brief and **may not edit the guard to pass**.

**Phase 4a colour: red.** The guard is new behaviour in the test suite and must
be observed failing against unmodified `src/` before any chain is touched
(T006). No runtime response changes — the document is the behaviour, and the
guard is what observes it. Characterisation would pin today's chains, which are
the thing being corrected; the spec's *Phase 4a* section carries the reasoning.

**Docker-free.** `dotnet test tests/Architecture.Tests` only. No Aspire fixture,
no Postgres, no solution-wide build (ADR-0103). `tests/Integration.Tests` is not
touched and is not run here.

**Parallelism.** T001–T006 are **strictly serial** — every one edits the same
new file, so ADR-0109's disjoint-file condition fails and no `[P]` would be
honest. T007–T009a are a real fan-out: four disjoint `*Endpoints.cs` files in
three contexts, no shared state, no ordering between them.

**No ADR, no constitution amendment, no `CLAUDE.md` edit.** ADR-0113 and
ADR-0119 already decide everything here (ADR-0144).

---

## Foundational — blocks everything

- **[T001] [US-1]** Create
  `tests/Architecture.Tests/ConcurrencyConflictDeclarationTests.cs` with the
  class-level XML doc that states, in this order: the claim; the **four**
  mechanisms that produce a 409 here; that the two sets are **typed in, not
  derived**; the question a new endpoint's author answers to join one (*can any
  of the four mechanisms answer 409 on this route?*); and the things the guard
  does not prove (spec, *What the guard cannot do*). Add the readers:
  `RepositoryRoot()`, `/`-normalisation, `\r` stripping, comment masking, and
  the `src/*/Api/**/*.cs` glob.
  *Depends on: nothing. Blocks: T002–T006.*

  **Done when:** the project compiles and a scratch assertion lists **11** files
  containing a mutating mapping, with forward slashes on both platforms.

- **[T002] [US-1]** The mapping walk (FR-004). For each
  `Map(Post|Put|Patch|Delete)` site capture file, line, verb, route literal, the
  nearest preceding `MapGroup` prefix in the same file, and the statement text to
  its terminating `;`. Build the route identity `"POST /cameras/{camera:guid}/retire"`.
  A site whose route literal is not a plain string literal is recorded
  **unreadable**, never skipped.
  *Depends on: T001. Blocks: T003–T006.*

  **Done when:** the walk yields **33** mappings across **11** files and **8**
  contexts, **zero** unreadable, and the retire mapping's identity is exactly the
  string above.

- **[T003] [US-1]** Census assertion A-1 (FR-004), including the independent flat
  sweep for `\.Map(Post|Put|Patch|Delete)\(` over the same directories. The walk's
  count and the sweep's count must be asserted equal to each other **and** to 33 —
  two numbers that can disagree, not one number checked twice.
  *Depends on: T002. Blocks: T004.*

  **Done when:** A-1 is green on unmodified `src/`, and deleting one mapping from
  a scratch copy fails it naming the count that moved.

- **[T004] [US-1]** The two pinned sets and the partition assertion A-2 (FR-003,
  FR-007): **29** routes that can answer `Status409Conflict` and **4** that
  cannot — `POST /rules/{name}/dry-run`,
  `POST /events/webhook/{integrationName}`, `POST /streams/authorize`,
  `POST /streams/kiosk-latency`. **Every row in both sets carries its mechanism
  or its reason** beside it (FR-003), and route identity is context-qualified
  (FR-003a). A route in neither set fails as *unclassified* (FR-005).
  *Depends on: T003. Blocks: T005.*

  **Done when:** the pinned 29 includes the four that do not yet declare it —
  `POST /cameras/{camera:guid}/retire`, `DELETE /devices/{clientId}`,
  `DELETE /kiosks/{clientId}` and `POST /events/manual` — so the assertion is
  **red** on unmodified `src/`. Writing the sets from today's source instead is
  the failure T006 exists to catch.

  > **Corrected at phase 6.** This task first said 28/5 and put
  > `POST /events/manual` in the cannot-answer set, on the reasoning that its
  > handler inserts. True, and not decisive: that route's 409 comes from
  > `IdempotentRequest`, not from EF. See spec, *Four mechanisms, not one*.

- **[T005] [US-1]** Failure messages (FR-005, FR-007). Every failure names file,
  line, verb and route. The two directions get **different** messages: an
  omission says the document denies a status the shared handler can produce on
  this route; a surplus says a declared conflict no write path can produce is the
  same defect pointing the other way. An unclassified route says which file to
  add it to and what question to answer.
  *Depends on: T004. Blocks: T006.*

  **Done when:** a scratch surplus 409 on `POST /streams/kiosk-latency` and a
  scratch omission on `DELETE /kiosks/{clientId}` produce messages that share no
  sentence.

## The red run — the gate between the halves

- **[T006] [US-1]** Run `dotnet test tests/Architecture.Tests` against
  **unmodified** `src/`. Capture the output verbatim; it is the engineer's brief
  and is quoted in the PR body.
  *Depends on: T005. Blocks: T007–T009.*

  **Done when** all four hold on the same run:
  - A-2 is **red naming exactly four routes**: `POST /cameras/{camera:guid}/retire`,
    `DELETE /devices/{clientId}`, `DELETE /kiosks/{clientId}`,
    `POST /events/manual`.
  - **No fifth route** appears — one more means a chain was terminated early.
  - The **four** that cannot answer are reported as correctly non-declaring
    (mirror green).
  - The **census is green**: 33 / 11 / 8.

  **A green run here is a phase-4 failure**, not a shortcut: either the pinned
  sets were copied from today's source, or the walk matched nothing.

  > **Phase 6 re-run.** The predicate widened after the first three declarations
  > had already landed, so the re-run's red names **exactly one** route —
  > `src/EventIngestion/Api/EventsEndpoints.cs:30 EventIngestion POST
  > /events/manual`. That output, not the original three-route one, is the brief
  > for T009a.

## The declarations — four disjoint files, real fan-out

- **[T007] [P] [US-1]** `src/CameraCatalog/Api/CameraEndpoints.cs`: add
  `.ProducesProblem(StatusCodes.Status409Conflict)` to the
  `writes.MapPost("/{camera:guid}/retire", Retire)` chain, and **correct the
  comment above it** (FR-002) — today it says 409 is absent *because retiring is
  idempotent*, which is true of the domain conflict and false of the lost update.
  The corrected comment says both: no domain conflict exists, and the shared
  Layer-2 handler can still answer 409 when the row moves underneath the save.
  *Depends on: T006. Blocks: T010.*

  **Done when:** the chain declares 409, the comment no longer contradicts it,
  and nothing else in the file changed.

- **[T008] [P] [US-1]** `src/Identity/Api/DevicesEndpoints.cs`: add
  `.ProducesProblem(StatusCodes.Status409Conflict)` to the
  `MapDelete("/{clientId}", Disable)` chain, with a one-line reason naming
  ADR-0113 Layer 2 and the load-mutate-save shape of `DisableDeviceCommandHandler`.
  *Depends on: T006. Blocks: T010.*

- **[T009] [P] [US-1]** `src/Identity/Api/KiosksEndpoints.cs`: the same on
  `MapDelete("/{clientId}", Disable)`, citing `DisableKioskCommandHandler`.
  *Depends on: T006. Blocks: T010.*

- **[T009a] [P] [US-1]** `src/EventIngestion/Api/EventsEndpoints.cs`: add
  `.ProducesProblem(StatusCodes.Status409Conflict)` to the
  `writes.MapPost("/manual", IngestManual)` chain (line 30), with a one-line
  reason naming **ADR-0142** and `IDEMPOTENT_REQUEST_IN_PROGRESS` — **not**
  ADR-0113 and not a lost update. The distinction is the whole point of the
  phase-6 correction: this route inserts, EF can raise nothing here, and the 409
  is `IdempotentRequest.ExecuteCreateAsync` refusing a key whose earlier request
  is still running. A comment citing Layer 2 here would be a fresh false
  justification of exactly the kind FR-002 exists to remove.
  *Depends on: T006. Blocks: T010.*

  **Done when:** the chain declares 409, the reason names the idempotency
  mechanism, and nothing else in the file changed. It is the only one of the
  nine keyed creates and rotations that was missing the line.

## Green, and the shape of the diff

- **[T010] [US-1]** Run `dotnet test tests/Architecture.Tests` again, plus
  `dotnet build -c Release` for the four edited projects and the test project.
  *Depends on: T007, T008, T009, T009a. Blocks: T011.*

  **Done when:** the new guard is green; `PreconditionDeclarationTests`,
  `EndpointScopeDeclarationTests`, `StaleCodeConventionTests` and
  `HandlerDeconstructionTests` pass **unmodified**; Release is clean of analyzer
  errors.

- **[T011] [US-1]** Confirm the diff's shape (spec, procedure step 6): `git diff
  src/` contains only four added `.ProducesProblem` lines and comment text — no
  `Produces<T>`, no route, no `RequireAuthorization`, no handler body, no
  `WithSummary`. Re-run the census commands and record **29** declaring rows.
  *Depends on: T010. Blocks: T012.*

- **[T012] [US-1]** Phase-5 note. Record the observation actually made and its
  limit: the guard and the census are source-level, `/openapi/v1.json` was not
  fetched because `MapOpenApi` is development-only and the stack needs Docker.
  Where it is cheap, do the throwaway metadata read (spec, procedure step 7) and
  say so; where it is not, say that instead. **Do not write "verified against the
  generated document" unless a document was read.**
  *Depends on: T011.*

  **Done when:** the note names what was observed, what was inferred, and states
  that the guard is a register — it proves the classification still describes the
  source, not that a future endpoint will be classified correctly.

## Not in scope

- Any new ADR or constitution amendment (ADR-0144).
- An `IOpenApiOperationTransformer`, endpoint filter or convention extension —
  rejected with reasons in `plan.md`.
- Adding 409 to the four endpoints that cannot answer one. That is the defect,
  pointing the other way.
- `WithSummary` prose and per-endpoint error-code documentation (#2087's
  surface).
- Changing any status code, route, policy or handler.
- Extending `PreconditionDeclarationTests`, or extracting a shared reader from
  it. It is a behaviour-preserving neighbour and must pass unmodified.

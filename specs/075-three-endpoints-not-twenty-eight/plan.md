# Plan — Spec 075, three endpoints, not twenty-eight

**Phase:** 2 (Plan) · **Spec:** `spec.md` · **Issue:** #2096
**ADRs:** ADR-0113, ADR-0119, ADR-0070, ADR-0139, ADR-0084, ADR-0052, ADR-0103,
ADR-0109, ADR-0036, ADR-0144.

## Shape of the change

Two halves, in a fixed order.

1. **One new guard** —
   `tests/Architecture.Tests/ConcurrencyConflictDeclarationTests.cs`. Enumerates
   every mutating mapping under `src/*/Api`, partitions them by whether their
   own chain declares `Status409Conflict`, and asserts that partition equals a
   pinned 28/5 classification with a pinned 33/11 census.
2. **Three fluent lines and two comment corrections** in three `src/*/Api`
   files.

**No bounded context is entered.** No Domain, Application or Infrastructure
work; no entity, value object, invariant, domain event, integration event,
message, migration, Aspire resource or frontend change. The Api layer is touched
only in its *metadata* — the registration-time `Produces` chain, not
request-handling code. There is therefore no entity/invariant section and no
messaging section to fill: saying so is more useful than inventing one.

## The mechanism question, answered concretely

The issue asks for "an endpoint filter or an OpenAPI convention". Four
mechanisms exist in this stack; three are real options and one is a category
error. The corrected population (spec, *The census, measured*) decides between
them.

| Mechanism | Does this stack support it? | Verdict |
|---|---|---|
| `AddEndpointFilter` | Yes, but it is **request-pipeline** machinery. A filter runs per request and contributes nothing to the OpenAPI document. | **Category error.** It cannot declare a response at all. |
| `IOpenApiOperationTransformer` registered in `ServiceDefaults` beside `AddOpenApi()` | Yes — .NET 10 `Microsoft.AspNetCore.OpenApi`, and all 9 services call `builder.Services.AddOpenApi()` at `Program.cs:11`, so one registration would reach every operation in the product. **This is the mechanism the issue is reaching for, and it would work.** | **Rejected on correctness.** A transformer sees the operation, its metadata and its HTTP method; it cannot see whether the command handler behind it inserts or updates. Keyed on the method it adds 409 to all 33 — no change on 25, correct on 3, **and a new false claim on 5**. It converts a defect that is visible in a diff into one produced centrally and invisibly. |
| An `IEndpointConventionBuilder` extension — `.ProducesConcurrencyConflict()` — applied at each site | Yes; ADR-0070's Minimal-API style already composes route groups and per-mapping chains this way. | **Rejected as speculative generality (ADR-0036).** It is a new shared abstraction whose only job is to spell one existing call differently, at 28 sites, to fix 3. |
| Per-endpoint `.ProducesProblem(StatusCodes.Status409Conflict)` | Yes — what 25 of the 33 already do. | **Chosen.** Three lines, each in a diff a reviewer can read, each next to the comment that explains it. |

**The deciding fact is that the antecedent is invisible at the point of
attachment.** Every central mechanism must answer "can this endpoint produce the
conflict?" from what an endpoint knows about itself, and no endpoint knows it.
That is the same obstacle `PreconditionDeclarationTests` names for the stale
half — four hops across the Application boundary — and it is why the fix stays
where the knowledge is.

## Where the guard lives, and why it is a new file

**A new file, not an extension of `PreconditionDeclarationTests`.**

That guard's claim is a biconditional over one call site: *the handler that reads
`If-Match` is the handler that answers 428 and 400*, settled by one body scan,
with reachability proven from `ConcurrencyHeaders`' three exits. Its strength is
that everything it asserts is derived.

This guard's claim is a **register**: a pinned classification of 33 routes that
no scan can derive. Folding a register into a derivation guard would let the
weaker claim borrow the stronger one's standing — precisely what the class-level
doc comments in that file exist to prevent. Separate files, separate claims,
separate doc comments about what each does not prove.

**No shared reader is extracted.** This guard needs verb, route literal, chain
text and whether the chain contains one token — about fifteen lines. It does
**not** need handler resolution, partial-class indexing, comment masking or
brace matching, which is most of what `PreconditionDeclarationTests` carries.
Extracting a shared base from a landed guard to serve a simpler consumer is a
refactor of a behaviour-preserving neighbour that must stay green and
unmodified (spec, *Phase 4a*), and it is not the smallest change (ADR-0036).
The duplication is fifteen lines of `Regex` and a `foreach`.

## Guard design

**Assertions, in the order they should fail.**

- **A-1, census (FR-004).** The mutating-mapping walk finds **33** mappings in
  **11** files across **8** contexts. Reported as three numbers so the failure
  says which moved. An independent flat sweep of the same directories for
  `\.Map(Post|Put|Patch|Delete)\(` must return the same 33 — the walk and the
  sweep count the same thing two ways, as `PaginatedConsumerTests` does.
- **A-2, the partition (FR-003).** The set of `"VERB /route"` strings whose
  chain contains `Status409Conflict` equals the pinned **28**; the complement
  equals the pinned **5**. A route in neither pinned set fails as *unclassified*
  (FR-005), never as compliant.
- **A-3, message polarity (FR-005).** A pinned-declaring route missing its 409
  and a pinned-non-declaring route that gained one produce **different**
  messages. The first says the document denies a status the shared handler can
  produce; the second says a declared conflict no write path can produce is the
  same defect pointing the other way.

**Route identity.** `"POST /cameras/{camera:guid}/retire"` — the verb plus the
group prefix plus the mapping's route literal, exactly as written in source,
lowercased for the verb only. The group prefix comes from the enclosing
`MapGroup("...")` in the same method; where a file maps two groups (Automation's
writes and reads, CameraCatalog's, EventIngestion's) the nearest preceding
`MapGroup` in the file wins. That is a lexical rule and it is stated in the doc
comment, because it is the one place the reader could bind the wrong prefix.

**Chain extent.** From the `Map*` token to the terminating `;` at the end of a
line, over `\r`-stripped text — the same statement-accumulation shape spec 072
used. Safe because no `MapGroup` in `src/*/Api` declares `Produces` (verified,
zero), so no declaration a mapping owns lives outside its own statement.

**What is written in the file and what is derived.** The two pinned sets are
typed in, and the doc comment says so in the first paragraph. The census
numbers are typed in. Everything else — the routes found, the partition, the
independent sweep — is derived. The guard's honest claim is therefore *"the
classification recorded on 2026-09-05 still describes the source"*, not *"the
source is correct"*.

## Boundary rules (ADR-0109, NetArchTest)

Nothing crosses a context boundary. `Architecture.Tests` already references every
`Api` project; this guard globs files rather than linking against endpoint types,
so **no project reference is added** and `BoundaryTests` sees no change. The
three edited `src/*/Api` files gain no `using`: `StatusCodes` and
`ProducesProblem` are already in scope in all three, verifiable from the sibling
declarations beside them.

## Files phase 4 will touch

| File | Change |
|---|---|
| `tests/Architecture.Tests/ConcurrencyConflictDeclarationTests.cs` | **new** — the guard and its doc comment |
| `src/CameraCatalog/Api/CameraEndpoints.cs` | one `.ProducesProblem(409)` on the retire mapping; correct the comment above it (FR-002) |
| `src/Identity/Api/DevicesEndpoints.cs` | one `.ProducesProblem(409)` on `DELETE /devices/{clientId}`, with a one-line reason |
| `src/Identity/Api/KiosksEndpoints.cs` | one `.ProducesProblem(409)` on `DELETE /kiosks/{clientId}`, with a one-line reason |

Nothing else. No `Program.cs`, no `ServiceDefaults`, no ADR, no constitution, no
`CLAUDE.md`, no frontend, no migration, no Aspire wiring.

## Risks

- **The pinned 28 is typed from today's source rather than from the intended
  classification.** Then the guard is green on its first run and proves nothing.
  Phase 4a's stated red — exactly three named routes — is the check, and a green
  first run is a phase-4 failure with that diagnosis.
- **A mapping whose chain spans an unusual shape** (a trailing comment after the
  `;`, a chain broken across a `#if`) is mis-extracted. Mitigated by A-1's
  two-way count and by FR-005's unclassified failure; there are none today.
- **`apps/shared` clients.** None needs a change: `problemDetail.ts` already
  keys on the `_STALE` suffix rather than the status (ADR-0119), so the three
  endpoints' advice is already correct at runtime. This spec changes only what
  the document says about them.

# Plan — Spec 085, seventeen endpoints, not eight

**Phase:** 2 (Plan) — ADR-0037. Gate: aligns with the constitution + ADRs.

---

## Shape of the change

Two halves, in this order, and the order is the gate.

1. **The guard** — four new assertions inside
   `tests/Architecture.Tests/EndpointScopeDeclarationTests.cs`, plus one line of
   reason on the existing `MappedOutsideTheChain` row. Run before any `src/`
   edit; it fails naming 17 mappings in 5 files. **That failure is the phase-4a
   red and it is quoted in the PR.**
2. **The declarations** — 17 added `.ProducesProblem(StatusCodes.Status403Forbidden)`
   lines across 5 files in 3 bounded contexts. Nothing else changes in them.

No production behaviour is written. The only runtime effect is on the OpenAPI
document ASP.NET generates from the metadata.

---

## Bounded context and layers

**None, and that is a fact about the change rather than an omission.** This spec
adds no domain concept, no entity, no value object, no invariant, no command, no
query, no handler, no migration, no message and no integration event. It touches
exactly two kinds of file:

| Layer | Files | What changes |
|---|---|---|
| `<Context>/Api` | 5 endpoint files in Identity, OverlayDesigner, AuditObservability | one chained call per affected mapping |
| `tests/Architecture.Tests` | 1 file | four assertions + one register comment |

There is therefore no domain → integration event to describe, and no messaging
leg. The "entities, invariants, messaging" sections a plan normally carries are
empty by construction; saying so is more useful than inventing content for them.

**Boundary rules (ADR-0044, NetArchTest).** No project reference is added or
changed. `Architecture.Tests` already references the Api assemblies — it reflects
over `LayoutLifecycleHub`, `Scope` and the StreamDistribution handler today. No
cross-context reference appears in `src/`: each of the five edits stays inside
its own context's Api project and cites only `Microsoft.AspNetCore.Http`'s
`StatusCodes`, which every one of them already imports.

---

## Where the guard lives, and why it is not a new file

Three sibling guards exist and they were placed on two different principles:

- `PreconditionDeclarationTests` — one file, **two statuses** (428 and 400),
  because both come off the *same* antecedent: the handler body that reads
  `If-Match`.
- `ConcurrencyConflictDeclarationTests` — its own file, because its antecedent
  (409 reachability) is not readable at all and had to be typed in.

The organising principle is the **antecedent**, not the status. This rule's
antecedent is *the effective authorization of a mapping* — which is precisely
what `EndpointScopeDeclarationTests` already computes, for all 56 mappings,
including the 28 that inherit it from a `MapGroup`. So it goes there.

Three concrete consequences, each a reason rather than a preference:

1. **No fourth copy of the reader.** `MaskLiterals`, `StatementEnd`,
   `WithoutComments` and the statement-span walk exist in three files today.
   Extending the file that already resolves group inheritance adds a fourth
   *consumer* without a fourth *copy*.
2. **Group inheritance is not re-derived.** 28 of the 54 are in the population
   only through `Read(file)`'s group map, keyed by receiver name, with its
   duplicate-name refusal. Re-implementing that in a new file is where a subtly
   different second answer comes from.
3. **The A9 self-scan covers the new rule for free.**
   `The_guard_offers_no_way_to_excuse_an_endpoint` scans `GuardSource` for
   `allowlist`/`exempt`/`suppress`/`#pragma warning disable`. Assertions added to
   that file inherit the no-soft-edge property without a second copy of the
   scan; a new file would need its own or would silently lack it.

**The tension, recorded so a reviewer can overrule it (spec §A1).** The class
doc's stated rule is *"an endpoint names the scope it needs"*, and this is a
second sentence: *"an endpoint that needs a scope declares the refusal"*. The
class doc must be widened to say both, the way
`PreconditionDeclarationTests` widened to hold the 400 beside the 428. If the
reviewer prefers a separate `ScopeRefusalDeclarationTests`, the cost is
re-deriving group inheritance — name it, do not absorb it silently.

---

## Guard design

Four assertions. Named for what they claim, not for what they check.

### G1 — `Every_scoped_endpoint_declares_the_refusal_its_scope_produces`

Per-file `[Theory]` over `EndpointFiles()`, matching the file's existing shape so
one failure reports every offender in a file at once.

- Population: `Read(file).Mappings` where `Kind == AuthorizationKind.Scoped`
  **and** `ScopeLiterals.ContainsKey(mapping.ScopeConstant)` — i.e. reuse
  `ScopedWithLiteral(file)`, which already applies both filters and already
  leaves an unresolvable constant to A3 so one cause does not fail twice.
- Claim: the mapping's chain span contains
  `.ProducesProblem(StatusCodes.Status403Forbidden)`.
- Requires one new field on `EndpointMapping`: `int ForbiddenDeclarations`,
  filled in `ReadMapping` from the same `[start..end]` span the summary and
  authorization are already read from. **Comments are already blanked and
  literals already masked** before that span is taken, so a commented-out
  declaration is not credited and the word appearing inside a `WithSummary`
  string is not either.

  **Corrected at phase 4a: this read `bool DeclaresForbidden`, which would have
  made G3 wrong.** G3 compares this walk against a flat sweep. A chain
  declaring the same 403 twice is legal; a flag reads one where the sweep reads
  two, and G3 then fails over correct source. A count keeps the two sides
  commensurable.
- Message names file, line, verb, full route and the scope literal, and says
  what the document currently asserts — that a wrong-scope caller gets something
  other than 403.

### G2 — `No_route_group_declares_the_refusal_its_members_must_declare`

`[Fact]` over the group spans `Read` already collects. Zero of 17 declare a 403
today. This exists because **OpenAPI inherits group metadata**: were a group to
declare one, G1's per-chain demand would be unsound and would fail correct code.
Failing on the group is the honest answer — it says *this shape is refused*, not
*this endpoint is wrong*.

The group span is already computed (`declaration.Index..end`) but currently only
its first literal and its authorization are kept. G2 needs the count retained
per declaration site.

**Corrected at phase 4a: this said to hang a `DeclaresForbidden` flag on
`RouteGroup` alongside `Prefix` and `Authorization`, and that would have
undercounted.** `Read` stores groups in a dictionary keyed by receiver name and
**overwrites** `groups[name]` when a name is declared twice — the case it
already refuses as unreadable. A count living on the stored `RouteGroup` is
overwritten with it, so the walk loses the first group's declarations while the
flat sweep still finds them, and G3 fails over correct source. Group
declarations are collected instead in their own list, one entry per declaration
site, independent of the name binding.

### G3 — `The_refusal_declarations_the_walk_finds_are_all_the_ones_there_are`

`[Fact]`. The mapping walk's sum of `ForbiddenDeclarations`, plus the group
walk's sum from G2, must equal an independent flat sweep of
`.ProducesProblem(StatusCodes.Status403Forbidden)` over `ApiSourceFiles()`.

Both sides read **38** before the `src/` edits (37 scoped + 1 anonymous, 0
groups) and **55** after. This is the
`PaginatedConsumerTests` property `PreconditionDeclarationTests` borrowed: it is
what stops a declaration moved somewhere the walk cannot see from reading as
"declares nothing". Without it, G1's sharpest edge would be silent.

Note the sweep covers `ApiGlob` (`src/*/Api/**/*.cs`), which is wider than
`EndpointGlob` — a 403 declared in a non-`*Endpoints.cs` Api file would make the
two disagree, which is the correct outcome.

### G4 — the register row gains its reason

`MappedOutsideTheChain`'s single row —
`/hubs/layouts`, `LayoutLifecycleHub` — gets a doc sentence recording that it is
outside **this** rule too, and why: a SignalR hub has no OpenAPI operation, so
there is nothing for `ProducesProblem` to write into. No new register, no new
row, no code change beyond the comment. The existing both-directions checks
(`Every_route_mapped_outside_the_readable_shapes_is_registered` /
`_still_exists`) already make a second such route fail rather than inherit the
exclusion; G4 only makes the reason readable.

**Explicitly not built: a mirror.** No assertion that an unscoped mapping must
*not* declare 403. `POST /streams/authorize` is `AllowAnonymous` and declares one
correctly via `AuthorizeWhepErrors.Forbidden` (ADR-0089). A mirror fails on it; a
mirror carved to exempt it is a register, and this spec's whole claim is that
this rule needs none. Same call `PreconditionDeclarationTests` made for its 400,
for the same reason.

---

## The seventeen declarations

Grouped by file, because a file is the unit of parallelism (ADR-0109) and each of
the five is disjoint from the others.

| File | Lines to touch | Added |
|---|---|---|
| `src/Identity/Api/DevicesEndpoints.cs` | 33, 43, 54 | 3 |
| `src/Identity/Api/KiosksEndpoints.cs` | 33, 43, 54 | 3 |
| `src/Identity/Api/WebhookRotationEndpoints.cs` | 39, 54 | 2 |
| `src/OverlayDesigner/Api/OverlayEndpoints.cs` | 30, 40, 47, 54, 66, 76, 88, 100 | 8 |
| `src/AuditObservability/Api/AuditEndpoints.cs` | 46 | 1 |

Each addition is one line placed among the chain's existing `ProducesProblem`
calls:

```csharp
.ProducesProblem(StatusCodes.Status403Forbidden)
```

`StatusCodes` is already imported in all five files (each already declares a 400
or a 404). **No comment is added beside the new lines.** The existing 403
declarations carry per-context comments explaining that fab resolution made 403
reachable; repeating a variant of that seventeen times is the drive-by-comment
failure (ADR-0036), and the reason now lives in a guard that fails the build,
which is the better place for it (ADR-0139).

**One line of the class doc does need correcting**, and it is not a drive-by:
`src/Identity/Api/WebhookRotationEndpoints.cs:136` says a caller gets *"the 403
they have earned"* six lines from a chain that has never declared one. After this
change the sentence is true of the document as well as of the runtime. No edit
needed — recorded so a reviewer sees it was checked, not missed.

---

## Ordering and dependencies

```
G1..G4 (one file)  ──►  RED, quoted  ──►  5 declarations [P]  ──►  GREEN
                                            │
                            disjoint files, real fan-out
```

The guard is **foundational**: it blocks everything, because its failure is the
phase-4a gate and a declaration added first would make the red unobtainable. The
five declaration files are mutually disjoint and carry `[P]`.

They span three bounded contexts (Identity ×3 files, OverlayDesigner ×1,
AuditObservability ×1), so the fan-out is genuine rather than nominal — but the
whole edit is 17 lines, so an orchestrator may reasonably do them serially. The
markers record independence, not an instruction to parallelise 17 lines.

---

## Files phase 4 will touch

**Test (foundational, blocks the rest):**

- `D:\Github\wt-2113\tests\Architecture.Tests\EndpointScopeDeclarationTests.cs`

**Source (five, disjoint, `[P]`):**

- `D:\Github\wt-2113\src\Identity\Api\DevicesEndpoints.cs`
- `D:\Github\wt-2113\src\Identity\Api\KiosksEndpoints.cs`
- `D:\Github\wt-2113\src\Identity\Api\WebhookRotationEndpoints.cs`
- `D:\Github\wt-2113\src\OverlayDesigner\Api\OverlayEndpoints.cs`
- `D:\Github\wt-2113\src\AuditObservability\Api\AuditEndpoints.cs`

**Not touched, and deliberately:** no `Program.cs`, no
`RequireScopeExtensions.cs`, no `AuthenticationDefaults.cs`, no realm JSON, no
`Shared.Contracts`, no frontend, no AppHost, no migration. The authorization
model is not changed — only the document's account of it.

---

## Risks

1. **The guard is written to the issue's wording and passes vacuously.** The
   issue says `RequireScope`; nothing in `src/` uses it. Mitigation: reuse
   `ScopedWithLiteral` / `Read`, which read both spellings and resolve groups.
   **Detection:** the red must name 17 mappings across 5 files. A red naming 8,
   or a green first run, means the population is wrong — stop, do not proceed to
   the declarations.
2. **The population silently narrows to own-chain mappings.** 28 of 54 inherit
   their scope. A red naming only OverlayDesigner's 8 is this failure exactly,
   because OverlayDesigner is the one gap file whose scopes are all in-chain.
   **A red of 8 is the trap, and 8 is also the issue's headline number** — the
   two coincide by accident, which makes a wrong red look like a right one.
3. **Someone "fixes" it at the group level.** Eight lines saved on
   `/overlays`, and G1 then fails on eight correct mappings. G2 exists so the
   failure lands on the group with an explanation instead.
4. **A green run is read as proof of behaviour.** It is source-vs-source. The
   spec's *What the guard cannot do* section is the answer; the verification note
   must repeat gaps 1 and 2 rather than claiming 54 routes were observed
   returning 403.
5. **Coverage / metrics.** No new branches; all five files stay far under 300
   LOC (largest post-change: 213). `S104`/`S138` are in the test projects'
   `NoWarn` (`Directory.Build.props:108`, `IsTestProject`), so growing the
   1825-line guard introduces no suppression — which ADR-0144 would otherwise
   bar.

---

## Gate (Phase 2)

Aligns with constitution §VIII (the scope model this declares), §Testing (red
first), ADR-0070 (Minimal APIs are the surface), ADR-0139 (a build failure, not a
convention), ADR-0036 (smallest change; the reader extraction and producer-2
census are named and deferred), ADR-0144 (no ADR written, no gate weakened, no
suppression added).

# Plan — Spec 091, six routes, not two

**Phase:** 2 (Plan) — ADR-0037. Gate: aligns with the constitution and the ADRs.

---

## Bounded contexts and layers touched

**Six files, five contexts, one layer.** Every edit is in an `Api` project's
endpoint mapping, and every edit is one line.

| Context | File | Layer |
|---|---|---|
| AuditObservability | `src/AuditObservability/Api/AuditEndpoints.cs` | Api |
| Identity | `src/Identity/Api/DevicesEndpoints.cs` | Api |
| Identity | `src/Identity/Api/KiosksEndpoints.cs` | Api |
| LayoutComposition | `src/LayoutComposition/Api/LayoutEndpoints.cs` | Api |
| OverlayDesigner | `src/OverlayDesigner/Api/OverlayEndpoints.cs` | Api |
| StreamDistribution | `src/StreamDistribution/Api/StreamEndpoints.cs` | Api |

**Plus one test file**, which is where the actual work is:
`tests/Architecture.Tests/RouteValueRefusalDeclarationTests.cs` (new).

**Domain: untouched. Application: untouched. Infrastructure: untouched.** No
entity, no value object, no invariant, no handler, no repository, no migration.

## Entities, value objects, invariants

**None introduced or changed.** The value objects already involved —
`ClientId`, `AuditEventIdentifier`, `LayoutIdentifier`, `OverlayIdentifier`,
`CameraIdentifier` — keep their guards exactly as they are (ADR-0038, ADR-0046,
ADR-0066, ADR-0105). This spec declares a status those guards already produce; it
does not move where the guard runs, what it rejects, or what it says.

The one thing worth stating as an invariant of the *contract*, because it is what
the guard enforces:

> For every route mapping under `src/*/Api`, if the resolved handler body can
> return 400, the mapping's own chain declares 400.

One-directional, for the reason spec.md gives.

## Messaging

**None.** No domain event, no integration event, no `Shared.Contracts` change, no
Wolverine handler, no queue. Six routes gain a line of OpenAPI metadata.

## Boundary rules

- **No cross-context project references** are added. Six independent Api projects
  each gain one line in their own file; none learns about another. NetArchTest is
  unaffected.
- **`Shared.Contracts` is untouched.** No versioned message changes, so no `V<N>`
  bump.
- **`ServiceDefaults` is untouched.** The declaration is per-mapping metadata,
  not a shared convention — deliberately, because a shared convention hoisted out
  of the chain is precisely what the guard's sweep cross-check exists to catch.

## Where the guard lives, and why not in an existing file

Four guards already read `src/*/Api` source text:

| Guard | Subject | Antecedent |
|---|---|---|
| `PreconditionDeclarationTests` | 428 + 400 for `If-Match` | a `ConcurrencyHeaders.TryRead*` call in the handler body |
| `ConcurrencyConflictDeclarationTests` | 409 | a **pinned register** (spec 075: not derivable) |
| `EndpointScopeDeclarationTests` | scope sentence + 403 | the chain's or group's `RequireAuthorization` |
| `SystemVariableReadScopeTests` | one context's scopes | built endpoint metadata (reflection, no Docker) |

`PreconditionDeclarationTests` is the nearest neighbour by *mechanism* — it
already resolves a mapping's handler body by method-group name across an Api
project's partial classes, and it already owns the both-spellings 400
declaration regex. **The new rule goes in a new file anyway**, for one reason
that outweighs the duplication:

**Its population is disjoint from that file's subject.** None of the six calls
`ConcurrencyHeaders`; all six are route-value refusals, not preconditions. A rule
whose subject the filename denies is a rule the next author does not find — and
this defect class was found five separate times precisely because nobody could
find the previous instance.

**Accepted cost, named:** a fifth copy of `RepositoryRoot()` / `ApiSourceFiles()`
/ `MaskComments` / `MaskLiterals` / `StatementEnd`. Spec 085 deferred extracting
them as a behaviour-preserving refactor of test infrastructure whose phase-4a
colour would be characterisation, not red. That judgement stands; this spec adds
a consumer and a copy, and does not pay for the extraction with a red-coloured
change that is really a refactor.

**Reuse, not reinvention:** the reader is copied from
`PreconditionDeclarationTests`, whose handler-body resolution is the exact
mechanism needed and is already proven against this corpus (it resolves 56 of 56
bodies). Do not write a second reader from scratch (ADR-0036: mirror existing
patterns).

## The guard's four assertions

Mirroring spec 085's shape, because that shape is what made #2113's derivation
checkable.

| # | Assertion | Kind | Reads today | Reads after |
|---|---|---|---|---|
| **G1** | Every mapping whose handler body can return 400 declares it | `[Theory]` over endpoint files | **red, 6 mappings / 6 files** | green |
| **G2** | No route group declares a 400 | `[Fact]` | green (0 of 17) | green |
| **G3** | Walk total = independent flat sweep | `[Fact]` | green, **49 = 49** | green, **55 = 55** |
| **G4** | Every mapping's handler body is resolved, none skipped | `[Fact]` | green, **56 of 56** | green |

**G2 is the soundness prerequisite.** OpenAPI inherits `MapGroup` metadata, so a
group-level 400 would make G1's per-chain demand fail correct code. Zero of the
17 groups declare any status today; G2 says so out loud, so the day someone adds
one, this guard fails instead of lying.

**G3 is what stops a hoist.** A declaration moved into a helper the walk cannot
see would read as "declares nothing"; the sweep disagrees and G3 fires. The
property comes from `PaginatedConsumerTests` and is already borrowed by two other
guards.

**G4 is what stops a vacuous pass.** If the handler-name resolution silently drops
a mapping — an inline lambda, a partial file the glob misses — that mapping
leaves the population and G1 goes green without checking it. There are zero
lambdas today; G4 makes that a fact the build maintains rather than one this spec
observed once.

**A `[Theory]` over files, not over contexts.** One `[Theory]` case per endpoint
file, so a failure names the file. This is also why the six declarations must not
be split into six commits — see ADR-0087 below.

## Alignment with the constitution and ADRs

- **ADR-0070** — Minimal APIs only. The fluent chain is the only place this
  metadata can live; there is no controller attribute alternative to consider.
- **ADR-0139** — a rule that fails the build, not a convention a reviewer
  remembers. This defect was found by human review five times across five specs.
  That is the case for a guard rather than a sixth one-off fix.
- **ADR-0036** — smallest possible change; no drive-by. Six lines added, nothing
  else in `src/`. No comment beside the new lines: the reason now lives in a
  guard that fails the build.
- **ADR-0103 / ADR-0052** — the guard is a pure source scan in
  `Architecture.Tests`, which references no Aspire fixture and needs **no
  Docker**. It runs in the fast lane.
- **ADR-0084** — code metrics. The new file will exceed 300 LOC if the reader is
  copied wholesale; `Architecture.Tests` is where the existing guards already sit
  at 2182 LOC (`EndpointScopeDeclarationTests`), so the limit's treatment there
  is established. Follow the neighbours; do not invent an exemption.
- **ADR-0065** — coverage gates are Domain/Application/Shared. Unaffected: no
  production logic is added.
- **ADR-0087** — each commit builds on its own. See below; this is the one place
  the plan constrains the engineer's hand.
- **ADR-0144** — the lane may not write an ADR. None is needed: constitution
  §VIII and ADR-0070 already lock the model; declaring a status it produces is
  implementation.
- **Constitution §Testing** — new behaviour starts red, and the failure is quoted
  in the PR body.
- **Constitution §IV** — N/A, stated explicitly in spec.md.

## Commit shape (ADR-0087) — the constraint that matters

**The guard and the six declarations land in one commit.**

Spec 085's precedent went the other way: `a7a2f00f` added
`EndpointScopeDeclarationTests`'s new assertions alone, and `f7c39615` added the
17 declarations afterwards. `a7a2f00f` compiles, so it satisfies ADR-0087 read
narrowly — but `dotnet test tests/Architecture.Tests` is **red at that commit on
`develop`**, and rebase-merge lands it individually. Anyone bisecting through
that range meets a failing architecture suite that has nothing to do with what
they are bisecting for.

That is worth not repeating, and it costs nothing to avoid: ADR-0139 requires the
red be **observed and quoted in the PR body**, not that it be committed. The
engineer runs the guard against unmodified `src/`, captures the verbatim failure,
then adds the six lines and commits both together.

**Six declaration commits, one per context, is worse still** — five intermediate
commits each red on a `[Theory]` that names the files not yet fixed. Refused.

## What is deliberately not designed here

- **The general rule.** #2142 owns the line between derivable and register-only
  for the whole class. This plan hands it three findings: the antecedent
  (spec.md), that one hop adds zero routes, and that the rule must run one way
  because the framework produces 400s no source scan can see.
- **Reading the emitted OpenAPI document.** All five guards read source. The gap
  is real and is #2142's.
- **The extraction of the shared reader.** Deferred with spec 085's reasoning.

## Gate (Phase 2)

The plan touches one layer in six contexts, adds no cross-context reference, no
message, no entity and no ADR. It reuses `PreconditionDeclarationTests`'s proven
reader rather than inventing one, and it names the single place it departs from
precedent (one commit, not two) with the reason.

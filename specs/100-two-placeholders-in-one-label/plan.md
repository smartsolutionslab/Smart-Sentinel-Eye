# Spec 100 — Plan

**Phase:** 2 (Plan) · **Date:** 2026-09-08 · **Base:** `8ecba0b8`

---

## Bounded context and layers

**SystemVariables**, read side only. Nothing is added to any layer — the slice
adds one test class under `tests/Integration.Tests/SystemVariables/` and touches
no `src/`.

The path under test, in order:

| Step | Component |
|---|---|
| Define ×2 | `SystemVariableEndpoints.Define` → `DefineVariableCommandHandler` → Postgres |
| Publish overlay | OverlayDesigner `POST /overlays` + `revisions/1/publish` → `OverlayRevisionPublishedV1` over RabbitMQ |
| Index | `OverlayRevisionPublishedV1Handler` → `InMemoryReverseIndex.UpsertOverlayReferences` (`:26-36`) — loops `ExtractNames`, so **both** names are indexed |
| Set ×2 | `SetVariableValueCommandHandler` (with `If-Match`, ADR-0113) |
| Read | `SystemVariableEndpoints.GetSnapshot` (`:57`) → `GetOverlaySnapshotQueryHandler` → `BuildSnapshotAsync` (`:31-66`) → `Resolver.Resolve` → `PlaceholderParser.Substitute` |

**The component under test is `GetOverlaySnapshotQueryHandler.BuildSnapshotAsync`.**
Everything else in the chain already has integration coverage for one variable;
this loop is the only link that has never run a second productive iteration.

## Entities, value objects, invariants

None introduced. The invariant being pinned is a behavioural one, not a domain
one: **the snapshot is keyed by every well-formed placeholder name the label
carries, not by the first**, and each name resolves independently.

## Messaging

No new domain or integration event. The test *rides* one — `OverlayRevisionPublishedV1`
(`Shared.Contracts`) — which is asynchronous, so the test must wait for the
index rather than assume it. `NFR_VariableResolutionLatencyTests.WaitUntilResolvableAsync`
(`:144-162`) already solves this: it polls the snapshot until the literal
placeholder disappears. **The new test needs a two-variable form of that wait**
— readiness is when *both* literals are gone, not the first. A wait that
checked only one name would race exactly the bug the test exists to catch, and
would turn a real failure into a flake.

## Boundary rules

- No cross-context project references. The test drives OverlayDesigner and
  SystemVariables over **HTTP**, through two separate `HttpClient`s, which is
  what both neighbours already do.
- ADR-0109 contention: the contention list is `src/Shared.Kernel/*`,
  `src/Shared.Contracts/*`, `src/AppHost/AppHost.cs`, `apps/shared/*`,
  `e2e/support/*`, `.github/workflows/ci.yml`, `Directory.Packages.props`,
  `global.json`. **Neither `NFR_VariableResolutionLatencyTests.cs` nor
  `ResolvedTextReachesItsFabTests.cs` is on it.** A new file is disjoint from
  everything regardless, so this slice is safe to run alongside any other.

---

## Where the test goes, and why

**New file: `tests/Integration.Tests/SystemVariables/TwoPlaceholdersInOneLabelTests.cs`.**

Both neighbours were read in full before deciding.

- **Not `NFR_VariableResolutionLatencyTests.cs` (202 lines).** Its subject is a
  §IV *figure*: warmup rounds, measured rounds, a median, a printed artefact
  (`:107-114`). Its class remark (`:9-51`) is an argument about *when* the
  measurement was taken and against which implementation. A correctness
  assertion dropped into it would be the only test in the file that does not
  produce a number, and would inherit a `LegBudgetMs` and a rounds loop it has
  no use for.
- **Not `ResolvedTextReachesItsFabTests.cs` (604 lines).** Its subject is the
  **SignalR push seam** — *who* receives a frame and *whose* fab it says it is.
  It resets three databases in `InitializeAsync` (`:150-156`) and opens hub
  connections. This test asserts a **GET response body**, never opens a hub, and
  needs one reset. Adding to a 604-line file also makes it a rebase magnet for
  every other SystemVariables slice, which a new file avoids by construction.
- **Not `VariableResolutionIntegrationTests`.** `git log --all -S … -- '*.cs'`
  shows the name has never named a file; importing it now would make sixteen
  specs' worth of documentation retroactively look like it described code.
  `TwoPlaceholdersInOneLabelTests` says what the file does.

**Shape** — mirrors the neighbours exactly:

```
[Collection(AspireCollection.Name)]
public class TwoPlaceholdersInOneLabelTests(AspireFixture aspire) : IAsyncLifetime
{
    public Task InitializeAsync() => aspire.ResetSystemVariablesAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    ...
}
```

No `[Trait]`, for the reason in `spec.md` — the CI filter at `.github/workflows/ci.yml:179`
excludes three categories and every file in this directory carries none.
`ResetSystemVariablesAsync` only, matching `NFR_VariableResolutionLatencyTests:64`:
the reverse index is in-process memory and survives a DB reset, and each run
uses fresh GUID-derived names, so resetting OverlayDesigner would only delete
rows nothing reads.

---

## Phase 4a colour, and the evidence

**Behaviour-preserving → characterisation, observed green.** No production code
changes, so there is no red to observe. A compile error is not a red test —
spec 061 `24e6fc4c` settled that.

The characterisation obligation is discharged by a **counterfactual against
production code**: apply a named mutation, watch the new test fail and the
named others hold, then revert and confirm green. The mutation is chosen to
match the narrow claim in `spec.md` — the *query handler's* multi-name loop —
rather than the substitution mechanism, which is already covered.

### M1 — the required mutation (isolating)

In `src/SystemVariables/Application/Queries/Handlers/GetOverlaySnapshotQueryHandler.cs`,
insert `break;` as the last statement of the `foreach` body, immediately after
line 63:

```csharp
            snapshot[name] = new VariableSnapshotEntry(variable.Value, variable.BooleanLabels);
            break;   // M1
```

Semantics: the snapshot loads only the **first resolvable** placeholder; every
later one renders as its literal.

**Written prediction.**

| Outcome | Test | Why |
|---|---|---|
| **FAILS** | `TwoPlaceholdersInOneLabelTests` happy path | expects `"Line A: 82.5 / Line B: 91.5"`, gets `"Line A: 82.5 / Line B: {{vB}}"` |
| GREEN | `TwoPlaceholdersInOneLabelTests` partial-resolution case | `vB` is unset, so its literal is expected either way — this half **cannot** detect M1, which is why it is not the test the counterfactual is scored on |
| GREEN | all 5 `GetOverlaySnapshotQueryHandlerTests` | four bind one placeholder; `Skips_archived_and_unset_variables…` (`:53-73`) binds two but **neither reaches `:63`** (`shift` is Unset → `continue` at `:60`; `unknown` is absent → `continue` at `:49`), so `break` never executes |
| GREEN | `NFR_VariableResolutionLatencyTests` | one placeholder (`:185`) |
| GREEN | every test in `ResolvedTextReachesItsFabTests` | asserts the **push** path (`VariableValueChangedDomainEventHandler`), a different `BuildSnapshotAsync`; and its label carries one placeholder |
| GREEN | `VariableValueChangedPreCommitTests.A_sibling_variable_still_resolves_from_storage` (`:128`) | the two-placeholder-both-resolving test — exercises the **other** loop (`VariableValueChangedDomainEventHandler.cs:87-119`), untouched by M1 |
| GREEN | `PlaceholderParserTests`, `ResolverTests`, `InMemoryReverseIndexTests` | code not touched |

**M1 isolates.** Exactly one assertion in the suite flips, and it is the new
one. If anything else goes red, the prediction is wrong and that is the
finding — report it rather than adjusting the mutation to fit.

### M2 — optional second data point (deliberately non-isolating)

Change `PlaceholderParser.Substitute` (`:58`) to
`PlaceholderRegex().Replace(labelText, evaluator, 1)`.

**Prediction:** the new happy path fails **and** `VariableValueChangedPreCommitTests:128`
fails. `PlaceholderParserTests.Substitute_leaves_literal_when_resolver_returns_null`
(`:45-52`) and `ResolverTests.Mixes_resolved_and_literal_placeholders` (`:66-73`)
**stay green**, because in both the second placeholder resolves to `null` and
its literal is the expected output either way.

M2 is recorded to make the honest limit explicit: the new test would also catch
a broken substitution mechanism, but it is **not needed** for that — an existing
unit test already fails there. M1 is the mutation that measures what this slice
actually adds. Run M2 only if M1's result is ambiguous.

## Risks

- **Flake on the async index.** Mitigated by the two-name readiness wait above.
  The 30 s ceiling from `NFR_VariableResolutionLatencyTests:148` is the
  precedent; a timeout must name both variables and the overlay in its message.
- **Docker required** (ADR-0103). The test cannot run in the `backend` job; it
  lands in `integration` and its cost is one more case on an already-booted
  fixture.
- **Value formatting.** `VariableValue.NumberValue(82.5).Render(...)` produces
  culture-invariant `"82.5"` (`ResolverTests:31-37`). Pick values that survive
  invariant formatting — `82.5` and `91.5` do; avoid trailing-zero decimals.

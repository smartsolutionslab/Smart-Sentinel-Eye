# Implementation Plan: The rule engine seam

**Spec**: [`spec.md`](./spec.md) · **Issue**: #1970 · **Branch**:
`refactor/1970-the-seams-section-ix-mandates`

**Phase**: 2 (Plan) of ADR-0037.

---

## 1. Shape of the change

**This feature adds no domain artefact.** No aggregate, no value object, no invariant, no
command, no query, no domain event, no integration event, no migration. Saying so first is
the plan's job, because the template's centre of gravity is a bounded-context slice and
this is not one — it is an extract-interface refactor inside one existing layer.

| Layer | Touched? | Why |
|---|---|---|
| `Automation/Domain` | **No** | `Rule` and its value objects are unchanged. The engine tag ADR-020 also names is OPEN-2, a different issue with a migration. |
| `Automation/Application` | **Yes** | The seam lives here: a new `IRuleEngine`, `RuleEvaluator` implements it, `FabEventIngestedV1Handler` depends on it. |
| `Automation/Infrastructure` | **Yes, one line** | `AutomationInfrastructureModule.cs:49` changes from a concrete registration to an interface registration. |
| `Automation/Api` | **No** | `RulesEndpoints.cs` is untouched. No route, no scope, no DTO moves. |
| `Shared.Kernel` / `Shared.Contracts` | **No** | No message, no DTO, no `V<N>` bump. |
| `src/AppHost` | **No** | No new runtime resource. |
| `.specify/memory/constitution.md` | **Yes, one row — GATED** | §IX's rule-engine row. **OPEN-1: the lane may not do this without explicit human authorisation** (ADR-0144). |
| `apps/` | **No** | No frontend change. `AelHelpPanel.tsx` documents the language, not the engine. |

**Nothing is foundational here.** There is no Shared.Kernel or Shared.Contracts work to
block on, so the orchestrator has no fan-out to plan around — the change is one file
created and three edited, in strict order.

---

## 2. Bounded context and layers

**Context: Automation, alone.** Assembly `SmartSentinelEye.Automation.Application`,
namespace `SmartSentinelEye.Automation.Application.Evaluation`.

The seam sits at the Application layer because that is where the decision it abstracts
already lives. It is **not** a Domain port: `RuleEvaluator` depends on `IRuleCache` and
`ILogger<T>`, reads a `JsonElement`-backed `EvaluationContext`, and returns
`RuleActionEffect` — an Application type. Pushing the interface into Domain would drag
those with it and would be the speculative generality §2.2 of the spec forbids.

**File layout** follows ADR-0093 (per-message-kind Application folders) as the existing
`Evaluation/` folder already does — `IRuleCache.cs` is the precedent for a port declared in
that folder rather than in a separate `Abstractions/` tree. **Mirror it; do not invent a
new folder.**

```
src/Automation/Application/Evaluation/
  IRuleCache.cs          existing — the precedent
  IRuleEngine.cs         NEW
  RuleEvaluator.cs       edited: ": IRuleEngine" on the declaration
  CompiledRule.cs        untouched
  RuleActionEffect.cs    untouched
```

---

## 3. The contract

```csharp
public interface IRuleEngine
{
    IReadOnlyList<RuleActionEffect> Evaluate(
        FabIdentifier fab, string triggerSource, string triggerKind, EvaluationContext context);
}
```

**Verbatim from `RuleEvaluator.cs:26`.** Spec §4.2 carries the reason: substituting the
existing `TriggerSource` / `TriggerKind` value objects here would introduce a throwing
validation where a cache miss silently occurs today — a behaviour change on the
event-to-overlay path. Recorded as OPEN-3.

**Documentation on the interface must say what the seam is for and nothing more**: that a
v2 engine substitutes here (ADR-020, §IX). It must **not** name AEL, CEL, or any language —
#1969 is blocked pending a human decision and this interface may not presuppose its answer.

**No `Option<T>`, no `Result<T, Error>`.** Neither is added, because neither exists on the
method today. `Evaluate` returns an empty list for "nothing matched"
(`RuleEvaluator.cs:37`), which is a total function with no absence to model — ADR-0141 is
satisfied by having no nullable parameter, not by wrapping a return that already cannot be
null. Introducing a `Result` here would be a behaviour change (callers would have to
unwrap) and is out of scope.

---

## 4. Entities, value objects, invariants

**None are added or changed.** For the record, so a reviewer need not re-derive it:

- `Rule` (`src/Automation/Domain/Rule/Rule.cs:19`) — `AggregateRoot<RuleIdentifier>`, every
  property a value object (`Fab`, `Name`, `TriggerSource`, `TriggerKind`, `Predicate`,
  `Action`, `State`, `Creation`, `PublishedAt?`, `ArchivedAt?`). §II-clean today and
  untouched by this feature.
- Invariants (`Rule.Create`, `Publish`, `Archive`) — untouched.
- `CompiledRule` (`Application/Evaluation/CompiledRule.cs:13`) — an Application-layer
  pre-parse cache entry, not a domain model. Untouched.

**§II is not engaged** — `PrimitiveBoundaryTests` walks aggregate state from domain roots
and asserts `roots.Count.ShouldBe(11)`; this feature adds no root and no property. The test
must pass **unmodified**; if it does not, something in the diff reached Domain and the
change is out of scope.

---

## 5. Messaging

**Unchanged, in both directions.**

- **Inbound**: `FabEventIngestedV1` is consumed by `FabEventIngestedV1Handler.Handle`
  (`:35`). Its Wolverine registration, queue, and per-module isolation (ADR-0088) are
  untouched.
- **Outbound**: the integration event the handler publishes via `IEventBus` is unchanged —
  same type, same version, same payload, same routing.
- **No domain event becomes an integration event.** Nothing is promoted.

The handler already destructures its message (CLAUDE.md house rule); that line is not
edited. **The only edit inside the handler is its constructor parameter type**, from
`RuleEvaluator evaluator` to `IRuleEngine engine`, plus the corresponding call at `:69`.
Renaming the parameter is optional and, if done, must not collide with another field name
— `HandlerDeconstructionTests` guards that gap.

---

## 6. Boundary rules

- **No cross-context project reference is added.** Everything is inside
  `SmartSentinelEye.Automation.*`.
- **`Shared.Contracts` is not touched**, so the versioned-message rule is not engaged.
- NetArchTest's boundary rules are not exercised by this diff, and must still pass.
- `EndpointScopeDeclarationTests` is not engaged — no `src/*/Api/*Endpoints.cs` file is
  edited. **If the diff touches `RulesEndpoints.cs`, the change has left its scope.**

---

## 7. DI

`src/Automation/Infrastructure/AutomationInfrastructureModule.cs:49`:

- **before**: `builder.Services.AddScoped<RuleEvaluator>();`
- **after**: `builder.Services.AddScoped<IRuleEngine, RuleEvaluator>();`

**The concrete registration is removed, not supplemented.** Leaving both would let a future
consumer take the concrete type and quietly un-seam the seam — the failure the §IX guard
exists to catch, one layer down where no guard is watching. `AddScoped` is kept: the
lifetime is not a variable here, and `:54`'s comment explains why a fresh evaluator per
scope matters for the cache snapshot.

`AddSingleton<IRuleCache, InMemoryRuleCache>()` (`:48`) and
`AddHostedService<RuleCacheSeederHostedService>()` (`:50`) are untouched.

---

## 8. Order of work, and the gate in the middle

```
  OPEN-1 resolved by a human  ──────►  T101 declare IRuleEngine
        (BLOCKS EVERYTHING)                     │
                                                ▼
                          T102 RuleEvaluator implements it
                                                │
                                                ▼
                      T103 handler + T104 DI  (must land together)
                                                │
                                                ▼
                          T105 constitution §IX row  ──►  guard flips green
```

**T103 and T104 are one commit.** Each commit must build on its own (ADR-0087 rebase-merge,
CLAUDE.md): changing the handler's parameter without changing the registration leaves the
container unable to resolve it, and the reverse leaves the handler unresolvable. Splitting
them breaks `git bisect` forever.

**T105 cannot precede T101.** The guard is symmetric — updating §IX while `IRuleEngine` is
still absent fails in the `whenAbsent` direction, which is the clerical-error exemption
ADR-0130 exists to prevent.

---

## 9. Risks, and what each would look like

| Risk | How it shows | Response |
|---|---|---|
| OPEN-1 is not resolved | `FoundingDecisionRecordTests` red, and the only fix the lane can reach is editing a guard | **Block the issue.** Do not touch the test. |
| Scope creep into a registry or factory | A second type appears alongside `IRuleEngine` | Review block — spec §2.2 names it |
| The value-object temptation (OPEN-3) | `TriggerSource`/`TriggerKind` appear in the signature | Behaviour change; reject and file |
| US2 moves an error message | A `DryRunRuleError` assertion needs editing | Stop, split US2 out; the behaviour moved |
| The automation leg regresses | spec 106's p95 gate fails | Re-run twice before concluding — the first run after machine churn reads like a regression |
| A worktree is created | C: fills, Docker engine stops answering | **Do not create one.** ~11 GB free. |

---

## 10. Alternatives considered

**Do nothing / defer to v2.** Rejected by the issue's own argument and by §IX: the seam's
whole purpose is that adding it later is the breaking change it was meant to avoid.

**Put `IRuleEngine` in `Automation/Domain`.** Rejected — it would drag `IRuleCache`,
`ILogger<T>` and a `JsonElement`-backed context into Domain, violating "Pure domain. No
I/O, no framework refs."

**Make `AelInterpreter` non-static and inject it.** Rejected as the wrong seam. §IX and
ADR-020 name the *engine*, not the interpreter; a v2 visual-workflow engine has no AEL
expression tree to evaluate, so an interface over `AelInterpreter` would be the seam a v2
engine could not use. It is also a wider diff — 4 call sites and 3 test files — for a
smaller result.

**Ship US1 and US2 as one story.** Rejected: their error handling is opposite
(swallow vs. report, spec §3 US2), so folding them together risks a behaviour change inside
a change declared behaviour-preserving. Two stories keeps the colour honest.

**Add a second implementation to demonstrate the seam.** Rejected — speculative generality;
§IX's carve-out authorises the interface, not a plugin ecosystem. The integration fixture's
stub (spec §6 step 5) demonstrates substitutability without shipping anything.

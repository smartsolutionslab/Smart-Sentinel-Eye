# Feature Specification: The rule engine seam

**Feature Branch**: `refactor/1970-the-seams-section-ix-mandates`

**Created**: 2026-09-09

**Status**: Draft — phase 1 (Specify) of ADR-0037, run in the ADR-0144 autonomous lane.

**Input**: #1970 — *"Constitution §IX mandates two v1 strategy interfaces that do not
exist."* Found by spec 047's audit (ADR-0130). The issue proposes *"two independent
slices, either orderable first. Each is an interface plus moving existing call sites
behind it — no behaviour change."*

**Scope**: **the rule-engine slice only.** `IRuleEngine` (ADR-020, §IX row 1). The
authorization slice (`IAuthorizationDecisionPoint`, ADR-023, §IX row 2) is deliberately
**not** in this spec — see §2.4, which recommends it be split into its own issue and
spec. The camera-adapter row (#1973) is out of scope by the issue's own terms.

**ADRs**: [ADR-0037](../../docs/adr/0037-guided-phased-workflow.md) (the phases),
[ADR-0144](../../docs/adr/0144-an-autonomous-delivery-lane.md) (the lane),
**ADR-020** (`docs/adr/0000-initial-decisions.md:34` — the mandate),
[ADR-0099](../../docs/adr/0099-hand-rolled-ael.md) (the language that exists),
**ADR-0130** (the audit that found the gap), ADR-0084 (max 4 parameters),
ADR-0051 (per-context DI), ADR-0141 (`Option<T>`, advisory).

---

## 1. The investigation

Phase 1 is investigation before design. Everything below was verified in this repo, at
`9bd2f216`, with the file:line evidence quoted. Nothing is taken from the issue on trust.

### 1.1 The absence verdict — the premise holds, with one correction

| Symbol | Declared in `src/`? | Evidence |
|---|---|---|
| `IRuleEngine` | **No** | `grep -rn "IRuleEngine" src/ --include=*.cs` gives 0 hits. |
| `IRuleStrategy`, `EngineTag` | **No** | 0 hits each. |
| Any seam-shaped interface in Automation | **Partially** | A regex over `interface I…` with eleven stems (Rule, Engine, Eval, Expression, Condition, Policy, Author, Permission, Access, Decision, Strategy) returns `IRuleCache`, `IRuleQuerySource`, `IRuleRepository`. All three are **storage** ports. None is an evaluation seam. |

The absence was re-checked by the **invariant half of the shape** — the `interface I…`
declaration form with a stem disjunction — not by the issue's single spelling. That is the
check spec 040 and #1714 did not do, and it is what turned up the correction in §2.4 on
the authorization side. On this side it confirms the issue: **`IRuleEngine` does not exist
under any name.**

**One thing the issue's grep found and did not report as a finding**: the single hit is
`tests/Architecture.Tests/FoundingDecisionRecordTests.cs`, which is not a stray mention.
See §1.4 — it is a gate on this feature.

### 1.2 What exists instead

Rule evaluation is a concrete class calling a **static** interpreter.

| Piece | file:line | Shape |
|---|---|---|
| `AelInterpreter` | `src/Automation/Application/Ael/AelInterpreter.cs:17` | **`public static class`**, one public method `Evaluate(AelExpression, EvaluationContext)` returning `AelValue` (`:19`). Never in DI — it cannot be. |
| `RuleEvaluator` | `src/Automation/Application/Evaluation/RuleEvaluator.cs:22` | `public sealed class RuleEvaluator(IRuleCache cache, ILogger<RuleEvaluator> logger)` — **no interface**. |
| its one public method | `RuleEvaluator.cs:26` | `IReadOnlyList<RuleActionEffect> Evaluate(FabIdentifier fab, string triggerSource, string triggerKind, EvaluationContext context)` |
| DI registration | `src/Automation/Infrastructure/AutomationInfrastructureModule.cs:49` | `builder.Services.AddScoped<RuleEvaluator>();` — **registered as the concrete type**. |
| its one production consumer | `src/Automation/Application/EventHandlers/FabEventIngestedV1Handler.cs:30` | constructor parameter `RuleEvaluator evaluator`; the call is `:69`. |

### 1.3 The call-site inventory — the cost of this change

**`AelInterpreter.Evaluate` — 4 production call sites, in exactly 2 files:**

| # | file:line | Path |
|---|---|---|
| 1 | `RuleEvaluator.cs:75` | live — predicate |
| 2 | `RuleEvaluator.cs:96` | live — value expression |
| 3 | `DryRunRuleQueryHandler.cs:94` | **dry run** — predicate |
| 4 | `DryRunRuleQueryHandler.cs:109` | **dry run** — value expression |

**`RuleEvaluator` — 1 production consumer, 1 DI line:**
`FabEventIngestedV1Handler.cs:30` and `AutomationInfrastructureModule.cs:49`.

`AelParser.Parse` has 4 further production sites (`CreateRuleCommandHandler.cs:36,:50`,
`CompiledRule.cs:63,:65`). Those are **compilation, not evaluation**, and a v2 engine that
brought its own language would replace them via the engine tag, not via this seam. They
are named so the count is honest, and excluded with a reason.

**So the issue's claim that this is cheap now is correct, and the number is small: two
production edits (one constructor parameter, one DI line) plus one class declaration** for
the live path. Test call sites — 3 direct `AelInterpreter.Evaluate` calls in
`Automation.Application.Tests/Ael/` — are unaffected: they test the interpreter, which
this feature does not touch.

### 1.4 The gate this feature must pass, which the issue does not mention

`tests/Architecture.Tests/FoundingDecisionRecordTests.cs:36-49` is a **consistency
guard**, not a text pin. It reads the code and the constitution and fails when they
disagree, in either direction:

- `existsInCode` — `DeclaredInSource("IRuleEngine")`, a scan of `src/` for a non-comment
  declaration (`:180-207`).
- `recordedAbsent` — whether the constitution contains the literal
  `` `IRuleEngine` — absent ``.
- The assertion is `recordedAbsent.ShouldBe(!existsInCode)` (`:168`).

**The moment `IRuleEngine` is declared in `src/`, this test goes red** unless constitution
§IX's rule-engine row stops recording it as absent. The guard's own failure message says
so: *"Update the row — this guard pins the record against drift, never against progress."*

That is a **constitution edit**, and ADR-0144 §"What the lane may not do" says the lane
*"may not amend the constitution or write an ADR autonomously."* This is **OPEN-1** in §5
and must be resolved by a human before phase 4 starts. It is not a design question — the
design is settled by §IX — it is a governance question about whether correcting a status
column counts as an amendment.

Two related guards were checked and are **not** engaged:

- `PrimitiveBoundaryTests` (`tests/Architecture.Tests/PrimitiveBoundaryTests.cs:64`) walks
  **aggregate state** from domain roots (`:78-96`) and asserts `roots.Count.ShouldBe(11)`
  (`:92`). This feature adds no aggregate and no domain-model property, so §II's automated
  rule is not engaged. See §4.2 for what §II *does* mean here.
- `EndpointScopeDeclarationTests` — a 2181-line source-text guard over
  `src/*/Api/*Endpoints.cs`. Automation's endpoints are not edited by this feature.

---

## 2. What this feature is

### 2.1 The problem, in one line

§IX mandates a strategy interface **in v1** so v2 lands without breaking changes. For the
rule engine it does not exist, so a v2 visual-workflow engine (n8n / Node-RED, per ADR-020)
cannot be substituted without editing the handler that consumes the evaluator — which is
the breaking change the seam exists to avoid.

### 2.2 What "smallest" means here, and the trap

This is the exact shape the Karpathy guideline *no speculative generality* forbids.
Constitution §IX carves it out — but **narrowly**: it authorises *the seam §IX names,
scoped as §IX scopes it*.

**This feature therefore does NOT include**, and any of these appearing in the diff is a
review block:

- a plugin registry, an `IRuleEngineFactory`, or engine selection by configuration;
- a second `IRuleEngine` implementation "to prove the seam works" — a fake in a test is not
  a second implementation, and none is needed;
- the **engine tag** ADR-020 also names (*"rule definitions are engine-tagged"*). There is
  no `Engine` / `Language` property on `Rule` (`src/Automation/Domain/Rule/Rule.cs:19-50`)
  and no such column (`RuleConfiguration.cs:23-127`). Adding one is a persisted-state
  change needing a migration — **behaviour-changing, a different colour of phase 4a, and a
  different issue**. Filed as OPEN-2. A seam with one engine does not need a tag to say
  which engine it is;
- any change to `AelInterpreter`, `AelParser`, `AelLexer`, or the AEL grammar;
- any change to `Rule`, `IRuleCache`, `CompiledRule`, or persistence.

**On naming.** ADR-020 names CEL; the built language is AEL (ADR-0099). That divergence is
one of #1969's rows, #1969 is blocked pending a human decision, and it does not block this.
`IRuleEngine` is therefore named exactly as §IX and ADR-020 name it and **carries no
language in its name, its members, or its documentation** — so nothing here presupposes an
answer to #1969.

### 2.3 The interface

One member, because one live decision exists.

```csharp
namespace SmartSentinelEye.Automation.Application.Evaluation;

public interface IRuleEngine
{
    IReadOnlyList<RuleActionEffect> Evaluate(
        FabIdentifier fab, string triggerSource, string triggerKind, EvaluationContext context);
}
```

**The signature is `RuleEvaluator.Evaluate`'s, character for character.** That is a
deliberate decision, not laziness — see §4.2, where changing it is shown to be a behaviour
change.

### 2.4 One issue or two — the recommendation is TWO, rule engine first

The issue says two independent slices. The investigation confirms they are independent and
adds a reason to order them:

| | Rule engine | Authorization |
|---|---|---|
| Bounded context | Automation only | `ServiceDefaults` + 9 contexts |
| Seam exists in part today? | **No** | **Yes** — `IFabAuthorizationGuard` (`src/ServiceDefaults/Authorization/IFabAuthorizationGuard.cs:37`), a DI-injected interface with one implementation, covering the *fab* half of every decision. The audit's grep for `DecisionPoint` could not see it. |
| Production edits | **2** + 1 class declaration | 2 `RequireAssertion` lambdas (`RequireScopeExtensions.cs:50`, `AuthenticationDefaults.cs:98`) + 1 Application-layer straggler (`AuthorizeWhepCommandHandler.cs:76-80`, which hard-codes `"sse.streams.read"` / `"sse.management"` at `:29,:31`) + a decision about whether `IFabAuthorizationGuard` folds in |
| Security boundary | No | **Yes** — constitution §Code Review requires `ultrareview` on Identity / EventIngestion / StreamKeeper |
| Nearby guard risk | None | `EndpointScopeDeclarationTests`, 2181 lines, parses endpoint **source text** for `.RequireAuthorization` / `.AllowAnonymous` (`:1391`, `:1403`) |

**Take the rule engine first.** It is contained in one context, touches no security
boundary, and needs no `ultrareview`. Both slices independently trip the §IX guard
(OPEN-1), and the guard tests each row separately — `:36-37` is a `[Theory]` with one
`[InlineData]` per symbol, and the file's own documentation at `:21-25` records that an
earlier draft sharing one sentinel between rows was fixed in review. So building one at a
time works and leaves the other eligible.

**A finding for the authorization issue when it is written, recorded here because it was
found here**: `RequireScope` (`RequireScopeExtensions.cs:76`) has **zero call sites** in
`src/` and `tests/`. The real mechanism is `.RequireAuthorization(Scope.Sse.*)` — 39 sites
across 12 files — all resolving to **one** `RequireAssertion` lambda. The authorization
seam is therefore much cheaper than "39 call sites" suggests, and the endpoints need not be
touched at all. The issue's cost framing for that slice should be corrected before it is
taken.

---

## 3. User stories

### US1 — P1 — a v2 rule engine can be substituted without editing its consumer

**Why first, and why it ships alone**: this is the whole §IX obligation for row 1. It is
independently shippable, independently observable, and nothing below is needed for it.

**Acceptance scenarios**

```gherkin
Scenario: the seam exists and the record agrees (the §IX obligation)
  Given the repository declares IRuleEngine in src/
  When FoundingDecisionRecordTests runs
  Then constitution §IX's rule-engine row no longer records the interface as absent
   And the guard passes in the "whenBuilt" direction

Scenario: happy path — an ingested event still fires its rule (behaviour preserved)
  Given an Active rule in fab "munich" triggered by (source "mqtt", kind "sensor")
    And its predicate matches the event payload
  When FabEventIngestedV1 for that fab arrives
  Then the handler resolves IRuleEngine from DI, not RuleEvaluator
   And the same RuleActionEffect list is produced, in the same declaration order
   And the same integration event is published

Scenario: conflict — two rules write the same SystemVariable
  Given two Active rules in one fab both matching the event
    And both are SetVariableValue actions on the same variable
  When the event is evaluated through IRuleEngine
  Then last-write-wins in declaration order, exactly as before (FR-012)

Scenario: bad input — one rule's predicate throws at runtime
  Given three Active rules match, and the second raises InvalidOperationException
  When the event is evaluated through IRuleEngine
  Then the second rule is logged and skipped
   And the first and third still produce their effects
   And the call does not throw

Scenario: no candidates
  Given no Active rule matches (fab, source, kind)
  When the event is evaluated through IRuleEngine
  Then an empty effect list is returned

Scenario: auth / isolation — fab scoping is unchanged
  Given rules exist in fab "munich" and fab "berlin"
  When an event from "munich" is evaluated
  Then only "munich" rules are considered (the #1252 regression), unchanged by the seam
```

**A note on the auth scenario.** `RuleEvaluator` is not an endpoint and performs no
authorization, so an endpoint-auth scenario would be theatre. The isolation guarantee it
*does* carry is fab scoping, so that is the scenario written. Endpoint authorization for
`/rules` is `RulesEndpoints.cs:38,:72` and is untouched by this feature.

### US2 — P2 — the dry run cannot silently disagree with the engine

**Deliberately P2, and deliberately not required for US1 to ship.**

`DryRunRuleQueryHandler` is a **second evaluation path** that calls the static interpreter
directly (`:94`, `:109`) and re-implements the truthiness rule. Its own comment at `:96-98`
reads: *"Same truthiness rule as RuleEvaluator — a non-boolean result does NOT match. A dry
run that disagreed with the live pipeline would be worse than no dry run at all."*

A v2 engine plugged in behind `IRuleEngine` would change the live path and **not** the dry
run — producing exactly the disagreement that comment forbids. So the seam as shipped in
US1 is incomplete, and saying so here rather than discovering it at v2 is the point.

It is P2 rather than P1 because folding it in is **not** free: the two paths handle errors
oppositely. `RuleEvaluator.cs:78,:100` catches and **swallows** (log, skip the rule);
`DryRunRuleQueryHandler.cs:114` catches and **reports**
(`DryRunRuleFailures.EvaluationFailed(ex.Message)`, surfaced to the operator). Reusing the
live path's private helpers would therefore change observable API behaviour. US2 needs its
own interface member that propagates, and its own tests.

```gherkin
Scenario: a dry run and the live pipeline agree on a non-boolean predicate
  Given a Draft rule whose predicate evaluates to the integer 1
  When the operator dry-runs it against a sample event
  Then it reports Matched: false
   And the live pipeline would likewise not fire it

Scenario: a dry run still reports an evaluation error to the operator
  Given a Draft rule whose predicate divides by zero
  When the operator dry-runs it
  Then the response is a failure naming the error, not a silent Matched: false

Scenario: bad request — the sample event is not JSON
  Given a dry-run request whose sample event is not parseable JSON
  When it is submitted
  Then the existing 400 with SampleEventNotJson is returned, unchanged
```

**If US2 cannot be done without changing a `DryRunRuleError` code, message or status, stop
and split it out.** An assertion that has to be edited is evidence the behaviour moved.

---

## 4. Constraints

### 4.1 Latency budget — this feature IS on the SLO path

**Leg: `event → overlay state` (≤ 200 ms).** `RuleEvaluator` runs inside
`FabEventIngestedV1Handler`, which is squarely on that leg. Spec 007 divides the 200 ms
into 50 ms ingest / **100 ms automation** / 50 ms resolve-and-broadcast, and spec 106
measures the automation part against a p95 ≤ 100 ms gate.

**Impact: one interface dispatch per ingested event, replacing one call on a sealed
class.** No allocation, no I/O, no additional await. The change is not measurable against a
100 ms gate.

**The obligation this creates**: spec 106's automation-leg measurement must still pass
after the change, and its figure must be **read and quoted** in the phase-5 note. §IV's own
warning applies — a leg recorded as measured before anyone read its figure claims a
discharge nobody earned. Reasoning about a virtual call is not a measurement.

### 4.2 Constitution §II, and why the signature is copied verbatim

§II bans primitive-typed **state on a domain model**. `IRuleEngine` is an **Application**
port, not a domain model; `PrimitiveBoundaryTests` walks aggregate state only (`:78-96`);
and §II's own scope clause binds *"members that carry state — properties, record
components, and the constructor or factory parameters that set them."* A method parameter
on an application service is none of those. **§II is not engaged.**

That leaves a judgement call, and it is made **against** widening, with a reason:

`RuleEvaluator.Evaluate` takes `string triggerSource, string triggerKind` — and the value
objects `TriggerSource` (`src/Automation/Domain/Rule/TriggerSource.cs:12`) and `TriggerKind`
(`TriggerKind.cs:10`) already exist. Putting them on the seam looks free. **It is not.**
`TriggerSource.From` validates and throws (`:19`, `MaximumLength = 16`). Today a
17-character trigger source simply misses the cache lookup —
`IRuleCache.LookupActive(FabIdentifier, string, string)` at `IRuleCache.cs:25` — and no
rule fires. Behind a value-object parameter it would **throw** instead, on the
event-to-overlay path, inside a message handler. **That is a behaviour change**, and it
would flip this feature's phase-4a colour.

So the seam copies the existing signature. Migrating the trigger pair to value objects —
through `IRuleCache`, `InMemoryRuleCache`, the handler and the evaluator together — is a
real and probably worthwhile refactor, and it is **OPEN-3**, not this feature.

Parameter count is 4, at ADR-0084's limit and unchanged.

**ADR-0141 (`Option<T>` over nullable parameters; advisory, Domain + Application):** this
interface has no nullable parameter and no absence to model. The advisory is satisfied
trivially, and nothing is added in order to satisfy it.

### 4.3 Boundary rules

`IRuleEngine`, `RuleEvaluator`, `RuleActionEffect` and `EvaluationContext` all live in
`SmartSentinelEye.Automation.Application`. **No cross-context project reference is added**
and `Shared.Contracts` is untouched — no message, no DTO, no `V<N>` bump. NetArchTest's
boundary rules are not engaged. No domain event becomes an integration event; the
integration event `FabEventIngestedV1Handler` already publishes is unchanged.

### 4.4 Locked choices this feature inherits

Hand-rolled `ICommandHandler` / `IQueryHandler` plus Wolverine dispatch (ADR-0042, 0057);
per-context `Add<Context>Infrastructure` DI (ADR-0051); xUnit + Shouldly + Moq (ADR-0052);
sentence-style test names (ADR-0053); hand-written builders (ADR-0054); Domain ≥ 90% /
Application ≥ 80% coverage (ADR-0065); SonarAnalyzer metrics (ADR-0084). Nothing here is
chosen anew.

---

## 5. Open items

- **OPEN-1 — BLOCKING. Human decision required before phase 4 starts.** Declaring
  `IRuleEngine` turns
  `FoundingDecisionRecordTests.Section_nine_agrees_with_the_code_about_its_strategy_interfaces("IRuleEngine")`
  red until constitution §IX's rule-engine row stops recording the interface as absent.
  ADR-0144 forbids the autonomous lane from amending the constitution. **Three ways out,
  and the choice is not the architect's:**
  1. the human authorises this specific row edit as a *status correction the guard
     demands*, with a patch-level version bump and no new ADR;
  2. the human writes the amendment (and any ADR) and the lane implements against it;
  3. the issue takes `agent:blocked` with this as the reason.

  The architect's reading, offered and not acted on: §IX already mandates the interface, so
  building it makes no new decision — the row is a *record of state*, and the guard exists
  precisely to force it to move. But precedent cuts the other way: version 1.5.0 bumped for
  exactly this kind of factual correction, with ADR-0130 behind it. **Do not let the lane
  decide this.**
- **OPEN-2 — file separately.** ADR-020 also mandates *"rule definitions are
  engine-tagged"*. No tag exists on `Rule` or in `RuleConfiguration`. Adding one is a schema
  plus migration change, behaviour-changing, and out of scope here. §IX's *strategy
  interface* column is discharged without it — but ADR-020's full text is not, and that
  should be visible on the board rather than assumed closed by this PR.
- **OPEN-3 — file separately.** `triggerSource` / `triggerKind` are `string` through
  `IRuleCache`, `RuleEvaluator` and the handler while the value objects exist. Migrating
  them is behaviour-changing at the boundary (§4.2) and is not this feature.
- **OPEN-4 — for whoever writes the authorization spec.** `RequireScope` has zero call
  sites; the real seam point is one `RequireAssertion` lambda; `IFabAuthorizationGuard`
  already covers the fab half. §2.4 has the evidence.
- **Not verified.** Nothing was built or run — phases 1–3 write no code and boot no stack,
  and C: is at ~96%. Every claim above is a source reading at `9bd2f216`. In particular,
  *that the covering tests in §7 are green today* is asserted from their existence and CI's
  state on `develop`; it is **not observed**. Phase 4a's first act is to observe them green
  before anything changes.

---

## 6. Independent end-to-end test procedure

Runnable by someone who did not write the code, proving the seam without reading the diff.

1. **Before the change**, with the AppHost stopped:
   `dotnet test tests/Automation.Application.Tests` and `dotnet test tests/Architecture.Tests`
   — capture both green. This is phase 4a's characterisation baseline (§7).
2. `grep -rn "interface IRuleEngine" src/` — one hit, in
   `src/Automation/Application/Evaluation/`.
3. `grep -n "IRuleEngine" src/Automation/Infrastructure/AutomationInfrastructureModule.cs`
   — one registration; and `grep -n "AddScoped<RuleEvaluator>" …` — **no hit**. The concrete
   registration is gone, not merely supplemented.
4. `grep -n "RuleEvaluator" src/Automation/Application/EventHandlers/FabEventIngestedV1Handler.cs`
   — **no hit**. The consumer names only the interface. *This is the step that proves the
   seam is real: an interface nobody depends on is decoration.*
5. **The substitution test, which is the whole point.** Boot the Aspire stack, publish a
   `FabEventIngestedV1` for a fab with one Active matching rule, and observe the resulting
   variable change in `management-web`. Then, **in the integration fixture only**, register
   a stub `IRuleEngine` returning a fixed effect, publish the same event, and observe the
   *stub's* effect arrive. The stub is a test double and does not ship — §2.2 forbids a
   second production implementation.
6. `dotnet test tests/Architecture.Tests --filter FoundingDecisionRecordTests` — green,
   which is only possible if §IX's row was updated (OPEN-1).
7. Re-run spec 106's automation-leg measurement and quote the p95 in the phase-5
   verification note (§4.1). Per the memory note on measurement runs, **run it twice** — the
   first run after machine churn reads like a regression.

---

## 7. Phase 4a colour

**BEHAVIOUR-PRESERVING → characterisation, observed GREEN.** The issue's claim is
confirmed, not merely accepted: US1 changes a type name in a constructor parameter and a DI
registration. No branch, no validation, no ordering, no persisted shape and no wire format
moves — and §4.2 identifies the one temptation that *would* have changed behaviour, and
rejects it.

The covering tests are captured passing **before** the change and must pass **unmodified**
after. **An assertion that has to be edited is evidence the behaviour moved: block, do not
adjust.**

| Test | Why it covers this |
|---|---|
| `tests/Automation.Application.Tests/Evaluation/RuleEvaluatorTests.cs` | The evaluator's own behaviour — ordering, skip-on-error, empty result, fab scoping. |
| `tests/Automation.Application.Tests/EventHandlers/FabEventIngestedV1HandlerTests.cs` | The consumer whose constructor parameter changes. |
| `tests/Automation.Application.Tests/Ael/AelInterpreterTests.cs` | The interpreter, untouched — proves the diff did not reach it. |
| `tests/Automation.Application.Tests/Queries/RuleQueryHandlerTests.cs` | Holds the **9 dry-run tests** (there is no separate `DryRunRuleQueryHandlerTests.cs`). US2's baseline; also proves US1 left the dry run alone. |
| `tests/Architecture.Tests/PrimitiveBoundaryTests.cs` | §II, including `roots.Count.ShouldBe(11)`. Must pass untouched. |
| `tests/Integration.Tests` — Automation suites | The live path end to end. |
| spec 106's automation-leg measurement | The `event → overlay state` leg (§4.1). |

**The one exception, and it is not an exemption.** `tests/Architecture.Tests/FoundingDecisionRecordTests.cs`
is *designed* to flip when the seam is built; its assertion is
`recordedAbsent.ShouldBe(!existsInCode)` (`:168`), and **that expression is not edited** —
the constitution moves instead (OPEN-1). If anyone proposes editing that assertion, or the
`[InlineData("IRuleEngine", …)]` at `:36`, it is a gate being weakened and ADR-0144 forbids
it outright.

If any *other* test in the table has to be edited, phase 4a has failed and the issue is
blocked.

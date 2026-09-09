# Tasks: The rule engine seam

**Spec**: [`spec.md`](./spec.md) · **Plan**: [`plan.md`](./plan.md) · **Issue**: #1970

**Phase**: 3 (Tasks) of ADR-0037. Tracking is at **feature granularity** — #1970 on
Project #13. No per-task issues (CLAUDE.md phase-3 row; the `[TNNN]` series stopped at
#1845 / spec 028). Add the feature issue by hand if it is not already on the board:

```sh
gh project item-add 13 --owner smartsolutionslab --url <issue-url>
```

Verify with `--limit 2000` — `item-list` defaults to 30 and a filled board looks empty.

---

## Phase 4a colour: **CHARACTERISATION, OBSERVED GREEN**

Behaviour-preserving (spec §7). The covering tests are captured **passing before** anything
changes and must pass **unmodified** afterwards. A red test here is a regression, not a
step. **An assertion that has to be edited is evidence the behaviour moved — block, do not
adjust.**

**The one designed exception**: `FoundingDecisionRecordTests` flips direction when the seam
is built. Its assertion is not edited; the constitution row moves instead (T105). Editing
that test — or its `[InlineData("IRuleEngine", …)]` — is weakening a gate, which ADR-0144
forbids outright.

---

## Read this before starting

**T000 is a hard gate and the lane cannot open it itself.** OPEN-1: declaring `IRuleEngine`
turns the §IX guard red until constitution §IX's rule-engine row is edited, and ADR-0144
says the lane may not amend the constitution. Nothing below T000 may begin without an
explicit human answer.

**Parallelism: there is almost none, and that is the honest answer.** The whole change is
one new file and three edits in a single project, in strict dependency order. `[P]` appears
on exactly two tasks — the two independent *baseline captures* in T001/T002, which read
disjoint test projects and write nothing. Marking the implementation tasks `[P]` would be a
decoration; ADR-0109's marker means disjoint files *and* no ordering, and neither holds.

**Also:**

- **Do not create a git worktree.** C: was at ~96% (~11 GB free) when this was written; a
  full disk stops the Docker engine and only a GUI restart recovers it.
- **One machine, one Aspire stack.** Every task that boots the fixture is serial with every
  other one, regardless of files.
- **Stop the stack before any `dotnet build`** — a running AppHost holds the service
  binaries and MSB3027 reads exactly like a broken build.

---

## Phase 0 — the gate

- [ ] **T000 [US1] BLOCKING — human decision on OPEN-1.** Ask the orchestrator/owner which
      of spec §5's three routes applies to constitution §IX's rule-engine row. **Record the
      answer in the PR body.** If the answer is route 3, apply `agent:blocked` with the
      reason and stop — the rest of this file does not run.

---

## Phase 1 — characterisation baseline (blocks all implementation)

- [ ] **T001 [US1]** Stop any running AppHost
      (`Get-Process SmartSentinelEye.AppHost -ErrorAction SilentlyContinue | Stop-Process -Force`)
      and confirm no `testhost` process names this repo. Container count is not a liveness
      test — check for the process.
- [ ] **T002 [P] [US1]** `dotnet test tests/Automation.Application.Tests` — **capture the
      verbatim passing output.** This is the characterisation evidence and it is quoted in
      the PR body. Covers `RuleEvaluatorTests`, `FabEventIngestedV1HandlerTests`,
      `Ael/AelInterpreterTests.cs`, and the 9 dry-run tests that live inside
      `Queries/RuleQueryHandlerTests.cs`.
- [ ] **T003 [P] [US1]** `dotnet test tests/Architecture.Tests` — **capture the verbatim
      passing output**, and specifically that
      `Section_nine_agrees_with_the_code_about_its_strategy_interfaces` passes today in the
      `whenAbsent` direction, and that `PrimitiveBoundaryTests` passes.
- [ ] **T004 [US1]** If either baseline is already red on this branch, **stop**. A
      characterisation refactor with no green baseline is a rewrite; fix or report the
      pre-existing failure first, do not build on it.

*T002 and T003 are `[P]`: different test projects, read-only, no fixture boot, no shared
file. They are the only genuinely parallel work in this feature.*

---

## Phase 2 — US1 (P1): the seam

- [ ] **T101 [US1]** Create `src/Automation/Application/Evaluation/IRuleEngine.cs` with the
      single member, **copied verbatim** from `RuleEvaluator.cs:26`:
      `IReadOnlyList<RuleActionEffect> Evaluate(FabIdentifier fab, string triggerSource, string triggerKind, EvaluationContext context);`
      XML doc states the seam's purpose (a v2 engine substitutes here — ADR-020, §IX) and
      **names no expression language** — not AEL, not CEL. #1969 is blocked and this
      interface may not presuppose its answer.
      *Do not add: a factory, a registry, an engine-selection knob, a second member.*
- [ ] **T102 [US1]** `RuleEvaluator.cs:22` — add `: IRuleEngine` to the declaration. **No
      other edit to this file.** The method body, the private helpers, the catch clauses at
      `:78` and `:100`, and the logging are untouched.
- [ ] **T103 [US1]** `FabEventIngestedV1Handler.cs:30` — change the constructor parameter
      from `RuleEvaluator evaluator` to `IRuleEngine engine` and update the call at `:69`.
      If the parameter is renamed, the new name must not collide with another field name —
      `HandlerDeconstructionTests` fails the build on that.
- [ ] **T104 [US1]** `AutomationInfrastructureModule.cs:49` — replace
      `AddScoped<RuleEvaluator>()` with `AddScoped<IRuleEngine, RuleEvaluator>()`. **Remove
      the concrete registration; do not keep both.** Lifetime stays `Scoped` (see `:54`).
      **T103 + T104 land in ONE commit** — each commit must build on its own under
      rebase-merge (ADR-0087), and either alone leaves the container unable to resolve the
      handler.
- [ ] **T105 [US1]** Constitution `.specify/memory/constitution.md` §IX — rule-engine row:
      stop recording `` `IRuleEngine` — absent ``; record the interface as present, citing
      this spec. **Only under T000's answer.** Version footer and amendment history per the
      route T000 chose. **Must come after T101**, never before — the guard is symmetric and
      fails in the `whenAbsent` direction too.
- [ ] **T106 [US1]** Re-run T002 and T003. **Every assertion must pass unmodified**, except
      that the §IX guard now passes in the `whenBuilt` direction. Quote the output.
- [ ] **T107 [US1]** Grep verification, spec §6 steps 2–4 — in particular
      `grep -n "RuleEvaluator" src/Automation/Application/EventHandlers/FabEventIngestedV1Handler.cs`
      returns **nothing**. An interface no consumer depends on is decoration, not a seam.
- [ ] **T108 [US1]** Integration: run the Automation suites in `tests/Integration.Tests`.
      Serial — boots the fixture.
- [ ] **T109 [US1]** Add the substitution proof (spec §6 step 5) to the integration fixture:
      a stub `IRuleEngine` returning a fixed effect, registered in the fixture only,
      producing the stub's effect for the same published event. **This is a test double and
      must not ship in `src/`.** This is the only new test in US1, and it is not a
      characterisation test — it is the observable evidence that the seam is real.
- [ ] **T110 [US1]** Phase 5: re-run spec 106's automation-leg measurement and **read the
      p95 off the run**, twice (the first run after machine churn reads like a regression).
      Quote both figures in the verification note. §IV's leg is `event → overlay state`; a
      figure not read is not a measurement.

**US1 is independently shippable and complete at T110.** Everything below may be dropped
without weakening it.

---

## Phase 3 — US2 (P2): the dry run behind the same seam

**May be deferred to its own PR. Do not start it until US1 is green.**

- [ ] **T201 [US2]** Capture the dry-run tests green. They live in
      `tests/Automation.Application.Tests/Queries/RuleQueryHandlerTests.cs` — **9 tests; there
      is no `DryRunRuleQueryHandlerTests.cs`**, so do not go looking for one and do not
      conclude the path is uncovered. Record the exact `DryRunRuleError` codes, messages and
      status codes asserted today — **these are the assertions that must not move.**
- [ ] **T202 [US2]** Add a second member to `IRuleEngine` for single-rule evaluation that
      **propagates** evaluation errors rather than swallowing them. It may not reuse
      `RuleEvaluator`'s private `TryEvaluatePredicate` / `TryEvaluateValueExpression` — those
      log and skip (`:78`, `:100`), which is the opposite of what the dry run must do.
- [ ] **T203 [US2]** `DryRunRuleQueryHandler` takes `IRuleEngine` and stops calling
      `AelInterpreter` directly (`:94`, `:109`). The existing try/catch at `:114` and its
      `DryRunRuleFailures.EvaluationFailed(ex.Message)` mapping stay exactly as they are.
- [ ] **T204 [US2]** Re-run T201's tests. **If any assertion needs editing, STOP** — the
      behaviour moved, US2 is not behaviour-preserving after all, and it becomes its own
      issue with its own red-first colour. Do not adjust the assertion.
- [ ] **T205 [US2]** Verify the dry-run endpoint end to end via the fixture: the
      non-boolean-predicate case reports `Matched: false`, the divide-by-zero case reports a
      failure naming the error, and a non-JSON sample still returns the existing 400.

---

## Phase 4 — follow-ups to file, not to build

- [ ] **T301** File **OPEN-2**: ADR-020's *"rule definitions are engine-tagged"* is
      undischarged. No tag on `Rule` (`Rule.cs:19-50`), no column
      (`RuleConfiguration.cs:23-127`). Schema + migration, behaviour-changing, **red**
      colour. Note that this PR discharges §IX's *interface* column only.
- [ ] **T302** File **OPEN-3**: `triggerSource` / `triggerKind` are `string` through
      `IRuleCache:25`, `RuleEvaluator:26` and the handler while `TriggerSource` /
      `TriggerKind` value objects exist. Migrating is behaviour-changing at the boundary
      (`TriggerSource.From` throws where a cache miss is silent today), so it is red, not a
      refactor.
- [ ] **T303** Split #1970: open the **authorization** slice as its own issue with spec
      §2.4's corrected framing — `RequireScope` has **zero** call sites; the real seam point
      is the `RequireAssertion` lambda at `RequireScopeExtensions.cs:50` (plus
      `AuthenticationDefaults.cs:98`); `IFabAuthorizationGuard`
      (`src/ServiceDefaults/Authorization/IFabAuthorizationGuard.cs:37`) **already covers the
      fab half**; and the one Application-layer straggler is
      `AuthorizeWhepCommandHandler.cs:76-80`. Flag that it touches a security boundary
      (`ultrareview`) and that `EndpointScopeDeclarationTests` parses endpoint source text.
      Leave it `agent:ready` only after OPEN-1 is settled — it trips the same §IX guard.
- [ ] **T304** Update the `AelInterpreter` description in `specs/007-automation/plan.md:379`,
      which still describes a pooled `Span<AelValue>` stack and a non-static
      `public sealed class AelInterpreter`. The shipped code is a static recursive
      tree-walker. **Optional, and only if it is a one-line correction** — a doc drift found
      in passing is not this feature's job, and mixing it in violates smallest-change.

---

## Dependency summary

```
T000 (HUMAN GATE)
  └─► T001 ─► T002 [P] ─┐
              T003 [P] ─┴─► T004 ─► T101 ─► T102 ─► {T103 + T104, one commit} ─► T105
                                                                                   │
                                                     T106 ─► T107 ─► T108 ─► T109 ─┘
                                                        └─► T110 (phase 5)
                                                                │
                                                                ▼
                                                    T201 ─► T202 ─► T203 ─► T204 ─► T205
```

T301–T304 depend on nothing and block nothing; they are issue-filing, not code.

---

## Definition of done (US1)

1. `IRuleEngine` is declared once in `src/`, with one member and one implementation.
2. `FabEventIngestedV1Handler` names no concrete evaluator; DI registers the interface only.
3. Every test in spec §7's table passes **unmodified**, with the §IX guard now green in the
   `whenBuilt` direction.
4. The substitution proof (T109) shows a stub `IRuleEngine` changing the observed outcome.
5. Spec 106's automation-leg p95 is **read** from two runs and quoted (T110).
6. The PR body carries: the T002/T003 verbatim green baseline, T000's recorded answer, the
   `[US1]` task IDs, and no `Co-Authored-By` footer (ADR-0086).

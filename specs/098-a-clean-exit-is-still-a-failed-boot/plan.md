# Implementation Plan: A clean exit is still a failed boot

**Feature**: 098 | **Issue**: #1930 | **Spec**: `./spec.md`
**Branch**: `fix/1930-a-service-that-exits-says-why`
**Created**: 2026-09-08

---

## Bounded context and layers

**None.** This is the integration-test fixture (ADR-0103, ADR-0068). No domain,
no application, no infrastructure, no API, no contract, no message, no migration,
no frontend file is touched.

The NetArchTest boundary rules are untouched because nothing crosses a boundary.
The `PrimitiveBoundaryTests` and `HandlerDeconstructionTests` guards are
unaffected for the same reason: there is no domain model and no handler here.

**Entities and value objects: none.** The only types involved are Aspire's, read
and never constructed: `CustomResourceSnapshot.State.Text` and
`CustomResourceSnapshot.ExitCode`, reached through dictionaries the fixture
already builds (`Dictionary<string, string> states`,
`Dictionary<string, int?> exitCodes`).

**Invariants**: one, and it already exists —
`ExitedNonZero(int?) => exitCode is not null and not 0`, whose doc comment
("*Did not exit non-zero*, not *exited zero*") is load-bearing and must not be
weakened. This change adds a second, disjoint predicate beside it rather than
relaxing it.

**Messaging**: none. No domain event, no integration event, no `Shared.Contracts`
type.

---

## The shape of the change

Everything is in **one file**:
`tests/Integration.Tests/Fixtures/AspireFixture.cs`.

Everything is tested from **one file**:
`tests/Integration.Tests/Fixtures/AspireFixtureReportSelectionTests.cs`.

Nothing is removed. No existing statement is reordered. The twelve
`WaitForResourceAsync` calls, the `StartupTimeout`, `ThrowIfAnyGatedResourceDiedAsync`,
`FormatMigrationFailureMessage`, `IsHealthy`, `SelectResourcesToReport`,
`FormatResourceStates`, `IsFatalStartupState` and `FatalStartupStates` are all
**untouched**.

### 1. `FormatLikelyCause` grows a second arm (US1)

Today one predicate produces one sentence:

```csharp
string[] died = states.Keys
    .Where(name => !IsHealthy(name, states[name], exitCodes) && ExitedNonZero(name, exitCodes))
    ...
return $"Likely cause: {named} — a non-zero exit is a failure, not a clean finish.\n";
```

After, two disjoint populations produce their own sentences, and the existing one
is byte-identical for the input it already handles.

- **Population A (unchanged)**: `!IsHealthy(…) && ExitedNonZero(…)`
  → `"<name> exited with code <N>"`, joined by `"; "`, closed with
  `" — a non-zero exit is a failure, not a clean finish."`
- **Population B (new)**: `!IsHealthy(…) && !ExitedNonZero(…)` and the state is
  in `EndedStates`
  → `"<name> reached <state> with exit code 0"` or
  `"<name> reached <state> and no exit code was recorded"`, closed with
  `" — a long-running resource that ends during startup is a failed boot, not a clean finish."`

The two sets are disjoint by construction (`ExitedNonZero` is the discriminator),
so a resource is named at most once and the ordering claim
`The_timeout_message_names_the_cause_before_it_lists_the_states` is unaffected.
Where both populations are non-empty, A's sentence goes first — the non-zero exit
is the stronger signal, and it is the sentence readers already recognise.

### 2. `EndedStates` — the narrower question

**Shipped as the set, without the `EndedDuringStartup` wrapper this section
named.** There is one call site, inside `FormatLikelyCause`, and a named
predicate around a single `Contains` at a single call site is the indirection
ADR-0036 asks not to add. Everything below still holds — the set, its exclusions,
and the `OrdinalIgnoreCase` comparison — because none of that was the wrapper.

```
Finished | Exited | Terminated   →  the process ran and ended   →  a cause
FailedToStart                    →  the process never ran       →  NOT a cause
RuntimeUnhealthy                 →  running but unwell          →  NOT a cause
```

**Why this is a new predicate and not a reuse of `IsFatalStartupState`.**
`IsFatalStartupState` is the *sweep's* question — "should the fixture refuse to
return?" — and its set deliberately includes `FailedToStart` and `RuntimeUnhealthy`.
The cause line asks a narrower question: "did this resource *end*?" Naming nine
`FailedToStart` dependents at the top of the report is exactly the noise #2061
removed, and spec 084's `StatesFromTheRunThatMotivatedThis()` is nine of them.

Two predicates over overlapping state sets is how a guard drifts — so the new set
is written as `FatalStartupStates` **minus** the two states that disqualify a
resource from being a cause, with a doc comment saying so. It is a second array
and not a computed subset: three entries spelled out read better than an
`Except`, and the doc comment carries the relationship that the code would
otherwise have to assert.

```csharp
private static readonly string[] EndedStates =
[
    KnownResourceStates.Finished,
    KnownResourceStates.Exited,
    "Terminated",
];
```

with a doc comment stating that it is `FatalStartupStates` minus the two states
that mean "never ran" and "still running", and that `"Terminated"` is a literal
for the reason `FatalStartupStates` already documents (the constant is `internal`
in `Aspire.Hosting.Dcp.Model.ExecutableState`).

`OrdinalIgnoreCase`, matching `Aspire.StringComparers.ResourceState`, as every
other state comparison in this file does.

### 3. `DescribeWhySelected` grows two arms (US2)

The `_ => state` fallback catches exactly the two rows spec §2 tabulated. Two arms
above it, both matching only what the first two arms did not:

```
("Finished" | "Exited" | "Terminated", 0)     → "<state>, exit code 0 — the process ran and stopped"
("Finished" | "Exited" | "Terminated", null)  → "<state> — the process ended and no exit code was recorded"
```

The fallback stays for everything else. The existing arms
(`code != 0` → "the process ran and died", `FailedToStart` → "never reached a
running state") are byte-identical.

### 4. Nothing else

No new field beyond `EndedStates`. Two new private statics, both extracted for
ADR-0084's 30-LOC method limit rather than for reuse: `UnhealthyResources`, the
`!IsHealthy` ordering both populations share, and `DescribeEnd`, population B's
per-resource clause.
No call site changes: `FormatTimeoutMessage` already calls `FormatLikelyCause`
and `FormatFailedResourceReport` already calls `DescribeWhySelected`.

---

## Boundary rules

- **No cross-context project reference.** The change is confined to
  `tests/Integration.Tests`, which references every context by design (ADR-0068).
  Nothing new is referenced.
- **No production assembly is recompiled.** `AspireFixture.cs` lives in a test
  project; `src/` is untouched.
- **`Shared.Contracts` is not involved**, because no message crosses a context.

---

## Constitution and ADR alignment

| Rule | How this plan meets it |
|---|---|
| §II — no primitives on a domain model | No domain model. The `string`/`int?` here are Aspire snapshot fields in a test formatter, the same shape the file already carries. |
| §IV — latency budget | N/A, stated in spec §10. Nothing on the event→overlay path. |
| §Testing — new behaviour starts red | Phase 4a is **red**, and the red is cheap: `FormatLikelyCause` returns `string.Empty` today for both new inputs. |
| ADR-0084 — 300 LOC/file, 30 LOC/method, complexity ≤ 10 | `AspireFixture.cs` is already over 300 lines; `S104` is `NoWarn`'d for test projects (`Directory.Build.props`), as spec 084 recorded. `FormatLikelyCause` gains one `Where`/`Select` pair and one branch — extract the two sentence builders into private statics if S1541 or the 30-LOC limit fires. |
| ADR-0105 — `Ensure.That` for argument guards | Not applicable: every touched member is a `static` formatter whose callers are inside the same class and already hold non-null dictionaries. Adding guards here would be drive-by error handling. |
| ADR-0141 — `Option<T>` over nullable parameters | Scope is Domain and Application. This is a test project; `int?` stays, matching the sixteen existing signatures in this file. |
| ADR-0049 — `CancellationToken` last | No new async member. |
| ADR-0144 — the lane may not write an ADR | **Honoured by omission.** The fast fail (spec §6) is #2146's and stays blocked. |
| ADR-0144 — the lane may not weaken a gate | No test deleted, no threshold lowered, no suppression added, no analyzer narrowed. One assertion is **inverted and rewritten**, declared in tasks.md, because the behaviour it pinned is the behaviour this slice changes. |

---

## The one existing assertion that changes, and why that is not a smuggle

`AspireFixtureReportSelectionTests.A_resource_with_a_captured_null_exit_code_is_not_named_as_a_cause`
asserts today:

```csharp
AspireFixture.SelectResourcesToReport(states, exitCodes).ShouldBe(["automation"]);
AspireFixture.FormatLikelyCause(states, exitCodes).ShouldBeEmpty();
```

on `["automation"] = "Finished"` with `["automation"] = null`. **That is #1930's
observation, spelled out, with an assertion that the report stays silent about it.**

The first line stays. The second is replaced by an assertion that the cause line
names `automation` and says no exit code was recorded, and the test is renamed to
say what it now holds. Its comment is rewritten to keep the finding it was
protecting — *do not render "exited with code " with nothing after it* — which the
new sentence honours by not using that phrasing at all.

**Constitution §Testing says an assertion that has to be edited is evidence the
behaviour moved.** It did, deliberately, and this document is the declaration.
This is a behaviour-*changing* slice, not a refactor, so the rule that blocks the
edit does not apply — but the edit must be visible in the PR body, not buried in
a diff.

**The five other cause-line tests must stay green unmodified.** They are the
guard that this change did not over-reach:

| Test | Input | Why it survives |
|---|---|---|
| `No_cause_is_claimed_when_the_one_shot_exited_cleanly` | `migrations` Finished 0 | `IsHealthy` is true for a one-shot that finished with 0 — excluded before either arm runs |
| `A_resource_that_crashed_and_came_back_is_not_named_as_a_cause` | `camera-catalog` Running 137 | `Running` is not an ended state, and `IsHealthy` is true |
| `The_report_names_a_likely_cause_when_a_resource_exited_non_zero` | non-zero exit | Population A, wording unchanged |
| `The_timeout_message_names_the_cause_before_it_lists_the_states` | assembly ordering | `FormatTimeoutMessage` is untouched |
| `No_cause_is_claimed_when_no_resource_states_were_captured` | empty dictionaries | Both populations empty |

An engineer who finds themselves editing any of these five has over-reached.
**That is the review instruction, and it is worth more than the new tests.**

---

## Risks

| Risk | Mitigation |
|---|---|
| The nine `FailedToStart` dependents of #2062 get named, reintroducing #2061's noise | `EndedStates` excludes `FailedToStart` by construction, and `No_cause_is_claimed_when_the_one_shot_exited_cleanly` runs over exactly that nine-service dictionary |
| A one-shot `migrations` finishing cleanly gets named | `IsHealthy` short-circuits both populations; test above |
| Two predicates over overlapping state sets drift apart | `EndedStates` is documented as a stated subset of `FatalStartupStates`, in the same doc-comment style the file already uses; both are static readonly arrays six lines apart |
| `FormatLikelyCause` exceeds 30 LOC (ADR-0084) | Extract the two sentence builders as private statics. Planned, not discovered |
| The change is cosmetic and nobody reads the report anyway | Phase 5 reconstructs and reads the #1930 report by hand and quotes its first three lines. That is the verification, not "tests green" |

---

## What is explicitly out of scope

- **The eight-minute wait on a dead `automation`** — #2146, `agent:blocked`,
  needs an ADR. Spec §6.
- **`RuleCacheSeederHostedService`'s exception filter** — spec §3. No supportable
  lead, no reproduction, no change.
- **Tests for `RuleCacheSeederHostedService`** — it has none. Worth a separate
  issue; not this slice, because writing them here would imply the lead was live.
- **WolverineFx** — spec §4. Nothing to change.
- **`ci.yml`** — spec §5. The `.trx` artifact already uploads on `always()`.
- **Closing #1930** — spec §7 US3. It stays open.

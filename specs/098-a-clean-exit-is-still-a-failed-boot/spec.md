# Feature Specification: A clean exit is still a failed boot

**Feature Branch**: `fix/1930-a-service-that-exits-says-why`

**Created**: 2026-09-08

**Status**: Draft — awaiting the phase-1 gate.

**Issue**: #1930 (`automation` intermittently exits during startup — one
occurrence, no evidence, two unproven leads)

**ADRs**: ADR-0103 (integration tests boot the real Aspire stack, no
Testcontainers), ADR-0068 (the fixture), ADR-0037 (phase gates), ADR-0139 /
constitution §Testing (new behaviour starts red), ADR-0144 (the lane, and the
three things it may not do), ADR-0084 (code metrics; `S104` is `NoWarn`'d for
test projects).

**Predecessors**: `specs/084-a-wait-that-names-what-died/` (#2066) ·
#1922 (the report names the resource that failed) · #1929 (the report carries
each resource's exit code) · #2064 / spec 062 (the migrations gate).

**Remainder tracked elsewhere**: #2146 (the fatal-state watchdog — filed
`agent:blocked`, needs an ADR).

> **This spec is a diagnostic, not a fix.** Nobody has reproduced the defect
> #1930 describes, and this document does not claim to. It closes the last place
> where the fixture's failure report stays silent about the exact shape #1930
> observed, and it records — in §3 and §4 — two leads that source reading does
> not support, so the next reader does not spend the afternoon this one did.

> **Every construct is named, not line-numbered.** `develop`'s recent history
> contains a commit whose whole subject is drifting line numbers
> (`8437de9 docs(specs): 062 — a false claim, an invented figure, and drifting
> line numbers`). Members and test names are the references here.

---

## 1. What #1930 actually asks for, and what is already delivered

#1930 observed `automation` in state `Finished` while every other service reached
`Running`. All 278 integration tests failed because they never ran. Re-running
the same commit passed; it has not recurred; the triggering PR was a
frontend-only `@testing-library/jest-dom` bump.

The issue names three reasons it could not be diagnosed. Checked against
`origin/develop` (`4a0bae62`), **two and a half of the three are already
closed** — and the issue itself says so for two of them:

| # | Reason it could not be diagnosed | Standing on `origin/develop` |
|---|---|---|
| 1 | The service's own output never reached the job log | **Closed by #1922.** `IsHealthy` no longer treats `Finished` as success for a long-running resource, so `SelectResourcesToReport` selects `automation` and `CaptureFailedResourceLogsAsync` dumps up to sixty lines of its log. |
| 2 | `Finished` fits both explanations — no exit code | **Closed by #1929.** `CaptureResourceStateMapAsync` records `evt.Snapshot.ExitCode` into `_exitCodes`, and `FormatResourceStates` prints `automation: Finished (exit code N)`. |
| 3 | A re-run flipped the conclusion to `success`, so the failure vanished from `gh run list` | **Half true, and the half that matters is already handled** — see §5. |

**What spec 084 (#2066) adds to this scenario: nothing.**

That is the first thing this spec had to establish, and it is not what a reader
of #2146 would expect. #2146's opening sentence says #2066 "makes each of the
twelve `Running` waits in `AspireFixture.InitializeAsync` stop when *its own*
resource enters a fatal state". **It does not.** Spec 084's own **FR-010** says
the opposite in as many words:

> **FR-010** — The twelve `WaitForResourceAsync` calls, their order and their
> comments MUST be left exactly as they are. This slice adds a check; it does not
> rewrite the waits.

What #2066 shipped is `ThrowIfAnyGatedResourceDiedAsync`, a **single sample taken
after every wait has returned** — its own comment says "Sampled here, at the last
instant of the boot". Spec 084's phase 4a had disproved the per-wait mechanism:
`Running` is published when the process launches, so the disjunction matches
`Running` and the wait returns before the death exists to be seen.

So in #1930's exact scenario:

1. `automation` dies during startup and never publishes `Running`.
2. `await ResourceNotifications.WaitForResourceAsync("automation", Running, cts.Token)`
   blocks. It is a plain target-state wait; it does not abort on a terminal state.
3. The shared eight-minute `cts` expires. The `catch (… OperationCanceledException
   or TaskCanceledException)` runs.
4. `ThrowIfAnyGatedResourceDiedAsync` **is never reached** — it is the last
   statement of the `try`.

`automation` **is** in `GatedResources` (question 1 of the brief: yes). The sweep
**would not fire** (question 2: no, because the wait on `automation` precedes it
and never returns). This is #2146's case, and #2146 is `agent:blocked` pending an
ADR — which ADR-0144 forbids this lane from writing.

**The 8-minute cost is therefore out of scope here**, and this spec does not
touch it. §6 says why the obvious fast-fail is not safe to build without that
ADR.

---

## 2. The one thing that is still silent

The timeout report is assembled by `FormatTimeoutMessage` in four sections:

```
Aspire AppHost did not start within 8 minutes.
<FormatLikelyCause>
Resource states:
  <45 lines>
Failed-resource logs:
  <sections>
Last camera-catalog logs:
  <tail>
```

`FormatLikelyCause` exists because of #2061, whose finding was that the cause
*was* printed — "once per state list, in the same typeface as the forty-four
resources that were fine". Its own doc comment states the rule: **"Prominence is
not ordering; it is a sentence at the top that names the resource."**

Its predicate is:

```csharp
!IsHealthy(name, states[name], exitCodes) && ExitedNonZero(name, exitCodes)
```

`ExitedNonZero` is `exitCode is not null and not 0`. So for the two exit codes
#1930's own analysis singles out:

| Observation | `SelectResourcesToReport` | `FormatLikelyCause` | `DescribeWhySelected` header |
|---|---|---|---|
| `automation: Finished`, exit **134** | selected | **names it** | `Finished, exit code 134 — the process ran and died` |
| `automation: Finished`, exit **0** | selected | **silent** | `Finished` |
| `automation: Finished`, exit **null** | selected | **silent** | `Finished` |

The exit-0 row is #1930's own decision branch — *"exit 0 → graceful stop. Nothing
in the app asks for one, so look at the orchestrator"* — and it is the row where
the report has nothing to say at the top. The null row is **test-pinned**:
`A_resource_with_a_captured_null_exit_code_is_not_named_as_a_cause` asserts
`FormatLikelyCause(...).ShouldBeEmpty()` on literally `["automation"] = "Finished"`,
`["automation"] = null`.

That test's stated reason is a **wording** problem, not a suppression judgement:

> Reading the null as non-zero would print "automation exited with code " — a
> cause line with no code in it.

Correct, and the remedy is a different sentence, not silence. A long-running
service sitting in a terminal state while the boot timed out waiting for it **is**
the cause of the timeout, whatever number it exited with. Exit code 0 changes
*where you look next*; it does not change *what broke*.

**That is the whole of this spec's code change.**

---

## 3. Lead 1 — `RuleCacheSeederHostedService` rethrowing `OperationCanceledException`

**Not supportable as a cause. Not disproved as a mechanism.** Three independent
findings, none of which required booting anything.

### 3a. The token can only be cancelled by something already stopping the process

`RuleCacheSeederHostedService` is an `IHostedService` (not a `BackgroundService`,
so `BackgroundServiceExceptionBehavior` — the #1900 shape the issue cites — does
not apply to it at all). Its `cancellationToken` is the one
`Microsoft.Extensions.Hosting.Internal.Host.StartAsync` passes down, which is
`CreateLinkedTokenSource(callerToken, _applicationLifetime.ApplicationStopping)`,
plus a `HostOptions.StartupTimeout` CTS when one is configured.

- `src/Automation/Api/Program.cs` ends `await app.RunAsync();` — no caller token.
- `grep -rn "StartupTimeout\|HostOptions" --include=*.cs src/` returns **nothing**
  outside four `ScenarioSimulator` comments about `StopHost`. The default is
  `Timeout.InfiniteTimeSpan`.

So the only trigger is `ApplicationStopping` — SIGTERM or SIGINT. **If that fired,
the process was already being told to stop.** The seeder's rethrow cannot be why
`automation` ended; at most it converts a clean stop into an unhandled
`OperationCanceledException` and a non-zero exit. It is an amplifier, not a cause,
and #1930's own root question ("why did automation stop?") would still be open.

### 3b. The shape is not specific to `automation`

`src/SystemVariables/Infrastructure/Resolution/ReverseIndexSeederHostedService.cs`
is the same class in every respect this lead depends on: `IHostedService`, an
awaited I/O call in `StartAsync` taking the token, and

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
```

as its only handler — with a doc comment making the same "best-effort" promise.
`KioskPrivilegeSweepHostedService` (identity) carries the same filter. Across
`src/` the filter appears at **28 sites in 9 contexts**.

A mechanism shared by three of the twelve gated services does not explain why
`automation` alone was observed in `Finished`. It is a coincidence of which file
somebody read first.

### 3c. The issue's falsification test does not distinguish this lead from anything

#1930 says: *"If the exit code turns out to be 0, this lead is dead."* True. But
the converse carries no information: **every** unhandled exception during host
startup exits non-zero, so a non-zero code is consistent with lead 1 and with any
other crash equally. The exit code refutes this lead; it can never support it.

### 3d. Consequence for this spec

**No code change to the seeder.** Changing the filter would be a behaviour change
on a path never observed to fire, motivated by a lead that cannot be a cause. It
is exactly the "speculative fix" ADR-0144 and the Karpathy guidelines rule out.
There are also **no tests for `RuleCacheSeederHostedService` at all**
(`grep -rln RuleCacheSeeder tests/` returns nothing) — writing some would be
worthwhile, and is a separate issue, not this one.

---

## 4. Lead 2 — WolverineFx 6.24.2 → 6.30.0

**Not supportable, and not falsifiable from source.** Recorded so it stops being
carried forward as if it were live.

- `Directory.Packages.props` still pins **6.30.0** across all six `WolverineFx.*`
  packages. Every integration run since #1907 has passed on that version.
- `AutomationInfrastructureModule` reaches Wolverine through
  `builder.AddWolverineForContext<AutomationDbContext>(...)` — the **same shared
  extension every one of the nine contexts calls**, with only a queue prefix, an
  outbox schema and a connection name differing. A 6.30.0 startup regression would
  take nine services down, not one.
- The AppHost wiring confirms nothing singles `automation` out: it declares
  `WithHttpEndpoint`, `WithReference(automationDb / rabbitmq / keycloak)`,
  `WaitFor(rabbitmq)`, `WaitFor(keycloak)`, and receives `WaitForCompletion(migrations)`
  from the same nine-element loop as its siblings. No sibling waits on its own
  database either.

The only support the lead ever had was a same-day merge. That is a date, not
evidence.

---

## 5. Reason 3 is overstated — the evidence survives a re-run

#1930 says CI history "hides this class of failure". Half of that is true: the
**conclusion** flips to `success` on a re-run, so `gh run list` shows nothing —
which is why MEMORY.md carries *"A re-run erases the failure from CI history"*.

But the **evidence** does not vanish. `.github/workflows/ci.yml`'s integration
job already ends with:

```yaml
- name: Upload integration test results
  if: always()
  uses: actions/upload-artifact@…
  with:
    name: integration-test-results
    path: '**/TestResults/*.trx'
    retention-days: 14
```

`if: always()`, and the `.trx` carries the fixture's full failure message. The
gap in #1930 was that the message said nothing useful in September 2026 — #1922
and #1929 fixed that — not that the message was thrown away.

**No CI change is in scope.** Saying so is the deliverable for reason 3.

---

## 6. What this spec deliberately does not build

**The fast fail.** Turning the twelve `Running` waits into waits that also stop on
a fatal state would convert #1930's eight minutes of silence into a named failure
in seconds. It is tempting, it is the shape of #2064's migrations gate, and it is
**not safe to build here**, for two reasons the fixture already documents:

1. **Spec 084's phase 4a disproved it.** `Running` is published when the process
   launches, so a disjunctive predicate matches `Running` and returns before the
   death happens. That is why FR-010 froze the waits and the check became a sweep.
2. **A resource passes through `Finished` on a healthy boot.** `TailResourceLogsAsync`
   records the observed transition sequence on Aspire 13.5.3:

   ```
   event-ingestion-gxkpyqjx  Running  -> Stopping
   event-ingestion-gxkpyqjx  Stopping -> Finished
   event-ingestion-gxkpyqjx  Finished -> Starting
   ```

   `IsFatalStartupState`'s own doc comment states the asymmetry that follows:
   *"A false positive here fails every integration run on every machine."*
   A continuous check that throws on the first `Finished` sighting is a
   false-positive generator; making it safe means adding a settle-and-re-read
   lifetime, which is #2146's supervised boot and #2146's ADR.

**ADR-0144 forbids this lane from writing an ADR.** So this is stated and stopped,
not attempted. #2146 stays `agent:blocked` and stays the owner.

**The 8-minute budget is not touched.** No timeout is widened; none is narrowed.

---

## 7. User stories

### User Story 1 — the engineer reading the next occurrence (P1)

*As the engineer who opens the `.trx` after `automation` dies again, I want the
first sentence of the failure to name `automation`, so that I do not have to find
one row among forty-five to learn what broke.*

**Independently shippable**: yes. One pure function, one formatter, one test
class, no Docker, no Aspire boot.

#### Acceptance scenarios (Gherkin)

**Happy path — the shape #1930 actually observed, exit code 0**

```gherkin
Given a startup timeout report whose captured states include
      automation = "Finished" and camera-catalog = "Running"
  And automation's captured exit code is 0
 When the likely-cause line is formatted
 Then it names automation
  And it states that automation ended in Finished with exit code 0
  And it states that a long-running resource that ends during startup is a
      failed boot, not a clean finish
```

**Happy path — no exit code was ever observed**

```gherkin
Given a startup timeout report whose captured states include
      automation = "Finished"
  And automation's captured exit code is null
 When the likely-cause line is formatted
 Then it names automation
  And it says no exit code was recorded
  And it never renders the phrase "exit code" followed by nothing
```

**Conflict — the existing non-zero wording must not move**

```gherkin
Given a startup timeout report whose captured states include
      migrations = "Finished" with exit code 134
 When the likely-cause line is formatted
 Then it reads exactly as it does today:
      "Likely cause: migrations exited with code 134 — a non-zero exit is a
       failure, not a clean finish."
```

**Bad request / false positive — a one-shot that succeeded**

```gherkin
Given migrations = "Finished" with exit code 0
 When the likely-cause line is formatted
 Then it is empty, because finishing with 0 is how a one-shot succeeds
```

**Bad request / false positive — a resource that never launched**

```gherkin
Given nine services in "FailedToStart" with no captured exit code
 When the likely-cause line is formatted
 Then none of them is named
  And the report's failure sections still describe each of them as
      "FailedToStart — never reached a running state"
```

> This scenario is the one that keeps #2061's fix intact. `FailedToStart` means
> the process never ran; naming nine of them at the top is the noise #2061
> removed. Only the states that mean *the process ran and ended* —
> `Finished`, `Exited`, `Terminated` — join the cause line.

**Bad request — a resource that crashed and came back**

```gherkin
Given camera-catalog = "Running" with a captured exit code of 137
 When the likely-cause line is formatted
 Then it is empty, because the failure section will not mention camera-catalog
      and a cause line must not point at a section that does not exist
```

**Auth**: N/A. Nothing here crosses a trust boundary; the whole change is a test
fixture's failure-report formatter, running only in CI and on developer machines.

---

### User Story 2 — the section header says which kind of ending it was (P2)

*As the same engineer, once the cause line has sent me to `---- automation (…) ----`,
I want the header to distinguish "ran and stopped cleanly" from "ran and died"
from "never launched", so that the header and the cause line agree.*

`DescribeWhySelected` today has three arms: non-zero exit, `FailedToStart`, and a
bare `_ => state` fallback that catches exactly the two rows §2 tabulated. It is
one `switch` arm.

```gherkin
Given automation = "Finished" with exit code 0
 When its failed-resource section header is formatted
 Then it reads "Finished, exit code 0 — the process ran and stopped"
```

```gherkin
Given automation = "Finished" with no captured exit code
 When its failed-resource section header is formatted
 Then it reads "Finished — the process ended and no exit code was recorded"
```

**P2 and not P1** because the answer is already two inches above in the state
list; this makes the two halves of the report agree rather than adding a fact.
Ship it in the same slice — same file, same test class — or drop it if the
reviewer disagrees, without affecting US1.

---

### User Story 3 — the record on #1930 and #2146 (P1, no code)

*As the next person to pick up #1930, I want the issue to say what source reading
ruled out on 2026-09-08, so that I do not re-derive §3 and §4.*

```gherkin
Given #1930 is still open with no reproduction
 When this spec is delivered
 Then #1930 carries a comment recording that lead 1 cannot be a cause (§3),
      that lead 2 has no support beyond a merge date (§4),
      that the CI artifact already preserves the evidence (§5),
      and that the fast fail is #2146's, blocked on an ADR (§6)
  And #1930 stays OPEN as an unreproduced observation
```

```gherkin
Given #2146's body claims #2066 shipped twelve per-wait fatal-state predicates
  And spec 084 FR-010 says the waits were left exactly as they are
 When this spec is delivered
 Then #2146 carries a comment correcting that premise
  And #2146 keeps its agent:blocked label
```

> **#1930 must not be closed by this PR.** It is an unreproduced observation with
> an improved diagnostic, which is a different thing from a fixed defect. Use a
> reference, not a closing keyword — MEMORY.md's *"A PR mention rarely
> auto-closes the issue"* cuts the other way here and is the behaviour we want.

---

## 8. Independent end-to-end test procedure

No Aspire boot. No Docker. Everything is `internal static` pure functions with an
existing Docker-free trait.

```sh
cd D:/Github/wt-1930
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj \
  -c Release --filter "Category=FixtureLogic"
```

Phase 5 additionally reconstructs the #1930 report by hand and reads it:

```sh
# A five-line scratch xUnit fact (deleted before the PR) that prints
# FormatTimeoutMessage over the captured state of the observed run:
#   automation = "Finished", exit code 0, plus eleven Running siblings.
# Read the first three lines of the output. The first must name automation.
```

The verification note quotes those three lines verbatim. That is the observable
behaviour: *the report that said nothing now says `automation`.*

---

## 9. Locked tech choices

| Concern | Choice | Authority |
|---|---|---|
| Integration stack | Real Aspire AppHost, no Testcontainers | ADR-0103 |
| The fixture | `AspireFixture`, `IAsyncLifetime` collection fixture | ADR-0068 |
| Test framework | xUnit + Shouldly | ADR-0052 |
| Test naming | Sentence-style with underscores | ADR-0053 |
| Docker-free selection | `[Trait("Category", "FixtureLogic")]`, selected by the `backend` job | #2064 |
| Guards | `Ensure.That` — **not used here**; every touched member is a `static` pure formatter with no argument preconditions to state | ADR-0105 |
| Collections | Explicit type + collection expression | `dotnet_style_prefer_collection_expression` |

No new package. No new abstraction. No config knob.

---

## 10. Latency budget impact

**N/A.** Nothing here runs in production. `AspireFixture` is a test-project type;
no leg of the event→overlay path is touched, and no production assembly is
recompiled by this change.

---

## 11. Assumptions, marked

- **A1 — `Finished` with exit code 0 is reachable for `automation`.** Unverified;
  it is one of the two branches #1930 itself enumerates. The change is correct
  either way, because the null-exit-code row takes the same new arm.

  **This bullet used to call that second row *observed*, and it is not.** The
  test it cited as the observation —
  `A_resource_with_a_captured_null_exit_code_is_not_named_as_a_cause`, now
  inverted — was hand-built for **#1918**: the class it lives in opens by saying
  so, and `StatesFromTheRunThatMotivatedThis` is run 33623647778's shape, not
  #1930's. Nothing in this repository holds a capture of #1930's run. The row is
  a *plausible* rendering of what #1930 reported, and the case for the new arm
  rests on the state a resource reached, not on a code someone recorded.
  Corrected at phase 4b (2026-09-08): a fixture written for one issue is not
  evidence about another, and this spec is a diagnostic, so what it claims to
  have seen is the whole of its value.
- **A2 — the `Terminated` string literal stays reachable.** Inherited from
  `FatalStartupStates`, whose doc comment explains why it cannot be a constant.
  **It is copied, not reused**: `FatalStartupStates` carries `FailedToStart` and
  `RuntimeUnhealthy`, and naming resources in those states is exactly the noise
  #2061 removed, so the cause line needs its own shorter set (T002). An earlier
  draft of this bullet said the array was reused; the two sets differ by the two
  entries that decide the behaviour.
- **A3 — no re-run has ever been needed to read a `.trx`.** §5 asserts the
  artifact survives a re-run from the workflow file, not from a retrieval that
  was performed. If a reviewer wants that proved, downloading attempt 1's
  artifact from any re-run run is a two-minute check.

---

## 12. What this spec is

**A diagnostic.** Not a fix — there is no reproduction to fix against, and §3 and
§4 say plainly that neither named lead can carry one. #1930 stays open as an
unreproduced observation with a better report waiting for it.

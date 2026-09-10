# Plan — spec 116

## Shape of the change

**No bounded context, no layers, no messaging.** This change touches
test code only: no aggregate, no value object, no domain or integration
event, no HTTP surface. The boundary rule (no cross-context project
references, `Shared.Contracts` only) is untouched — indeed the defect is
a *test* reaching across a context boundary inside a timing window, and
the fix moves that reach out of the window rather than removing it.

Two files change, plus one new file:

| File | Change |
|---|---|
| `tests/Integration.Tests/LayoutComposition/LayoutLifecycleIntegrationTests.cs` | Hoist + doc comment + drop instrumentation |
| `tests/Architecture.Tests/StopwatchWindowScopeTests.cs` | **New** — the guard |

## The fix (T004)

```csharp
Guid camera = await LayoutRequests.RegisterCameraAsync(aspire);

Stopwatch sw = Stopwatch.StartNew();
HttpResponseMessage created = await layouts.PostAsJsonAsync(
    "/layouts",
    SingleTileBody($"Line-{Guid.NewGuid():N}".Substring(0, 16), camera));
```

Two lines moved. Nothing else in the test body changes — the 500 ms
figure, the `If-Match "0"` publish, and every assertion stay as they are.
The uncommitted instrumentation block (lines 46–48) is removed in the
same commit.

The class doc comment must record **what the number is a budget for**:

- the 500 ms is a **local SLO for the layout-composition synchronous
  command path** — create + publish, and nothing else;
- it is **not** one of constitution §IV's six event-to-overlay legs. §IV
  budgets an asynchronous event path; this is a synchronous HTTP command,
  so §IV neither grants this figure nor governs it;
- the camera registration is a **precondition, not part of the path** —
  the same sentence `SignalRRevocationIntegrationTests.cs:63-65` already
  uses about its version lookup.

## The guard (T003)

**Rule, stated generally:** a *closed* stopwatch window in the
integration suite — `Stopwatch <v> = Stopwatch.StartNew();` through
`<v>.Stop();` — must not contain a call that provisions an authenticated
client or registers a prerequisite in another bounded context:
`CreateAdminClientAsync`, `CreateAuthenticatedClientAsync`,
`RegisterCameraAsync`.

A window with **no** matching `.Stop();` is a polling clock, not a budget
window, and is out of scope **by that definition** — not by a heuristic
about the test's name or folder.

### Why it is honest and not over-fitted

Scanned every `Stopwatch.StartNew()` in `tests/Integration.Tests`: 15
declarations, all of the form `Stopwatch <ident> = Stopwatch.StartNew();`
(no `var` spelling to handle). Nine are closed windows, six are polling
clocks. Of the nine closed windows **exactly one** violates the rule —
`LayoutLifecycleIntegrationTests.cs:30-44`. `CommandLatencyTests`,
`IngestThroughputMeasurementTests` (×2), `SignalRRevocationIntegrationTests`,
`OverlayLifecycleIntegrationTests`, `OverlayPushIntegrationTests`,
`WhepHandshakeLatencyTests` and `ResolvedTextReachesItsFabTests` already
comply. So the guard goes red today for exactly the filed defect and
green after the fix, with zero collateral.

### Placement — a deviation from the brief, with its reason

The brief proposed `tests/Integration.Tests` with
`[Trait("Category", "FixtureLogic")]`, mirroring
`AspireFixtureReportSelectionTests`. **Plan puts it in
`tests/Architecture.Tests` instead.** Both run Docker-free in the
`backend` job, so the CI outcome is identical; three things decide it:

1. **Precedent for this exact kind of test.** Three guards already scan
   `tests/Integration.Tests` **from disk** —
   `IntegrationTestSelectionTests`, `LogTailCoverageTests`,
   `GuardBanWiringTests` — and `IntegrationTestSelectionTests` documents
   why: a project reference "would drag the Aspire hosting and DCP
   dependency graph into a project that today runs in seconds with no
   Docker". `AspireFixtureReportSelectionTests` is not a source scanner;
   it is pure decision logic about fixture behaviour.
2. **Self-scan hazard.** A scanner living inside the tree it scans reads
   its own file. The counterfactual fixture (T007) is a source snippet
   containing the literal violating shape; in-tree, the guard would
   report itself, and the cure — excluding its own filename — is exactly
   the fragile self-exemption this repo has been bitten by.
3. **No trait needed.** `coverage-check.ps1` already runs
   Architecture.Tests in the `backend` job, so the guard cannot fall
   outside a filter that has to be remembered.

If the orchestrator prefers the brief's placement, it is a namespace and
a trait — one-line change — plus a self-exclusion. Recorded so the
choice is visible rather than silent.

### Decomposition (ADR-0084: ≤300 LOC/file, ≤30 LOC/method, ≤4 params, complexity ≤10, depth ≤3)

The window scan is the risky method. Keep it in three pure pieces plus
thin `[Fact]`s:

- `StripComments(string source)` — block then line comments, so a
  commented-out call is not a violation. Mirror
  `IntegrationTestSelectionTests`' two compiled regexes.
- `ClosedWindows(string source)` → `(Variable, StartLine, Body)` — for
  each `Stopwatch <ident> = Stopwatch.StartNew();`, the text up to the
  **first `<ident>.Stop();`**, or nothing if there is none.
- `ForbiddenCalls(string window)` → the provisioning names present.
- `[Fact] A_closed_budget_window_does_not_provision_inside_the_clock` —
  aggregate over the tree, assert empty, report every offender at once.
- `[Fact] A_polling_clock_is_not_a_budget_window`.
- `[Fact] A_window_that_provisions_inside_the_clock_is_reported`
  (counterfactual, on an in-memory snippet).

**Match the declared variable, not any `.Stop();`.**
`StreamHealthTransitionTests` contains two `.Stop();` calls and no
`Stopwatch.StartNew()` at all — a scanner keyed on a bare `.Stop();`
would pair a stopwatch in one file with an unrelated `.Stop()` in
another, or close a window at the wrong statement.

### Path handling

Enumerate `*.cs` under `tests/Integration.Tests`, excluding `/obj/` and
`/bin/`. Report paths relative to the repository root with
`Path.GetRelativePath(...).Replace(Path.DirectorySeparatorChar, '/')` —
`GetRelativePath` yields the platform separator, and a hard-coded
backslash is green on Windows and red on Linux CI. The exclusion check
runs against the normalised `/`-form, as
`IntegrationTestSelectionTests:383-393` already does.

## Constraints carried into implementation

- Do **not** raise the 500 ms budget (ADR-0144: no gate weakening).
- Do **not** add a `Category` trait to the layout test — that would move
  it out of `ci.yml:179` and remove a CI gate.
- Do **not** touch `IngestThroughputMeasurementTests` or the
  `AspireCollection` composition.
- ADR-0105 `Ensure.That` for argument guards, ADR-0049 `CancellationToken`
  last, ADR-0141 / §II — no new production surface here, but they bind
  any code that does appear.
- ADR-0053 sentence-style test names; Shouldly assertions (ADR-0052).
- Nothing outside `tests/` changes.

## Verification — a measurement, run twice each side

Run the CI-filtered suite
(`--filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"`)
**twice before** and **twice after**, and report what the timed section
costs with the camera registration removed. Two runs each side because
the first run after machine churn looks exactly like a regression — a
single before/after pair cannot distinguish the fix from the warm-up.

Boot one Aspire stack at a time: two concurrent boots produce
`FailedToStart`, which reads exactly like a code defect.

The guard's own evidence is the counterfactual (T007) — constructing what
it claims to catch — not merely its green run.

## Latency

**None of §IV's six legs.** Synchronous command path, local SLO;
§VII's dashboard obligation is **not engaged**. Stated here so the next
reader does not have to re-derive it from the absence of a claim.

# Spec 116 — The budget times what it names

**Issue:** #2119
**Branch:** `fix/2119-the-budget-times-what-it-names`
**Status:** Phase 3 complete — awaiting gate
**Lane:** autonomous (ADR-0144)
**ADRs:** 0037 (phases), 0144 (lane, 4a colour), 0036 (smallest change),
0139 (red first), 0103 (Aspire fixture), 0084 (code metrics), 0053
(test naming), 0052 (xUnit + Shouldly).
Constitution §IV (latency budget), §VII (dashboard obligation), §Testing.

## Problem

`tests/Integration.Tests/LayoutComposition/LayoutLifecycleIntegrationTests.cs`,
test `Create_and_publish_a_layout_yields_a_Published_revision_within_500_ms`:

```csharp
Stopwatch sw = Stopwatch.StartNew();                        // line 30
HttpResponseMessage created = await layouts.PostAsJsonAsync(
    "/layouts",
    SingleTileBody($"Line-{Guid.NewGuid():N}".Substring(0, 16),
        await LayoutRequests.RegisterCameraAsync(aspire)));  // line 33
```

`RegisterCameraAsync` mints or reuses an admin token for **camera-catalog**
and `POST`s `/cameras` — a full round trip into a *different bounded
context* — and it is evaluated as an argument, so it sits **inside** the
clock. The class doc comment (lines 12–13) calls the window
"the 500 ms SLO budget for the synchronous command path" for **layouts**.
The number therefore measures layout create + publish **plus** a camera
registration somewhere else.

**This is a blocking gate, not a measurement script.** The test carries
no `[Trait("Category", …)]`, so it falls inside `ci.yml:179`'s filter
(`Category!=Measurement&Category!=Disruptive&Category!=Maintenance`) and
runs in the `integration` job on every PR.

Observed on one dev box: the timed section reaches **~401 ms** against a
500 ms budget — roughly 20% headroom on a blocking job, and a GitHub
runner is slower. Contended runs on the same box have failed at **670 ms**
and **849 ms**. The flake is not hypothetical, and when it fires it
accuses layout-composition of a breach that camera-catalog caused.

**The real complaint is meaning, not the number.** Nothing near the test
says what the 500 ms *is*. It is a **local SLO for the layout-composition
synchronous command path**, and it is **not** one of constitution §IV's
six event-to-overlay legs — §IV budgets an asynchronous event path; this
is a synchronous HTTP command. A figure whose meaning lives nowhere near
it is this repository's recurring defect (§IV's own leg table, §II, the
Phase 3 board gate).

**Working-tree note:** the branch currently carries an uncommitted
instrumentation block at lines 46–48 that appends each measurement to
`%TEMP%/sse-2119-budget.txt`. It produced the figures above. It must
**not** survive into the fix.

## Decision already taken (recorded, not relitigated)

Issue **option 1** — hoist the camera registration to a local declared
**before** `Stopwatch.StartNew()`.

- **Not option 2** (raise the budget). Weakening a gate to reach green is
  a blocked outcome under ADR-0144.
- **Not option 3** (a class- or collection-shared camera). xUnit builds a
  new test-class instance per test, so this class's `InitializeAsync`
  cannot share anything; sharing would need new state on the
  collection-level `AspireFixture`. That camera would couple 8 currently
  independent tests to one camera-catalog row, and 3 of them need several
  *distinct* cameras (2×2 wall: 4; archive/branch: 2; collision: 2).
  Changing 8 tests to fix 1 is against ADR-0036.
- **Option 1 is already the house pattern, twice in this suite.**
  `SignalRRevocationIntegrationTests.cs:63-65` hoists a version lookup out
  of the clock — "Read the precondition before the clock starts: this
  window measures archive→push, not the version lookup."
  `OverlayLifecycleIntegrationTests.cs:36-57` keeps client creation
  outside its clock and says inside it why. Mirroring an existing pattern
  beats inventing a fixture (CLAUDE.md, "Read before write").

## Scope

**In scope**

- Hoist `RegisterCameraAsync` to a local before the stopwatch in the one
  offending test; drop the instrumentation block.
- Rewrite the class doc comment so it states what the 500 ms is a budget
  **for**, and that it is not a §IV leg.
- A source-scanning guard that makes the shape unrepeatable.
- Before/after measurement of the CI-filtered integration suite.

**Out of scope**

- Raising or lowering the 500 ms figure.
- Adding a `Category` trait to the layout test — that removes a CI gate.
- `IngestThroughputMeasurementTests` and the `AspireCollection`
  composition.
- The other 8 tests in the class; any production code.

## User story (P1 — the only slice)

**As** a reviewer reading a failed `integration` job, **I want** the
layout budget to time only the layout command path, **so that** a red run
names the context that actually breached and the figure means what its
comment says.

## Acceptance scenarios

```gherkin
Scenario: the window times only the layout command path
  Given the layout lifecycle test
  When the camera is registered before Stopwatch.StartNew()
  Then the timed section spans POST /layouts and the publish only
  And every existing assertion of the test still holds unchanged
  And the 500 ms figure is unchanged
```

```gherkin
Scenario: the figure says what it is a budget for
  Given the class doc comment
  Then it states the 500 ms is a local SLO for the layout-composition
       synchronous command path
  And it states this is not one of constitution §IV's six
       event-to-overlay legs, because §IV budgets an event path
```

```gherkin
Scenario: the guard is red on the tree as it stands   # phase 4a artifact
  Given the guard scanning tests/Integration.Tests
  When it runs against the unfixed tree
  Then it fails naming LayoutLifecycleIntegrationTests.cs and
       RegisterCameraAsync
  And it names no other file
```

```gherkin
Scenario: the guard is green after the hoist
  Given the fixed tree
  Then no closed stopwatch window contains CreateAdminClientAsync,
       CreateAuthenticatedClientAsync or RegisterCameraAsync
```

```gherkin
Scenario: a newly written violation is rejected   # the bad-request analogue
  Given a source snippet whose closed window calls RegisterCameraAsync
  When the window scanner reads it
  Then it reports that window
```

```gherkin
Scenario: a polling clock is out of scope by definition, not by heuristic
  Given a Stopwatch with no matching <variable>.Stop();
  Then the scanner yields no window for it
  And a bare ".Stop();" on some other object does not close a window
```

**Auth: N/A.** No HTTP surface, scope or token change. The test still
mints an admin client via `CreateAdminClientAsync`; that call simply
moves outside the window — where, for `layouts`, it already was.

## Independent end-to-end test procedure

1. On the unfixed tree, run the CI-filtered integration suite
   (`--filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"`)
   **twice**; record the timed section for the layout test each time.
2. Apply the fix; run the same filtered suite **twice** more; record the
   same figure.
3. Report the delta — what the timed section costs once the camera
   registration is outside the clock.
4. Run the guard red before the fix and green after, and prove it can
   fail by counterfactual.

Runs are repeated because the first run after machine churn looks exactly
like a regression.

## Latency budget impact

**None of constitution §IV's six event-to-overlay legs.** This is a
synchronous HTTP command path, and the 500 ms is a local SLO that §IV
neither grants nor governs. §VII's dashboard obligation binds implemented
§IV legs and is therefore **not engaged** by this change.

## Locked tech choices

xUnit + Shouldly (ADR-0052), sentence-style test names (ADR-0053), Aspire
fixture for integration (ADR-0103, no Testcontainers), SonarAnalyzer
metrics (ADR-0084). Test-only change with no production code, so §II
value objects, ADR-0141 `Option<T>` and ADR-0105 `Ensure.That` have no
new surface to apply to — recorded so their absence is a stated fact
rather than an omission.

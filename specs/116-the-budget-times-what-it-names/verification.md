# Verification — spec 116 (issue #2119)

## What was verified

`LayoutLifecycleIntegrationTests.Create_and_publish_a_layout_yields_a_Published_revision_within_500_ms`
now times only the layout-composition command path. The camera
registration — a Keycloak token mint for `camera-catalog` plus a
`POST /cameras` into a **different bounded context** — is a precondition
declared before `Stopwatch.StartNew()`.

The 500 ms figure is unchanged, no `Category` trait was added, and the
test remains in the set `ci.yml:179` runs on every PR.

## Measurement

Configuration: the one CI actually gates on —
`dotnet test tests/Integration.Tests -c Release --no-build --filter
"Category!=Measurement&Category!=Disruptive&Category!=Maintenance"`.
Not the single class: the issue's own figures differ threefold between
the two, and only the full suite gates a merge.

The timed section's value was captured by a temporary append to
`%TEMP%/sse-2119-budget.txt` placed **after** `sw.Stop()`, identical in
all four runs, and present in **no commit**. Two runs per side, because
the first run after machine churn on this box looks exactly like a
regression.

| Run | Tree | Timed section | Suite |
|---|---|---|---|
| before 1 | camera registration inside the window | **145.4 ms** | 457 passed, 9m18s |
| before 2 | camera registration inside the window | **120.3 ms** | 457 passed, 8m41s |
| after 1 | registration hoisted out | **148.0 ms** | 457 passed, 10m46s |
| after 2 | registration hoisted out | **100.1 ms** | 457 passed, 9m32s |

**The deliverable figure — what the timed section costs with the camera
registration removed — is 100.1 ms and 148.0 ms**, against 120.3 ms and
145.4 ms with it.

### What that does and does not show

**It does not show a headroom improvement, and this note will not claim
one.** The four figures overlap: the before pair spans 120–145 ms and
the after pair spans 100–148 ms. The spread *within* each side is larger
than the difference *between* them, so on an uncontended box the
registration's cost is not resolvable at two runs a side. A widened
budget would have been fitted to exactly this kind of observation, which
is why the correction is the window and not the number.

**The issue's own premise did not reproduce.** It reports the timed
section at 401 ms in this configuration; four runs today put it at
100–148 ms, which is the range the issue attributes to the *single-class*
run (127–160 ms). Whatever produced 401 ms on that day was not
reproduced here, and no figure in this table is evidence for or against
it.

What the change does remove is an **exposure**. Inside the window sat two
pieces of work whose cost is not bounded by anything this test controls:
a `camera-catalog` round trip, and — depending on suite ordering — a
Keycloak token mint, since `AspireFixture`'s token cache is keyed per
resource and `camera-catalog` is a different key from
`layout-composition`. Under contention that exposure is what the issue
records failing at 670 ms and 849 ms. Those load-sensitive breaches were
**not reproduced today and remain unobserved**.

## Phase 4a — RED

`tests/Architecture.Tests/StopwatchWindowScopeTests.cs`, committed red at
`55339d7c` before the fix at `71b7f6cd`.

```
[xUnit.net 00:00:01.50]     SmartSentinelEye.Architecture.Tests.StopwatchWindowScopeTests.A_closed_budget_window_does_not_provision_inside_the_clock [FAIL]
  Error Message:
   Shouldly.ShouldAssertException : violations
    should be empty but had
1
    item and was
[tests/Integration.Tests/LayoutComposition/LayoutLifecycleIntegrationTests.cs:30-44 (window 'sw') calls RegisterCameraAsync]

Additional Info:
    1 of 9 closed stopwatch windows under tests/Integration.Tests provision inside the clock, so each one's elapsed figure includes work that is not on the path the test is named for:

  tests/Integration.Tests/LayoutComposition/LayoutLifecycleIntegrationTests.cs:30-44 (window 'sw') calls RegisterCameraAsync

Failed!  - Failed:     1, Passed:   359, Skipped:     0, Total:   360, Duration: 4 s
```

After the fix:

```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 163 ms
Passed!  - Failed:     0, Passed:   360, Skipped:     0, Total:   360, Duration: 4 s
```

Colour justification: the production code is untouched, but the covering
test **is itself the thing being edited**, so ADR-0144's characterisation
path — "the same tests must pass unmodified" — cannot hold. ADR-0144
resolves that ambiguity to behaviour-changing, because that path fails
loudly.

### The guard was proved by counterfactual, not by its own verdict

Three synthetic snippets, green from the start:

- a closed window containing `CreateAdminClientAsync` **is** reported,
  and the asserted line range (6–9) is checked, so the range in the
  failure message is tested rather than assumed;
- an unclosed polling clock containing `RegisterCameraAsync`, followed by
  a bare `unrelated.Stop();` from a `new Stopwatch()` declaration, is
  **not** reported — the `StreamHealthTransitionTests` shape, which a
  scan keyed on a bare `.Stop();` would close wrongly;
- a `//` and a `/* */` mention of a forbidden call inside a window are
  **not** reported.

The window census was also produced twice by independent mechanisms — an
ad-hoc PowerShell scan before the guard existed, and the guard itself —
and both found **9 closed windows, 6 polling clocks, one offender**.

## Latency (constitution §IV)

**This touches none of §IV's six event-to-overlay legs.** §IV budgets the
asynchronous *event arrival → overlay rendered* path across RTP ingest,
decode, presentation buffer, event → overlay state, composite and
headroom. A synchronous HTTP create-then-publish is on none of them, so
§IV neither grants this 500 ms figure nor governs it, and §VII's
dashboard obligation — which binds implemented §IV legs — is not engaged.

That sentence now lives in the test's class doc comment, which is the
part of this issue that outlives the fix.

## Build

`dotnet build SmartSentinelEye.slnx -c Release` — **Build succeeded, 0
Warning(s), 0 Error(s)** (TreatWarningsAsErrors is Release-only).

## Not observed

- **A GitHub runner.** Every figure here is one Windows dev box. The
  issue's argument that a runner is slower is untested and untestable
  from here; CI on the PR is the first observation of it.
- **The load-sensitive breach.** No run today exceeded 148 ms, so the
  670 ms and 849 ms failures the issue cites were not reproduced and the
  fix's effect on them is inferred from what the window contains, not
  measured.
- **A regression of the guard on Linux.** Paths are normalised to `/`,
  but the guard has only run on Windows here.

## Final run on the committed tree

Instrumentation removed, rebuilt, and re-run in the CI-filtered
configuration against the exact tree at `71b7f6cd`:

```
Passed!  - Failed:     0, Passed:   457, Skipped:     0, Total:   457, Duration: 9 m 8 s
```

Five CI-filtered suite runs in total, all 457/457.

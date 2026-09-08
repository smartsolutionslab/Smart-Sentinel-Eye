# Verification note — spec 099

**Issue:** #119 · **Branch:** `test/119-a-source-that-stops-goes-degraded`
**Covers:** T002, T004, T005 (both counterfactuals) and the phase-6 remediation
**Date:** 2026-09-08 · **Machine:** Windows 11, Docker Desktop 28.3.3, one Aspire
stack at a time

Everything below is `-c Release`. Debug hides the warnings CI treats as errors,
and the point of this note is that every claim in it can be re-run.

---

## T002 — assumption A1: what was measured, and what was not

**A1** predicted that `PATCH /v3/config/paths/patch/{name}` makes MediaMTX drop
its current source and re-dial, so the SFU's own `ready` flag goes false while
the new address is unreachable.

**The measurement T002 asked for was never taken.** T002 specified watching
`GET /v3/paths/get/cam-{guid}` across the patch and timing `ready` in both
directions. Nobody did that. What exists instead is the *consequence* of A1,
measured end to end at `/streams` by the tests themselves:

| Transition | Observed (final run) | Budget |
|---|---|---|
| `Healthy → Degraded` after the repoint away | **2.1 s** | 15 s |
| `Degraded → Healthy` after the repoint back | **2.1 s** | 15 s |

So **A1 is confirmed indirectly**: the outcome it predicts happens on every run,
by a mechanism nobody watched. The `plan.md` §Fallback (delete + re-add) was
never needed and remains untested. Recorded this way rather than ticked off,
because "the test passed" is not the reading T002 asked for.

`tasks.md:54` asks for "the two elapsed times" — the SFU-level ones — as *"the
only evidence that 15 s is the right number rather than an inherited guess"*.
What stands in their place is the pair above, one layer out: the transition as a
client of `/streams` sees it. They sit at about a seventh of the budget, which
matches `spec.md`'s arithmetic (one sweep in flight, the SFU's dial, one more
sweep) at its optimistic end.

---

## T004 — both tests green on an unmodified tree

`dotnet build -c Release` first:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Then
`dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~StreamHealthTransition|FullyQualifiedName~RtspTestSourceHealth" --logger "console;verbosity=detailed"`:

```
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds [14 s]
  Standard Output Messages:
 Healthy -> Degraded in 2,1s (budget 15s).

  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [5 s]
  Standard Output Messages:
 Degraded -> Healthy in 2,1s (budget 15s).

  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_pointed_at_the_fixture_source_reaches_Healthy [2 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_at_an_unreachable_address_reaches_Degraded [2 s]

Test Run Successful.
Total tests: 4
     Passed: 4
 Total time: 3,7861 Minutes
```

The 14 s and 5 s are whole-test durations — camera registration and the settle
to `Healthy` included. The transition itself is the 2.1 s the test prints and
now asserts. `RtspTestSourceHealthTests` ran in the same session and was
undisturbed, which is the point of including it in the filter.

(The decimal comma is the machine's locale. On the CI runner the same line
prints `2.1s`.)

---

## T005 — counterfactual 1 (AS-3): the latch the issue describes

`ReportStreamHealthCommandHandler` mutated to return early when the stream is
already `Healthy`. Verbatim, phase 4a:

```
[xUnit.net 00:02:50.21]     SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds [FAIL]
[xUnit.net 00:02:50.21]       System.TimeoutException : Stream for camera 01a07ffe-a2e6-7cda-a79c-5501446105d2 did not reach 'Degraded' within 15s. Last observed state: 'Healthy'. A stream that stayed 'Healthy' after its SFU path was repointed away is the latch spec 099 exists to catch; 'Provisioning' means no stream was provisioned and 'Offline' means the 5-minute window elapsed - both different defects.
  Failed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds [28 s]
[xUnit.net 00:03:06.40]     SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [FAIL]
[xUnit.net 00:03:06.40]       System.TimeoutException : Stream for camera 01a07fff-0740-7485-9236-e7d72bb33fc0 did not reach 'Degraded' within 15s. Last observed state: 'Healthy'. A stream that stayed 'Healthy' after its SFU path was repointed away is the latch spec 099 exists to catch; 'Provisioning' means no stream was provisioned and 'Offline' means the 5-minute window elapsed - both different defects.
  Failed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [16 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_pointed_at_the_fixture_source_reaches_Healthy [588 ms]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_at_an_unreachable_address_reaches_Degraded [2 s]
```

Reverted, same session:

```
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds [12 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [6 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_pointed_at_the_fixture_source_reaches_Healthy [1 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.RtspTestSourceHealthTests.A_camera_at_an_unreachable_address_reaches_Degraded [2 s]
```

**Read the second failure carefully — it is the finding phase 6 acted on.** Both
new tests failed, not one, while `spec.md` predicted *"AS-1 is the only test that
fails"*. The recovery test's message says *did not reach **'Degraded'**, last
observed **'Healthy'*** — that is its **arrangement**, the wait that must succeed
before recovery can be provoked at all. Its recovery assertion never executed. So
this run exercised the `Healthy → Degraded` edge twice and the recovery edge zero
times, and the spec claimed an outcome its own run had disproved. `spec.md` AS-3
now records that in place of the old sentence.

---

## T005b — counterfactual 2 (AS-3b): the control the recovery edge lacked

`tasks.md` wrote this mutation as *optional*. The run above is why it is not.
Applied in the working tree only, reverted immediately, never committed:

```diff
--- a/src/StreamDistribution/Domain/Stream/Stream.cs
+++ b/src/StreamDistribution/Domain/Stream/Stream.cs
@@ -205,6 +205,12 @@ public sealed class Stream : AggregateRoot<StreamIdentifier>
         Ensure.That(clock).IsNotNull();
         EnsureNotRetired(nameof(ReportHealthy));
 
+        // COUNTERFACTUAL (spec 099, T005 second mutation) - reverted, never committed.
+        if (State == StreamState.Degraded)
+        {
+            return;
+        }
+
         StreamState previous = State;
```

`dotnet test ... -c Release --filter "FullyQualifiedName~StreamHealthTransition|FullyQualifiedName~RtspTestSourceHealth"` — verbatim:

```
[xUnit.net 00:02:59.28]     SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [FAIL]
  Failed SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds [19 s]
  Error Message:
   System.TimeoutException : Stream for camera 01a0801e-0e6e-71ad-a473-792c71155698 did not reach 'Healthy' within 15s. Last observed state: 'Degraded'. A stream that stayed 'Healthy' after its SFU path was repointed away is the latch spec 099 exists to catch; 'Provisioning' means no stream was provisioned and 'Offline' means the 5-minute window elapsed - both different defects.
  Stack Trace:
     at SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.WaitForStateAsync(HttpClient streamClient, Guid camera, String expectedState, TimeSpan timeout) in D:\Github\wt-119\tests\Integration.Tests\StreamDistribution\StreamHealthTransitionTests.cs:line 225
   at SmartSentinelEye.Integration.Tests.StreamDistribution.StreamHealthTransitionTests.Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds() in D:\Github\wt-119\tests\Integration.Tests\StreamDistribution\StreamHealthTransitionTests.cs:line 160
--- End of stack trace from previous location ---

Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 42 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

**This is the evidence the recovery edge was missing.** Exactly one test red — the
recovery one — dying at the `Healthy` wait *after* the restore, having reached
`Degraded` normally on the way in: `Last observed state: 'Degraded'` says the
outage half still worked and only the return was refused. The other three, AS-1
and both `RtspTestSourceHealthTests` cases, passed.

(The `line 160` and `line 225` in that stack are the file as committed at
`798f2d44`. Phase 6's fixes moved the recovery assertion to `:190` and
`WaitForStateAsync`'s throw to `:256`.)

Reverted with `git checkout -- src/StreamDistribution/Domain/Stream/Stream.cs`,
same filter, same session:

```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 28 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

`git diff -- src/` is empty on the delivered branch. Neither mutation appears in
a commit.

---

## The budget, and why it now bites

`sinceOutage` starts *before* the repoint call, while `WaitForStateAsync`'s own
15 s deadline starts only after it returns. The enforced quantity was therefore
`repoint + 15 s` while the printed one was `repoint + wait` — a transition
printing `15.6s (budget 15s)` passed green, and `plan.md`'s *"budget breached ⇒
finding"* rule could not fire. Both tests now assert the figure they print:

```csharp
sinceOutage.Elapsed.ShouldBeLessThan(TransitionTimeout);
```

The enforced quantity is now the printed one, and it is stricter than the
deadline inside the wait loop rather than looser.

---

## The CI job that will run this

`integration tests (Docker)` — `.github/workflows/ci.yml:128`, filter
`Category!=Measurement&Category!=Disruptive&Category!=Maintenance`
(`ci.yml:179`). The class carries `[Collection(AspireCollection.Name)]` and no
`[Trait]`, so it is inside that job's selection and outside the Docker-free
`backend` job, which selects `Category=FixtureLogic` only.

**This note records local runs only.** The PR's own CI run is the first
observation of these two tests on the runner, and nothing here should be read as
a claim about their behaviour there.

## What is still not verified

- **The SFU-level mechanism** — see T002 above. Confirmed by consequence, not by
  reading `ready`.
- **Flakiness under load.** Four green runs of these two tests locally, one of
  them the counterfactual revert. The first phase-4a run failed all four tests at
  1 ms in the collection fixture (cold Docker image cache) and passed on every
  attempt after; that failure mode is the fixture booting, not these tests.
- **`plan.md` §Fallback** (delete + re-add instead of patch). Never exercised.

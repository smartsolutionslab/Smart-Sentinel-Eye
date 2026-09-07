# Verification: A boot that does not pass over a corpse

**Feature**: 084 | **Issue**: #2066 (re-scoped) | **Task**: T013
**Branch**: `fix/2066-a-wait-asks-how-it-finished`
**Written**: 2026-09-07, at **phase 6** — see "Provenance" below.

---

## Provenance, because it changes what these blocks are worth

T013 was not done at phase 5. The three runs it asks for were made, and their
output survived only inside the phase-4b agent's handover message. **On a spec
whose entire subject is a green signal that proved nothing, an unrecorded
verification is an unverified one**, so the note is written now rather than
skipped.

- **§1, §2 and §3 are verbatim**, recovered from that handover in this session's
  transcript. They were **not re-run** at phase 6; two Docker boots is the wrong
  price for an artifact when the text exists.
- **§4 was re-run at phase 6**, because the branch was rebased twice onto a
  moving `develop` and the phase-5 figure no longer describes any tree that
  exists. The figure below is the one observed after the final rebase.
- Where a stack trace quotes a line number, **that number is the tree it was
  read from** — `9a371b3b`, before phase 6 restructured the branch and edited
  the file. The constructs are named alongside, and the constructs are the
  reference. (`develop`'s tip already carries one commit about line numbers that
  went stale; this note declines to add another.)

Elapsed times are read off `[xUnit.net HH:MM:SS.ss]` markers, **never**
`Duration:`, which read `8 ms` for a nine-minute boot in spec 062.

---

## 1. Pre-change — the red, and it is a *pass*

Scratch line in `src/Identity/Api/Program.cs`:

```csharp
// SCRATCH (#2066 phase 5) — provoke a startup death. REVERT.
if (Environment.GetEnvironmentVariable("PATH") is not null)
{
    throw new InvalidOperationException("SCRATCH: deliberate identity failure.");
}
```

`PATH`, not `args.Length >= 0`: the latter trips `S3981` under `-c Release` with
`TreatWarningsAsErrors`, which 062 paid for twice.

### 1a. A filtered suite passing over a corpse

Filter `GetCameraIntegrationTests` — four tests, none of which touches identity:

```
[xUnit.net 00:02:43.02]   Finished:    SmartSentinelEye.Integration.Tests

Test Run Successful.
Total tests: 4
     Passed: 4
 Total time: 2,7312 Minutes

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

with identity confirmed dead **in that same run**, 58 seconds before the suite
finished:

```
fail: SmartSentinelEye.AppHost.Resources.identity[0]
      12: 2026-09-06T23:23:45.7980000Z Unhandled exception. System.InvalidOperationException: SCRATCH: deliberate identity failure.
```

**That is the defect.** `InitializeAsync` completed normally, the fixture handed
out its `HttpClient`s, and four tests reported green over a stack with a dead
service in it. No unit test can express this; it is why `plan.md` § "The honest
limits" ¶1 says the committed tests are not the gate.

### 1b. The same stack, when a test *does* talk to identity

Same scratch, filter
`TokenAudienceIntegrationTests.A_client_enrolled_at_runtime_mints_a_token_that_names_it`:

```
[xUnit.net 00:03:15.93]     SmartSentinelEye.Integration.Tests.Identity.TokenAudienceIntegrationTests.A_client_enrolled_at_runtime_mints_a_token_that_names_it [FAIL]
[xUnit.net 00:03:15.93]       Polly.Timeout.TimeoutRejectedException : The operation didn't complete within the allowed timeout of '00:00:30'.
[xUnit.net 00:03:15.93]       ---- System.Threading.Tasks.TaskCanceledException : The operation was canceled.
[xUnit.net 00:03:15.93]       -------- System.IO.IOException : Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request..
[xUnit.net 00:03:15.93]       ------------ System.Net.Sockets.SocketException : The I/O operation has been aborted because of either a thread exit or an application request.
...
[xUnit.net 00:03:15.94]         D:\Github\wt-2066\tests\Integration.Tests\Identity\TokenAudienceIntegrationTests.cs(104,0): at ...RegisterDeviceAsync(String adminToken)
[xUnit.net 00:03:15.94]         D:\Github\wt-2066\tests\Integration.Tests\Identity\TokenAudienceIntegrationTests.cs(82,0): at ...A_client_enrolled_at_runtime_mints_a_token_that_names_it()
```

The stack stops in the **test body**, not the fixture: the boot was pronounced
healthy and the death surfaced three minutes later as a socket error naming
nothing.

---

## 2. The Docker-free cases

`Category=FixtureLogic`, no boot:

```
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 4 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

Seventeen of the forty-seven are `AspireFixtureStartupGateTests`, and they pass
**unmodified** against the implementation.

**They were never observed red, and the branch no longer pretends otherwise.**
The only failure they could have shown is `22 × CS0117` against a tree lacking
`IsFatalStartupState` (15) and `FormatResourceDeathMessage` (7) — measured at
`ea32966d`, and *not* a red test: spec 061 ruled on exactly this in `24e6fc4c`.
Phase 6 restructured the branch into a prelude commit that introduces the two
members with no call site, then these cases green on arrival, then the fix. No
commit on the branch fails to compile.

---

## 3. Post-change — the same filter that passed 4/4

Same scratch, same `GetCameraIntegrationTests` filter:

```
[xUnit.net 00:02:27.05]     SmartSentinelEye.Integration.Tests.CameraCatalog.GetCameraIntegrationTests.Another_fabs_camera_is_refused_exactly_as_an_unknown_one_is [FAIL]
[xUnit.net 00:02:27.05]       System.InvalidOperationException : identity reached Finished with exit code -532462766 — a resource that ends during startup is a failure, not a start.
The fixture stopped here rather than reporting a healthy boot over a dead resource.
identity log:
2026-09-06T23:27:06.5899763Z Waiting for resource 'rabbitmq' to enter the 'Running' state.
...
2026-09-06T23:28:48.1800000Z [sys] Starting process...: Cmd = C:\Program Files\dotnet\dotnet.exe, Args = ["run", "--project", "D:\\Github\\wt-2066\\src\\Identity\\Api\\SmartSentinelEye.Identity.Api.csproj", ...]
2026-09-06T23:28:59.2190000Z Unhandled exception. System.InvalidOperationException: SCRATCH: deliberate identity failure.
2026-09-06T23:28:59.2190000Z    at Program.<Main>$(String[] args) in D:\Github\wt-2066\src\Identity\Api\Program.cs:line 7
[xUnit.net 00:02:27.05]       Stack Trace:
[xUnit.net 00:02:27.06]         D:\Github\wt-2066\tests\Integration.Tests\Fixtures\AspireFixture.cs(864,0): at SmartSentinelEye.Integration.Tests.Fixtures.AspireFixture.ThrowIfAnyGatedResourceDiedAsync()
[xUnit.net 00:02:27.06]         D:\Github\wt-2066\tests\Integration.Tests\Fixtures\AspireFixture.cs(348,0): at SmartSentinelEye.Integration.Tests.Fixtures.AspireFixture.InitializeAsync()

Test Run Failed.
Total tests: 4
     Failed: 4
```

**Four passed before, four failed at fixture init after**, on the same filter and
the same provocation. Read off it:

- The throw is inside `ThrowIfAnyGatedResourceDiedAsync`, called from
  `InitializeAsync`, **before any test body runs** — `(864,0)` and `(348,0)` are
  that tree's line numbers for those two constructs.
- It is an `InvalidOperationException`, so it passes straight through the
  `catch (… OperationCanceledException or TaskCanceledException)` in
  `InitializeAsync` rather than being reported as "did not start within 8
  minutes" (FR-006).
- The state is **`Finished`**, not `FailedToStart` — #1918's exact shape, and the
  case `WaitForResourceHealthyAsync` would not have caught (`spec.md` §4).
- The exit code, the state and the resource's own log are all in the message
  (FR-007), and no forty-five-line state list is printed.

---

## 4. The healthy path, re-run at phase 6

Scratch reverted (`git diff src/` empty), rebased onto `origin/develop`
`16c06cf0`, rebuilt `-c Release`, full suite with **CI's filter**:

```
Test run for D:\Github\wt-2066\tests\Integration.Tests\bin\Release\net10.0\SmartSentinelEye.Integration.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   424, Skipped:     0, Total:   424, Duration: 9 m 37 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

**424 passed, 0 failed, 9 m 37 s**, on a box verified quiet first: zero
containers on the engine and no `dotnet` or `testhost` process belonging to any
other worktree. The twelve extra state reads changed nothing.

**CI's filter, not "no filter".** An unfiltered `dotnet test` is not green on
this machine and never was: `RunModeIngestAttributionTests` is
`Category=Measurement` and needs `SSE_RUNMODE_*` pointing at a long-lived
run-mode stack. `ci.yml` excludes it, and `tasks.md` T012 has been corrected
from "no filter" to the filter CI actually runs.

### Two runs discarded, and what they accidentally showed

The figure above is the third attempt. The first two were taken while a **second
worktree was booting its own full Aspire stack on the same Docker engine** — two
tunnelproxies and two of every container — and both are discarded rather than
reported.

**Attempt 1**: 327 of 424 failed, `stream-distribution` in `FailedToStart`, and
the fixture reported it as an eight-minute `OperationCanceledException` —
*"failed to reach one of the target states: [Running] before the operation was
cancelled"* — thrown from the **existing** `WaitForResourceAsync` line. The new
sweep was never reached.

**Attempt 2**: `Failed: 1, Passed: 423, Total: 424, Duration: 13 m 38 s`. The
failure was
`GatewayRoutingIntegrationTests.Gateway_forwards_the_health_route_to_each_context_service(context: "audit-observability")`
at `[xUnit.net 00:05:52.84]` — a 30-second `Polly.Timeout.TimeoutRejectedException`
→ `TaskCanceledException` → `IOException` → `SocketException`, inside the test
body.

**Both are observations, and the cause in both was a busy machine rather than a
startup failure.** Neither was a controlled provocation and neither is offered
as evidence for anything. What makes them worth a paragraph is that each is the
shape this spec is about, arriving unbidden: attempt 1 is the old wait answering
*"timed out"* where a resource had died, which is §2's premise; attempt 2 is
`plan.md` limit 8 in the flesh — a gateway test failing inside its own body with
a socket error naming nothing, the failure mode this change fixes for the twelve
gated resources and explicitly does **not** fix for `api-gateway`.

**They show the shape of each gap, not its frequency.** Nothing here says how
often an un-gated `api-gateway` bites on a quiet machine; on the evidence of
this session, the answer is unknown. Contention is the stated cause both times:
`00:05:52` falls inside the overlap window, the failure is a timeout rather than
an assertion, and 13 m 38 s against phase 5's 8 m 44 s is a 56% slowdown with no
other explanation.

The phase-5 figure was **423 passed** on the pre-rebase tree. It is superseded
rather than repeated: `921dde95` (spec 083) extended an integration test and
`16c06cf0` (#2113) grew the architecture suite, so the earlier number describes
no tree that now exists.

`Architecture.Tests`, same build:

```
Passed!  - Failed:     0, Passed:   325, Skipped:     0, Total:   325, Duration: 10 s - SmartSentinelEye.Architecture.Tests.dll (net10.0)
```

**325**, not the 311 this branch was briefed with: `16c06cf0` (#2113) grew the
suite after the brief was written. The observed number is recorded rather than
the expected one.

---

## 5. What this note does **not** claim

- **T010, the `camera-catalog` proof, was withdrawn** at phase 6 with its
  argument rather than skipped: the sweep has no name-dependent branch — `name`
  is a loop variable over a `static readonly string[]`, passed unmodified to
  `TryGetCurrentState`, to `CaptureOneResourceLogAsync` and into the message,
  and `IsOneShot` is deliberately never consulted — while phase 4a's *pre-change*
  camera-catalog provocation already observed a dead camera-catalog reaching
  this point. A second .NET project dying on the same DCP path buys nothing.
- **`Terminated` and `RuntimeUnhealthy` were never observed.** They are in the
  set on decompile evidence and are covered by unit test only (`plan.md` limit 5).
- **No container death was provoked**, so `Unknown`'s fail-open choice is
  untested against the three containers among the twelve (`plan.md` limit 7).
- **`api-gateway` is not gated**, and two of its tests wait on it with the
  construct phase 4a disproved (`plan.md` limit 8). Carried to #2146.
- **Nothing here is a timing claim about the change.** It saves no time; it
  changes the verdict (`spec.md` §10).
- **Linux is unaddressed** (`plan.md` limit 6).

---

## 6. Machine and versions

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26100 |
| .NET SDK | 10.0.400 (`global.json` pins `10.0.300`, `rollForward: latestFeature`) |
| Aspire | 13.5.3 (`Aspire.Hosting.Keycloak` 13.5.3-preview.1.26425.3) |
| Docker | `C:` 16 GB free before and after (238 G total, 94% used), unchanged across all three attempts |
| Scratch lines | reverted; `git diff src/` empty, and no commit on the branch contains one |

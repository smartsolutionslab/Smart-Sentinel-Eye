# Spec 076 — An RTSP source the fixture can reach

**Issue:** #198 · **Branch:** `test/198-an-rtsp-source-the-fixture-can-reach`
**Phase:** 1 (Specify) · **Date:** 2026-09-05
**ADRs:** ADR-0103 (integration tests are Aspire-only — the decision that rules
out the side-car the issue proposes, and the one that makes this an AppHost
resource instead), ADR-0024 (Aspire is the composition root), ADR-0111 (the
Scenario Simulator, and the `camera-sim` precedent for one MediaMTX pulling RTSP
from another by container DNS), ADR-0011 (WebRTC SFU — RTSP ingest,
passthrough), ADR-0052 (xUnit + Shouldly), ADR-0109 (parallel markers),
ADR-0037 (phased workflow), ADR-0144 (autonomous lane — no ADR is written here).

## The issue as filed, and what survives contact with the repository

> Implement `AspireFixture.StartRtspTestSourceAsync()` … boot a side-car
> `mediamtx` … container as an RTSP **publisher**, fed by `ffmpeg` with the
> bundled `testsrc` or a looping H.264 sample; return the source URL
> (e.g. `rtsp://<sidecar>:8554/test`) plus a `DisposeAsync` handle.

**The need is real and unmet. The build described is one the repository has
already done, under another name, for another lane.** The issue is from spec 002
(August). Spec 056 built exactly the container it asks for.

| Claim as filed | Measured on `fa305927` |
|---|---|
| there is no reachable RTSP source | **False as a repo statement, true as a fixture statement.** `fixture-video` is a fully-configured RTSP publisher in the AppHost |
| `AspireFixture.StartRtspTestSourceAsync()` must be written | Does not exist anywhere. Spec 002 **T021 is ticked `[x]`** and the helper was never written — the tick covers that task's other four clauses |
| build a side-car container from the test | **ADR-0103 forbids it.** Integration infrastructure comes "**exclusively**" from booting the AppHost |
| follow-up #197 is unblocked by this | **#197 is CLOSED.** That half of the issue's rationale is spent |
| `ListStreamsByCamerasIntegrationTests` defers Healthy | **True.** Still asserts `"Degraded"` for all five, doc comment still says deferred |
| `WhepHandshakeLatencyTests` defers for want of a source | **Partly.** It defers the browser-side WebRTC negotiation, which needs a *headless-browser harness*, not just a source. A source alone does not unblock it |

**Outcome: partly built.** The publisher exists. What is missing is three things,
and the third is the only one the issue named:

1. `fixture-video` is gated `isRunMode && !isE2ETests`. `AspireFixture` passes
   `E2ETests=true`. **The one video source the repository has is switched off in
   the one lane that asked for it.**
2. Nothing in `AspireFixture` names its URL, so a test could not reach it even
   if it were running.
3. No test asserts a camera reaches `Healthy`.

### Why the gate is where it is, and why moving it is not a reversal

`AppHost.cs` argues the exclusion in a comment written deliberately:

> So this is present where a browser needs a picture and absent where nothing
> consumes one -- an integration run has no browser, and would otherwise pay for
> a container, a 45 MB bind mount and a permanently looping FFmpeg it never
> reads.

Every clause is true **of the composition as it stood**. The load-bearing one is
the last: *it never reads*. This spec is the point at which an integration test
starts reading it. The comment does not forbid the change; it states the
condition under which the exclusion was correct, and this spec removes that
condition. **The comment must be rewritten in the same commit** — a comment that
argues against the code beside it is this repository's recorded defect class,
not a cosmetic lag.

### What already works, so that nothing here is speculative

- **A MediaMTX pulling RTSP from a sibling MediaMTX by container DNS is a built,
  running path.** `SimulatorOptions.RtspHost` defaults to `camera-sim:8554` and
  the main SFU pulls it (ADR-0111). `fixture-video` is the same image on the
  same DCP network; `rtsp://fixture-video:8554/loop` is the same shape.
- **The SFU pulls without a reader.** `mediamtx.yml` sets no `sourceOnDemand`,
  so MediaMTX's default applies and a path with a `source` dials out on
  creation. This is *why* the five unreachable cameras reach `Degraded` today
  with no browser attached — the same mechanism that will reach `Healthy`
  against a source that answers.
- **`fixture-video` grants the reads.** Its `authInternalUsers` block gives the
  anonymous `any` user `read` and `playback`.
- **`RtspUrl` accepts the hostname.** It requires the `rtsp://` scheme, length
  under 2048, and no `user:password@` segment. `fixture-video` is a legal `Uri`
  host.
- **The state machine allows it.** `StreamState`: `Provisioning → Healthy` on
  first frame decoded; `StreamHealthWatcher.PollInterval` is 2 s.

## Locked technical choices

| Concern | Choice | Why |
|---|---|---|
| Where the source lives | An **Aspire resource**, not a test-spawned container | ADR-0103: integration infrastructure comes exclusively from the AppHost |
| Which source | The **existing `fixture-video`**, reused | A second publisher beside an identical one is the thing this run was told not to build; #2103 already counts three MediaMTX containers |
| How a test opts in | By **pointing a camera at the URL**, not by starting a container | The resource is up either way; a test that never names the URL pays nothing beyond container start |
| The URL | `rtsp://fixture-video:8554/loop`, surfaced as a constant on `AspireFixture` | Matches `SimulatorOptions.RtspHost`'s established `<service>:8554` form |
| Image tag | Unchanged — `bluenviron/mediamtx:latest-ffmpeg` | The resource already exists; this spec adds **no new container**. Pinning all three is #2103's job and is not smuggled in here |
| Test framework | xUnit + Shouldly, `AspireCollection` | ADR-0052, ADR-0103 |

## Assumptions, marked

- **A1 — DCP gives containers mutual DNS under `E2ETests=true`.** Proven for run
  mode (`camera-sim`); the fixture is also run mode, and the
  `MTX_AUTHHTTPADDRESS` override at `AppHost.cs:309` exists precisely because
  the fixture runs containers on a shared network with host processes outside
  it. Not yet observed with two containers under the fixture. **If it fails,
  T007's red output says so on the first run** — this is the one assumption
  phase 4 disproves cheaply rather than late.
- **A2 — a `-c copy` loop is not a meaningful cost. Measured at phase 5.**
  `runOnInit` publishes for the whole integration run; the clip is already
  H.264 so nothing transcodes. Two figures:

  - **Suite cost.** The CI-filtered integration suite on this branch:
    **404/404 in 613 s**. The same suite at `origin/develop`, where
    `fixture-video` is absent from the integration lane: **402/402 in 605 s**.
    **+8 s (+1.3 %) for +2 tests**, against a 30-minute job timeout.
  - **Container cost.** `fixture-video` accumulated **2 CPU-seconds over 105 s
    of wall clock — ≈ 1.9 % of one core** — at 34.8 MiB RSS. Read with `docker
    top -o etime,time`, which reports cumulative CPU time; `docker stats`
    reports an instantaneous rate and cannot answer this question at all.

  **What these figures are not.** One A/B pair, same box, back to back, on
  Windows/Docker Desktop — not `ubuntu-latest`, and not repeated. This
  repository's own guidance is that a measurement run needs repeating before it
  is believed, and the settling evidence for the real thing is `integration`
  job durations across several CI runs, which is not yet in hand. Read +8 s as
  "no cost visible at this resolution", not as a number to defend.

  The contingency is unchanged and still unbuilt: if the cost ever turns out to
  matter, the remedy is a second `runOnDemand` path in `fixture-video.yml`,
  deliberately *not* built now (no speculative generality).
- **A3 — `Healthy` arrives within 15 s.** Container start + FFmpeg start + SFU
  dial + three poll intervals. The test budget is 30 s, matching
  `ListStreamsByCamerasIntegrationTests`; the issue's ~15 s is the expectation,
  not the assertion.

## User stories

### US-1 (P1) — An integration test can point a camera at a source that answers

*The only story that must ship. Independently shippable and observable
end-to-end: register a camera, watch it go `Healthy`.*

As an engineer writing an integration test, I can register a camera against an
RTSP URL the Aspire stack actually serves, so that I can assert the `Healthy`
half of the stream state machine instead of only the failure half.

### US-2 (P2) — The existing badge test asserts a mix

As a reviewer, I can see `ListStreamsByCamerasIntegrationTests` assert both
`Healthy` and `Degraded`, so the batch endpoint is shown to *distinguish* states
rather than to report one state five times.

**Out of scope:** `WhepHandshakeLatencyTests`. Its deferral is a
headless-browser harness, not a source. Saying so is the finding; changing it is
a different spec.

## Acceptance scenarios

### AS-1 — happy: a camera pointed at the fixture source reaches Healthy

```gherkin
Given the Aspire stack is booted by AspireFixture with E2ETests=true
  And the fixture-video resource is Running
When a camera is registered with rtspUrl "rtsp://fixture-video:8554/loop"
Then GET /streams?cameraIdentifiers=<id> reports state "Healthy"
 And it does so within 30 seconds of registration
```

### AS-2 — contrast: an unreachable camera still reaches Degraded

```gherkin
Given the Aspire stack is booted by AspireFixture
When a camera is registered with rtspUrl "rtsp://10.0.6.1/h264"
Then GET /streams?cameraIdentifiers=<id> reports state "Degraded"
```

*This is the conflict case that gives AS-1 its meaning. Without it, a test
asserting `Healthy` could be passing because the watcher reports `Healthy` for
everything — which is the exact failure mode spec 056 was written to close, in
its own domain.*

### AS-3 — mixed batch: the endpoint distinguishes

```gherkin
Given three cameras at "rtsp://fixture-video:8554/loop"
  And two cameras at unreachable addresses
When GET /streams?cameraIdentifiers=<all five>
Then three report "Healthy" and two report "Degraded"
```

### AS-4 — bad request: the composition guard still holds and stops lying

```gherkin
Given the AppHost model built with E2ETests=true
When the resource names are listed
Then they contain "fixture-video"
 And they do not contain "camera-sim"
 And they do not contain "scenario-simulator"
 And they do not contain "pgadmin"
```

*`E2ETests_argument_excludes_the_dev_only_resources` asserts three absences today
and says nothing about `fixture-video`. Its class doc does — it states `E2ETests`
"also removes the Vite apps and `fixture-video`". After this spec that sentence
is false, and a false doc on the guard whose whole job is explaining the switch
is worse than no doc. The presence assertion is added so the guard covers the
resource its doc names.*

### AS-5 — auth: the SFU reads the fixture source without a credential

```gherkin
Given the main mediamtx pulls rtsp://fixture-video:8554/loop
Then the pull succeeds against fixture-video's permissive internal user
 And no credential appears in any camera's stored RtspUrl
```

*`RtspUrl.From` rejects a `user:password@` segment, so a design needing a
credential in the URL could not be registered at all. Recorded because it
constrains the design, not merely because it passes.*

## Independent end-to-end test procedure

Not a unit test and not the suite. A human, or phase 5, does this:

1. `dotnet test tests/Integration.Tests --filter "FullyQualifiedName~RtspTestSource"`.
2. While it runs, `docker ps` shows a `fixture-video` container alongside
   `mediamtx`.
3. `curl -s localhost:<mediamtx-api>/v3/paths/list` shows the `cam-<guid>` path
   with `"ready": true` and a `source.type` of `rtspSource`.
4. `ffplay rtsp://localhost:<fixture-video-rtsp>/loop` shows moving pictures —
   the source is publishing, not merely listening.
5. Record the wall clock from registration to `Healthy`, and `fixture-video`'s
   CPU from `docker stats` (assumption A2).

**In CI** the same test runs in the existing Docker integration job. Step 4 has
no equivalent there and is not required; steps 1–3 are what CI proves. The job
already carries MediaMTX, so **no image pull is added** — `fixture-video` uses
the same `bluenviron/mediamtx:latest-ffmpeg` tag the SFU already pulls, and
Docker shares the layers.

## Latency budget impact

**N/A.** Nothing here runs in production or on the event-to-overlay path.
`fixture-video` is gated to run mode, and the change stays inside a gate that
`publish` mode never evaluates. No leg of constitution §IV is touched, and this
spec makes no §VII dashboard claim.

The one indirect connection worth stating: an integration lane that can observe
`Healthy` is a precondition for automating the camera → SFU leg's measurement,
which today is a manual reading. That is a future spec's benefit, not a claim
this one discharges.

## Non-goals

- No new container, no new image, no fourth floating tag (#2103 is neither fixed
  nor worsened here).
- No `runOnDemand` path until A2 is measured.
- No headless-browser harness; `WhepHandshakeLatencyTests` is untouched.
- No change to `fixture-video.yml`, `mediamtx.yml`, or `camera-sim.yml`.
- No production, Helm, or `deploy/` change.
- No board mutation (Projects v2 rate limit; the human handles it).

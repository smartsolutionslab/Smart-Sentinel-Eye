# Spec 077 — Click-to-first-frame, measured not proxied

**Issue:** #199 · **Branch:** `test/199-click-to-first-frame-measured-not-proxied`
**Phase:** 1 (Specify) · **Date:** 2026-09-06
**ADRs:** ADR-0031 (the PR body's latency section — read below, because the
issue's acceptance over-reads it), ADR-0108 (browser e2e runs against a live
`aspire run` stack), ADR-0129 / ADR-0128 (what the overlay budget is and is not),
ADR-0144 (the lane may not weaken a gate to reach green).
**Supersedes in part:** spec 002's T086 deferral (`plan.md:645-648`).

---

## The issue as filed, and what survives contact with the repository

The need is real, unmet, and the blocker is genuinely cleared. Four of the
issue's own premises are not accurate, and two of them change the design rather
than merely the prose.

### 1. There is no `Watch` button, and no camera row that opens a stream

The issue says: *"navigates to a camera row, clicks `Watch`"*. Nothing in
`apps/management-web` renders a control called Watch. The path an operator
actually takes is:

- `apps/management-web/src/features/cameras/CamerasPage.tsx:85` — the camera's
  **name** is a `<Link to={`/cameras/${row.cameraIdentifier}`}>`. That link is
  the only click.
- `apps/management-web/src/features/cameras/CameraDetailPage.tsx:118` — the
  detail page mounts `<CameraViewer …/>` unconditionally. There is no second
  click, no play control, no consent step.

So **click-to-first-frame is: click the camera's name in the list → first
decoded frame in the `<video>` on the detail page.** That is the operator
gesture, and it is one click, as the SLO assumes. The proposal's shape survives;
its locator does not.

### 2. `tests/E2E.Tests/` does not exist and must not be created

The issue offers *"New `tests/E2E.Tests/` project (or wherever the Playwright
tests land)"*. Playwright tests land in `e2e/`, driven by `playwright.config.ts`
at the repository root, with five projects (`chromium`, `seed`, `kiosk`, `wall`,
`cleanup`) — 26 spec files. A `tests/E2E.Tests/` C# project would be a second
home for the same job. **This spec adds one file to `e2e/`.**

### 3. The instrument is not an open choice — spec 002 already decided it

The issue offers *"`RTCPeerConnection.connectionState === 'connected'` (or first
`video.onloadeddata`, whichever is cleaner)"*. It is not a choice:

> **FR-013:** The viewer-path latency budget (click in UI → **first decoded
> frame in `<video>`**) MUST be ≤ **3 seconds p95** …
> — `specs/002-watch-camera-live/spec.md:283`

`connectionState === 'connected'` is a **transport** fact and would contradict
FR-013. #2111 is open precisely because a tile reports Live off the transport
state alone while showing black. Choosing it here would record a number that
does not mean what the SLO says, in the same way and for the same reason.

**Decided: the first decoded frame, read as `getVideoPlaybackQuality()
.totalVideoFrames` crossing zero.** The repository already owns this instrument
— `e2e/kiosk-shows-a-label-over-video.spec.ts:88-102` reads exactly this, and
says why in its own doc-comment: *"counts frames the decoder actually produced.
Deliberately **not** `currentTime`, which can advance over a stalled track."*
`video.onloadeddata` is close in meaning but is an **event**, so a test that
attaches after it has fired observes nothing; a monotonic counter can be
sampled at any time. See §Locked technical choices for the resolution
consequence, which is the part that is easy to get wrong.

### 4. Click-to-first-frame is **not** one of constitution §IV's six legs

The brief asked this directly, and the answer is load-bearing.

§IV's table budgets **event arrival → overlay rendered ≤ 800 ms** across six
legs. Click-to-first-frame is a different budget belonging to spec 002, and both
documents already say so:

> This is NOT the constitution's 800 ms event-to-overlay budget — that begins
> ticking after the first frame is rendered.
> — `specs/002-watch-camera-live/spec.md:284-286`

> Spec 002 produces the FIRST frame; the 800 ms event-to-overlay budget begins
> ticking AFTER that.
> — `specs/002-watch-camera-live/plan.md:68`

**Therefore this spec changes no cell of §IV's table, and §VII's dashboard
obligation (ADR-0117, which binds implemented §IV legs) does not attach to it.**
Nothing here may be written or reviewed as discharging one of those six rows.
A spec that produced a latency figure and let a reader assume it moved §IV would
be the clerical error §IV warns about twice, arriving from the other side.

### 5. ADR-0031 does not require what the acceptance says it requires

The acceptance reads: *"PR body for spec 003+ cites the measured value per
ADR-0031, replacing the auth-hook proxy."* ADR-0031 mandates a **Latency budget
impact** section containing *"which leg(s) of the latency budget are affected,
measured value vs budget, or `N/A — <reason>`"*. Its subject is §IV's legs. For
a PR that is not on the event-to-overlay path — which is most of them, and this
one — the ADR-0031-correct content is `N/A`.

So the durable obligation the issue is reaching for comes from **spec 002
FR-013 and `plan.md:707-708`** (*"CI emits the measured click-to-first-frame
from the integration test; if it drifts above 3 s p95, the PR is blocked"*),
not from ADR-0031. This spec satisfies that sentence for the first time.
**No ADR-0031 amendment is proposed and none is needed.**

### What is accurate, and is the whole reason to build this

`tests/Integration.Tests/StreamDistribution/WhepHandshakeLatencyTests.cs` says
so itself, in the doc-comment the brief quoted:

> The real browser-side WebRTC negotiation is deferred to a headless-browser
> harness per the plan; the auth-hook round-trip is the dominant
> operator-controlled term in that latency budget.

It times `POST /streams/authorize` — 20 iterations — and asserts
`p95 < 3000 ms`. The auth hook is an HTTP round-trip to a neighbouring
container; spec 002's own decomposition budgets it inside the *"≤ 200 ms
`/streams/{id}` lookup"* term. **An assertion of `< 3000 ms` against a quantity
budgeted at a fraction of 200 ms is a tripwire that cannot trip.** It is not
wrong about anything; it simply cannot fail for the reason it exists. That is
the gap, and it is a bigger one than "part of the handshake is unmeasured".

---

## Locked technical choices

| Concern | Choice | Source |
|---|---|---|
| Harness | Playwright, existing `e2e/` suite, `chromium` project | ADR-0108, `playwright.config.ts` |
| Surface | management-web `/cameras` → `/cameras/{id}` | `CamerasPage.tsx:85` |
| Instrument | `getVideoPlaybackQuality().totalVideoFrames > 0` | spec 002 FR-013; `kiosk-shows-a-label-over-video.spec.ts:88` |
| Clock | `performance.now()`, **in the page**, not the test process | §Resolution, below |
| Video source | `FIXTURE_VIDEO_RTSP_URL` | `e2e/support/live-video-wall.ts:44` (spec 076, #198) |
| Sample count | 20 sequential opens after one discarded warm-up | issue; mirrors `WhepHandshakeLatencyTests` |
| Threshold | p95 < 3000 ms, **asserted** | spec 002 FR-013 + `plan.md:707` |
| Repeat-before-believing | the config's existing `retries: isCI ? 2 : 0` | `playwright.config.ts:15` |
| Cleanup | camera named `E2E …`, swept by the existing teardown | `e2e/support/retire-e2e-cameras.teardown.ts` |

### Resolution — why the clock lives inside the page

`expect.poll` backs off (100 ms, 250 ms, 500 ms, 1 s …). Timing a 3 s budget
with it would add up to half a second of quantisation, **always in the direction
of over-reporting**, and would silently consume a sixth of the budget in
measurement error. The harness therefore starts a `requestAnimationFrame` loop
**in the page**, marks `t0` there, and resolves with `performance.now() - t0`
when a `<video>` first reports a produced frame. At 25 fps the true floor is one
frame period (40 ms); rAF sampling reaches it, and Playwright polling does not.

`expect.poll` is still the right tool for spec 056's *"is the picture moving"*
question, which is not a stopwatch. Two different jobs, two different tools.

### The fixture's keyframe interval is a real and irreducible term

`src/AppHost/Resources/clips/sim-loop.mp4`, measured with `ffprobe`:
H.264 Constrained Baseline, 1280×720, **25 fps, GOP 25 — a 1.000 s keyframe
interval**, 20 s, served `-c copy` so the SFU passes it through unchanged
(`fixture-video.yml:48-52`).

A WebRTC receiver cannot decode before an IDR. A viewer joining at a uniformly
random phase therefore waits **0–1000 ms, mean 500 ms, p95 ≈ 950 ms** for its
first decodable frame, and no amount of product work removes it. Spec 002's
decomposition already budgeted this as *"≤ 1 s decoder warmup"*
(`plan.md:703`), so the fixture is representative rather than pathological — but
it means roughly a third of the 3 s budget is spent on a property of the source,
and the measured p95 will sit well above the handshake cost alone. **Any reading
of the resulting figure that omits this term will misattribute it.**

**This prediction stood untested through the first three green runs, and was
briefly recorded as refuted.** T005's harness measured a 107 ms spread and the
term was written off. It could not have been tested that way: the loop clicked,
waited for a frame, navigated back and clicked again, and the frame it waits for
is *by construction* an exact IDR instant — so every click landed at a fixed
offset from an IDR and every sample was `1000 ms − N` for `N` the loop's own
un-timed overhead, constant and independent of the product's cost. A tight band
is what the keyframe term **predicts** under a phase-locked loop, not evidence
against it; p95 reproducing as 843 / 843 / 856 ms across three separate runs is
the tell, because an independently-sampled product cost does not repeat to the
millisecond.

**With a uniformly random 0–1000 ms wait inserted before each click (T008), the
prediction is confirmed.** `elapsed + delay` is constant modulo exactly 1000 ms
in two branches a GOP apart; the spread reopened to 893 / 1177 ms and p95 to
1158 / 1420 ms. See §*Assumptions, marked* item 2 for the figures and for **S**,
the setup cost the product owns, which this is the first measurement of.

---

## Assumptions, marked

1. **The MediaMTX path stays pulled between opens.** `mediamtx.yml` sets no
   `sourceOnDemand`, so a path added via `MediaMtxRtspGateway.AddPathAsync`
   pulls its source continuously rather than per reader. The 20 opens are
   therefore warm and measure re-negotiation, not path establishment. *If this
   is wrong, every sample pays an RTSP dial and the figure is meaningless —
   T004's discarded warm-up is where that would show, and it must be looked at,
   not skipped.*
2. **Predicted p95 ≈ 1.4–2.2 s** (≤ 200 ms lookup + WHEP POST + ICE/DTLS over
   the container-network path + 0–1000 ms IDR + decode). Recorded as a
   prediction so the first observed figure either confirms or refutes it. **A
   first observation above 3 s is a finding to report, never a threshold to
   raise** (ADR-0144; and #2119 is open because a budget drifted that way).

   **Observed, on independent samples (2026-09-06, two 20-open runs on a warm
   stack):**

   | | run 1 | run 2 |
   |---|---|---|
   | p50 | 608 ms | 851 ms |
   | **p95** | **1158 ms** | **1420 ms** |
   | max | 1226 ms | 1459 ms |
   | min | 333 ms | 282 ms |
   | spread | 893 ms | 1177 ms |
   | warm-up (discarded) | 1165 ms | 744 ms |

   p95 lands just below the predicted band and the SLO passes with roughly 2.1–
   2.6× headroom. The earlier 843 / 843 / 856 ms is superseded: it was a
   phase-locked artifact, not a measurement of this population.

   **S ≈ 290 ms** — the setup cost alone (SPA route change, WHEP POST, ICE,
   DTLS, first RTP), with the IDR wait near zero. **Nothing had measured it
   before.** Two estimators agree: the smallest of the 40 samples is 282 ms (a
   direct upper bound, since `elapsed = S + IDR wait`), and the pooled mean of
   789 ms sits ~500 ms above `S` when the phase is uniform over a 1 s GOP. So
   about a quarter of the figure is the product and the rest is the fixture.
   **A future regression should be read off the minimum sample, not off p95** —
   p95 moves with the source, the minimum moves with S.
3. **Importing `FIXTURE_VIDEO_RTSP_URL` from `live-video-wall.ts` is accepted
   friction.** The constant's home is a wall-shaped module and this consumer is
   not a wall. Moving it is a rename touching spec 056's files for no behaviour;
   the smaller change is to import it. *If a third consumer appears, it should
   move to its own module then.*
4. **`retire-e2e-cameras.teardown.ts` sweeps by the `E2E ` name prefix**, so the
   harness's camera must carry it, as every other e2e-registered camera does.

---

## User stories

### US-1 (P1) — The click-to-first-frame SLO is measured through a browser

*As the person who has to answer "does opening a camera take under three
seconds", I want the figure produced by an actual browser opening an actual
camera and seeing an actual picture, so that the number means what FR-013 says
it means.*

**Independently shippable.** One new file in `e2e/`. No `src/` change, no
`apps/` change, no contract, no migration, no board mutation. It can be built,
run and observed on its own, and it is the whole of the P1 slice.

### US-2 (P2) — The proxy stops asserting a budget it never measured

*As a reviewer, I want `WhepHandshakeLatencyTests` to assert against something
it can actually fail, so the suite has no assertion that is structurally
incapable of going red.*

**Deliberately out of the P1 slice, and it needs a human's word.** Narrowing an
existing assertion is close enough to *weakening a gate* (ADR-0144's second
forbidden act) that the lane should not do it on its own judgement — even though
the motive here is the opposite, and the correct gate would exist by then. Three
options, none chosen here:

- keep the timing, drop the `< 3000 ms` assertion, print the figure (it is a
  cheap, useful, Docker-lane number);
- re-target it at its own sub-budget from `plan.md:703` (the ≤ 200 ms lookup
  term), which is a threshold nobody has yet defended;
- leave it exactly as it is, and accept a permanently-green tripwire.

Its doc-comment must be corrected either way, because after US-1 it no longer
describes the world: the harness it defers to will exist.

---

## Acceptance scenarios

### AS-1 — happy: an operator opens a camera and a picture arrives inside the budget

```gherkin
Given the Aspire stack is running with `fixture-video` serving rtsp://fixture-video:8554/loop
  And an operator has signed in to management-web
  And a camera named "E2E ClickToFrame <stamp>" is registered at that source
  And one warm-up open has already decoded a frame, and its timing is discarded
When the operator clicks the camera's name in the list, twenty times in sequence,
     returning to the list between opens
Then each open records the elapsed time from the click to the first frame the
     decoder produces on that page
  And the p95 of the twenty samples is below 3000 ms
  And the p50, p95, max and the full sample list are printed on success as well
     as on failure
```

### AS-2 — the red that makes AS-1 mean something: no source, no frame, no number

```gherkin
Given the same harness
  And the camera is registered at rtsp://10.0.5.98/stream, which nothing serves
When the harness opens the camera
Then no frame is ever decoded
  And the harness fails naming the camera and the address, rather than reporting
     a time
```

This is the **observed first red** (see §Three declarations in `tasks.md`), and
it is a sequencing step rather than a retained test — `cold-stack.ts` is
explicit that a budgeted wait must never be spent on an absence, because it
turns every future failure into a stall.

### AS-3 — conflict: a frozen source is not a fast source

```gherkin
Given a source that emitted exactly one frame and stopped
When the harness measures the open
Then the measurement is not reported as a healthy sub-second result on the
     strength of that single frame
```

Satisfied by construction, and stated so it cannot be lost: the harness times
the **first** frame per open and re-opens from a torn-down peer connection each
time, so a stalled source yields at most one fast sample and nineteen timeouts.
The complementary check — that the picture keeps moving — already exists and is
not duplicated here (`kiosk-shows-a-label-over-video.spec.ts`).

### AS-4 — bad request: a camera the operator may not see yields no measurement

```gherkin
Given a camera identifier the signed-in operator cannot resolve
When the harness navigates directly to /cameras/{that identifier}
Then the page shows "No such camera" and mounts no <video>
  And the harness records no sample rather than a zero
```

Covered today by `camera-detail.spec.ts`'s indistinguishability test; restated
here because a harness that treated "no video element" as "0 ms" would report a
perfect score for a journey nobody took — the exact defect `CameraViewer.tsx`
already guards against in its own comment (*"Null rather than zero: a zero would
read as a perfect score for a journey nobody timed"*).

### AS-5 — auth: the measurement is taken as a real operator, holding a real token

```gherkin
Given the harness signs in through the real Keycloak login as `operator`
When it opens the camera
Then the WHEP open is authorised by the MediaMTX external-auth hook against that
     operator's bearer token
  And no `fetch` appears in the harness to arrange state or mint a token
```

The no-`fetch` rule is this suite's own, learned twice and written down in
`camera-detail.spec.ts`: a test that reaches around the UI exercises the API
while claiming to exercise the application. It matters more here than usual —
the measured quantity **includes** the auth hook, so a harness that skipped the
UI's token path would time a different journey.

---

## Independent end-to-end test procedure

Runnable by a person, without reading the harness's source:

1. Check `C:` free space (≥ 6 GB), then `dotnet run --project src/AppHost` and
   wait for `fixture-video`, `mediamtx`, `management-web` and Keycloak to be
   healthy.
2. `pnpm exec playwright test e2e/click-to-first-frame.spec.ts --project=chromium`
3. Read the printed line. It must name p50, p95, max and the twenty samples.
4. **Confirm the figure is not the polling grid.** Samples that cluster on
   100 / 250 / 500 / 1000 ms boundaries mean the clock leaked back into the test
   process. Do **not** read a tight cluster as reassuring, and do not expect any
   particular width: each open waits a random 0–1000 ms before the click
   (`DECORRELATION_WINDOW_MS`), so the samples are independent draws and their
   spread is a *result*. Observed 893 ms and 1177 ms — the source's 1.000 s GOP
   being paid, as this spec predicted. A run that clusters inside ~150 ms is the
   signal that the decorrelation wait has been removed and the harness is
   phase-locked again.
5. Open management-web at `http://localhost:5173`, sign in as `operator`, click
   the camera the run left behind (or a fresh one at the fixture URL) and watch
   a picture appear. **A person confirms it is a picture; the harness confirms
   how long it took.** The two claims are different and neither substitutes.
6. Re-run step 2 once. Per this repository's own rule, a measurement run is not
   believed until it has been repeated — the first run after machine churn looks
   exactly like a regression.

---

## Latency budget impact

**N/A — not on the event-to-overlay path.** Click-to-first-frame is spec 002's
viewer-path SLO (FR-013), explicitly disclaimed as the 800 ms budget by spec 002
itself. **No cell of constitution §IV changes**, no leg's *Measured* column
moves, and §VII's dashboard obligation does not attach.

The figure this spec produces belongs to spec 002's SLO. It goes: into the
test's printed output on every run, into the Playwright report artifact CI
already uploads, and into this spec's phase-5 verification note. It does **not**
go into §IV, and it does **not** become a sixth `kiosk-latency` measurement name
— that closed set is §IV-scoped, the reporter is a kiosk tile rather than an
operator's console, and adding a name is a server contract change
(`stream-distribution/streams/kiosk-latency`) for a figure with no dashboard to
reach.

---

## Non-goals

- **Inter-display synchronisation, playout alignment, overlay timing.** Different
  budget, different spec, and ADR-0128 puts the first out of scope entirely.
- **A dashboard, a metric export, or a `kiosk-latency` measurement name.** #1940
  is where the dashboard question lives and it is unsettled; this spec must not
  be read as answering it.
- **Measuring the kiosk wall's tile-open time.** A wall opens many tiles at once
  under a layout; that is not the one-click operator gesture FR-013 budgets.
- **Retiring or narrowing `WhepHandshakeLatencyTests`.** US-2, P2, human's call.
- **Raising the 3 s threshold.** Forbidden by ADR-0144 and by this spec.

---

## Review gate (Phase 1)

Spec reviewed, no `[NEEDS CLARIFICATION]` outstanding. Two questions a reviewer
should answer before Phase 4 rather than during it:

1. **US-2's disposition** — narrow, re-target, or leave `WhepHandshakeLatencyTests`?
   P1 does not depend on the answer.
2. **Whether the harness is gated out of the default e2e run.** `plan.md`
   §Cost recommends *not* gating it, with the measured job times. A reviewer who
   disagrees should say so now, because the mechanism differs (a Playwright tag
   vs. a project) and changing it later is not free.

# Spec 094 — A tile reports Live when a picture arrives

**Issue:** #2111 · **Branch:** `fix/2111-a-tile-reports-live-when-a-picture-arrives`
**Worktree:** `D:/Github/wt-2111`
**Phase:** 1 (Specify) · **Date:** 2026-09-07
**ADRs:** ADR-0037 (the phased workflow), ADR-0074/0075 (the two React apps and
their state), ADR-0129 (what the overlay budget is and is not), ADR-0117 (a leg
not subject is not exempt), ADR-0139/§Testing (new behaviour starts red),
ADR-0144 (the lane may not weaken a gate, and may not write an ADR).
**Depends on the decision recorded in:** spec 077 §3 (the first-frame instrument
is not an open choice), spec 002 FR-013 (click → first decoded frame ≤ 3 s p95).

---

## The issue as filed, and what survives contact with the repository

Every line reference in #2111 is accurate and the defect is real. Four of its
supporting claims are not, and **two of them change the design**.

### Confirmed, by reading the code

- `apps/shared/src/ui/composites/useWhepSession.ts:135-141` — `transitionTo('live')`
  has **exactly one** producer, inside the `connected` branch of
  `onConnectionStateChange`. Verified by grep: the five `transitionTo(` call
  sites are `:116` (offline), `:127` (reconnecting, from `scheduleRetry`),
  `:141` (live), `:167` (connecting), `:187` (reconnecting, from Degraded
  health). One `live`.
- `:64-65` — the doc comment reads exactly as quoted.
- `WhepClient.connect()` reports attachment **in no form**:
  `apps/shared/src/streaming/WhepClient.ts:57-83`, `ontrack` writes
  `videoEl.srcObject` and returns void. No callback, no return value, no field.
- `attemptRef.current = 0` and the grace-timer clear happen **only** inside the
  `connected` branch (`:136-140`).

### Not accurate, and it does not matter — 1. a third retry producer exists

The issue says retries are scheduled *"only from `failed`/`disconnected`"*.
There is a third: `client.connect(...).catch(...)` at `:168-171`. It is
immaterial to the scenario — a session that negotiates cleanly and reaches
`connected` resolves that promise — but the sentence is not true of the code.

### Not accurate, and it changes the design — 2. `framesDecoded` cannot be reused

The issue proposes sizing this against *"the `framesDecoded` sampling
`CameraViewer` already runs every 5 s"*, calling it *"an existing signal"*.

**The sampling exists.** `CameraViewer.tsx:112-139`, `DECODE_SAMPLE_INTERVAL_MS
= 5_000`, feeding `decodeSampleFrom` (`kioskLatency.ts:172-190`), which reads
`inbound-rtp` / `kind: 'video'` / `framesDecoded`. **It cannot serve this
purpose**, for four independent reasons:

1. **It is gated on `status === 'live'`** (`:113`). Under any design where
   `live` means media, it can never run before `live` — the signal that would
   decide the gate is switched on by the gate. Circular.
2. It lives in `CameraViewer`, not in the hook that owns the state machine, and
   it reports **latency**, not liveness.
3. It needs **two** samples 5 s apart to produce anything
   (`decodeElapsedBetween` returns null when `frames <= 0`). Its cadence is more
   than 3× the watchdog window this spec chooses.
4. It reaches the receiver through `WhepClient.stats()`, which returns **null**
   on any peer connection without `getStats` (`WhepClient.ts:120-123`) —
   including the existing test fake. At the hook, "no `getStats`" and "no media"
   would be the same value.

### Not accurate, and it changes the design — 3. the instrument is already decided

The issue offers `framesDecoded`. The repository chose a different instrument
**seventeen days ago, citing this issue by number**:

> **Decided: the first decoded frame, read as `getVideoPlaybackQuality()
> .totalVideoFrames` crossing zero.** … `connectionState === 'connected'` is a
> **transport** fact and would contradict FR-013. #2111 is open precisely
> because a tile reports Live off the transport state alone while showing black.
> — `specs/077-click-to-first-frame-measured-not-proxied/spec.md:63-72`

It is in use in two places already —
`e2e/click-to-first-frame.spec.ts:286` and
`e2e/kiosk-shows-a-label-over-video.spec.ts:93` — both stating the same reason:
it counts frames the decoder actually produced, unlike `currentTime`, which
advances over a stalled track.

**This spec uses that instrument.** Introducing `framesDecoded` alongside it
would put a second, weaker answer next to a decided one.

### Not accurate, and it is the trap — 4. the necessity argument does not transfer

The issue argues a bare gate cannot produce a false negative, because
`srcObject` is only ever written by `ontrack`, so **attachment** is strictly
necessary for a picture. That is true of attachment.

It is **not** true of the instrument the issue then recommends. `framesDecoded`
— and `getVideoPlaybackQuality` equally — is an API a browser may not offer.
`getVideoPlaybackQuality` is absent from jsdom (verified directly: `typeof
video.getVideoPlaybackQuality === 'undefined'` on jsdom 30.0.1) and from
Firefox. A gate on an **absent instrument** says "not live" over perfectly good
video, forever, which is the exact false negative the issue argues is
impossible.

So the design needs a rule the issue does not have: **absence of a measurement
is not evidence of absence of media.** FR-005 below.

### Also found, at phase 6 — the counter is per element, not per session

**`totalVideoFrames` is not reset between sessions on the same tile, so a bare
`> 0` test closes only the case of a tile that has never shown a picture.**
Three links, each checkable:

1. W3C: the counter *"is reset to 0 when the media element load algorithm is
   invoked"* — that is, on a write to `srcObject`.
2. `srcObject` is written **only by `ontrack`** (`WhepClient.ts:70`, `:82`), as
   §"the necessity argument does not transfer" above already asserts. A session
   that delivers no video track never fires `ontrack`, by definition — so no
   write, so no reset.
3. `WhepClient.teardownLocally()` (`:233-238`) stops the receiver tracks and
   closes the peer connection. It **never clears `videoEl.srcObject`**.

So a tile that has *ever* shown a picture carries its frame count into every
later session on the same element:

> Session one goes Live at 5000 frames → the transport reports `failed` → the
> ladder opens session two → session two negotiates cleanly with **no track** →
> a `> 0` poll reads 5000 → **Live over a black tile, watchdog disarmed,
> `attemptRef` reset.**

That is #2111 exactly, in the case a fab wall meets **more often than a cold
boot**: a camera stops publishing video while its MediaMTX path stays alive. (A
cold path answers the WHEP POST with 404 and takes the `connect().catch` route,
which was already handled.) It is not a regression — `develop` says Live here
too — but the fix must not be described as closing the class while this stands.

**FR-002 therefore baselines the counter when the watch is armed, and requires
strictly greater.** Monotonicity is not assumed in either direction: a
*decrease* is the load algorithm having run, and re-baselines rather than
counting as a frame, because nothing orders `ontrack` against `connected` and a
baseline left above a post-reset count would hold a tile that is showing video
on `Reconnecting…` for thousands of frames.

**One guard changed with it, deliberately: G3.** It pinned
`producedFrames = 1` statically before render, so no frame was ever *added* — it
was modelling a decoder producing a frame before its own session began, which
real hardware does not do. Its stub now advances the counter during the session.
That is behaviour genuinely moving, recorded here; it is not an assertion
adjusted to accommodate a fix, which stays forbidden.

### Also found — the existing suite currently asserts the defect

`ontrack` appears exactly **once** in
`apps/shared/src/ui/composites/CameraViewer.test.tsx` — as a field declaration
on the fake at `:18`. It is **never fired**. So every one of that file's ten
tests that reaches `connected` (six through `goLive()`, the rest inline) does so
with no track ever delivered, and asserts `Live`. They are not wrong
— they were written to test the transport ladder — but it means "the tests are
green" has never been evidence about this behaviour, and it is why FR-005 is
load-bearing for phase 4a rather than merely defensive.

### Adjacent, and out of scope

`apps/shared/src/streaming/WhepClient.test.ts:429` records this follow-up as
*"reported against #2109"*. It was filed as **#2111**; #2109 is a seven-item
list that explicitly carves #2108 out and contains no such item. A stale
cross-reference. Not touched by this work — `WhepClient.ts` and its tests are
untouched entirely (see §Boundary).

---

## What this is

A tile on a fab wall labels itself **Live** when its ICE/DTLS transport comes
up. That is a statement about a socket. An answer whose video `m=` line is
rejected or marked `a=inactive`, alongside an accepted audio one, negotiates
cleanly, reaches `connected`, fires `ontrack` for audio only, and produces
**Live over a black rectangle** — with no error, no log and no retry, for the
life of the page. #2108 closed the case where a track arrives unattachable; this
is the case where the video track never arrives at all, so #2108's fallback
never gets a chance. Reachable through a MediaMTX SDP drift (#2103).

The tile is to say **Live when a picture has arrived**, and when no picture
arrives it is to say so and retry, rather than lie or hang.

---

## Locked technical choices

| Concern | Choice | Where it was decided |
|---|---|---|
| What counts as a picture | `getVideoPlaybackQuality().totalVideoFrames > 0` on the tile's own `<video>` | spec 077 §3; used at `click-to-first-frame.spec.ts:286`, `kiosk-shows-a-label-over-video.spec.ts:93` |
| Where the gate lives | `useWhepSession` only — the hook already holds `videoRef` and passes that element to `connect()` | ADR-0074 (`apps/shared` is shared by both apps) |
| Watchdog window `N` | **3000 ms** — spec 002 FR-013's own budget | §"The two numbers" |
| Media poll interval | **250 ms** | §"The two numbers" |
| Failure route | the **existing** retry ladder — `scheduleRetry` → `reconnecting` → jittered backoff → `retryNonce` | `useWhepSession.ts:125-131`, unchanged |
| Missing instrument | falls back to today's behaviour exactly | FR-005 |
| Test framework | vitest + jsdom + Testing Library, the `FakePeerConnection` from #2108 | ADR-0052, ADR-0103 |

**No new dependency, no new status value, no new contract, no change to
`WhepClient`, no change to any `Shared.Contracts` type, no backend change.**

---

## The two numbers

The issue asks for *"N seconds"* and leaves both numbers open. Neither is
chosen for roundness.

### `N = 3000 ms`

Spec 002 **FR-013** (`specs/002-watch-camera-live/spec.md:283`) budgets *click in
UI → first decoded frame in `<video>` ≤ 3 seconds p95*.

`connected → first frame` is a **proper sub-interval** of that interval: the
click, the `/streams/{id}` lookup, the WHEP POST and ICE/DTLS all happen before
`connected` fires. So a session that has been `connected` for 3000 ms **without
a frame has already breached FR-013 on its own**, whatever the earlier terms
cost. `N = 3000 ms` is therefore the smallest value that cannot fire on a
session still inside its own published SLO, and no wider margin needs
justifying.

**Spec 002 also budgets this sub-interval directly, and it is far smaller.** Its
decomposition of FR-013 reads *"≤ 200 ms `/streams/{id}` lookup + ≤ 500 ms WHEP
POST + ≤ 1 s ICE/DTLS handshake + **≤ 1 s decoder warmup** + 300 ms headroom"*
(`specs/002-watch-camera-live/plan.md:703`). Everything up to and including the
handshake precedes `connected`; **the decoder warmup is exactly the interval
this watchdog bounds, and it is budgeted at 1 s**. `N = 3000 ms` is 3× the
budgeted term and 2.3× that term plus the whole headroom — deliberately loose,
because the cost of firing early is a needless renegotiation on a healthy fab
camera and the cost of firing late is three seconds of honest `Connecting…`.

Cross-checked against measurement rather than assumed. Spec 077 measured the
whole click-to-first-frame interval on a live stack (2026-09-06, two 20-open
runs): **p95 1158 / 1420 ms, max 1459 ms, min 282 ms**, with the product's own
setup cost **S ≈ 290 ms** and the rest the fixture's 1.000 s keyframe interval.
`N` sits at roughly **2× the observed maximum of a strictly larger interval**.

The keyframe term is the one that could plausibly stretch on real hardware — a
receiver cannot decode before an IDR, and a camera with a 2 s or 4 s IDR
interval makes the wait longer. `N = 3000 ms` clears a 2 s IDR outright. A 4 s
IDR would trip the watchdog once and recover on the retry, at the cost of one
extra WHEP negotiation per open. Recorded as **Assumption 1**, with the
counter-evidence to look for.

### Poll interval = 250 ms

1. It is **1/12 of `N`**, so watchdog quantisation is ≤ 8 % of the window.
2. It bounds the extra dark scrim over an already-good picture at 250 ms — under
   9 % of FR-013's 3 s, comfortably inside the 2.1–2.6× headroom spec 077
   measured, and it **cannot move that figure at all**, because
   `click-to-first-frame.spec.ts` reads `totalVideoFrames` off the element, not
   the `Live` label.
3. It is the magnitude already chosen for the neighbouring
   `ICE_GATHERING_CAP_MS = 250` in the same subsystem (`WhepClient.ts:22`).
4. It is a **synchronous property read**, not a `getStats()` promise, so it
   costs nothing at this cadence.
5. It survives background-tab timer clamping (≥ 1 s) with room to spare. A
   `requestAnimationFrame` loop — the shape spec 077 uses for its in-page
   stopwatch — **stops entirely** in a background tab, which would let the
   watchdog demote a healthy backgrounded tile. Deliberately not that.

6. It is a **self-rescheduling `setTimeout`, not `setInterval`** — because a
   self-rescheduling timeout cannot overlap or pile up under a slow tick, and
   for no other reason. `setInterval`/`clearInterval` are **available**:
   `CameraViewer.tsx` already calls `window.setInterval` twice (`:118`, `:164`),
   `useWallAlignment.ts:137` once more, and `window` is an allowed global, so
   `no-undef` never fires and no lint config would have needed changing. An
   earlier draft of this record justified the choice by a constraint that does
   not exist; the choice stands, the reason is this one.

The interval runs only between `connected` and the first frame, and never again
for that session.

**Rejected:** `requestVideoFrameCallback`. Exact and event-driven, but a third
instrument next to a decided one, absent from jsdom, absent from Firefox, and it
saves perhaps four synchronous property reads per session.

---

## Latency budget impact — constitution §IV

**Leg affected: none of the six.**

§IV budgets *event arrival → overlay rendered ≤ 800 ms*, decomposed into
Camera → SFU, SFU → kiosk decode, presentation buffer, **Event → overlay state
(RabbitMQ + projection)**, composite + render, headroom. This change alters
**when a session's status becomes `live`**. That is session establishment, which
sits before the §IV clock starts — and the repository says so in its own words,
in the requirement that governs the interval:

> **FR-013:** The viewer-path latency budget (click in UI → first decoded frame
> in `<video>`) MUST be ≤ **3 seconds p95** for the v1 happy path. **This is NOT
> the constitution's 800 ms event-to-overlay budget — that begins ticking after
> the first frame is rendered.**
> — `specs/002-watch-camera-live/spec.md:283-286`

Restated at `e2e/click-to-first-frame.spec.ts:19-25`. Two independent statements
of the same boundary, one of them in the spec that owns the budget.

No cell of §IV's two tables moves, and §VII's dashboard obligation (ADR-0117)
does not attach to anything here.

**The indirect effect, named rather than waved away.** Three effects in
`CameraViewer` are gated on `status === 'live'`: the decode sampler
(`receive_to_decoded`, `:112`), the lag sampler (`presentation_buffer` +
`wall_skew`, `:158`) and the playout actuator (`:213`). All three now start at
the **first frame** instead of at `connected`. No leg's **value** changes:
`decodeElapsedBetween` and `lagBetween` both already return null when no frames
arrived between samples, so what is removed is null samples taken before there
was anything to sample. The alignment controller converges from the first frame
rather than from a socket. Strictly fewer meaningless reads; no budget moves.

**The quantity that could move, and its baseline.** The governed figure here is
spec 002 **FR-013**, and — unlike #1714's leg — **a baseline exists**: spec 077,
2026-09-06, p95 1158 / 1420 ms, minimum 282 ms, S ≈ 290 ms. It cannot move,
because the instrument reads the element rather than the label. Phase 5 may
re-run `e2e/click-to-first-frame.spec.ts` as confirmation; that needs a live
Aspire stack, so **ask before booting**. Spec 077 records that a regression
should be read off the **minimum** sample, not off p95.

**#1714 is not this spec's blocker.** It is #2157's, for a leg this change does
not sit on.

---

## Is any of this an ADR? — No

Recorded explicitly, because ADR-0144 makes writing one a blocked outcome and
the issue is close enough to the line to deserve an answer rather than a
silence.

**Not an ADR**, on four grounds:

1. **Precedent.** Six timing constants already live in this subsystem with no
   ADR behind any of them, each argued at its declaration:
   `RETRY_BASE_MS`, `RETRY_CAP_MS`, `DISCONNECT_GRACE_MS`
   (`useWhepSession.ts:51-53`), `ICE_GATHERING_CAP_MS` (`WhepClient.ts:22`),
   `DECODE_SAMPLE_INTERVAL_MS`, `LAG_SAMPLE_INTERVAL_MS`
   (`CameraViewer.tsx:19,39`). A seventh with a stated argument is the
   established shape.
2. **No architecture moves.** No new transport, dependency, contract, status
   value, or cross-context anything. `reconnecting` already exists and the retry
   ladder is entered unchanged.
3. **No §IV leg is touched and none breaches**, so §IV's *"any leg breaching its
   budget triggers an ADR-class review"* does not fire.
4. **No locked decision is amended.** This *implements* a decision spec 077
   already recorded — that `connectionState` is a transport fact and contradicts
   FR-013 — rather than making a new one.

**Where the line actually is**, for the next reader: removing FR-005's fallback
so the gate binds on every engine would change what *Live* means on browsers
that cannot be asked, and adding a **frozen-tile** watchdog *after* `live` would
put a new liveness claim on a running session. Either would want an ADR. Neither
is in this spec.

---

## User stories

### US-1 (P1) — A tile that shows no picture does not claim to be showing one

**As** an operator watching a fab wall,
**I want** a tile to say `Live` only when a frame has been decoded into it,
**so that** a black rectangle labelled Live cannot be mistaken for a quiet
production cell.

This is the whole slice. It is independently shippable, independently
observable, and it is one file of production code.

#### AS-1.1 — Happy path: a picture arrives, the tile goes Live

```gherkin
Given a camera whose stream health is Healthy
  And the browser reports getVideoPlaybackQuality on the tile's video element
When the peer connection reaches "connected"
  And the element has produced no frames yet
Then the tile shows "Connecting…" and does not show "Live"
When the element's totalVideoFrames rises above the count it held when the watch was armed
Then the tile shows "Live" within 250 ms
  And a "connecting→live" resilience transition is logged
```

#### AS-1.2 — The residual case #2108 does not close: no picture, ever

```gherkin
Given a camera whose stream health is Healthy
  And an answer that negotiates cleanly but delivers no video track
When the peer connection reaches "connected"
  And totalVideoFrames stays at zero
Then the tile still shows "Connecting…" 2999 ms after "connected"
  And it has never shown "Live"
```

#### AS-1.3 — Conflict: the watchdog re-enters the existing ladder

```gherkin
Given the session of AS-1.2
When 3000 ms have passed since "connected" with no frame produced
Then the tile shows "Reconnecting…"
  And after the jittered backoff a second WHEP session is negotiated
  And the tile is never left on "Connecting…" with no retry scheduled
```

#### AS-1.4 — Bad-request equivalent: the backoff grows

```gherkin
Given a source that reaches "connected" and produces no frame, every time
When three watchdog cycles have elapsed
Then the retries are spaced by the existing ladder — 1 s, 2 s, 4 s, …, capped at 15 s
  And they are not all spaced by the base delay
```

*Because `attemptRef` is reset by "connected" today, a naive watchdog would
retry every `N + 1 s` forever with no backoff — one WHEP POST per 4 s per tile,
250 tiles, indefinitely. FR-004 moves the reset.*

#### AS-1.5 — A blip after a picture does not re-arm anything

```gherkin
Given a tile that has been Live for an hour
When the peer connection reports "disconnected" and then "connected" within the grace window
Then the tile stays Live
  And no media watch is started
  And no watchdog can demote it
```

#### AS-1.8 — A frame from an earlier session is not this session's picture

```gherkin
Given a tile that has been Live and has produced 5000 frames
When the transport fails and the ladder opens a second session on the same element
  And that session negotiates cleanly but delivers no video track
Then the tile does not show "Live", although totalVideoFrames still reads 5000
  And it shows "Reconnecting…" 3000 ms after that session reached "connected"
```

*The counter is reset only by the media element load algorithm, and the only
write to `srcObject` is `ontrack`, which this session never fires. See
§"the counter is per element, not per session".*

#### AS-1.6 — Auth: an unauthorized session is unchanged

```gherkin
Given a WHEP POST that returns 401
When the session fails to negotiate
Then the tile shows "Reconnecting…" with the WhepError's message
  And no media watch was ever started, because "connected" never fired
```

*Auth handling is untouched. Listed because the phase-1 gate asks for it, and
because "untouched" is the claim being made.*

#### AS-1.7 — A browser that cannot be asked keeps today's behaviour

```gherkin
Given a browser whose video element has no getVideoPlaybackQuality
When the peer connection reaches "connected"
Then the tile shows "Live" immediately, exactly as it does today
  And no media watch and no watchdog are started
```

---

## Functional requirements

- **FR-001** — `useWhepSession` MUST NOT transition to `live` on
  `connectionState === 'connected'` alone when the tile's `<video>` element
  offers `getVideoPlaybackQuality`.
- **FR-002** — It MUST transition to `live` once that element's
  `totalVideoFrames` is **strictly greater than the count read when this
  session armed its watch**, observed by polling every **250 ms** while
  `connected` and unconfirmed. Not greater than zero: the counter belongs to the
  element and survives teardown, so a previous session's frames would otherwise
  be read as this one's (AS-1.8). A reading **below** the baseline is the media
  element load algorithm having run, and MUST re-baseline rather than confirm.
- **FR-003** — If `totalVideoFrames` is still zero **3000 ms** after
  `connected`, it MUST call the existing `scheduleRetry`, entering `reconnecting`
  and the existing jittered ladder. **A tile MUST NOT be left on `Connecting…`
  with no retry scheduled.**
- **FR-004** — `attemptRef.current = 0` MUST move from the `connected` branch to
  the media-confirmed path, so that a repeatedly mediumless source backs off
  rather than retrying at a fixed cadence forever. The grace-timer clear stays
  on `connected`, where a transport blip is what it is about.
- **FR-005** — When `getVideoPlaybackQuality` is **absent** from the element,
  behaviour MUST be **byte-for-byte today's**: promote on `connected`, reset
  `attemptRef` there, start no interval and no watchdog. Absence of a
  measurement is not evidence of absence of media.
- **FR-006** — Media confirmation MUST be **per session and sticky**: once
  confirmed, a later `disconnected`/`connected` blip MUST NOT re-arm the watch,
  and MUST NOT be able to demote a tile that is showing video.
- **FR-007** — Both new timers MUST be cleared in the effect's existing cleanup,
  alongside `retryTimer` and `graceTimer`, so unmount and renegotiation leak
  nothing.
- **FR-008** — The change MUST add **no new `eslint-disable`**. The existing
  `react-hooks/set-state-in-effect` suppression at `:115` MUST remain, untouched
  and unweakened.
- **FR-009** — Reading the instrument MUST NOT throw out of a timer callback.
  Safari has thrown `InvalidStateError` from `getVideoPlaybackQuality()` on an
  element carrying no video; an unreadable instrument MUST be treated as "no
  frames", which is what the watchdog path already handles.
- **FR-010** — Nothing may promote to `live` once a retry is scheduled.
  `scheduleRetry` MUST clear the media timers, so a frame ticked by a receiver
  between `failed` and teardown cannot confirm media for a session that is
  already being replaced — which would show `Reconnecting… → Live → Connecting…`
  and give the ladder back a rung it had climbed.

### Non-functional

- **NFR-001** — No §IV leg changes. No new latency measurement is reported.
- **NFR-002** — `pnpm lint`, `pnpm typecheck` (now covering `e2e/`) and
  `pnpm test` clean.
- **NFR-003** — The ten existing `CameraViewer.test.tsx` tests and the
  twenty-one `WhepClient.test.ts` tests pass **unmodified**. If one has to
  be edited, behaviour moved somewhere it was not meant to: stop, do not adjust.

---

## Out of scope, and why each is a separate thing

- **A media window that grows with the ladder** — the fix for the
  deterministically-slow source of Assumption 1, e.g.
  `min(N · 2^attempt, cap)` in place of the fixed `N`. Not taken here, and the
  reason is specific rather than a preference: **it cannot be made without
  editing R3**, the only test pinning FR-004. R3's second cycle advances exactly
  `N` and then the 2 s ladder delay; under a growing window that session's
  watchdog is at `2N`, so the cycle-two retry never fires. Measured, not
  reasoned: with `Math.min(MEDIA_WATCHDOG_MS * 2 ** attemptRef.current, 24_000)`
  the suite fails R3 at *"expected … to have a length of 3 but got 2"* and AS-1.8
  at *"Unable to find an element with the text: Reconnecting…"*. Editing a
  red-first test to accommodate a later fix is the thing ADR-0144 forbids, so
  this belongs to a separate issue with its own phase 4a, not to a widening
  applied here.
- **A frozen-tile watchdog** — a session that goes `live` and then stops
  producing frames while the transport stays `connected`. A different defect,
  needing a frame-rate delta rather than a threshold crossing, and able to
  demote a healthy low-frame-rate camera. The `streamState === 'Degraded'` path
  (`:186`) already covers source loss from the server side.
- **#2157's restructuring** of the dual-owner state machine. See §Boundary.
- **#2109 item 7** — a missing `whepUrl` parks a tile on `Idle` with no
  transition, no retry and no log (`:107`). Same file, adjacent shape,
  separately filed, not fixed here.
- **Any change to `WhepClient`.** The instrument reads the video element the
  hook already owns, so the client needs no attachment callback. The issue's
  framing implies one; it is not needed.
- **Firefox.** `getVideoPlaybackQuality` is absent there, so FR-005 applies and
  the defect persists on Firefox. Named as a limitation, not silently accepted:
  the kiosk and the e2e suite are Chromium (ADR-0108).

---

## Boundary — does this collide with #2157?

**Same file, different concern, and it does not do #2157's work.**

#2157 is about **who owns `status`**: the offline branch derives it inside the
connection effect while `transitionTo` maintains a `statusRef` mirror for dedupe
and logging, and the `react-hooks/set-state-in-effect` suppression at `:115`
stands in for the redesign. This spec adds **one new condition** on one existing
transition, plus two timers, inside machinery that already exists.

- It does not touch the offline branch, the suppression, or `statusRef`.
- It derives nothing during render — the trap `694ad2a8` documented and #2157
  repeats. Every new `transitionTo` fires from a timer callback inside the
  effect, the same shape as the existing `scheduleRetry` and
  `onConnectionStateChange` calls the lint rule cannot see. **No new suppression
  should be needed, and FR-008 makes that a gate**: if the engineer finds one is
  required, that is a signal to stop, not to suppress.
- It leaves #2157 one more thing to carry — per-session media confirmation.
  Marginal, and additive.

**Recommendation: proceed, do not wait, do not subsume.** #2157 is blocked
behind #1714, a measurement of a leg this change does not sit on; blocking a
user-visible correctness defect behind it would be blocking a bug fix on
unrelated work. Phase 7 should comment on #2157 noting the new state its
restructuring must preserve.

**One thing for a human, not for the lane** (T009): #2157's stated reason for
being blocked does not hold as written. It says restructuring *"changes when a
tile learns its session went offline, which is exactly what that budget
measures"* — but the §IV leg it names, *Event → overlay state (RabbitMQ +
projection)*, measures the overlay projection pipeline, not the stream session's
status, and `e2e/click-to-first-frame.spec.ts:19-25` records that the §IV clock
begins after the first frame. A "before" measurement may still be prudent for a
restructure; the *reason on the issue* is not the one that makes it so.
**Reported, not acted on** — re-labelling a blocked issue is not this spec's
call.

---

## Independent end-to-end test procedure

Reproduces the defect and its fix without CI, and **without booting Aspire**
for the first three steps.

1. **Unit, no stack.** `cd apps/shared && npx vitest run src/ui/composites/` —
   the new `CameraViewerMedia.test.tsx` covers AS-1.1 through AS-1.7 against the
   `FakePeerConnection` and a stubbed `getVideoPlaybackQuality`.
2. **The old suite, unmodified.** The same command must leave the ten
   `CameraViewer.test.tsx` tests green with no edits (NFR-003).
3. **Counterfactual, on the branch before the fix.** Stub
   `getVideoPlaybackQuality` to return `{ totalVideoFrames: 0 }` and confirm the
   tile reports `Live` — proving the new tests could fail.
4. **Against a live stack** *(needs `aspire run`; ask first)*. Open
   `management-web` on a fixture camera; the tile reaches Live as before, and
   `e2e/click-to-first-frame.spec.ts` still reports a p95 under 3000 ms with its
   **minimum** sample near 282 ms.
5. **The real fault, provoked** *(needs the stack)*. Per the recorded technique,
   patch the MediaMTX path so the answer carries no decodable video while the
   session still negotiates. Today: `Live` over black, forever. After: the tile
   holds `Connecting…` for 3 s, then `Reconnecting…`, then retries on a growing
   backoff, with `[resilience] connecting→reconnecting` in the console.

---

## Assumptions, marked

1. **Real fab cameras have an IDR interval below 2 s.** The measured fixture is
   1.000 s (`sim-loop.mp4`, spec 077). `N = 3000 ms` clears a 2 s interval
   outright; a 4 s interval would trip the watchdog once per open and recover on
   the retry. *If a fab camera is found with a longer IDR interval, the symptom
   is one extra WHEP negotiation per open, visible as a
   `connecting→reconnecting→connecting→live` sequence in the resilience log.
   That is a finding to report against `N`, not a threshold to raise silently.*

   **"Recovers on the retry" is probabilistic and phase-dependent, not
   guaranteed** (recorded at phase 6). The argument holds for a *fixed IDR
   cadence*, where each attempt lands at a different phase against the keyframe
   clock and one of them lands early. It does **not** hold for a source whose
   first frame is **deterministically** later than `N` — an SFU-side pull
   starting on first subscriber (`runOnDemand` is in use: `camera-sim.yml`,
   `CameraSimProvisioner.cs:118-120`), a transcode fallback spinning up (ADR-0012),
   sixteen tiles negotiating at once. There every attempt is killed at exactly
   `N`, each retry restarts the wait, and the tile shows `Reconnecting…`
   forever — where `develop` shows Live and the picture eventually appears.
   **This is the one case where this change is strictly worse than what it
   replaces**, and it is accepted in writing rather than fixed here; see
   §Out of scope.
2. **`getVideoPlaybackQuality` is present on every browser the kiosk runs on.**
   Chromium and Safari 15.4+ have it; jsdom and Firefox do not (verified for
   jsdom directly). FR-005 makes absence safe rather than fatal.
3. **`autoPlay playsInline muted` keeps the element decoding**, so
   `totalVideoFrames` advances without a user gesture. Not assumed on
   principle — spec 077 read this exact property through this exact element for
   40 successful samples.

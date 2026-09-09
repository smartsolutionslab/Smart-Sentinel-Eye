# Spec 108 — A span finer than its budget

**Issue:** #1714 · **Branch:** `perf/1714-the-path-measured-end-to-end`
**Phase:** 1 (Specify) · **Date:** 2026-09-09
**ADRs:** ADR-0037 (phased workflow), ADR-015 + constitution §IV (the budget and its
four-state leg table), ADR-0117 (§VII binds *implemented* legs; a discharge must be
earned), ADR-0118 (one sink per environment), ADR-0122 (a browser measurement reaches
the sink through a service, and says whether it is a whole leg), ADR-0128 (playout
alignment against the SFU's RTCP clock, not PTP), ADR-0129 (a label is *aged*, never
frame-matched), ADR-0135 (medians do not add), ADR-0108 (e2e against a live `aspire run`
stack), ADR-0144 (the lane may not write an ADR, amend the constitution, or weaken a
gate).
**Constitution:** §IV, §VII.

**Scope:** test-only. No file under `src/` or `apps/` is modified. No edit to
constitution §IV's table.

---

## 1. The premise, re-established before anything is planned

#1714's **body** says three of six legs are unbuilt, on the strength of a search scoped
to `apps/kiosk-web`. **The body is stale. Every leg is built.** Three of its own comments
already say so; this section is the independent check the brief required, made against
today's tree rather than against those comments.

| Leg (§IV) | Built? | Evidence — read this session |
|---|---|---|
| Camera → SFU ≤ 80 ms | **yes** (third-party) | `src/AppHost/Resources/mediamtx.yml:24-25` `metrics: yes`, `metricsAddress: :9998`; `src/AppHost/AppHost.cs:139` publishes the endpoint |
| SFU → kiosk decode ≤ 120 ms | **yes** | `apps/shared/src/observability/kioskLatency.ts:191-209` `decodeSampleFrom`, `:249-260` `decodeElapsedBetween`; driven from `apps/shared/src/ui/composites/CameraViewer.tsx:158-189`, emitted `:181` |
| Presentation buffer ≤ 200 ms | **yes** | pure engine `apps/shared/src/observability/wallAlignment.ts` (`lagBetween:169-179`, `bufferDelayBetween:201-209`, `classifyWall:298-328`, `settleAlignment:373-453`); control loop `apps/kiosk-web/src/features/cell/useWallAlignment.ts:137-209`; actuator `apps/shared/src/streaming/WhepClient.ts:188-212` `setPlayoutTarget` |
| Event → overlay state ≤ 200 ms | **yes** | `src/ServiceDefaults/EventToOverlayLatency.cs:29`; call sites `src/SystemVariables/Application/EventHandlers/SystemVariableValueRequestedV1Handler.cs:100`, `src/LayoutComposition/Application/EventHandlers/OverlayHighlightRequestedV1Handler.cs:49` |
| Overlay composite + render ≤ 50 ms | **yes** | `apps/shared/src/observability/kioskLatency.ts:135-142` `measureOverlayDraw` (two chained rAF); driven from `apps/kiosk-web/src/features/cell/CellPage.tsx:415-427` |
| Headroom ≤ 150 ms | n/a | arithmetic remainder |

The ADR-0129 label hold is built too and is not one of the six rows:
`apps/shared/src/observability/labelDelay.ts:58-65` `labelDelayFor` (cap 200 ms at `:31`),
held at `apps/kiosk-web/src/features/cell/useLabelDelay.ts:29-132`, wired at
`CellPage.tsx:399`, achieved hold emitted at `useLabelDelay.ts:97` and `:128` →
`CellPage.tsx:393-397`.

**Verdict: the issue's title is the accurate part; its body's table is not.** Nothing here
needs building. What is missing is a figure and a watcher.

### 1a. §IV's table, checked against the above

§IV (`.specify/memory/constitution.md:147-154`) records four states. Three cells match what
is in the tree. **One does not, and it is an over-claim rather than an under-claim.**

| Leg | §IV *Measured* says | This session found |
|---|---|---|
| Camera → SFU | **yes (SFU metrics)** | **No duration metric exists.** See §1b. |
| SFU → kiosk decode | in part — receive-to-decoded only | matches; ADR-0122's naming refusal still holds |
| Presentation buffer | recorded, not yet observed | matches |
| Event → overlay state | recorded, not yet readable | matches — and #2072 adds that the histogram measures the *wrong span* |
| Composite + render | yes | matches |

### 1b. Camera → SFU has no duration instrument — observed, not inferred

Read off the **running stack** this session (`GET http://127.0.0.1:10759/metrics`,
MediaMTX's published metrics endpoint, 159 lines):

```
rtsp_sessions_inbound_rtp_packets_jitter 0
rtsp_sessions_rtp_packets_jitter 0
srt_conns_us_snd_duration 0
webrtc_sessions_inbound_rtp_packets_jitter 0
webrtc_sessions_rtp_packets_jitter 0
```

Those five lines are the **entire** time-like content of the exposition. The rest are
counters (bytes, packets, sessions, muxers). RTP **jitter** is interarrival variance, not a
one-way delay; `srt_conns_us_snd_duration` belongs to SRT, which this stack does not use
(RTSP in, WebRTC out).

`tests/Integration.Tests/StreamDistribution/SfuLatencyIsReadableTests.cs:35-39` asserts the
body *contains the string `paths`* — that the endpoint answers with something. It reads no
duration, and its own summary is honest about being configuration rather than measurement.

**So §IV's `yes (SFU metrics)` for this leg rests on an endpoint being reachable, not on a
figure anyone has read.** And a genuine one-way camera → SFU delay is unobtainable for the
same reason ADR-0128 and ADR-0129 give twice over: it needs a clock shared with the camera,
which is PTP hardware this system does not have. **This is a finding for a human. This
feature does not correct §IV** (ADR-0144), and it does not attempt the leg.

---

## 2. What is actually wrong, and it is not that nothing was measured

Two harnesses already measure parts of this path. Neither is the thing #1714 asks for, and
the reasons are different.

**Server-side — `tests/Integration.Tests/SystemVariables/ResolvedTextReachesItsFabTests.cs`.**
`A_value_change_reaches_a_hub_client_in_the_fab_it_changed_in` (`:171-216`) times
`PUT /system-variables/{name}/value` returning → the `ResolvedOverlayTextChanged` SignalR
frame landing on a subscribed hub client (`Stopwatch` `:190`, stopped `:192`). Its doc
comment records the figures #2072 carries: **555 ms and 758 ms against a 200 ms budget**
(`:121-133`), asserted against a `PushCeilingMs = 5_000` (`:133`). It **excludes the
browser, the React re-render and the ADR-0129 label hold** (`:164-168`).

**Browser-side — `e2e/kiosk-shows-a-label-over-video.spec.ts:326-424`**, "the span from a
value being submitted to it being visible". Five iterations, `Date.now()` at both ends on
one machine, `LEGS_COVERED`/`LEGS_NOT_COVERED` printed rather than implied (`:238-239`).

**This second one is the closest existing thing to an end-to-end figure, and it disqualifies
itself in its own output:**

> `console.info('[span] instrument error: ~±1000 ms (polled observation + automation round trips)')`
> — `e2e/kiosk-shows-a-label-over-video.spec.ts:302-308`

**An instrument whose error bar is larger than the SLO cannot say whether the SLO holds.**
±1000 ms against an 800 ms budget and a 200 ms leg. That is the defect this feature fixes,
and it is the whole of P1.

Where the error comes from, precisely:

- **The tail.** The end is observed by `await expect(label).toContainText(value)` (`:373`),
  a polling assertion whose interval backs off 100 / 250 / 500 / 1000 ms. Always-positive
  quantisation. `e2e/click-to-first-frame.spec.ts:40-46` names this exact trap and rejects
  the same mechanism for the same reason.
- **The head.** `submittedAt = Date.now()` (`:369`) is stamped in Node *before*
  `setValue()`, which is a `fill` and a `click` — two Playwright round trips including
  actionability checks — all folded into every sample.

Neither is a product cost. Both are the harness measuring itself.

---

## 3. The measurement, stated honestly before anything else

### 3a. The span this feature times

**From the operator's click on "Set value" landing, to the tile having painted the new
value.**

| | Stamp | Taken by | Clock |
|---|---|---|---|
| **t0** | a capture-phase, one-shot `click` listener on the operator page | the browser, at the moment the gesture lands | `Date.now()` |
| **t1** | a `MutationObserver` on `[data-testid="camera-viewer-overlay-label"]`, resolved after **two chained `requestAnimationFrame`s** | the kiosk page, after paint | `Date.now()` |

`elapsed = t1 − t0`.

The double-rAF is not invented here. It is the idiom the product already uses to call an
overlay *painted* rather than *changed* — `apps/shared/src/observability/kioskLatency.ts:135-142`
`measureOverlayDraw`. Mirroring it means t1 means the same thing the product means by it.

### 3b. One clock, and why that is defensible here

Both stamps are `Date.now()`, read in two Chromium contexts of **one browser on one
machine**, driven by one Playwright process. `Date.now()` reads the OS wall clock, so this
is *two readers of one clock* — the shape spec 053 examined and found safe, and the shape
`e2e/kiosk-shows-a-label-over-video.spec.ts:220-226` already relies on.

It is **not** the shape spec 053 left open (a host stamp minus a container stamp), and it
is **not** a browser clock subtracted from a server clock. No server stamp enters the
figure at all.

The existing `sharesOneClock()` refusal (`:255-264`) is **kept and extended**: it already
refuses when `PW_TEST_CONNECT_WS_ENDPOINT` names a remote browser. Both pages come from the
same browser instance, so the one guard covers both ends.

> **Corrected at phase 6 (2026-09-09). One browser is one *machine*, not one clock, and
> §3b was written as though it were.**
>
> The two stamps are taken in **two Chromium renderer processes**. Each extrapolates
> `base::Time::Now()` from its own latched tick/wall pair, so `Date.now()` in one can sit a
> constant offset away from `Date.now()` in the other even though both descend from one OS
> clock. That offset lands **whole** in every sample, in whichever direction it happens to
> point.
>
> **`sharesOneClock()` does not test this and cannot.** It reads an environment variable. It
> rules out the remote-browser case and says nothing about the two local contexts.
>
> **Neither does SC-002's calibration** — see the correction under SC-002 below. So the
> harness now **bounds the offset directly**, by bracketing: read the kiosk clock, then the
> operator clock, then the kiosk clock again. The middle read happened between the outer
> two, so with K the kiosk clock and O = K + δ the operator's,
> `δ ∈ [middle − after, middle − before]` — an interval whose width is the round trip, and
> whose derivation does not depend on which page is read first. Seven brackets are
> intersected, and the bound is taken **before and after** the loop so drift across the run
> is visible rather than assumed. It is printed as a **named term** in the instrument-error
> line.
>
> This also settles an old reading. A one-way probe once reported `min −88 ms` and it was
> attributed to round-trip jitter — an explanation available only if the kiosk was read
> first, because under the other ordering a round trip can only push the figure positive.
> A one-way probe cannot tell real skew from its own jitter. The bracket does not have to:
> **measured at phase 6, δ ∈ [−4, +6] ms and δ ∈ [−3, +4] ms across two runs, both intervals
> containing zero.**

**On a distributed deployment this subtraction would be meaningless.** Recorded in the file,
not only here.

### 3c. What the span is not — head overshoot, and no tail undershoot

**It overshoots at the head. It is not §IV's span, and a figure from it must never be
quoted as "the 800 ms SLO holds".**

- **Head overshoot.** §IV's span begins at *event arrival*. t0 is the operator's click, so
  the figure additionally contains the browser's `fetch`, the API gateway hop, and the
  SystemVariables service accepting the write — work that happens *before* arrival.
  **Bounded, not ignored**: the harness also records the submit request's own
  round trip via `page.on('response')` and prints it beside every sample, so a reader can
  subtract it rather than guess at it.
- **No tail undershoot.** t1 is after paint. That is exactly what §IV means by *overlay
  rendered*, and it is the end #2072's server-side figure explicitly does not reach.

The honest statement of what a run licenses:

> Over N iterations on this machine, the interval from an operator's click landing to the
> tile having painted the value had a p50 of X ms and a p95 of Y ms, of which Z ms was the
> submit round trip.

and what it does **not** license:

> The 800 ms SLO holds. / §IV's event → overlay state leg is measured. / The path is watched.

**The figure is a bracket, and quoting either end alone is an error** (re-review finding 4).
The raw p50 is a **ceiling**: it contains the browser's `fetch` and the gateway hop, which
precede §IV's span. The p50 net of each sample's own submit round trip is a **floor**: the
request's `responseEnd` includes the service's own processing of the write, which is
*genuinely inside* event → overlay state, so subtracting it removes real span along with the
overshoot. The harness prints both ends and calls the pair a bracket. **Measured on this
machine: [82, 234] ms.**

Phase 6 established that the run-to-run spread is almost entirely this one term — the submit
round trip p50 moved 152.2 → 36.0 → 79.1 ms across three runs while the net p50 held at
102 → 88.5 → 82 ms. **That is a reason to quote the bracket, not a reason to distrust the
instrument.**

**How much of the 800 ms this span can possibly account for — 250 ms, and the arithmetic is
not a subtraction.** §IV's 800 is `80 + 120 + 200 + 200 + 50 + 150`. This span covers **two**
of those rows: *event → overlay state* (200) and *overlay composite + render* (50). The other
**400 ms of budgeted legs — camera → SFU, SFU → kiosk decode, presentation buffer — are not
serial terms of this span at all** (ADR-0129, §3e): they are the picture's path, and they
enter the label's only by holding it back to the tile's frame age. The remaining 150 ms is
headroom, an arithmetic remainder rather than a term.

So a p50 read against 800 ms suggests headroom that has not been demonstrated. **Against the
250 ms this span actually covers it is a little over 3× at phase 5's warm figures and under
2× at phase 6's cold ones** — and even that omits the hold. The harness prints this
arithmetic beside every set of figures rather than leaving a reader to make the comparison
themselves.

### 3d. Consistency with #2072 — the check the brief requires

#2072 measured **555 ms and 758 ms** for `PUT` returns → SignalR frame, server-side.

This feature's span **strictly contains** that one, and adds at both ends: the submit round
trip and service acceptance before it, and the SignalR client → Redux → React re-render →
ADR-0129 label hold → paint after it. So the prediction, **written down before the run**:

> **P — this feature's p50 must land at or above #2072's 555 ms**, on a comparably warm
> stack, and plausibly several hundred milliseconds above it.

**If it lands materially below 555 ms, one of the two is wrong**, and the disagreement is
the finding — not a number to average. The two candidates to check first, in order: whether
the two runs were in the same thermal regime (#2072's figures are marked *cold stack*, and
`e2e/kiosk-shows-a-label-over-video.spec.ts`'s own cold/warm sequences differ ~3×), and
whether the new instrument's t1 is firing on a re-render that carries a stale value.

**The label hold is inside this figure and is not a defect.** ADR-0129 §4: *"a held label is
a later label, and lateness is what the budget bounds."* It is reported separately (US2) so
the two can be told apart.

### 3e. The six legs do not sum to this span, and the harness must say so

Three of §IV's legs — camera → SFU, decode, presentation buffer — are legs of the
**picture's** path, not the label's. They do not appear in the SLO span serially. Since
ADR-0129 they enter it by exactly one route: the label is **held back by its tile's own
frame age**, and frame age is buffer plus decode processing
(`apps/kiosk-web/src/features/cell/useWallAlignment.ts:243`,
`apps/shared/src/observability/labelDelay.ts:58-65`).

So the video half bounds the SLO span through the hold, capped at 200 ms
(`labelDelay.ts:31`), and not otherwise.

`e2e/kiosk-shows-a-label-over-video.spec.ts:238-239` already prints covered/not-covered
legs. That stays, and its wording is corrected to say *why* three are not covered rather
than only that they are not. **Whether §IV's six-row table should read as a serial sum is a
question for a human, and this spec does not answer it** (ADR-0144).

### 3f. Medians do not add (ADR-0135)

At no point does this feature construct a whole-path number by summing per-leg figures. The
span is measured as **one subtraction**; the per-leg figures are reported **beside** it,
never into it. The existing harness already refuses that construction in words
(`:222-228`); this keeps the refusal and gives it something to stand beside.

---

## 4. Is an end-to-end figure obtainable on this machine? Leg by leg

| Leg | Obtainable in this pass? | Mechanism / reason |
|---|---|---|
| **The SLO span** (event → overlay rendered) | **Yes**, to ~±20 ms, with the §3c head overshoot named | US1 |
| Overlay composite + render | **Yes** | `overlay_draw` already emitted; captured via `page.on('console')` (US2) |
| SFU → kiosk decode | **In part only** — `receive_to_decoded` | ADR-0122's naming refusal stands: a browser cannot see the sending end without a clock shared with the SFU. Unchanged by this feature. |
| Presentation buffer | **Yes** — the achieved buffer, per tile | `presentation_buffer` already emitted (US2) |
| Label hold (ADR-0129, not a §IV row) | **Yes** | `label_delay` already emitted (US2) |
| Wall skew / *aligned but late* | **Yes, but needs a second tile** | US3; today's live-video wall seeds **one** tile (`e2e/support/seed-live-video-wall.setup.ts:85`, `#tile-0-*` only) |
| **Camera → SFU** | **No** | §1b — no duration metric exists, and a one-way figure needs a clock shared with the camera (PTP, absent). **Named as not measured rather than left implied.** |
| Inter-display sync | **No, and out of scope** | ADR-0128 §2 — not one of the six rows |

### 4a. The instrument that already exists, and that nothing reads

Every browser-side leg is emitted through **one chokepoint**:

```
console.info('[latency]', { measurement, camera, elapsedMilliseconds });
```

`apps/shared/src/observability/kioskLatency.ts:87`. Its own comment says it is there
because *"it is how these numbers get seen during manual verification"*. Five measurement
names pass through it — `overlay_draw`, `receive_to_decoded`, `presentation_buffer`,
`wall_skew`, `label_delay` — a closed set the server enforces
(`src/StreamDistribution/Api/StreamEndpoints.cs:175-190`) and an architecture test pins
(`tests/Architecture.Tests/KioskMeasurementContractTests.cs`).

**No test in `e2e/` subscribes to it.** `grep -rn "\[latency\]\|on('console')" e2e/` returns
nothing. So four legs' figures are being emitted, on every run of the existing wall specs,
into a channel nobody listens on. **US2 is a listener. It is the cheapest measurement in
this feature and it needs no product change.**

---

## 5. User Scenarios & Testing *(mandatory)*

### User Story 1 — The whole-path span has a figure finer than the budget it tests (P1)

An engineer can read what the event-to-overlay-rendered path costs on a wall with real
video, from a figure whose instrument error is small compared with the 800 ms SLO — and can
compare it with #2072's server-side 555/758 ms.

**Why this priority:** it is the issue. Everything else refines or surrounds it.

**Independent Test:** boot the stack in run mode, run the one spec twice, read two sets of
figures off the output. Delivers value with nothing else built.

**Acceptance Scenarios**

1. **Given** a booted run-mode stack and the seeded live-video wall, **When** the span test
   runs N iterations, **Then** it prints every sample, p50, p95, max, the spread, the submit
   round trip per sample, and the instrument's own error estimate — and the printed error is
   **smaller than 100 ms**, not the ±1000 ms the harness prints today.
2. **Given** the same run, **When** the figures are printed, **Then** the legs covered and
   the legs **not** covered are printed beside them, with the reason three are not covered
   (§3e) rather than only the fact.
3. **Given** a run where the browser is remote (`PW_TEST_CONNECT_WS_ENDPOINT` set),
   **When** the test starts, **Then** it **refuses** and reports what it could not establish,
   producing no figure — the existing FR-009 refusal, kept reachable.
4. **Given** an iteration where the value never reaches the tile within the observe timeout,
   **When** the timeout expires, **Then** the run **fails naming that iteration**. A value
   that never arrives is a defect, not an "unmeasured span".
5. **Given** a stamped pair where `t1 < t0`, **When** the elapsed time is computed, **Then**
   it is reported and **fails the run** — never clamped to zero, which would manufacture a
   perfect score.
6. **Given** a completed run, **When** the figures are compared with #2072's, **Then** a p50
   **below 555 ms** is reported in the verification note as a **disagreement to resolve**,
   not averaged away (§3d).

### User Story 2 — The legs the wall already measures are reported beside the span (P2)

The same run also reports what the tile's own instruments said while it was being measured,
so "the span is X" arrives together with "and the picture was Y old, and the label was held
Z".

**Why this priority:** independently shippable, needs no product change, and it is what
turns one number into a reading of the path. Deferring it still leaves US1 a complete slice.

**Independent Test:** run the spec with a `page.on('console')` listener attached and confirm
`[latency]` lines for at least `overlay_draw`, `presentation_buffer` and `label_delay` are
captured and summarised.

**Acceptance Scenarios**

1. **Given** a run on a wall with decoding video, **When** the span iterations complete,
   **Then** the harness prints, per measurement name, the sample count and the p50/max of
   every `[latency]` line it captured during the run.
2. **Given** a measurement name that produced **no** samples, **Then** the harness prints it
   as **`no samples`** with its name — never omits it, and never prints a zero. An
   instrument that cannot read must say so (spec 095); a silent absence is
   indistinguishable from a healthy wall.
3. **Given** the captured `receive_to_decoded` figures, **Then** they are printed under that
   name with **no budget beside them**, because the leg is measured *in part* (ADR-0122).

### User Story 3 — Aligned, and late, reported together (P3)

On a wall of two tiles, the run reports both the **spread** across tiles (alignment quality)
and each tile's **absolute lag** (what alignment costs the budget), so a wall that is
aligned but late is distinguishable from one that is aligned and prompt.

**Why this priority:** it is the brief's explicit "report both", and it is the one item that
needs a new fixture — a two-tile seeded wall — because today's seeds one
(`seed-live-video-wall.setup.ts:85`). Legitimately deferrable at the phase-3 gate.

**Independent Test:** run against a two-tile wall and confirm both `wall_skew` samples and
per-tile `presentation_buffer` samples are captured and printed together.

**Acceptance Scenarios**

1. **Given** a two-tile live-video wall, **When** the run completes, **Then** `wall_skew`
   samples and per-camera `presentation_buffer` samples are printed **together**, with the
   33 ms intra-wall bound (ADR-0128 §2) named beside the skew and the 200 ms leg budget
   named beside the buffer.
2. **Given** the same run, **Then** the note records **both** figures — never the spread
   alone, which is the reading that would call a late wall healthy.

### Edge cases

- **A stale re-render.** The MutationObserver must resolve on the mutation that carries the
  **expected** value, not on any mutation of the label — each iteration uses a
  distinguishable value (the existing `SPAN{n}` idiom, `:367`) so a leftover cannot match.
- **The label hold delays t1 by design.** Not subtracted, not excluded. Reported separately
  as `label_delay` (US2) so a reader can see the split.
- **A backgrounded tab.** Chromium throttles rAF in hidden tabs, which would inflate t1.
  Playwright keeps the page active, and the existing absurd-value guard
  (`kioskLatency.ts:79`, > 60 s) already covers the pathological case for the emitted legs.
  For the span, an iteration exceeding the observe timeout fails rather than being dropped.
- **CI is a shared runner.** See §7.
- **Machine churn.** The first run after machine churn looks exactly like a regression. Two
  runs, both recorded, or it is not a measurement (SC-001).

---

## 6. Requirements *(mandatory)*

### Functional

- **FR-001** The span in `e2e/kiosk-shows-a-label-over-video.spec.ts` is stamped by an
  **in-page clock at both ends**: a capture-phase one-shot `click` listener on the operator
  page for t0, and a `MutationObserver` plus two chained `requestAnimationFrame`s on the
  kiosk page for t1. Both use `Date.now()`.
- **FR-002** The polled `expect(label).toContainText(value)` **stops being the timing
  mechanism**. It may remain as a correctness assertion; it must not produce the figure.
- **FR-003** The harness records the submit request's own round trip (via
  `page.on('response')` on the operator page) and prints it beside every sample, so the §3c
  head overshoot is bounded rather than described.
- **FR-004** The printed instrument-error line is **replaced, not deleted**, and states the
  new error with its arithmetic (rAF quantisation at each end plus `Date.now()` resolution).
  Deleting it would be the same defect as the ±1000 ms figure: a number without its
  resolution reads as more precise than it is.
- **FR-005** `sharesOneClock()` is kept and its refusal path stays reachable. A run that
  cannot establish one clock reports what it could not establish and produces no figure.
- **FR-006** A negative elapsed time fails the run, naming the iteration. It is never
  clamped to zero.
- **FR-007** `LEGS_COVERED` / `LEGS_NOT_COVERED` are kept and the *reason* three legs are
  not covered is printed with them (§3e).
- **FR-008** Every sample is printed **before** any assertion, so the figures survive a red.
- **FR-009** *(US2)* A `page.on('console')` listener on the kiosk page captures `[latency]`
  lines for the run's duration and prints, per measurement name, count and p50/max.
- **FR-010** *(US2)* A measurement name with zero captured samples is printed as
  `no samples`, never omitted and never zero.
- **FR-011** *(US2)* `receive_to_decoded` is printed with **no budget beside it**
  (ADR-0122).
- **FR-012** *(US3)* A two-tile live-video wall fixture, and both the skew and the per-tile
  absolute lag reported together.
- **FR-013** No file under `src/` or `apps/` is modified. No file under `.specify/` is
  modified. `git diff --stat` touches only `e2e/` and `specs/`.
- **FR-014** **No latency budget is asserted.** See §7.

### Non-functional

- **NFR-A** The instrument's stated error is < 100 ms, against ±1000 ms today.
- **NFR-B** The span test's runtime stays inside the e2e job's existing envelope; the
  iteration count may rise from 5 only as far as that allows.

### Out of scope

- **Any change to constitution §IV's table**, including the Camera → SFU over-claim found in
  §1b. A human writes that (ADR-0144, and constitution §Governance requires an ADR).
- **A dashboard.** §VII's obligation for these legs is **#1940**, which asks whether
  readable-in-the-sink discharges §VII at all, and is explicitly unsettled. So the
  *"or watched"* half of #1714's title **cannot be discharged here**, and this feature does
  not close #1714.
- **Camera → SFU.** §1b — no instrument, and no clock to build one against.
- **Inter-display sync.** ADR-0128 §2; not one of the six rows.
- **Making `EventToOverlayLatency` readable outside its process**, and #2072's finding that
  it records the wrong span. Both are product changes on the leg, not measurements of it.
- **Reducing the head overshoot to zero** by reading the acceptance stamp back from the
  audit row. It needs database access from the Playwright process, which `e2e/` has never
  had. Named as the escalation if the overshoot proves to dominate.

---

## 7. Why no budget is asserted, stated rather than assumed

`e2e/kiosk-shows-a-label-over-video.spec.ts` runs in CI on `ubuntu-latest`
(`.github/workflows/ci.yml:190-258`), on a shared runner hosting Postgres, RabbitMQ,
Keycloak, MinIO, MediaMTX, the fixture video and eight services. Spec 106's reasoning
applies in full and is not re-derived here: **a figure taken there is a figure about the
runner**, and a p95 gate against 800 ms would either flake or pass and be quoted.

There is a second reason, specific to this leg. **#2072 has already measured a breach** —
758 ms server-side against a 200 ms budget, on a span this one strictly contains. An
assertion at 800 ms would therefore land permanently red, and a permanently-red gate is the
strongest possible pressure to weaken it later. ADR-0144 forbids weakening a gate; the way
not to be tempted is not to install one that cannot pass.

**The cost, stated honestly:** a printed figure nobody asserts is a figure nobody watches.
That cost is real and it is #1940's, not this feature's, to pay down. What this feature can
do without a budget is **tighten a ceiling**, which is a strengthening and therefore
permitted: `OBSERVE_TIMEOUT_MS` is 60 s today, against an observed cold worst case of
1587 ms. Proposing a lower ceiling is a P2 task with its arithmetic, not a P1 obligation.

---

## 8. Success Criteria

- **SC-001** Two independent runs on an idle machine each produce a full set of figures, and
  **both** are recorded in the verification note. One run is not a measurement.
- **SC-002** A **calibration counterfactual** is run and recorded: a known delay is injected
  into the observed path and the instrument reports it within its stated error. An
  instrument that cannot see an injected delay is not an instrument. The prediction is
  written down before the run.

  > **Corrected at phase 6: C1 establishes scale and linearity, and nothing about the
  > origin.** The injection is on the **kiosk side** — it defers the observed mutation — so
  > the delayed and undelayed arms of a paired run both go through the **same two-clock
  > subtraction**, and a **constant offset between the two contexts cancels in the
  > difference**. Concretely: a −60 ms offset would make every sample 60 ms too small, which
  > is the headroom-flattering direction, while C1 still recovered 296 ms of 300 exactly.
  >
  > Two further limits of C1, stated where the criterion is rather than left to be inferred:
  > it injects into the **tail**, so it says nothing about the head (click dispatch,
  > Playwright's actionability checks, the browser's `fetch`); and its `±32 ms` is a
  > **dispersion over pairs**, not a per-sample bound — the ten pairs ran 277–340 against
  > 300, i.e. **−23/+40 per pair**.
  >
  > SC-002 is therefore **necessary and not sufficient**. The constant offset is bounded by
  > the bracketed probe in §3b, which is a separate mechanism and reports its own figure.
- **SC-003** The new instrument's figures are compared with the old harness's on the same
  machine, and the difference is explained rather than merely noted.
- **SC-004** The verification note states the §3d comparison with #2072's 555/758 ms, and
  says explicitly whether the two agree.
- **SC-005** The note names **which legs were not measured and why** — camera → SFU (§1b),
  decode's sending end (ADR-0122), inter-display sync (ADR-0128) — so no cell of §IV is left
  implying a discharge nobody earned.
- **SC-006** `git diff --stat` touches only `e2e/` and `specs/`.

---

## 9. Locked tech choices (no new decisions)

| Concern | Choice | Authority |
|---|---|---|
| Browser harness | Playwright against a live `aspire run` stack, no managed webServer | ADR-0108, `playwright.config.ts` |
| In-page clock | one-shot capture listener + rAF, stamped in the page | existing: `e2e/click-to-first-frame.spec.ts:262-300` |
| "Painted", not "changed" | two chained `requestAnimationFrame` | existing: `apps/shared/src/observability/kioskLatency.ts:135-142` |
| Cross-page subtraction | `Date.now()`, two readers of one OS clock | spec 053; existing `sharesOneClock()` |
| Per-leg browser figures | `page.on('console')` on the shipped `[latency]` line | existing: `kioskLatency.ts:87` |
| The wall | the seeded live-video wall | existing: `e2e/support/live-video-wall.ts`, `seed-live-video-wall.setup.ts` |
| Percentile index arithmetic | `Math.ceil(n * 0.95) - 1`, **and no p95 below n = 20** | existing: `e2e/click-to-first-frame.spec.ts:486-488`, which takes **20** samples |
| Median at an even count | the **mean of the two middles**, never a silently chosen side | phase 6 |
| Cross-context clock offset | **bracketed** kiosk → operator → kiosk, bounded by the round trip | phase 6, §3b |
| Figures stated as a range | every sample printed, never a lone median | existing: `:294-300`; spec 106 verification |

**Is a new ADR the honest answer? No — for what this feature builds.** Every mechanism is
already decided and already in the tree; this is an instrument sharpened and a shipped
console channel finally listened to.

**But two things this feature *finds* need a human, and neither is optional:**

1. **§IV's table.** Moving any *Measured* cell, or correcting the Camera → SFU over-claim in
   §1b, is a constitution amendment — and §Governance requires an ADR plus a version bump.
   **The lane may do neither** (ADR-0144). Phase 4 must not edit
   `.specify/memory/constitution.md`.
2. **A measured breach of the 800 ms SLO.** §IV: *"any leg breaching its budget triggers an
   ADR-class review."* #2072 has already carried one such breach to a human and is open. If
   this feature's whole-path figure breaches, it is **evidence to add to #2072 or a new
   finding issue**, never a number to adjust and never a table to edit.

**Consequence, stated plainly: #1714 cannot be closed by this feature.** It delivers the
*measured* half of the title. The *watched* half is #1940, and the record-keeping is a
human's.

---

## 10. Latency-budget impact (constitution §IV)

**Leg: all of them, and none.** This feature adds no code to the event-to-overlay path — no
file under `src/` or `apps/` changes — so it cannot erode any leg. It measures the path.

**It does not change §IV's table, and phase 4 must not edit it.** Three cells read *recorded,
not yet observed*, *recorded, not yet readable* and *in part*, and one (Camera → SFU) reads
`yes` on evidence §1b shows is an endpoint check rather than a figure. Writing into any of
them on the strength of this run would be exactly the over-claim §IV warns about:
*"a leg recorded as measured before anyone has read its figure claims a discharge nobody
earned."*

What this feature licenses, once SC-001 and SC-004 are satisfied, is a **note beside those
rows and a finding issue** — both written by a human.

---

## 11. Gate (Phase 1 → Phase 2)

1. No `[NEEDS CLARIFICATION]` markers remain. ✅
2. **The premise verdict in §1 is accepted**: every leg is built, the issue's body is stale,
   and §IV's Camera → SFU cell is an over-claim (§1b). This is the check the brief demanded
   before any planning.
3. **§3c's head overshoot is accepted** as the honest description of what is timed, *or* the
   escalation (reading the acceptance stamp back from the audit row, which needs database
   access `e2e/` does not have) is chosen instead.
4. **§7 is accepted**: the figure is printed and no budget is asserted, including the stated
   cost of that choice.
5. **US3 is kept or explicitly deferred** — it is the one item needing a new fixture.
6. It is understood that **#1714 does not close here** (§9).

### Recorded guesses

- **G1** That both Chromium contexts and the Playwright process read one OS clock via
  `Date.now()`. Argued in §3b from spec 053 and from the existing harness relying on it, but
  the two-*page* case specifically is a new application of it. **Marked because the whole
  figure rests on it.** SC-002's calibration is what tests it: an injected delay of known
  size must come back the same size.

  > **G1 is no longer a guess, and the sentence above named the wrong test for it.**
  > SC-002 cannot see a constant offset — both arms of a paired run go through the same
  > subtraction, so it cancels (see SC-002). The bracketed probe added at phase 6 (§3b) is
  > what tests G1, and it **measures** rather than argues: two runs, δ ∈ [−4, +6] ms and
  > δ ∈ [−3, +4] ms, both containing zero, both taken twice per run so drift would show.
  > G1 held. It was not established by the evidence originally cited for it.
- **G2** That the head overshoot (§3c) is small relative to the span. `CommandLatencyTests`
  puts a comparable write's p95 under 200 ms server-side, but that excludes the browser's
  own `fetch` and the gateway. FR-003 measures it rather than assuming it — if it turns out
  to dominate, that is the finding and the escalation in §6 is the answer.
- **G3** That a `MutationObserver` on the label's subtree fires for the value change. The
  label is rendered at `apps/shared/src/ui/composites/CameraViewer.tsx:348-372` and React
  updates its text node; a `characterData` + `childList` + `subtree` observer covers both
  shapes React may take. **Verified in phase 4 before any figure is quoted** — an observer
  that never fires would surface as scenario 4's timeout, not as a wrong number.

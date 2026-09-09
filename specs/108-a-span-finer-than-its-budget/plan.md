# Plan: A span finer than its budget

**Spec**: `specs/108-a-span-finer-than-its-budget/spec.md` · **Issue**: #1714
**Phase**: 2 (Plan) · **Date**: 2026-09-09

---

## 1. Bounded context and layers — why this section is short

**None are touched.** This feature adds no file under `src/` and none under `apps/`. There
is no aggregate, no value object, no invariant, no domain event, no integration event, no
migration, no DI registration, no Aspire resource.

The whole artefact is **one Playwright spec file** plus, for US3, one fixture and one setup
file — all under `e2e/`.

That is not an accident of scoping; it is the finding. Every leg is instrumented already
(spec §1) and every browser-side figure is already emitted through
`apps/shared/src/observability/kioskLatency.ts:87`. What was missing was a listener and a
clock fine enough to matter. **Both are test-side.**

**Boundary rules are therefore trivially satisfied**: no cross-context project reference is
added, `Shared.Contracts` is untouched, and `NetArchTest` has nothing new to judge. The
architecture tests that *are* relevant — `KioskMeasurementContractTests` (the closed set of
five measurement names) and `LatencyLegRecordTests` — must continue to pass **unmodified**,
which they will, because no measurement name is added or renamed.

---

## 2. The files

| File | Change | Story |
|---|---|---|
| `e2e/kiosk-shows-a-label-over-video.spec.ts` | the span harness, US2's console listener | US1, US2 |
| `e2e/support/live-video-wall.ts` | a second tile in the fixture shape | US3 |
| `e2e/support/seed-live-video-wall.setup.ts` | seed the second tile | US3 |
| `specs/108-.../verification.md` | phase 5 | — |

**One file for US1 and US2, and it must stay one file.** The existing file's own comment
(`:191-203`) records why: the span test and the label-over-video check drive the **same
variable on the same wall**, and in separate files they race — in CI they run in one worker
in alphabetical order, and the span ran first, leaving the label reading `SPAN0` and failing
the other test every time. Splitting them now would reintroduce a failure the repository has
already paid for.

**US3 changes the shared fixture, which US1 and US2 read.** That is the one coupling in this
feature, and it is why US3 is last rather than parallel.

---

## 3. The instrument

### 3a. t0 — the operator page

Mirrors `e2e/click-to-first-frame.spec.ts:262-300` exactly, with `Date.now()` in place of
`performance.now()` because the stamp must be comparable across two pages.

```
install, per iteration, on the operator page:
  window.__spanStart = { t0: null }
  document.addEventListener('click', () => { state.t0 = Date.now() },
                            { capture: true, once: true })
```

Then `setValue()` runs as today (`fill`, then `click`). Playwright's actionability checks
are paid **before** the listener stamps, because the listener fires on the dispatched
gesture, not on the call to `click()`. That is the whole of the head-side fix.

**The `fill` happens before the listener is installed** — otherwise the fill's own click on
the input would consume the one-shot listener. Ordering: fill → arm → click.

### 3b. t1 — the kiosk page

```
install, per iteration, on the kiosk page, BEFORE the operator clicks:
  observe [data-testid="camera-viewer-overlay-label"]
    with { childList: true, characterData: true, subtree: true }
  on a mutation whose textContent contains the expected value:
    requestAnimationFrame(() => requestAnimationFrame(() => {
      state.t1 = Date.now(); disconnect();
    }))
```

The double-rAF is the product's own definition of *painted*
(`apps/shared/src/observability/kioskLatency.ts:135-142`). Using a different one would make
t1 mean something the product does not mean.

**Armed before the click, never after** — a race in the other order loses fast iterations
silently, which is the failure mode that produces a *better* figure.

### 3c. The subtraction, and the guards

`elapsed = t1 − t0`, both `Date.now()`, one OS clock (spec §3b).

Three guards, and each has a stated reason:

| Guard | Behaviour | Why |
|---|---|---|
| `t0` or `t1` missing | **fail**, naming the iteration | an observer that never fired must be loud; silence would drop the slow samples and improve the figure |
| `elapsed < 0` | **fail**, reporting the value | a stepped clock, not a fast journey. Never clamped — a clamp manufactures a perfect score (spec 106) |
| `elapsed` > observe timeout | **fail**, naming the iteration | a value that never arrives is a defect, not an "unmeasured span" (the existing file's own rule, `:407-417`) |

**Every one of these fails rather than skips.** A measurement harness whose error path drops
samples reports a distribution of the samples that were fast enough to be observed.

### 3d. The error budget, printed

```
rAF quantisation at t1        ≤ 2 frames ≈ 33 ms at 60 Hz
click-dispatch granularity    ~ 1 frame  ≈ 16 ms
Date.now() resolution         2 × 1 ms
                              ─────────
stated instrument error       ≈ ±50 ms
```

Against ±1000 ms today, and against an 800 ms SLO. **The line is replaced, not deleted**
(FR-004): a figure without its resolution reads as more precise than it is, which is exactly
what the current harness refuses to let happen and what this must keep refusing.

### 3e. The head overshoot, bounded rather than described

`page.on('response')` on the operator page, filtered to the system-variables write, gives
the submit round trip per iteration. Printed beside each sample. A reader can then subtract
it to approximate §IV's own span start, and phase 5 can say whether it dominates.

---

## 4. US2 — the console listener

```
page.on('console', msg => {
  if (msg.text().startsWith('[latency]')) capture(msg.args())
})
```

`apps/shared/src/observability/kioskLatency.ts:87` emits
`console.info('[latency]', { measurement, camera, elapsedMilliseconds })` — a **structured
second argument**, not an interpolated string. So the harness must read `msg.args()` and
`jsonValue()` the object rather than regex the text. Reading the text would work today and
would break silently the moment the object gains a field.

Attached **before** navigation, detached after the run. Summarised per `measurement` name:
count, p50, max. A name with zero samples prints `no samples` (FR-010) — spec 095's whole
subject is that a silent instrument is indistinguishable from a healthy one.

`receive_to_decoded` prints with no budget beside it (FR-011, ADR-0122).

**Sampling cadence bounds what US2 can see.** `presentation_buffer` and the lag samples are
driven on a 2 s interval (`CameraViewer.tsx:47`), `receive_to_decoded` on 5 s (`:27`). A
five-iteration span run may be shorter than a few of those periods. So US2 must either run
long enough to collect samples or print `no samples` honestly — it must **not** wait for
them and call the wait part of the span.

---

## 5. US3 — the second tile

`e2e/support/seed-live-video-wall.setup.ts:85-93` seeds **one** tile (`#tile-0-camera`,
`#tile-0-overlay`), and its comment says one is enough for spec 056's purpose. It is not
enough here: `skewAcross` over one tile has no spread, so `wall_skew` is never reported
(`apps/kiosk-web/src/features/cell/useWallAlignment.ts:200-208`).

US3 seeds a second tile pointed at the **same** `FIXTURE_VIDEO_RTSP_URL`. Two receivers on
one MediaMTX path share one sender clock, which is precisely ADR-0128's alignment reference.

**The risk US3 carries, and why it is P3:** widening the shared fixture changes what every
other spec reading `readLiveVideoWall()` sees — `kiosk-shows-a-label-over-video.spec.ts`'s
US1 asserts on `getByTestId('camera-viewer-overlay-label').first()` and
`isDecodeOngoing` iterates `perElement`, so a second tile that fails to decode would turn a
passing check red for a reason that has nothing to do with it. Phase 4 must run the full
kiosk + wall projects after this change, not only the edited file.

---

## 6. What is deliberately not built

| Not built | Why |
|---|---|
| A latency assertion / budget gate | spec §7 — shared runner, and #2072 has already measured a breach this span contains |
| A dashboard | #1940 is unsettled; §VII's discharge shape is not decided (ADR-0117 vs ADR-0118) |
| A `data-overlay-painted-at` DOM attribute | it is a product change for a test's convenience; the MutationObserver reads what the product already renders |
| A window global in the bundle | same reason. The only window global here is installed **by the test**, as `__clickToFirstFrame` already is |
| Reading `EventToOverlayLatency` from the sink | that is #1940 and #2072's territory, and ADR-0118 gives no programmatic read |
| Camera → SFU | spec §1b — no duration metric exists on the running stack, and no clock to build one against |
| Summing legs into a whole-path number | ADR-0135; medians do not add, and three legs are not serial terms of this span anyway (spec §3e) |
| Any edit to `.specify/memory/constitution.md` | ADR-0144 and §Governance. **Including** the Camera → SFU over-claim §1b found |

---

## 7. Risks, and what each would look like

| Risk | Symptom | Response |
|---|---|---|
| The MutationObserver never fires (G3) | every iteration times out | It surfaces as a **failure naming the iteration**, not a wrong figure. Verify the observer on one iteration before quoting anything |
| The observer fires on a stale re-render | figures implausibly small; disagreement with #2072 | Distinguishable per-iteration values (`SPAN{n}`) make a stale match impossible; if it still happens, the value check is wrong, not the clock |
| `Date.now()` steps mid-run (NTP) | one negative or one huge sample | Fails the run by §3c's guards. Never clamped |
| The two pages do not share a clock (G1) | figures wrong by a constant offset | ~~SC-002's calibration is the test for this~~ — **wrong, corrected at phase 6.** C1 injects on the kiosk side, so both arms of a paired run go through the same two-clock subtraction and a **constant offset cancels in the difference**; C1 recovers 296 of 300 whether the offset is 0 or −60 ms. The test is the **bracketed probe** (spec §3b): kiosk → operator → kiosk bounds the offset by the round trip, ordering-independently. Measured: δ ∈ [−4, +6] ms and δ ∈ [−3, +4] ms |
| The head overshoot dominates | submit round trip comparable to the span | FR-003 prints it, so this is *observed* rather than suspected. The escalation is named in spec §6 |
| US3 destabilises other kiosk specs | unrelated wall specs go red | Run the whole `kiosk` and `wall` projects, not the edited file |
| The figure breaches 800 ms | expected, per #2072 | **A finding for a human, never a threshold to move.** No assertion exists to weaken |

---

## 8. Constitution and ADR alignment

| Rule | How this complies |
|---|---|
| §IV — every PR on the path cites its leg | Spec §10: no code is added to the path; the feature measures it. The legs are named and so are the ones it cannot reach |
| §IV — the table stays current, in both directions | Nothing is written into it. §1b's over-claim is **reported**, not corrected (ADR-0144) |
| §VII — dashboards bind implemented legs | Unchanged. #1940 owns the discharge question and is open |
| §II — value objects | No domain model is touched |
| ADR-0122 — a fragment is named as a fragment | `receive_to_decoded` printed with no budget |
| ADR-0129 — never call the hold frame accuracy | The hold is reported as `label_delay` under that name; nothing in this feature claims frame matching |
| ADR-0135 — medians do not add | One subtraction for the span; per-leg figures beside it, never into it |
| ADR-0144 — no ADR, no constitution edit, no weakened gate | No `.specify/` edit; no assertion removed or loosened. Tightening `OBSERVE_TIMEOUT_MS` is a *strengthening* and is optional |
| Karpathy — smallest possible change | The instrument changes; the test's subject, wall, fixture and file all stay |

---

## 9. Definition of done for phase 2

The plan is aligned if a reader agrees that:

1. No `src/` or `apps/` file needs to change for US1 and US2. *(If phase 4 finds one does,
   that is a finding and a scope question, not a quiet edit.)*
2. The clock argument in spec §3b is sound for **two pages**, and something *measures* it
   rather than an argument replacing it. **Corrected at phase 6:** the thing that measures
   it is the bracketed probe in §3b, not SC-002's calibration — which cannot see a constant
   offset at all, because both arms of a paired run go through the same subtraction.
3. Printing without asserting is the right posture here, for the two reasons in spec §7.
4. #1714 does not close on this work, and the artefacts say so.

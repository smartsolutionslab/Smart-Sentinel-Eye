# Tasks: A span finer than its budget

**Spec**: `specs/108-a-span-finer-than-its-budget/spec.md` ·
**Plan**: `specs/108-a-span-finer-than-its-budget/plan.md` · **Issue**: #1714

---

## Declarations for the lane (ADR-0144)

**Engineer: frontend.** The entire deliverable is TypeScript under `e2e/` — a Playwright
harness, an in-page `MutationObserver`, a `console` listener, and (US3) a fixture seeded
through the management UI. No C#, no Aspire wiring, no CI change. `frontend-engineer` owns
it end to end; `frontend-reviewer` at phase 6.

**New ADR or constitution amendment? For what is built, no. For what is found, yes — and
that part must not be attempted here.**

- **Built:** every mechanism is already decided and already in the tree — ADR-0108 (e2e
  against a live stack), the in-page clock idiom (`e2e/click-to-first-frame.spec.ts:262-300`),
  the double-rAF definition of *painted* (`apps/shared/src/observability/kioskLatency.ts:135-142`),
  the shipped `[latency]` console line (`:87`), ADR-0122's naming refusal, ADR-0135.
- **Found, and reserved for a human:**
  1. **§IV's Camera → SFU cell reads `measured: yes (SFU metrics)` and the SFU publishes no
     duration metric** (spec §1b, observed on the running stack this session). Correcting it
     is a constitution amendment; §Governance requires an ADR and a version bump.
  2. **Any movement of a *Measured* cell** on the strength of this run — same reason.
  3. **A measured breach of the 800 ms SLO**, which §IV says triggers an ADR-class review,
     and which #2072 has already carried to a human for the enclosed leg.

  **Phase 4 must not edit `.specify/memory/constitution.md` or write an ADR.** These go in
  the verification note and the PR body, and into a finding issue.

**Consequence: #1714 does not close on this work.** It delivers the *measured* half of the
title; the *watched* half is #1940, which asks whether readable-in-the-sink discharges §VII
at all and is unsettled. The PR must say so rather than using a closing keyword.

### Phase 4a colour: **characterisation, observed GREEN — plus a mandatory calibration**

This feature changes **no product behaviour**. No file under `src/` or `apps/` is modified
(FR-013, checkable by `git diff --stat`). There is no red available and manufacturing one
would be dishonest — a deliberately wrong constant that is then "fixed" is a red for the
test's own constant, not for the system (spec 106, spec 061).

So the obligation is constitution §Testing's other one: the covering tests are captured
**passing before** the change and must pass **unmodified after**.

**The covering tests, named before anything is edited:**

- `e2e/kiosk-shows-a-label-over-video.spec.ts` US1 — *a tile shows an overlay label over
  video that is actually decoding*. Untouched by this work and must stay green.
- The `kiosk` and `wall` Playwright projects in full — US3 widens a shared fixture.
- `tests/Architecture.Tests/KioskMeasurementContractTests.cs` and `LatencyLegRecordTests.cs`
  — the closed measurement-name set. No name is added or renamed, so both must pass
  unmodified.

**But "observed green" is worth nothing on its own here**, because a measurement harness's
characteristic failure is producing a plausible number for the wrong thing. So the green run
is paired with a counterfactual, run and recorded, with its prediction written down first.

### The calibration counterfactual (SC-002) — the load-bearing evidence

**C1 — inject a known delay into the observed path.** In the *kiosk page only*, and from the
test rather than from product code, delay the observed mutation by a known **300 ms**
(install a wrapper that defers the observer's resolution by exactly 300 ms, or use
Playwright's route interception to hold the realtime frame — whichever phase 4 finds
cleaner, so long as the injected quantity is exact and named).

> **Prediction, written before the run:** every sample rises by **300 ms ± the stated
> instrument error (~50 ms)** and by nothing else. If the rise is not ≈300 ms, then either
> the two pages do **not** share a clock (spec G1) or t1 is not firing where it is believed
> to fire — and in **either** case **no figure from this harness may be quoted**. This is the
> single most important thing to verify about this feature.

> **What C1 cannot see, added at phase 6 and load-bearing.** The injection is on the
> **kiosk side**, so the delayed and undelayed arms both go through the same
> `t1(kiosk) − t0(operator)` subtraction and a **constant offset between the two renderer
> processes cancels in the difference**. A −60 ms offset would make every sample 60 ms too
> small — the headroom-flattering direction — while C1 still recovered 296 of 300 exactly.
> C1 establishes **scale and linearity**, and nothing about the **origin**.
>
> Two smaller limits, recorded so the figure is not quoted tighter than it is: C1 injects
> into the **tail**, so it says nothing about the head (click dispatch, actionability, the
> `fetch`); and its `±32 ms` is a **dispersion over pairs**, not a per-sample bound — the ten
> pairs ran 277–340 against 300, i.e. **−23/+40 per pair**.

**C3 — bound the cross-context clock offset by bracketing (added at phase 6).** Read the
kiosk clock, then the operator clock, then the kiosk clock again. The middle read happened
between the outer two, so `δ ∈ [middle − after, middle − before]` — width equal to the round
trip, and ordering-independent, which is exactly what a one-way probe cannot offer. Seven
brackets intersected, taken **before and after** the loop so drift shows.

> **Prediction:** an interval containing zero and narrow against the p50. If it **excludes**
> zero, every figure this harness has produced is wrong by the offset and none may be quoted
> without the correction. If the two probes of one run **disagree**, the clocks drifted
> during it and the run is a range, not a figure.

**C2 — remove the arming and confirm it is loud.** Skip installing the kiosk-side observer
for one iteration.

> **Prediction:** that iteration **fails naming itself** (plan §3c). It must not be silently
> dropped from the population — a harness that drops its unobserved samples reports a
> distribution of the samples that were fast enough to be seen.

Both are reverted immediately. Both outcomes are recorded verbatim in `verification.md`.

---

## Phase 3 gate

- [ ] **G1** The premise verdict (spec §1) is accepted: every leg is built, #1714's body is
      stale, and §IV's Camera → SFU cell is an over-claim.
- [ ] **G2** The head overshoot (spec §3c) is accepted, or the audit-row escalation is
      chosen instead.
- [ ] **G3** Printing without asserting (spec §7) is accepted, with its stated cost.
- [ ] **G4** US3 is kept or explicitly deferred.
- [x] **G5** The feature's issue (#1714) is on Project #13. **Verified 2026-09-09** —
      `gh project item-list 13 --owner smartsolutionslab --limit 2000 --format json` filtered
      on `content.url` ending `/1714` returns **1**. Checked by `content.url` and with an
      explicit `--limit`: the number filter returns zero, and `item-list` defaults to 30, so a
      filled board looks empty without both.

---

## User Story 1 — The whole-path span has a figure finer than the budget it tests (P1)

**Independently shippable. US2 and US3 may both be dropped and this remains a complete
slice.**

- [ ] **T001 [US1]** Capture the baseline **before editing anything**: run
      `pnpm test:e2e -- --project=kiosk kiosk-shows-a-label-over-video` on a booted run-mode
      stack and save the verbatim `[span]` output — figures, median, range, and the
      `instrument error: ~±1000 ms` line. **This is the characterisation capture**; without
      it there is nothing to compare against and SC-003 cannot be met.
- [ ] **T002 [US1]** Add the kiosk-side observer helper: install a `MutationObserver` on
      `[data-testid="camera-viewer-overlay-label"]` with
      `{ childList, characterData, subtree }`, resolving on a mutation whose `textContent`
      contains the expected value, then two chained `requestAnimationFrame`s, stamping
      `Date.now()`. Validate what crosses the `page.evaluate` boundary at runtime — **nothing
      type-checks `e2e/`** (#2121), so a bad shape must throw rather than become `NaN ms`
      (`e2e/click-to-first-frame.spec.ts:314-318` is the idiom).
- [ ] **T003 [US1]** Add the operator-side t0: a capture-phase, one-shot `click` listener
      stamping `Date.now()`. **Order matters — fill, then arm, then click**; arming before
      the fill lets the input's own click consume the one-shot listener.
- [ ] **T004 [US1]** Rewire the iteration loop: arm the kiosk observer **before** the
      operator click, run the click, await both stamps, subtract. The polled
      `expect(label).toContainText(value)` **stops producing the figure** (FR-002); it may
      remain as a correctness assertion.
- [ ] **T005 [US1]** The three guards of plan §3c, each **failing** and naming its iteration:
      a missing stamp, a negative elapsed (never clamped), and an overrun of the observe
      timeout.
- [ ] **T006 [US1]** *[P]* Add the submit round trip via `page.on('response')` on the
      operator page, printed beside every sample (FR-003). Disjoint from T002–T005: it
      touches the reporting path and the operator page only.
- [ ] **T007 [US1]** Replace — never delete — the instrument-error line with the new
      arithmetic from plan §3d (FR-004). Keep `LEGS_COVERED` / `LEGS_NOT_COVERED` and add
      the *reason* three legs are not covered (spec §3e, FR-007).
- [ ] **T008 [US1]** Confirm `sharesOneClock()`'s refusal path is still reachable and still
      produces no figure (FR-005).
- [ ] **T009 [US1]** Print every sample **before** any assertion (FR-008), so the figures
      survive a red.

**T001 → T002 → T003 → T004 → T005 → T007 → T009. T006 is `[P]` against T002–T005.**

---

## User Story 2 — The legs the wall already measures are reported beside the span (P2)

- [ ] **T010 [US2]** *[P]* Attach a `page.on('console')` listener on the kiosk page before
      navigation, capturing lines whose text begins `[latency]`. **Read `msg.args()` and
      `jsonValue()` the structured second argument** — do not regex the text; the product
      emits an object (`kioskLatency.ts:87`) and a text parse would break silently when it
      gains a field.
- [ ] **T011 [US2]** Summarise per `measurement` name: count, p50, max. A name with zero
      samples prints **`no samples`**, never omitted and never zero (FR-010, spec 095).
      `receive_to_decoded` prints with **no budget beside it** (FR-011, ADR-0122).
- [ ] **T012 [US2]** Confirm against the sampling cadences — `presentation_buffer`/lag every
      2 s (`CameraViewer.tsx:47`), `receive_to_decoded` every 5 s (`:27`) — that a run either
      collects samples or prints `no samples`. **It must not wait for them and fold the wait
      into the span** (plan §4).

**T010 → T011 → T012. T010 is `[P]` against all of US1: a separate listener, a separate page
hook, no shared lines with T002–T009.**

---

## User Story 3 — Aligned, and late, reported together (P3)

**Deferrable at the phase-3 gate. It is the only story that changes a shared fixture.**

- [ ] **T013 [US3]** Add a second tile to `e2e/support/live-video-wall.ts` and
      `seed-live-video-wall.setup.ts`, pointed at the same `FIXTURE_VIDEO_RTSP_URL` — two
      receivers on one MediaMTX path share one sender clock, which is ADR-0128's alignment
      reference.
- [ ] **T014 [US3]** Report `wall_skew` samples and per-camera `presentation_buffer` samples
      **together**, with the 33 ms intra-wall bound (ADR-0128 §2) beside the skew and the
      200 ms leg budget beside the buffer. **Never the spread alone** — that is the reading
      that calls a late wall healthy.
- [ ] **T015 [US3]** Run the **whole** `kiosk` and `wall` Playwright projects, not the edited
      file. Widening the shared fixture can turn unrelated specs red — `isDecodeOngoing`
      iterates `perElement`, so a second tile that fails to decode breaks a check that has
      nothing to do with this work (plan §5).

**T013 → T014 → T015. Depends on US1 (it reads the same harness) and must not start before
US1 is green.**

---

## Phase 4a — capture the evidence

- [ ] **T016** Run the covering tests **before** the change and record them green:
      `e2e/kiosk-shows-a-label-over-video.spec.ts` US1, the `kiosk` and `wall` projects, and
      `KioskMeasurementContractTests` + `LatencyLegRecordTests`. Quote the output verbatim.
- [ ] **T017** Run them **after** the change and confirm they pass **unmodified**. An
      assertion that has to be edited is evidence behaviour moved: block, do not adjust.
- [ ] **T018** Run **C1** (the injected 300 ms). Record the before/after figures and state
      whether the rise was ≈300 ms. **If it was not, stop: no figure may be quoted, and the
      finding is that the clock assumption (G1) or t1's firing point is wrong.**
- [ ] **T019** Run **C2** (arming removed for one iteration). Record the verbatim failure and
      confirm the iteration failed by name rather than being dropped.
- [ ] **T020** Confirm `git diff --stat` touches only `e2e/` and `specs/` (FR-013, SC-006).

**T016 → (T017 ∥ T018 ∥ T019) → T020. T018 and T019 are `[P]` — different mutations,
reverted independently.**

---

## Phase 5 — verification note

- [ ] **T021** Stop any other Aspire stack first — **one machine, one stack**; two concurrent
      boots give `FailedToStart` that reads exactly like a code defect. Then boot in **run
      mode** (`dotnet run --project src/AppHost`), so the fixture video and real tiles are
      live — the condition CI never has.
- [ ] **T022** Run the measurement **twice** on an otherwise idle machine and record **both**
      full sets of figures (SC-001). One run is not a measurement, and the first run after
      machine churn looks exactly like a regression.
- [ ] **T023** Compare with the T001 baseline and explain the difference rather than noting
      it (SC-003).
- [ ] **T024** Compare with **#2072's 555 ms / 758 ms** and state plainly whether the two
      agree (SC-004, spec §3d). **A p50 below 555 ms is a disagreement to resolve, not a
      figure to average.**
- [ ] **T025** Write `specs/108-a-span-finer-than-its-budget/verification.md` containing:
      both runs' full sample lists and percentiles; the submit round trip per run; the US2
      per-leg summaries (including every `no samples`); C1's and C2's verbatim outcomes; and
      the conditions (run mode, tile count, machine, warm or cold).
- [ ] **T026** State in the note and in the PR body, explicitly:
      - **§IV's table is not edited**, and why (ADR-0144, §Governance).
      - **Which legs were not measured and why** (SC-005): camera → SFU (spec §1b), decode's
        sending end (ADR-0122), inter-display sync (ADR-0128 §2).
      - **#1714 does not close here** — the *watched* half is #1940.
- [ ] **T027** File the findings as issues for a human, **without** `agent:ready`, each
      naming its evidence:
      1. §IV's Camera → SFU cell reads `measured: yes (SFU metrics)`; the running SFU
         publishes counters and RTP jitter, no duration (spec §1b).
      2. The whole-path figure against the 800 ms SLO — as a comment on **#2072** if it
         corroborates that breach, or as a new issue if it is a distinct one.

---

## Dependency graph

```
G1..G5  (phase-3 gate)
   │
   ├── T001 ──► T002 ──► T003 ──► T004 ──► T005 ──► T007 ──► T009   [US1, P1]
   │              └────────────────[P]──── T006
   │
   ├── T010 ──► T011 ──► T012                                        [US2, P2]  [P] with US1
   │
   └── (after US1 green) T013 ──► T014 ──► T015                      [US3, P3]
                              │
                              ▼
        T016 ──► { T017 | T018 | T019 } ──► T020                     [phase 4a]
                              │
                              ▼
        T021 ──► T022 ──► T023 ──► T024 ──► T025 ──► T026 ──► T027   [phase 5]
```

**Foundational / blocking:** T001 blocks everything — the baseline cannot be taken after the
instrument changes. T013 blocks nothing but risks everything (plan §5), which is why T015
re-runs the whole projects.

**Parallel `[P]`:** T006 (reporting + operator page) against T002–T005 (kiosk observer);
US2's T010 against all of US1 (a separate page hook, disjoint lines). **US3 is not `[P]`** —
it edits the fixture US1 and US2 both read.

---

## Definition of done

- [ ] US1's harness produces figures with a stated instrument error **< 100 ms** (NFR-A).
- [ ] Two runs recorded, both in full (SC-001).
- [ ] C1 recorded and the injected delay recovered within the stated error (SC-002).
- [ ] The #2072 comparison stated either way (SC-004).
- [ ] Legs not measured named with their reasons (SC-005).
- [ ] `git diff --stat` touches only `e2e/` and `specs/` (SC-006).
- [ ] No `.specify/` file changed; no ADR written.
- [ ] Findings filed for a human (T027); **#1714 left open**.

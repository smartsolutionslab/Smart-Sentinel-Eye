# Spec 095 — Tasks

**Phase:** 3 (Tasks) · **Date:** 2026-09-07
**Spec:** `./spec.md` · **Plan:** `./plan.md` · **Issue:** #2109
**Engineer:** `frontend-engineer` (every file is `.ts`/`.tsx` under
`apps/shared`; no backend, no infra, no Aspire).

**Phase 4a colour — RED.** The spec adds lines that are not emitted today, which
is new behaviour under constitution §Testing. There is no behaviour-preserving
task here, so there is no characterisation lane and no ambiguity to resolve. The
behaviour-preserving *claims* (spec FR-006) are held down by the **existing**
suites, which must pass **unmodified** — that is T009, and an assertion that has
to be edited is a block, not an adjustment.

**The reds that are the evidence are T004-T006** — four failing cases on a
component that runs, with the doubles asserted reached. **T002 and T003 are
guards, not reds**: they cover a pure function being introduced, and a commit
that names an export before it exists does not compile, which spec 061's
`24e6fc4c` already ruled is not a red test. See T002 for the whole reasoning and
what changed at phase 4b.

**Format:** `[ID] [P?] [Story] description`. `[P]` = safe to run in parallel
because the task owns files no other in-flight task writes (ADR-0109).

---

## Foundational — blocks everything after it

- **[T001] [US1]** Declare the required-field lists once per module and derive
  both the sampler and the new predicate from them.
  - `apps/shared/src/observability/wallAlignment.ts`: a module `const` naming
    `jitterBufferDelay`, `jitterBufferEmittedCount`, `totalProcessingDelay`,
    `framesDecoded` in that order; `lagSampleFrom` reads it; add
    `missingLagFieldIn(report): string | null`.
  - `apps/shared/src/observability/kioskLatency.ts`: the same for
    `framesDecoded`, `totalProcessingDelay`, `totalDecodeTime`; add
    `missingDecodeFieldIn`.
  - Both predicates answer `null` when there is **no** `inbound-rtp`/
    `kind:'video'` stat (plan invariant 5) and when every field reads.
  - `lagSampleFrom` and `decodeSampleFrom` keep their exact signatures and
    return values (plan invariant 1).

  **Blocks T004, T005, T006.** It is the only task both samplers depend on, and
  it is what makes predicate/sampler drift impossible rather than merely
  unlikely.

---

## US1 — An operator can tell a wall that cannot align from one that is aligned

### Phase 4a — the reds, written and observed failing before any fix

- **[T002] [P] [US1]** *Red — the pure predicate names an omitted field.*
  `apps/shared/src/observability/wallAlignment.test.ts`. Three cases:
  a report whose `inbound-rtp` video stat is built **without the
  `totalProcessingDelay` key** (delete it — do **not** set it to `null`, per
  the brief: exercise the omitted-field path) → expect
  `missingLagFieldIn(report) === 'totalProcessingDelay'`; a complete report →
  `null`; a report with no video stat at all → `null`.
  ~~**Red because `missingLagFieldIn` does not exist** — the failure is a
  `TypeError`/type error, and that is the honest red for a function being
  introduced. Quote it.~~

  **Corrected at phase 4b: T002 and T003 are guards, not reds, and their output
  is not ADR-0139 evidence.** A commit whose tests name an export that does not
  exist fails to *compile* (`TS2305 ×2` here), and spec 061's `24e6fc4c` already
  ruled that **a compile error is not a red test** — it is a commit that breaks
  ADR-0087's build-on-its-own rule. For a *new pure function* the two cannot
  both hold: making the commit compile means adding the function, and adding the
  function makes these six cases pass. So T001's predicates are folded into the
  same commit as T002/T003, which lands green, and the six cases stand as
  guards on the predicates' contract.

  **The ADR-0139 evidence for this spec is T004-T006**, where the component
  runs, the doubles are reached and asserted reached, and zero `[resilience]`
  lines come out. Those four reds are the ones quoted in the PR body.

- **[T003] [P] [US1]** *Red — the same, for the decode leg.*
  `apps/shared/src/observability/kioskLatency.test.ts`, mirroring T002 over
  `missingDecodeFieldIn` and the omitted `totalProcessingDelay`. Same three
  cases. This is the twin the issue does not list (spec §"the decode twin").

- **[T004] [US1]** *Red — the composite reports the missing field once, and
  only from a stat that exists.*
  `apps/shared/src/ui/composites/CameraViewerAlignment.test.tsx`. Extend the
  existing `WhepClient` double with a *reading* `stats` mock. Two cases:
  1. `stats()` resolves a report whose video stat **omits**
     `totalProcessingDelay` → after `advanceTimersByTimeAsync(20_000)`, a
     `console.info` spy has recorded **exactly one** `[resilience]` call with
     `{subsystem: 'stream', transition: 'stats-field-missing',
     cameraIdentifier: 'cam-42', field: 'totalProcessingDelay'}` — one, not ten,
     though the lag sampler ticked ten times.
  2. `stats()` resolves a report with **no** `inbound-rtp` video stat → **zero**
     `stats-field-missing` calls (spec FR-002; plan risk R2).

  **Assert the double ran before asserting the outcome** — `expect(statsMock,
  'the lag sampler must actually have run').toHaveBeenCalled()`. Plan risk R1:
  this suite has already once passed with the code under test deleted, and its
  own comment says so.
  **Red: today zero `[resilience]` lines are emitted in case 1.**
  Depends on T002 landing the predicate's *name*; may be written in parallel
  with it and run after.

  **Corrected at phase 4b:** this task said *"the current double only has
  `statsThrows`"*. It has `setPlayoutTargetThrows` too, and T005 depends on
  that half being swappable — a task that under-describes the double it is
  extending invites the extension that clobbers the other case.

- **[T005] [US1]** *Red — a playout target that never applies says so.*
  Same file. A double whose `setPlayoutTarget` returns `false` (the
  browser-capability case — distinct from the existing `setPlayoutTargetThrows`
  double). With `playoutTargetMilliseconds={120}` and 20 s of timers: exactly
  one `[resilience]` `{transition: 'playout-target-unsupported',
  cameraIdentifier: 'cam-42'}`, the double was called, and
  `container.querySelector('video')` is
  still non-null (spec 045 FR-013 survives).
  **Red: today the boolean is discarded and nothing is emitted.**

- **[T006] [US1]** *Red — a flapping tile does not re-report.*
  Same file. Drive the double's `onConnectionStateChange` through
  `connected → disconnected → connected` and advance timers across both live
  windows. Expect the count still `1`. **Red today for the same reason as
  T004/T005** (nothing is emitted at all), and it is what pins the ref at
  component scope rather than inside the effect closure (plan risk R3).

**Phase 4a hand-off:** T002-T006 are written and run by `test-writer`, and the
**verbatim failing output** is what `frontend-engineer` receives as its brief
and what goes in the PR body. The engineer may not edit these tests to pass
(ADR-0144).

### Phase 4b — the implementation

- **[T007] [US1]** Report a missing statistics field from `CameraViewer`.
  `apps/shared/src/ui/composites/CameraViewer.tsx`. In **both** the decode
  sampler and the lag sampler, on the `current === null` branch only, call the
  matching predicate and, when it names a field not already in
  `reportedMissingFieldsRef` (a `useRef<Set<string>>` at component scope), add
  it and `logResilienceEvent('stream', 'stats-field-missing', {cameraIdentifier,
  field})`. One shared ref across both samplers (plan
  §"Where the once-per-session state lives"). Nothing else in either effect
  changes — same interval, same gating, same payloads.
  Depends on **T001, T004**.

- **[T008] [US1]** Report a playout target that did not apply.
  Same file, the effect at `:217-227`. Capture `setPlayoutTarget(...)`'s
  boolean; treat a thrown call as not-applied; on the first not-applied for a
  `live` session, set `reportedNoPlayoutRef` and
  `logResilienceEvent('stream', 'playout-target-unsupported',
  {cameraIdentifier})`. The `try`/`catch` stays and still swallows (spec 045
  FR-013). `useWhepSession.ts:331-333`'s `?? false` is **not** touched
  (spec FR-005).
  Depends on **T005**. Independent of T007's logic but writes the same file, so
  **not** `[P]` with it — T007 then T008, or one commit each, in order.

### Phase 4b — the behaviour-preserving guard

- **[T009] [US1]** The existing suites pass **unmodified**.
  `wallAlignment.test.ts`, `kioskLatency.test.ts`, `CameraViewer.test.tsx`,
  `CameraViewerAlignment.test.tsx`'s three original cases,
  `CameraViewerMedia.test.tsx`, `labelDelay.test.ts`,
  `apps/kiosk-web`'s `useWallAlignment.test.ts`, `useLabelDelay.test.ts` and
  `CellPage.test.tsx`. Run them and record the counts.
  **An assertion that has to be edited is a block, not an adjustment** — it is
  evidence spec FR-006 was broken and behaviour moved. Note that
  `CameraViewerAlignment.test.tsx`'s existing throwing-double case will now
  *also* emit a `playout-target-unsupported` line; it asserts nothing about the
  console, so it must still pass untouched. If it does not, T008 is wrong.
  Depends on **T007, T008**.

- **[T010] [US1]** `pnpm typecheck` (now covering `e2e/`) and `pnpm lint` clean.
  Depends on **T009**.

- **[T011] [US1]** Each commit builds on its own (ADR-0087). Verify per commit,
  not at the branch tip: T001's predicates land with their reds, T007 and T008
  land separately. Conventional Commits; **no `Co-Authored-By` footer and no
  session trailer** (ADR-0086). Do not push.

---

## Dependency graph

```
T001 ──┬─> T004 ─> T007 ─┬─> T009 ─> T010 ─> T011
       │                 │
       ├─> T005 ─> T008 ─┘
       └─> T006 ─────────┘

T002 [P] ─┘   (predicate red, wallAlignment)
T003 [P] ─┘   (predicate red, kioskLatency)
```

**Parallel markers:** T002 and T003 own disjoint files
(`wallAlignment.test.ts` / `kioskLatency.test.ts`) and run together.
T004-T006 all write `CameraViewerAlignment.test.tsx` and are **serial**.
T007 and T008 both write `CameraViewer.tsx` and are **serial**.

**Fan-out beyond this spec:** 095 (`apps/shared`), 096 (`CellPage.tsx`) and 097
(`WhepClient.ts`) own disjoint file sets and are all `[P]` with each other —
three frontend engineers can run at once. 095's foundational task is T001 and it
blocks only 095.

---

## Verification (phase 5)

`pnpm --filter @smart-sentinel-eye/shared test` is the CI-visible proof.
**Observation** is step 4 of the spec's end-to-end procedure: a two-tile kiosk
in Firefox, one `playout-target-unsupported` line per tile, none repeated over
five minutes, and none at all in Chromium.

**§IV citation for the PR body:** touches *SFU → kiosk decode* and
*Presentation buffer*; **no measurement required** — the change adds a bounded
`console.info` to branches that already return early and alters no timer, render
or actuation. **No *before* figure exists**: §IV records the presentation buffer
as *recorded, not yet observed* and #1714 is open because nobody has read that
figure off a running wall. **No cell of §IV's table moves.**

## Gate — phase 3

- Tasks are atomic and each names the file it writes.
- Phase 4a's colour is declared per task: **red**, with T002/T003 recorded at
  phase 4b as guards on a new pure function rather than reds (see T002).
- **The board gate:** #2109 is already on Project #13 (status *In Progress*), so
  nothing needs adding — verify with `--limit 2000`, never the 30-item default.
  Per-task issues are **not** created (the repo stopped after spec 028).
- **Two decisions want a human before phase 4:** the three-way split (095 / 096 /
  097, plus item 6 accepted and item 7 folded into #2157), and the ADR flag —
  Zod at the read boundary is stopped, not taken.

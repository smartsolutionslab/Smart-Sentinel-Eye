# Spec 094 — Tasks

**Phase:** 3 (Tasks) — ADR-0037
**Spec:** `spec.md` · **Plan:** `plan.md`
**Issue:** #2111
**Branch:** `fix/2111-a-tile-reports-live-when-a-picture-arrives`
**Worktree:** `D:/Github/wt-2111`

---

## Engineer

**One `frontend-engineer`.** One TypeScript file in `apps/shared`, a React hook
and its timers. No C#, no bounded context, no migration, no Aspire resource, no
infrastructure. A `backend-engineer` has nothing to do here.

Phase 4a is a `test-writer` first, per ADR-0144's two-agent split: it writes
T002 and T003, runs them, and returns the **verbatim** failure. The engineer
receives that output as its brief and **may not edit the tests to pass**.

---

## Phase 4a colour — RED

**Behaviour-changing.** A tile that reports `Live` off `connectionState` alone
will stop doing so. A test arriving green is a phase-4 failure, not a shortcut
(constitution §Testing, ADR-0139).

There is a **second, green** obligation running alongside it, and it is not an
exemption from the red — it is NFR-003. The ten tests in
`CameraViewer.test.tsx` and the twenty-one in `WhepClient.test.ts` must pass
**unmodified** after the change. They are the characterisation. **If one has to
be edited, behaviour moved somewhere it was not meant to: block, do not adjust.**

### Baseline, recorded before anything changed

`cd D:/Github/wt-2111/apps/shared && npx vitest run src/ui/composites/ src/streaming/`
→ **Test Files 4 passed (4) · Tests 38 passed (38)** (2026-09-07).
The engineer's "after" run must show 38 of these 38 still green, plus the new
file's tests.

### What the red is, concretely

All in a **new** file,
`apps/shared/src/ui/composites/CameraViewerMedia.test.tsx`, so
`CameraViewer.test.tsx` stays byte-identical — the strongest form the
characterisation claim can take. The separate-file-per-concern shape is the one
`CameraViewerAlignment.test.tsx` already set.

**The harness exists and needs no browser.** Copy the `FakePeerConnection`
double from `CameraViewer.test.tsx:12-56` (#2108 established it): it reaches
`connected` via `setConnectionState('connected')` and **never fires `ontrack`**,
which is precisely the negotiated-but-mediumless session. The one piece it does
not have is the instrument, and jsdom does not supply it —
verified directly on jsdom 30.0.1: `typeof video.getVideoPlaybackQuality ===
'undefined'`. Supply it on the prototype so it exists before React creates the
element, and remove it in `afterEach`:

```ts
let producedFrames = 0;

function installFrameCounter() {
  Object.defineProperty(window.HTMLVideoElement.prototype, 'getVideoPlaybackQuality', {
    configurable: true,
    value: () => ({ totalVideoFrames: producedFrames }),
  });
}

function removeFrameCounter() {
  delete (window.HTMLVideoElement.prototype as Record<string, unknown>)['getVideoPlaybackQuality'];
}
```

| # | Test | Colour today | What it pins |
|---|---|---|---|
| R1 | `Does not claim Live while the element has produced no frames` — install the counter at 0, `goLive()`, advance 2999 ms, expect `Connecting…` and **no** `Live` | **RED** — the tile says Live the moment `connected` fires | FR-001, AS-1.2 |
| R2 | `Retries a session that connects and never produces a frame` — same, advance 3000 ms → `Reconnecting…`; advance the 1 s backoff → `FakePeerConnection.instances` has length 2 | **RED** — today it never leaves Live and there is exactly one instance forever | FR-003, AS-1.3, and the "Connecting… forever" objection |
| R3 | `Backs off across repeated mediumless sessions` — three cycles; the third retry is spaced by ~4 s, not by the base 1 s (`Math.random` pinned to 0.5, as the existing suite does) | **RED** — no retries happen at all today | FR-004, AS-1.4 |
| G1 | `Goes Live as soon as a frame is produced, and stays there` — flip `producedFrames` to 1, expect `Live` within 250 ms; advance 30 s → still `Live`, still one instance | green today (it says Live from `connected`) — **a guard, not a red**, and it must be labelled as one | FR-002/FR-006, AS-1.1 |
| G2 | `Promotes on connected when the browser cannot report frames` — **no** counter installed, `goLive()`, expect `Live` immediately and no second instance within 30 s | green today | FR-005, AS-1.7 |
| G3 | `A transport blip after a picture does not demote the tile` — counter at 1, go Live, then `disconnected` → `connected`, advance 30 s → still `Live`, one instance | green today | FR-006, AS-1.5 |

**Three reds and three guards, said plainly rather than counted as six reds.**
G1–G3 cannot fail before the change; claiming them as red would be the shortcut
ADR-0139 exists to prevent.

**The counterfactual** (T004): before the fix, R1's failure must be the tile
reporting `Live` — not a harness error, not a missing element. A red that fails
for the wrong reason proves nothing, and this repository has caught guards
failing that way.

---

## Tasks

| ID | P | Story | Task |
|---|---|---|---|
| T001 | | US-1 | Record the green baseline: run `npx vitest run src/ui/composites/ src/streaming/` in `apps/shared` and capture the output verbatim. It is the NFR-003 comparison point and the proof the 38 were green before anything moved. |
| T002 | | US-1 | Write `CameraViewerMedia.test.tsx` with R1, R2, R3 — the `FakePeerConnection` copy, the prototype frame counter, fake timers. Run it. **Return the verbatim failure.** `test-writer`. |
| T003 | | US-1 | Add G1, G2, G3 to the same file. Run. They pass; record that they do and label them guards. `test-writer`. |
| T004 | | US-1 | Counterfactual: confirm R1 fails **because the tile reports `Live`**, quoting the assertion message — not because the harness is wrong. |
| T005 | | US-1 | Implement in `useWhepSession.ts`: `MEDIA_POLL_MS`/`MEDIA_WATCHDOG_MS`, `canObserveFrames`/`framesProduced`, the three-way `connected` branch, the arming function, the `attemptRef` move (FR-004), the two timers in the existing cleanup (FR-007). Read the effect's captured `videoEl`, never `videoRef.current`. `frontend-engineer`. |
| T006 | | US-1 | Run the full `apps/shared` suite plus `apps/management-web`. **All 38 baseline tests green with zero edits** (NFR-003) and the new file green. Any edit to an existing test is a stop, not an adjustment. |
| T007 | | US-1 | `pnpm lint` — assert **no new `eslint-disable`** and the `:115` suppression untouched (FR-008, I-5). If a new suppression appears necessary, **stop and report**: the lane may not weaken a gate (ADR-0144). |
| T008 | [P] | US-1 | `pnpm typecheck` (covers `e2e/` since today) and `pnpm -r --filter "./apps/**" build`. Disjoint from T009. |
| T009 | [P] | — | Comment on **#2157**: this landed per-session media confirmation in the same file, one more thing its restructuring must carry; and its §IV premise does not hold as written (`spec.md` §Boundary). **Comment only** — do not relabel, do not unblock. Disjoint from T008. |
| T010 | | — | Phase 5 verification note. Steps 1–3 of the spec's procedure need no stack. Steps 4–5 need `aspire run` — **ask before booting**; another track holds a PR on CI. |

### Dependencies

```
T001 → T002 → T004 → T005 → T006 → T007 → {T008 ‖ T009} → T010
        T003 ↗
```

T002 and T003 both write the same new file and are therefore **not** `[P]`
(ADR-0109 marks `[P]` only for disjoint files). T004 must run on the code as it
stands **before** T005 — once the fix lands, the counterfactual is unavailable.

### Parallelism

**Almost none, and that is the honest answer.** One production file, one new
test file, one engineer. Only T008 and T009 are genuinely disjoint — a build
command and a GitHub comment.

Nothing is foundational in the ADR-0109 sense: no `Shared.Kernel`, no
`Shared.Contracts`, no `AppHost` resource. Nothing blocks other tracks, and this
work blocks nothing.

---

## Gate — phase 3

- [x] Tasks atomic and dependency-ordered.
- [x] Phase 4a colour declared: **red**, with the concrete red named and the
      green characterisation obligation named separately.
- [x] ADR verdict recorded: **not an ADR** (`spec.md`, four grounds).
- [x] §IV answered: **no leg**, with the baseline that does exist (spec 077)
      and the one that does not (#1714, for #2157's leg, not this one).
- [x] Engineer named: `frontend-engineer`, after `test-writer`.
- [x] #2157 collision assessed: additive, proceed, comment.
- [ ] **The feature's issue on Project #13.** #2111 is already on the board
      (status *In Progress*) — verify with `--limit 2000`, not the default 30.
      `/speckit-tasks` adds nothing to the board.

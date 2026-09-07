# Spec 094 — Plan

**Phase:** 2 (Plan) — ADR-0037
**Spec:** `spec.md` · **Issue:** #2111
**Branch:** `fix/2111-a-tile-reports-live-when-a-picture-arrives`
**Worktree:** `D:/Github/wt-2111`

---

## Bounded context and layers

**None.** This is entirely frontend, in `apps/shared`, and touches no bounded
context, no domain model, no persistence, no messaging, no `Shared.Contracts`
type and no API surface. The DDD boundary rules (no cross-context project
references, communication only via `Shared.Contracts`) are satisfied vacuously —
`NetArchTest` has nothing to check here, because no C# changes.

Recorded rather than omitted, because the plan template asks for it and a silent
"n/a" is how a boundary claim goes unchecked.

| Layer | Change |
|---|---|
| `apps/shared/src/ui/composites/useWhepSession.ts` | the only production file |
| `apps/shared/src/ui/composites/CameraViewerMedia.test.tsx` | new, the phase-4a evidence |
| everything else | untouched |

**`WhepClient.ts` is deliberately untouched.** The issue's framing — *"there is
no channel on which attachment could be observed"* — implies adding one. None is
needed: the hook already holds `videoRef` and hands **that same element** to
`client.connect(videoEl, …)` (`useWhepSession.ts:106, 168`), so the element the
client writes to is the element the hook can read. Adding an `onTrack` callback
to `WhepClient` would be a second channel for a fact the first one already
carries, and it would answer the weaker question (a track attached) rather than
the decided one (a frame produced).

---

## The state machine, before and after

`status` moves through `idle → connecting → {live | reconnecting | offline}`.
No value is added or removed. **One transition gains a precondition, and one
side effect moves.**

```
                        TODAY                         AFTER
  connected     →  clear grace                   clear grace
                   attemptRef = 0                 if confirmed: → live      (blip)
                   → live                         else if no instrument:
                                                     attemptRef = 0; → live (FR-005)
                                                  else: arm the watch, stay connecting

  frames > 0    →  (unobservable)                 confirmed = true
                                                  clear both timers
                                                  attemptRef = 0            (FR-004)
                                                  → live

  N elapsed,    →  (does not exist)               clear the poll timer
  no frames                                       scheduleRetry(…)          (FR-003)
                                                  → reconnecting → ladder

  failed        →  scheduleRetry                  unchanged
  disconnected  →  grace 5 s → scheduleRetry      unchanged
  offline       →  → offline                      unchanged
  Degraded      →  → reconnecting                 unchanged
```

### Invariants

- **I-1.** `live` implies either a frame was produced into this tile's element,
  or the element could not be asked (FR-005). Never "the socket came up".
- **I-2.** Confirmation is **per session and monotone**. `let mediaConfirmed =
  false` is a local of the connection effect's closure, so it is created fresh
  per effect run — which is per `WhepClient` instance — and only ever set to
  true. A blip cannot un-confirm a tile that is showing video (FR-006, AS-1.5).
- **I-3.** **Every path out of `connected` ends in a scheduled transition.**
  There is no state in which the tile sits on `Connecting…` with no timer
  pending: either the poll timer will confirm, or the watchdog will retry.
  This is the invariant that answers the issue's own objection, and it is what
  AS-1.3 asserts.
- **I-4.** `attemptRef` is reset **only where the session demonstrably
  succeeded**. Today that is `connected`; after this change it is media
  confirmation — or `connected` on the FR-005 fallback path, where `connected`
  *is* the strongest available evidence. Without this, backoff never grows in
  the mediumless case: `connected` fires on every retry, resets the counter, and
  the tile hammers the SFU at a fixed `N + 1 s` forever (AS-1.4).
- **I-5.** No new lint suppression. Every new `transitionTo` fires from a timer
  callback inside the effect — the same shape as the existing `scheduleRetry`
  and `onConnectionStateChange` calls, which `react-hooks/set-state-in-effect`
  does not see. The existing suppression at `:115` stays exactly as it is
  (FR-008).

---

## Shape of the change

Two module constants, beside the three already there:

```ts
const MEDIA_POLL_MS = 250;
const MEDIA_WATCHDOG_MS = 3_000;
```

Two helpers, module-scope and pure, so they are testable and so the feature
detection sits in one place — mirroring the "support is checked rather than
assumed" pattern this codebase already uses twice, at `WhepClient.stats()`
(`:120-123`) and `WhepClient.setPlayoutTarget()` (`:190-192`):

```ts
function canObserveFrames(videoEl: HTMLVideoElement): boolean;   // typeof … === 'function'
function framesProduced(videoEl: HTMLVideoElement): number;      // 0 when it cannot be asked
```

Inside the connection effect, beside `retryTimer` and `graceTimer`: two more
timer handles and one boolean, all cleared in the **existing** cleanup (FR-007).
The `connected` branch of `onConnectionStateChange` grows the three-way decision
drawn above; nothing else in the effect changes.

**Read the element captured at the top of the effect (`videoEl`), never
`videoRef.current`.** The captured one is the element this session's
`WhepClient` was handed. Reading the ref would let a remount hand the timers a
different element than the one the session is writing into.

### Metrics (ADR-0084)

`useWhepSession.ts` is 216 lines. The change adds roughly 45, landing near 260 —
**under the 300 LOC ceiling**, with the watchdog arming extracted to a named
local function to keep the effect body's method length and nesting depth inside
their limits. If the file crosses 300, that is a signal the arming logic wants
its own module, **not** a signal to raise the limit.

---

## Messaging

**None.** No domain event, no integration event, no RabbitMQ, no
`Shared.Contracts` addition. The only observable emission is the existing
`logResilienceEvent('stream', 'connecting→reconnecting', …)`, produced by
`transitionTo` for free — which is also what makes the fault diagnosable in a
console for the first time.

---

## What is deliberately not built

- **A frozen-tile watchdog after `live`.** Different defect, different
  instrument (a rate, not a threshold), and able to demote a healthy
  low-frame-rate camera. Out of scope in `spec.md`.
- **An `onTrack` callback on `WhepClient`.** See above.
- **A Firefox path.** FR-005 covers it by keeping today's behaviour; a second
  instrument for one browser is speculative generality.
- **Reusing `CameraViewer`'s decode sampler.** `spec.md` §"Not accurate" gives
  the four reasons it cannot serve.

---

## Risks

| Risk | Mitigation |
|---|---|
| A slow real camera trips the watchdog on a healthy stream | `N` derived from FR-013, cross-checked against spec 077's measured p95/max; symptom is one extra negotiation, logged, not a black tile (Assumption 1) |
| The ten existing `CameraViewer` tests need editing to pass | FR-005 makes them inert in jsdom — verified: jsdom 30.0.1's `HTMLVideoElement` has no `getVideoPlaybackQuality`. NFR-003 makes an edit a stop condition |
| A new lint suppression is needed | FR-008 / I-5. If one is needed, stop and report — the lane may not weaken a gate (ADR-0144) |
| The added scrim delays the picture | ≤ 250 ms, and the FR-013 e2e instrument reads the element, not the label, so the measured figure cannot move |
| Timer leak on unmount | FR-007 — cleared in the effect's existing cleanup, and `CameraViewer.test.tsx`'s unmount test already guards the shape |

---

## Constitution and ADR alignment

- **§II value objects / primitives** — n/a, TypeScript frontend.
- **§IV latency** — no leg affected; argued in full in `spec.md`.
- **§VII dashboards (ADR-0117)** — no obligation attaches; no leg's state
  changes, no new measurement is reported.
- **§Testing / ADR-0139** — behaviour-changing, so **red**. See `tasks.md`.
- **ADR-0074/0075** — the change lives in `apps/shared`, consumed unchanged by
  both `management-web` and `kiosk-web`; no Redux/RTK Query surface moves.
- **ADR-0052/0103** — vitest + jsdom + Testing Library, the `FakePeerConnection`
  double; no Testcontainers, no Aspire fixture needed.
- **ADR-0086/0030** — Conventional Commits, no `Co-Authored-By`, no session
  trailer.
- **ADR-0087** — each commit builds on its own; `pnpm typecheck` now covers
  `e2e/`.
- **ADR-0144** — no ADR is written here (`spec.md` records the verdict), no gate
  is weakened, phase 4a is not skipped.

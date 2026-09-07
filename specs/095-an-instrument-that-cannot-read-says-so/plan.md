# Spec 095 — Plan

**Phase:** 2 (Plan) · **Date:** 2026-09-07 · **Spec:** `./spec.md`
**ADRs:** ADR-0074/0075 (frontend), ADR-0122 (browser measurements), ADR-0128,
ADR-0129, ADR-0117, ADR-0109 (parallel work owns disjoint files), ADR-0144.

---

## Bounded context and layers

**No backend context is involved.** This is entirely browser-side, in
`apps/shared` — the package both React apps consume (ADR-0074). No
`Shared.Contracts` message changes, no DTO changes, no endpoint changes, so
none of the cross-context boundary rules are engaged. `NetArchTest` has nothing
to say about this change and will not be extended.

The frontend layering that does apply:

| Layer | File | What it owns after this change |
|---|---|---|
| Pure arithmetic / predicates | `apps/shared/src/observability/wallAlignment.ts` | reads a statistics report into a `LagSample`, **and now** names the field it could not read |
| Pure arithmetic / predicates | `apps/shared/src/observability/kioskLatency.ts` | the same, for `DecodeSample` |
| Reporting channel | `apps/shared/src/observability/resilienceLog.ts` | **unchanged** — two new transition strings, no new code |
| Composite (the only stateful layer touched) | `apps/shared/src/ui/composites/CameraViewer.tsx` | decides *whether* to report, holds the once-per-session state |
| Session hook | `apps/shared/src/ui/composites/useWhepSession.ts` | **deliberately untouched** (spec FR-005) |

**The purity rule is the design constraint** (spec FR-007). `labelDelay.ts:14-17`
states it for this family — *"Pure, and deliberately so: no React, no browser
objects. The arithmetic is where this is right or wrong."* `wallAlignment.ts`
and `kioskLatency.ts` follow it. So the two observability modules gain a pure
predicate and gain **no** logging; `CameraViewer` does the reporting, because
`CameraViewer` is already the layer that owns the sampling interval, the
`previous` sample and the `status === 'live'` gate.

---

## Entities and value objects

No domain entities: this is a browser package, and §II's primitive ban scopes to
domain models. The two structures involved are existing TypeScript types.

| Type | Where | Invariant |
|---|---|---|
| `LagSample` | `wallAlignment.ts` | all four counters are numbers, or the value is not constructed at all (`lagSampleFrom` answers `null`) |
| `DecodeSample` | `kioskLatency.ts` | same, for its three counters |
| **new:** `missingLagFieldIn(report): string \| null` | `wallAlignment.ts` | answers the name of the **first** required field, in declaration order, that is present-but-not-a-number on the first `inbound-rtp`/`kind:'video'` stat; answers `null` both when every field reads and when there is no such stat at all |
| **new:** `missingDecodeFieldIn(report): string \| null` | `kioskLatency.ts` | the same, over `framesDecoded`, `totalProcessingDelay`, `totalDecodeTime` |

**Why a second pure predicate rather than a richer return type.** Widening
`lagSampleFrom` to `LagSample | { missingField: string } | null` would change
every call site's type and every existing test's expectation — a behaviour-
preserving change turning into a wide refactor, which §Testing then wants
characterised. A sibling predicate is additive: nothing existing changes shape,
the existing suites stay green unmodified, and the cost is a second pass over a
handful of stats **on the null path only**, at 0.5 Hz. That trade is recorded
here so the review does not re-litigate it.

**The two predicates must share the field list with their samplers.** A
predicate that drifts from its sampler would name a field the sampler does not
require, or stay silent about one it does. Each file declares its required
fields **once** as a `const` array and both functions read it. This is the one
piece of new structure the change introduces, and it exists to make the drift
impossible rather than to be reused.

---

## Invariants the change must preserve

Stated as invariants because they are what the characterisation half of phase 4a
holds down:

1. `lagSampleFrom` and `decodeSampleFrom` keep their signatures and their
   `null` returns. Every existing caller behaves identically.
2. `labelDelayFor(null)` still answers `null`; the ADR-0129 label hold still
   fails open (spec 046 FR-011/013).
3. A tile that cannot be aligned still shows video (spec 045 FR-013) — the
   `try`/`catch` around `setPlayoutTarget` stays.
4. `presentation_buffer` and `receive_to_decoded` keep their existing gating,
   cadence and payload. No measurement is added, removed or re-shaped.
5. No report is emitted when there is no `inbound-rtp` video stat (spec FR-002).
6. At most one line per camera per mounted tile per session (spec FR-003/004).

---

## Messaging

**None.** No domain event, no integration event, no `Shared.Contracts` change,
no RabbitMQ. The only outward signal is a `console.info` through
`logResilienceEvent`, whose `[resilience]` prefix and `{subsystem, transition,
...detail}` shape are a stable observable contract (`resilienceLog.ts:3-8`).
Two new `transition` values are added:

| Transition | Subsystem | Detail | Emitted from |
|---|---|---|---|
| `stats-field-missing` | `stream` | `{ cameraIdentifier, field }` | `CameraViewer` lag sampler and decode sampler |
| `playout-target-unsupported` | `stream` | `{ cameraIdentifier }` | `CameraViewer` playout effect |

The detail key is `cameraIdentifier` because `useWhepSession.ts:137` — the only
existing `[resilience]` site that names a camera — spells it that way, and a
stable observable contract must not carry two spellings of the same thing. The
artefacts said `camera` until phase 4b; spec FR-001 records the correction.

`subsystem: 'stream'` rather than a new one: `ResilienceSubsystem` is a closed
union of four, `WhepClient.ts:79` already files a stream-shape defect under
`stream`, and widening the union for two lines would be speculative generality.

---

## Where the once-per-session state lives

Three candidates, and the choice matters because getting it wrong is how this
becomes noise:

- **Module-level `Set`** — survives unmount, so a tile that remounts after a
  layout change never reports again, and tests leak state between cases.
  Rejected.
- **`useState`** — a state write on the timer path re-renders a wall of live
  video to record a fact nobody displays. Rejected for the reason
  `useWallAlignment.ts:82-88` gives for holding lags in a ref.
- **`useRef<boolean>` per effect, per mounted `CameraViewer`** — reset on
  remount, invisible to render, no re-render. **Chosen.**

One ref per report kind (`reportedMissingStatRef`, `reportedNoPlayoutRef`), both
declared beside the existing `onLagMeasuredRef`.

`stats-field-missing` is reported from **two** samplers in the same component
(decode at 5 s, lag at 2 s) over the **same** underlying stat. They share one
ref keyed by field name — a `useRef<Set<string>>` — so a wall missing
`totalProcessingDelay` says it once, not once per sampler. The decode sampler's
`totalDecodeTime` and the lag sampler's `jitterBufferDelay` are different fields
and each still gets its own line.

---

## Boundary rules

- **No cross-context project references** — N/A; nothing in `src/` is touched.
- **`apps/shared` may not import from `apps/kiosk-web` or `apps/management-web`.**
  Unchanged: the new predicates live in `apps/shared/src/observability` and are
  consumed by `apps/shared/src/ui/composites/CameraViewer.tsx`.
- **`useWhepSession.ts` is out of bounds** (spec FR-005) — this is a
  *contention* rule, not an architectural one, and it is what keeps 095 clear of
  spec 094's just-landed 129 lines and of #2157's `agent:blocked` claim.

---

## File ownership and collisions (ADR-0109)

| File | 095 | Claimed by |
|---|---|---|
| `apps/shared/src/observability/wallAlignment.ts` | **writes** | — |
| `apps/shared/src/observability/wallAlignment.test.ts` | **writes** | — |
| `apps/shared/src/observability/kioskLatency.ts` | **writes** | — |
| `apps/shared/src/observability/kioskLatency.test.ts` | **writes** | — |
| `apps/shared/src/ui/composites/CameraViewer.tsx` | **writes** | last touched by spec 045 (`9021eaf5`); **not** touched by spec 094 |
| `apps/shared/src/ui/composites/CameraViewerAlignment.test.tsx` | **writes** | — |
| `apps/shared/src/ui/composites/useWhepSession.ts` | never | spec 094 (`5ee1d6cd`, `e7e454fc`), #2157 |
| `apps/shared/src/ui/composites/CameraViewerMedia.test.tsx` | never | spec 094 |
| `apps/kiosk-web/src/features/cell/CellPage.tsx` | never | spec 096 |
| `apps/shared/src/streaming/WhepClient.ts` | never | spec 097 |

**Verified, not assumed:** `git diff --stat 631f1595~1 e7e454fc -- apps/` shows
spec 094 changed exactly `useWhepSession.ts` and `CameraViewerMedia.test.tsx`.
`CameraViewer.tsx` was last written by spec 045. So 095, 096 and 097 own
disjoint file sets and can all run `[P]`.

---

## Risks

**R1 — the tests pass because the double was never reached.**
`CameraViewerAlignment.test.tsx:31-38` carries a comment about exactly this:
an earlier version omitted the `onConnectionStateChange('connected')` call, so
every effect's `status !== 'live'` guard held and all three tests passed with
the code under test deleted. Every new test in this spec must assert the double
actually ran **before** asserting the outcome, mirroring the existing
`expect(statsThrows, 'the lag sampler must actually have run')` pattern.
T003 in `tasks.md` is that guard.

**R2 — reporting on the not-yet path.** If FR-002 is implemented as "the sample
was null, so report", every tile emits a line every two seconds from mount until
the first frame decodes. The red for the conflict scenario is what prevents
this, and it is written *before* the fix (T002).

**R3 — the once-per-session ref reset.** `CameraViewer`'s effects are keyed on
`status`, so a tile that flaps `live → reconnecting → live` re-runs them. The
refs must live at component scope, not inside the effect closure, or a flapping
tile reports on every recovery. Asserted by T006.

**R4 — an unverified premise driving a design.** Spec assumption A1: no browser
in this repository's experience has been observed omitting these fields, and
#1889 observed the opposite. The mitigation is that the change costs one branch
if A1 is false, and the spec records A1 so a later reader does not treat this
work as evidence.

## Gate — phase 2

The plan introduces no new pattern, no new dependency, no new abstraction and no
ADR. It reuses `logResilienceEvent` (three existing users), the existing
`WhepClient` test double, and the existing ref-not-state convention from
`useWallAlignment.ts`. Constitution §IV impact is stated in `spec.md` and moves
no cell of the leg table.

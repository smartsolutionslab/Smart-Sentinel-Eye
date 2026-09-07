# Spec 095 — An instrument that cannot read says so

**Issue:** #2109 (items 3 and 4 of nine) · **Branch:** `fix/2109-seven-silent-drops`
**Worktree:** `D:/Github/wt-2109`
**Phase:** 1 (Specify) · **Date:** 2026-09-07
**ADRs:** ADR-0037 (the phased workflow), ADR-0074/0075 (the two React apps),
ADR-0122 (a browser measurement reaches the sink through the service, and says
whether it is a whole leg), ADR-0128 (playout alignment against the SFU's RTCP
clock, not PTP), ADR-0129 (the label is *aged*, never frame-matched — and it
fails open), ADR-0117 (an implemented leg is subject to §VII; a discharge has to
be earned), ADR-0139 + constitution §Testing (new behaviour starts red),
ADR-0144 (the lane may not write an ADR and may not weaken a gate).
**Constitution:** §IV (the latency budget and its four-state leg table), §VII.

---

## The issue as filed, and what survives contact with the repository

#2109 lists seven defects in its body and two more in a comment — **nine, not
seven**. This spec takes **two of them** (items 3 and 4) plus **one the issue
does not list**. The other six are triaged in *§ What this spec does not take*,
with the reasoning, because "record the rest" is one of the three options the
issue itself puts up and picking it silently would be the same defect as the
ones being fixed.

### Confirmed against today's tree (`e7e454fc`)

- **Item 3's mechanism is real, and the whole chain holds.**
  `apps/shared/src/observability/wallAlignment.ts:71-96` — `lagSampleFrom`
  returns `null` unless all four of `jitterBufferDelay`,
  `jitterBufferEmittedCount`, `totalProcessingDelay` and `framesDecoded` are
  numbers. `CameraViewer.tsx:167-170` drops the sample on that null, so
  `onLagMeasured` is never called, `useWallAlignment.ts:109` never populates
  `lagsRef`, and therefore: `frameAgeFor` (`:243`) answers `null` for every
  tile, `skewAcross(heldLags)` is computed over an empty array so no
  `wall_skew` is reported at all (`:200-208`), `released` stays empty
  (`:160`), and `reportKioskLatency('presentation_buffer', …)` never fires
  (`CameraViewer.tsx:202-204`). Three instruments and one control loop, all
  silent, all indistinguishable from a wall that is aligned and healthy.
- **Item 4's discard is real.** `CameraViewer.tsx:217-227` calls
  `setPlayoutTarget(...)` inside a `try`, ignores the returned boolean, and
  swallows the throw. `WhepClient.setPlayoutTarget` (`:188-212`) returns `false`
  when there is no peer connection or no `getReceivers`, and returns `false`
  when no video receiver carries `jitterBufferTarget` — the browser-capability
  case. Nothing reads that answer anywhere in the tree.
- **The two lines the issue quotes exist, at different line numbers.** See
  *§ Citations that drifted*.

### Not listed by the issue, and it belongs here — the decode twin

`apps/shared/src/observability/kioskLatency.ts:176` reads the **same**
`totalProcessingDelay` in `decodeSampleFrom`, with the **same** bare `null`, on
a **different leg** — SFU → kiosk decode, §IV's `in part` row. The issue lists
`wallAlignment.ts` and stops. Fixing one and not the other would leave the
identical silence one file over, on the leg whose instrument has already failed
silently once. It is in scope.

### Not accurate — 1. #1889 is precedent for the opposite claim

The issue says of item 3: *"This failure class has already happened once —
issue #1889."*

**#1889's closing comment says the field was present.** Verbatim: *"the cause
was not a missing statistic. All three fields were present and numeric
throughout (`framesDecoded: 876`, `totalProcessingDelay: 45.46`,
`totalDecodeTime: 7.08`)"* — the sampler was called once in forty seconds
because `useWhepSession` returned a fresh `stats` identity every render.

So there is **no observation, anywhere in this repository, of
`totalProcessingDelay` being absent.** #1889 is genuine precedent for the
*shape* — a silent null that made a dead instrument look like a healthy one for
forty seconds, and a leg recorded as measured when it was not — and it is cited
here for exactly that. It is not evidence that this field goes missing.

This changes what gets built. The fix is **not** to widen the type, supply a
default, or treat the field as optional; there is no evidence it is. The fix is
that the null says which field it could not read.

### Not accurate — 2. a non-actuating wall does not look converged

The issue says of item 4: *"the controller keeps reporting `wall_skew` and the
dashboard shows a converging-looking loop."*

`wall_skew` is computed from `skewAcross(heldLags)` over the **achieved** lags
read out of `getStats` — never the setpoint. `CameraViewer.tsx:195-197` says so
in as many words, and `WhepClient.ts:173-178` says `jitterBufferTarget` is
write-only for exactly this reason. A browser that cannot actuate therefore
reports a spread that **does not close**, and after enough consecutive breaches
its tiles collect `TileAlignmentBadge`s (`useWallAlignment.ts:160`,
`CellPage.tsx:445`).

The defect survives the correction, in a weaker and more precise form: nothing
distinguishes *"this engine cannot hold a playout target"* from *"this wall has
not converged yet"*, and the first is permanent while the second is expected on
every startup. But "converging-looking" is inverted, and a spec written to
restore a convergence signal would be built against a symptom that does not
occur.

### Citations that drifted

| Issue says | Today |
|---|---|
| `wallAlignment.ts:79-85` | `:77-86` (the four-way `typeof` guard) |
| `CameraViewer.tsx:168-170` | `:169-170` |
| `CameraViewer.tsx:222-226` | `:217-227` |

Small drift, all three still land inside the construct they name. Recorded
because six of the nine items' citations drifted further (see the triage
section), and because a citation nobody re-measured is how this repository's
records have gone wrong before.

---

## User stories

### US1 (P1) — An operator can tell a wall that cannot align from one that is aligned

**As** the person watching a 250-camera fab wall,
**I want** a kiosk that cannot read the statistic its alignment depends on to
say so once,
**so that** a permanently dead control loop stops being byte-identical to a
perfectly working one.

Everything in this spec is P1 and is one story. There is no P2: a partial
delivery — say, the playout half without the statistics half — leaves the
kiosk still unable to distinguish the two causes of an unconverged wall, which
is the whole point.

---

## Functional requirements

**FR-001 — A statistics read that fails names the field it could not read.**
When `lagSampleFrom` or `decodeSampleFrom` answers `null` because a required
field on the `inbound-rtp` / `kind: 'video'` stat is not a number, the caller
emits one `logResilienceEvent('stream', 'stats-field-missing', { cameraIdentifier,
field })` naming the **first** absent field in declaration order.

**The detail key is `cameraIdentifier`, not `camera`.** `useWhepSession.ts:137`
is the only existing `[resilience]` site that names a camera, and it spells it
`cameraIdentifier`. `[resilience]` is a stable observable contract that
Playwright asserts on and remote-debug sessions grep, so two spellings of "which
camera" inside subsystem `stream` would be a wart in a contract that exists to
be read. Corrected at phase 4b — this FR and FR-004 both said `camera` when
written, and so did T007/T008.

**FR-002 — Absent is not the same as not-yet.** A report carrying **no**
`inbound-rtp` video stat at all emits nothing. That is a session that has not
started producing, it happens on every mount, and reporting it would put a line
on the console every two seconds for the normal case. Only a stat that is
*present* with a *missing field* is reported.

**FR-003 — Once per mounted tile per session, not at a cadence.** A missing
field is a permanent property of the engine, not a stream of dropped events.
#2084's decade-cadence (`countReportableSkew`, `CellPage.tsx:517-530`) exists
because frames arrive continuously and each one is a fresh drop; a browser does
not grow a stats field halfway through a session. One line, then silence.
Deliberately **not** reusing that helper, and this FR is where that choice is
recorded.

**The scope is the mounted tile, and the tile is a grid position — not the
camera.** `CellPage.tsx:290` keys tiles on `positionKey(row, col)`, so a layout
revision that puts a different camera at (0,0) reuses the same `CameraViewer`
instance and changes only the `cameraIdentifier` prop. The latch does not reset,
and the newly-placed camera's equally unreadable instrument is not reported
again. **That is deliberate.** A missing `totalProcessingDelay`, like an absent
`jitterBufferTarget` in FR-004, is a property of the browser engine and not of
the camera, so one line per kiosk is the whole of the information there is to
give; resetting per camera would multiply an engine-level fact by however many
cameras pass through a slot overnight. `cameraIdentifier` in the detail
therefore **names the tile that noticed** rather than scoping the report — and a
reader who takes it as a scope will wait for a line this code deliberately does
not emit. Written down because this FR said "once per camera" until code review,
and an unstated scope is the same defect class the spec exists to close.

**FR-004 — A playout target that did not apply says so.** When
`setPlayoutTarget` answers `false` for a session whose status is `live`, or
throws, `CameraViewer` emits one `logResilienceEvent('stream',
'playout-target-unsupported', { cameraIdentifier })`. Once per mounted tile, on
the same rule as FR-003 and with the same scope: the tile is a grid position, an
engine that carries no `jitterBufferTarget` carries none for any camera the wall
later puts in that slot, and `cameraIdentifier` names the tile that noticed.

**FR-005 — The `?? false` in `useWhepSession` is left alone.**
`useWhepSession.ts:331-333` answers `false` when `clientRef.current` is still
null — a legitimate transient between mount and connect, which happens on
every tile every time. Reporting there would log on every mount. The report
belongs at the `CameraViewer` call site, guarded on `status === 'live'`, where
a `false` means the receiver refused. **This also keeps spec 095 out of
`useWhepSession.ts` entirely**, which #2157 (`agent:blocked`) and spec 094 both
have claims on.

**FR-006 — Behaviour is unchanged; only the silence is.** No timer, no render,
no actuation, no fallback and no default value changes. Specifically:

- `lagSampleFrom` / `decodeSampleFrom` keep their signatures and keep returning
  `null`. Callers keep dropping the sample.
- The ADR-0129 label hold keeps failing open. `labelDelayFor(null)` still
  answers `null` and the label still shows immediately (FR-011 of spec 046).
  **Changing that would be ADR-scale and is refused here** — see *§ The ADR
  question*.
- The playout effect keeps its `try`/`catch` and a tile that cannot be aligned
  keeps showing video (spec 045 FR-013).

**FR-007 — The pure functions stay pure.** `wallAlignment.ts` and
`kioskLatency.ts` are pure-arithmetic modules by deliberate design
(`labelDelay.ts:14-17` states the rule for the family). No logging is added
inside them. FR-001's naming is a **separate pure predicate** in the same file,
answering the missing field's name or `null`; the caller does the reporting.

---

## Acceptance scenarios

### Happy — the instrument reads, and nothing new is said

```gherkin
Given a tile whose receiver reports jitterBufferDelay, jitterBufferEmittedCount,
      totalProcessingDelay and framesDecoded as numbers
When  the lag sampler has run for twenty seconds
Then  a lag is reported to the wall on every interval after the first
And   no [resilience] line naming stats-field-missing is emitted
```

### The defect — a missing field is named once

```gherkin
Given a tile whose inbound-rtp video stat omits totalProcessingDelay entirely
      (the key is absent, not null)
When  the lag sampler has run for twenty seconds — ten intervals
Then  exactly one [resilience] line is emitted, subsystem "stream",
      transition "stats-field-missing", detail carrying cameraIdentifier and
      field "totalProcessingDelay"
And   no lag is reported to the wall
And   the tile is still showing video
```

### Conflict — a session not yet producing is not a defect

```gherkin
Given a tile whose statistics report carries no inbound-rtp video stat at all
When  the lag sampler has run for twenty seconds
Then  no [resilience] line is emitted
```

### The defect — a playout target that never actuates

```gherkin
Given a live tile on an engine whose video receivers carry no jitterBufferTarget
And   the wall has decided a target of 120 ms
When  the playout effect has run for twenty seconds
Then  exactly one [resilience] line is emitted, subsystem "stream",
      transition "playout-target-unsupported", detail carrying cameraIdentifier
And   the tile is still showing video
```

### Bad request / auth

**N/A, stated rather than omitted.** Nothing in this spec crosses a trust
boundary: no endpoint, no DTO, no token, no scope. `logResilienceEvent` writes
to the browser console (`resilienceLog.ts:14`). The two measurements that *do*
reach the service (`presentation_buffer`, `receive_to_decoded`) are unchanged in
shape, cadence and authorisation.

---

## Independent end-to-end test procedure

Runnable by a person with the repo and a browser; **no Aspire stack required.**

1. `cd D:/Github/wt-2109 && pnpm install`
2. `pnpm --filter @smart-sentinel-eye/shared test` — the two new suites pass.
3. **The counterfactual.** In
   `apps/shared/src/ui/composites/CameraViewerAlignment.test.tsx`, in the case
   *"Names a statistics counter its receiver does not report, once for the
   session"*, change `videoStatWithout('totalProcessingDelay')` to
   `videoStatWithout()` — one token — so the double reports a stat with every
   field. Re-run: that case **fails** at `expect(named).toHaveLength(1)`,
   proving it is driven by the absent field and not by the double existing.
   Revert.

   **As first written this step said to delete the whole `stats` mock**, which
   does not do what it claims: with the mock gone the double's `stats` is
   `undefined` (or, after the double was reworked to be swappable, throws), and
   the case fails on a *missing statistics call* rather than on a *present stat
   with an absent field* — a different failure, proving nothing about the thing
   under test. Corrected at phase 4b, before phase 5 quoted it.
4. **On a real wall (optional, and the only part needing the stack):** boot the
   AppHost, open the kiosk on a two-tile layout in Firefox — an engine that does
   not implement `jitterBufferTarget` — and read the console. Expect one
   `[resilience] {subsystem: 'stream', transition: 'playout-target-unsupported',
   cameraIdentifier: …}` per tile and no repetition over five minutes. In
   Chromium expect none.

Step 4 is how the change is *observed* rather than merely tested, and it is the
verification note's material at phase 5. Steps 1-3 stand alone if no wall is
available.

---

## Locked tech choices (nothing new is introduced)

| Concern | What is used | Where it already exists |
|---|---|---|
| The report channel | `logResilienceEvent('stream', …)` | `resilienceLog.ts:9`; used by `WhepClient.ts:79` for #2108's `track-without-stream`, by `useWhepSession.ts:137`, by `CellPage.tsx:195` for #2084 |
| Test framework | vitest + `@testing-library/react` + jsdom | `CameraViewerAlignment.test.tsx` |
| Statistics doubles | the existing hand-written `WhepClient` class double | `CameraViewerAlignment.test.tsx:29-47` |

`[resilience]` is a **stable observable contract**: new transitions are additive
— nothing anywhere reads the *set* of transitions, so adding two changes no
existing reader — and the shape is not changed.

**Half the reason first given for that was false, and this spec quoted it rather
than checking it.** `resilienceLog.ts:3-8` says *"Playwright asserts on it and
kiosk remote-debug sessions grep for it"*, and the sentence above used to repeat
it. **`grep -rn resilience e2e/` returns nothing** — not one of the 21 Playwright
specs mentions the prefix. What actually asserts on `[resilience]` is vitest:
`resilienceLog.test.ts`, `layoutHub.test.ts`, `WhepClient.test.ts`,
`CellPage.test.tsx`, `CameraViewer.test.tsx` and this spec's own
`CameraViewerAlignment.test.tsx`. The remote-debug half is unverifiable in either
direction. The conclusion survives untouched — an additive transition is safe
against every reader, asserted or hypothetical — but the premise was a record
nobody re-checked, which is precisely the pattern this spec exists to close.
Found in code review.

The same claim in `resilienceLog.ts`'s own docstring is **left as found and
reported as a finding**: it belongs to spec 011, this spec touches no other file
outside its own list, and a one-line comment correction in a shared file the
change never otherwise opens is a decision for a human rather than a drive-by.

---

## Latency-budget impact (constitution §IV)

**Legs touched: SFU → kiosk decode (≤ 120 ms) and Presentation buffer /
playout alignment (≤ 200 ms).** Both are `Implemented: yes`.

**No measurement is required, and no *before* figure can be cited.**

- The change adds a `console.info` on branches that **already return early**,
  bounded to at most one line per mounted tile per session (FR-003, FR-004). No
  timer, interval, render, actuation or network call is added, removed or
  re-timed (FR-006). The path's cost is unchanged by construction, and the
  guard against that claim is FR-006's behaviour-preserving assertions in
  `tasks.md`.
- **A *before* figure for the presentation buffer does not exist.** §IV records
  that row as *"recorded, not yet observed"* — the number reaches the sink, but
  **no person has ever read it off a running wall**, which is why #1714 is open
  and why the row is not `yes`. Inventing a baseline to compare against would
  be worse than saying this.
- **No cell of §IV's table moves.** Not the decode row, not the presentation
  buffer row. A leg is not measured because a unit test passed — §IV calls that
  *"a §VII discharge nobody earned"* — and this spec produces no figure.
- The change makes a *future* measurement more trustworthy: after it, a run
  that reports no `presentation_buffer` samples can be told apart from a run
  whose statistics could not be read. That is the honest claim, and it is
  smaller than "improves the budget".

---

## The ADR question — raised, not answered

#2109 closes by asking one question for all nine items: *"when a field the
kiosk depends on is absent, what should happen?"* and offers three options.

**Option 2 (signal per site, as #2084 did) needs no ADR** and is what this spec
takes. `logResilienceEvent` on its own transition is an established pattern with
three existing users; applying it to two more sites is implementation.

**Option 1 (validate every DTO read at the boundary with Zod) needs an ADR.**
ADR-0079 scopes Zod to *form input* — *"Zod schemas define input shapes and
validation … form validation via `zodResolver`"* — and there is **no
`safeParse` or `.parse` on any read path in `apps/shared/src/api`** today
(verified by grep). Moving Zod onto response parsing changes a locked decision
about where validation lives, for every context, and would want an ADR
amending 0079's scope.

**ADR-0144 forbids the lane writing one, so it is flagged and stopped here.**
Nothing in this spec depends on the answer. If a human decides for option 1
later, it supersedes spec 096's approach (below) and not this one.

**Item 3 does not touch ADR-0129.** The label hold's fail-open is preserved
byte-for-byte by FR-006. Changing *that* would be ADR-scale; making a missing
statistic observable is not, and the two are kept apart deliberately.

---

## What this spec does not take, and why

The remaining six items of #2109 are **not** dropped. Two are recommended as
their own specs; one is recommended as accepted-in-writing.

### → Spec 096 (recommended, parallel, not blocked): items 1, 2, 5

`apps/kiosk-web/src/features/cell/CellPage.tsx` only. Disjoint from every file
this spec touches, so it runs `[P]` alongside 095 under ADR-0109.

All three reproduce **only under DTO drift**, and this is checkable rather than
assumed:

- `LayoutDto.cs:70-74` declares `Guid? OverlayIdentifier` in a positional
  record, and `LayoutComposition` sets **no** `DefaultIgnoreCondition`
  (grep: the only one in `src/` is `HttpKeycloakAdminClient.cs:38`). So today's
  server always emits `"overlayIdentifier": null`, never omits it. **Item 1's
  `undefined` is unreachable from this server.**
- `LayoutDto.cs:25` declares `string Fab`, non-nullable. **Item 2's `undefined`
  is likewise unreachable from this server.**

They are real hazards against a changed or third-party server — which is #2103's
risk, one layer up — and item 2's fix is one line matching #2084's own
(`typeof fab !== 'string' || fab === ''`, `CellPage.tsx:379`). Worth doing.
Worth doing *honestly*, as hardening rather than as a bug fix, which is why it
wants its own spec rather than being smuggled into this one.

Two corrections that spec will need:

- **Item 1's stated blast radius is wrong.** `tilesToBoundOverlays`
  (`CellPage.tsx:532-540`) *adds* `undefined` to the set; it does not empty it.
  A layout with one omitted identifier and one real one keeps matching pushes
  for the real one. The issue's *"drops all pushes for the life of the page"*
  holds only when **every** tile omits the field — in which case no overlay is
  bound to any tile and dropping overlay pushes is correct. The genuine residual
  harm is `useGetOverlayQuery('')` firing with `skip: false` (`:354-355`), which
  requests `/overlays/` — the collection URL — and whose response then meets
  `overlay?.revisions.find(...)` at `:358`. Whether that is a silent 404 or a
  thrown `TypeError` is unresolved by reading and should be settled by the red
  test, not asserted.
- **Item 5's stated symptom holds for one of its three sub-cases.** An
  *omitted* or *renamed* `text` makes `liveText` undefined (`:385`), so
  `renderOverlay` is undefined (`:401-403`) and the tile renders **no label at
  all** — visible, not a *"plausible-looking static template"*. That symptom
  occurs only if the **placeholder delimiter** changes while `text` survives.

### → Spec 097 (recommended, parallel): items 8 and 9

`apps/shared/src/streaming/WhepClient.ts` only — `:158-159` (a WHEP answer with
no `Location` leaves `sessionUrl` null, so `close()` never issues the DELETE and
MediaMTX holds the session to its own timeout) and `:230` (`.catch(() =>
undefined)` swallows every release failure). Both citations drifted (the issue
says `:127-128` and `:200`); both constructs are present and unchanged.

Separate because the user is different — this is a server-side resource leak on
a reconnecting 250-camera wall, not a kiosk indicator — and because it carries a
gate hazard: `WhepClient.test.ts:320-334` is a **passing test asserting the
silence is correct** (*"close() without a captured session URL performs local
teardown only"*). Changing that behaviour means changing an existing assertion,
which §Testing treats as evidence the behaviour moved. It needs a spec that
decides deliberately whether the silence is intended, not a drive-by.

### → Accepted, recorded, not fixed: item 6

`kioskLatency.ts:99-100`, `if (token === null) return;`. Recommended
**accepted in writing** rather than fixed, and the issue itself ranks it lowest
for the right reason: `console.info('[latency]', …)` fires at `:87` *before*
`send` is reached, so every measurement leaves a trace whether or not it is
posted. A null token means the kiosk is unauthenticated or mid-renewal — a
transient the session layer already reports (`gateway.ts:76-113` logs
`renew-start` / `renew-success` / `renew-failure` / `expired`), so a second
line here would duplicate an existing signal at five measurements × N tiles ×
every renewal. Fixing it would add noise to buy nothing.

### → Accepted, recorded, not fixed: the `?? false` that can latch

**Found in code review, and it is not one of #2109's nine.**
`useWhepSession.ts:331-332` answers `false` whenever `clientRef.current` is null,
and FR-005 leaves that alone. `status === 'live'` covers the report almost
everywhere — but not on the branch where the connection effect returns early
(`!whepUrl || !videoEl`, or `offlineMessage !== null`): the client is nulled in
cleanup and not rebuilt, while `transitionTo('offline')` only moves `status` on
the *next* render. If `playoutTargetMilliseconds` also changes in that commit,
the playout effect reads a `false` meaning *"there is no client"* as one meaning
*"the receiver refused"*, latches `reportedNoPlayoutRef`, and the tile claims for
the rest of its life that its engine cannot hold a target.

**Accepted rather than fixed, for three reasons.** It is narrow: it needs a
target change inside the one render where the client is gone and `status` has
not caught up. Its root is the same write-once latch FR-003 discusses, so
patching it here would be a second and differently-shaped answer to one
question. And the clean fix is a tri-state distinguishing *no client* from
*receiver refused*, which lives in `useWhepSession.ts` — the file FR-005 keeps
this spec out of entirely, and which #2157 (`agent:blocked`) and spec 094 both
have claims on. **It belongs to #2157**, with item 7 above.

The cost of leaving it is one false console line on one tile, on a transition
that also produces an `offline` state the operator can see. The cost of taking
it here is a change to a state machine two other specs are holding.

### → Kept as it is, recorded: `inboundVideoStatIn` in both modules

The three-line helper is duplicated verbatim in `wallAlignment.ts` and
`kioskLatency.ts`, and the argument that hoisted `REQUIRED_*_FIELDS` — one
declaration, so two readers cannot drift — does apply to *which stat to read*
as well. It is not taken, deliberately.

The `REQUIRED_*_FIELDS` hoist removed drift **within** a module, between a
sampler and the predicate that must describe exactly it; it introduced no file
and no dependency. Hoisting `inboundVideoStatIn` instead puts an import edge
between two pure modules that are deliberately independent, or invents a third
module for three lines — at phase 6, with no observed drift and no test pressure.
The two legs of §IV are separately owned, and a selection rule that later differs
by leg (a specific SSRC, say) is a change either copy can make alone.

Recorded rather than left silent, because "defensible house shape" and "nobody
noticed" are indistinguishable after the fact.

### → Not in scope at all: item 7 and #2108

**Item 7** (`useWhepSession.ts:149`, `if (!whepUrl || !videoEl) return
undefined;`) is a genuine conflation — `whepUrl` absent after the stream query
resolved is a defect, `videoEl` null and `whepUrl` undefined *while the query is
loading* are the normal path on every mount. But the file is claimed twice
over: spec 094 rewrote 129 lines of it three commits ago, and **#2157 is
`agent:blocked`** on restructuring that exact state machine behind a #1714
measurement. Touching it now collides with both. It should be folded into
#2157's spec when #1714 unblocks it, and this spec says so rather than leaving
it looking forgotten.

**#2108** is merged (`392c14cc`, `af89aa17`) and is explicitly not this issue's.

---

## Out of scope

- Any change to what is rendered, actuated, timed or measured (FR-006).
- Zod on any read path (needs an ADR — see above).
- `useWhepSession.ts`, in any form (FR-005).
- `CellPage.tsx` and `WhepClient.ts` (specs 096 / 097).
- Moving any cell of constitution §IV's leg table.
- Inter-display synchronisation — unbuilt and out of scope per ADR-0128, and
  not a §IV row.

## Assumptions, marked

- **A1 (unverified premise, carried from the issue):** that a browser the fab
  actually deploys omits `totalProcessingDelay`, or omits `jitterBufferTarget`.
  Firefox and Safari are named in #2109; **this repository has measured
  neither**, and #1889 — the one real observation it has — found the field
  *present*. The work does not depend on A1 being true: FR-001 and FR-004 make
  the absence legible if it ever occurs and cost one branch if it never does.
  Recorded so nobody later reads this spec as evidence the field goes missing.
- **A2:** `getStats()` reports fields per-stat and not per-report, so naming the
  first absent field on the first `inbound-rtp` video stat is sufficient. True
  of the code being read; not verified against a browser.

## Gate — phase 1

No `[NEEDS CLARIFICATION]` remains. Two items for the reviewer to confirm before
phase 2 advances:

1. **The split.** Three specs (095 here, 096 CellPage, 097 WhepClient) plus one
   accepted item and one deferred to #2157 — rather than one spec for nine.
2. **The ADR flag.** Option 1 (Zod at the read boundary) is stopped, not taken.

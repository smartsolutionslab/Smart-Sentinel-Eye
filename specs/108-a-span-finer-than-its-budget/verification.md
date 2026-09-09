# Verification note — spec 108

**Issue:** #1714 · **Branch:** `perf/1714-the-path-measured-end-to-end`
**Covers:** SC-001 (two runs of my own), SC-002/SC-003 (the printed legs and their
`no samples`), SC-004 (the #2072 comparison), and the two C# classes phase 4a could not run
**Date:** 2026-09-09 · **Phase:** 5 (ADR-0037)

**Machine:** one developer laptop — Windows 11 Pro 10.0.26100, Docker Desktop, Node
v24.5.0, pnpm 10.30.3, Chromium headless. **One Aspire stack**, Debug, warm ~3 h at the
first run and ~4 h at the last, nothing else driving the box.
`e2e/kiosk-shows-a-label-over-video.spec.ts` was **not modified** by this phase — it is
phase 4a's artifact under the characterisation rule, green before and green after, and this
branch adds only this file on top of commit `0e56b178`.

---

## Read this first: what the figure does not include

> **The ADR-0129 label hold never engages on the wall these figures come from, and the
> reason is not that the run was too short. It is that the hold does not work on a
> single-tile wall at all.**

Every run — phase 4a's two and my two — prints `label_delay: no samples`. Phase 4a
reported that and named `frameAgeFor` returning null as the cause. **The observation is
right and the stated mechanism is wrong**, and the difference matters because the wrong one
reads as a fixture artefact while the right one is a product defect.

`frameAgeFor` (`apps/kiosk-web/src/features/cell/useWallAlignment.ts:243`) does **not** gate
on tile count:

```ts
const frameAgeFor = useCallback((tileKey: string) => lagsRef.current.get(tileKey)?.lagMilliseconds ?? null, []);
```

It reads a **ref**, and it is read during `CellPage`'s render. On a wall of two or more
tiles the settle interval writes `CellPage` state every 2 s, so the age is re-read on every
cycle and reaches the tile. **On a one-tile wall `aligning` is false, that interval never
runs, and nothing else re-renders `CellPage` after the first lag sample lands** — so the
`null` captured at first render is the `frameAgeMilliseconds` prop the tile keeps for the
life of the page. The unit test admits this in its own words
(`apps/kiosk-web/src/features/cell/CellPage.test.tsx:594-601`): it fires a fake highlight
frame to force the render, because *"with no lag reported the controller settles to no
change and re-renders nothing"*. The test manufactures a re-render production does not
have.

**Two throwaway probes, run on this stack and then deleted, settle it.** Neither was
committed and neither touched product source. Same camera, same overlay, same clip, same
stack — only the tile count differs:

| | one tile | two tiles (1×2) |
|---|---|---|
| `presentation_buffer` | 29 samples | 58 samples |
| `receive_to_decoded` | 11 samples | 22 samples |
| `wall_skew` | **0** | 28 samples, 0–2 ms |
| **`label_delay`** | **0 samples** | **10 samples — 35, 41, 35, 36, 39, 43, 36, 40, 41, 45 ms** |

Both probes soaked the wall for 45 s first, so the tile's lag sampler had reported many
times before any label changed, then drove five value changes. The one-tile probe's own
lines:

```
[probe] after 45 s soak: presentation_buffer=21 sample(s) values=[9, 10, 10, 9, 14, 15, 13, 15, 15, 13, 13, 12, 12, 14, 16, 13, 16, 13, 15, 14, 15] label_delay=0
[probe] label_delay: 0 sample(s)
```

and the two-tile probe's:

```
[probe2] after 45 s soak: presentation_buffer=44 wall_skew=21 label_delay=0
[probe2] label_delay: 10 sample(s) [35, 41, 35, 36, 39, 43, 36, 40, 41, 45]
```

Ten holds for five value changes across two tiles is exactly one hold per tile per change.
So the mechanism works; it is unreachable on a wall of one.

### How much of the 800 ms this figure does not account for

§IV's 800 ms is `80 + 120 + 200 + 200 + 50 + 150`. This span covers **two** of those rows —
*event → overlay state* (200 ms) and *overlay composite + render* (50 ms) — so it spans
**250 ms of budgeted terms, not 800.** Comparing 79 ms against 800 ms suggests 10×
headroom; against the 250 ms it actually spans it is 3.2×.

On top of that, **the ADR-0129 hold is a term the budget explicitly permits and it is
absent from every figure here.** Its cap is 200 ms. **Its observed size on this fixture is
35–45 ms**, not 200 — the two-tile probe measured it directly, and it agrees with the
arithmetic (`presentation_buffer` 15–17 ms + decode processing ≈ 24 ms ≈ 39–41 ms). So:

- **absent from every figure: the hold.** On this fixture that is **~35–45 ms**. On a fab
  camera with a deeper buffer it can be anything up to the **200 ms** cap, and nobody has
  measured one.
- **not covered at all, by design:** camera → SFU (80 ms), SFU → kiosk decode (120 ms),
  presentation buffer (200 ms). Since ADR-0129 they are not serial terms of the label path;
  they enter it *only* through the hold above.
- **headroom (150 ms)** is an arithmetic remainder, not a term.

**Nothing here licenses "the 800 ms budget holds."** It licenses a figure for 250 ms of it,
with a term worth ~35–45 ms on this hardware missing from the figure.

---

## The figures I observed

`pnpm exec playwright test --project=kiosk kiosk-shows-a-label-over-video`, twice, back to
back, on the warm stack. **Both runs passed, 8/8 including seeds and cleanup.**

| | phase 4a run 1 | phase 4a run 2 | **my run 1** | **my run 2** |
|---|---|---|---|---|
| n | 10 | 10 | 10 | 10 |
| p50 | 180 ms | 162 ms | **79 ms** | **79 ms** |
| p95 | 363 ms | 260 ms | **236 ms** | **226 ms** |
| max | — | — | 236 ms | 226 ms |
| p50 net of the submit round trip | 105 ms | 96 ms | **60 ms** | **65 ms** |
| `overlay_draw` p50 / max | 33–46 / **86** across the pair | | 38 / 48 | 30 / **56** |
| `label_delay` | no samples | no samples | no samples | no samples |

**My p50 is less than half phase 4a's, and I am not averaging them.** Both pairs were taken
on this machine on a warm stack; mine were taken later, on a stack that had been up ~4 h
and had already carried phase 4a's writes and one of my own runs through the same path. The
honest reading is a **p50 somewhere between 79 and 180 ms over four runs**, and a single
number would be wrong within a day — the rule spec 106 had to learn the same way.

**The whole tail is the submit round trip, and that is head overshoot outside §IV's span.**
Both of my runs put their maximum on iteration 7, and in both the submit round trip accounts
for nearly all of it (236 ms of which 177 ms; 226 ms of which 136 ms). Subtracting each
sample's own printed round trip:

| | run 1 net | run 2 net |
|---|---|---|
| samples (ms) | 37, 40, 49, 57, 59, 60, 61, 66, 67, 68 | 33, 48, 50, 53, 57, 65, 74, 77, 82, 90 |
| p50 | 60 | 65 |
| max | 68 | 90 |

Twenty samples between **33 and 90 ms** with no outlier — the raw spread of 168–180 ms was
the network, not the path. That arithmetic is mine, on the harness's own per-iteration
lines; the harness itself prints only the round trips' p50 and max.

**Phase 4a's "iteration 0 slowest in 2 of 3 runs" does not reproduce here.** Iteration 0 was
104 ms (4th largest) in run 1 and 58 ms (the smallest) in run 2. The first-write penalty is
real elsewhere, but it is not visible in these two runs, so it should not be cited as
evidence from within them.

### Run 1 — verbatim

```
[decode] 13 → 38 frames in 1000ms (+25, threshold 10) across 1 element(s)
[span] iteration 0: 104 ms (submit round trip 37 ms)
[span] iteration 1: 77 ms (submit round trip 17 ms)
[span] iteration 2: 66 ms (submit round trip 17 ms)
[span] iteration 3: 74 ms (submit round trip 17 ms)
[span] iteration 4: 72 ms (submit round trip 35 ms)
[span] iteration 5: 56 ms (submit round trip 16 ms)
[span] iteration 6: 79 ms (submit round trip 18 ms)
[span] iteration 7: 236 ms (submit round trip 177 ms)
[span] iteration 8: 124 ms (submit round trip 58 ms)
[span] iteration 9: 100 ms (submit round trip 32 ms)
[span] 10 sample(s) — 56 / 66 / 72 / 74 / 77 / 79 / 100 / 104 / 124 / 236 ms
[span] p50 79 ms, p95 236 ms, max 236 ms, range 56-236 ms, spread 180 ms
[span] submit round trip: p50 32 ms, max 177 ms over 10 sample(s) — subtract it to approach the section IV span start
[span] instrument error: ~±52 ms (2 rAF ≈ 33 ms + click dispatch ≈ 17 ms + 2 × Date.now() 1 ms) — in-page stamps at both ends; the polled assertion no longer produces the figure
[legs] 13 [latency] line(s) captured during the run
[legs] overlay_draw: 12 sample(s), p50 38 ms, max 48 ms — budget 50 ms (section IV composite + render)
[legs] receive_to_decoded: no samples — no budget: a fragment of a leg, not a leg (ADR-0122)
[legs] presentation_buffer: 1 sample(s), p50 23 ms, max 23 ms — budget 200 ms (section IV presentation buffer)
[legs] wall_skew: no samples — bound 33 ms (ADR-0128 section 2, intra-wall)
[legs] label_delay: no samples — cap 200 ms (ADR-0129 hold — not a section IV leg)
  ok 5 [kiosk] › e2e\kiosk-shows-a-label-over-video.spec.ts:691:5 › the span from a value being submitted to it being visible (10.1s)

  8 passed (47.4s)
```

### Run 2 — verbatim

```
[decode] 2 → 27 frames in 1000ms (+25, threshold 10) across 1 element(s)
[span] iteration 0: 58 ms (submit round trip 25 ms)
[span] iteration 1: 100 ms (submit round trip 23 ms)
[span] iteration 2: 79 ms (submit round trip 14 ms)
[span] iteration 3: 63 ms (submit round trip 15 ms)
[span] iteration 4: 70 ms (submit round trip 17 ms)
[span] iteration 5: 106 ms (submit round trip 24 ms)
[span] iteration 6: 73 ms (submit round trip 16 ms)
[span] iteration 7: 226 ms (submit round trip 136 ms)
[span] iteration 8: 136 ms (submit round trip 62 ms)
[span] iteration 9: 69 ms (submit round trip 19 ms)
[span] 10 sample(s) — 58 / 63 / 69 / 70 / 73 / 79 / 100 / 106 / 136 / 226 ms
[span] p50 79 ms, p95 226 ms, max 226 ms, range 58-226 ms, spread 168 ms
[span] submit round trip: p50 23 ms, max 136 ms over 10 sample(s) — subtract it to approach the section IV span start
[legs] 13 [latency] line(s) captured during the run
[legs] overlay_draw: 12 sample(s), p50 30 ms, max 56 ms — budget 50 ms (section IV composite + render)
[legs] receive_to_decoded: no samples — no budget: a fragment of a leg, not a leg (ADR-0122)
[legs] presentation_buffer: 1 sample(s), p50 22 ms, max 22 ms — budget 200 ms (section IV presentation buffer)
[legs] wall_skew: no samples — bound 33 ms (ADR-0128 section 2, intra-wall)
[legs] label_delay: no samples — cap 200 ms (ADR-0129 hold — not a section IV leg)
  ok 5 [kiosk] › e2e\kiosk-shows-a-label-over-video.spec.ts:691:5 › the span from a value being submitted to it being visible (10.1s)
```

### The instrument, and its error bar

In-page stamps at both ends: a capture-phase one-shot `click` listener on the operator page
for `t0`, a `MutationObserver` plus two chained `requestAnimationFrame`s on the kiosk page
for `t1` — the product's own definition of *painted*
(`apps/shared/src/observability/kioskLatency.ts:135-142`). Both `Date.now()`, both Chromium
contexts of one browser on one machine.

**Stated error: ~±52 ms** (2 rAF ≈ 33 ms + click dispatch ≈ 17 ms + 2 × `Date.now()` 1 ms).
*Superseded twice — see phase 6 for the skew term, and re-review findings 2 and 3 for the
shape and the size of the rAF term.*
Phase 4a's paired calibration recovered a 300 ms injected delay as a mean of 296 ms across
ten adjacent pairs, all inside 277–340 — an observed spread of ~±32 ms, comfortably inside
the stated ceiling. **I did not re-run that calibration**; its apparatus was reverted and is
not on this branch. I take it on phase 4a's report, and say so rather than restating it as
if I had watched it.

**±52 ms against a p50 of 60–79 ms is an error bar comparable to the figure itself.** That
is a very different situation from the ±1000 ms this instrument replaced — the error is now
smaller than the thing measured rather than larger — but it is not a precise number, and the
p50 should be read as *tens of milliseconds*, never as "79".

> **Corrected at phase 6.** Three things in the two paragraphs above.
>
> 1. **"both Chromium contexts of one browser on one machine" is one machine, not one
>    clock.** The two stamps are taken in two renderer *processes*. §IV's figure is now
>    reported with a **measured** bound on the offset between them — see phase 6 below.
> 2. **"comfortably inside the stated ceiling" compared the wrong quantities.** ±32 ms is
>    the dispersion *over pairs*; the per-pair worst case is **−23/+40 ms**.
> 3. **The calibration could never have caught the error the ±52 ms line was missing.** C1
>    injects on the kiosk side, so both arms of a paired run go through the same
>    `t1(kiosk) − t0(operator)` subtraction and a constant offset cancels in the difference.
>    It was reasonable to take it on phase 4a's report; it was not the evidence the clock
>    assumption needed.

---

## `overlay_draw` exceeds its 50 ms budget — confirmed, not smoothed

**Confirmed.** Phase 4a saw a maximum of 86 ms across its two runs. I saw:

| observation | p50 | max | budget |
|---|---|---|---|
| my run 1 | 38 ms | 48 ms | 50 ms |
| my run 2 | 30 ms | **56 ms** | 50 ms |
| one-tile probe | — | **59 ms** (21, 25, 26, 59, 41, 33, 25) | 50 ms |
| two-tile probe | — | **80 ms** (33, 34, 35, 36, 62, 61, 63, 80, 49, 57, 51, 52, 40, 42) | 50 ms |

Four independent observations on this machine, three of which put the **maximum** over §IV's
*overlay composite + render* budget of 50 ms — so phase 4a's 86 ms is not an anomaly. The
**p50** is inside the budget every time (30–46 ms). This is a tail breach, on one laptop,
headless, with the whole stack on the same box; it is not by itself a verdict on the leg.
**It is a finding for #1714, and nothing here should be read as that row being clean.** The
two-tile wall is where it is worst, which is the direction that matters: real walls have
more than one tile.

---

## The #2072 reconciliation — the reasoning holds, with one correction

Spec 108 §3d wrote the prediction down before the run: *"this feature's p50 must land at or
above #2072's 555 ms."* **It did not. It landed at 79 ms, seven times below.** Per SC-004
that is a disagreement to report, not a number to average.

**Phase 4a's explanation checks out against the file.**
`tests/Integration.Tests/SystemVariables/ResolvedTextReachesItsFabTests.cs:127-134` says, in
the `PushCeilingMs` doc comment, verbatim:

> *"Phase 5 measured this exact server-side leg at **555 ms and 758 ms on a cold stack**"*

and the test that produces those figures:

- resets **three** contexts in `InitializeAsync` — SystemVariables, LayoutComposition,
  OverlayDesigner;
- creates a fresh overlay and variable, then takes **exactly one** stopwatch sample around
  **one** `SetValueAsync`.

So each figure is **the first push of that message type through Wolverine and RabbitMQ after
a reset** — cold JIT, cold EF model, cold broker topology, one sample. Mine are writes 1–10
on a path that has been carrying the same message for hours. Those are not the same
population, and a 7× gap is what a cold first message costs elsewhere in this repo.
**The consequence is a finding for #2072: its 555/758 ms cannot be read as steady state**,
and the `PushCeilingMs` comment that cites them does not say so.

**The correction.** Phase 4a also offered supporting evidence from inside its own runs —
*"the first-write penalty is visible in the runs themselves (iteration 0 slowest in 2 of
3)"*. **That does not reproduce.** In my two runs iteration 0 was 4th largest and then the
smallest. The cold-stack argument stands on the test file; the in-run evidence does not, and
citing it would be reading a pattern into ten samples.

**One caveat on "strictly contains".** #2072's `t1` is a frame arriving at a **.NET
`HubConnection` in the test process**; mine is a **paint in a browser**. The server-side
publish is common to both, and my span starts earlier (the click, before the write) and ends
later (after React and paint) — so it contains #2072's *server work*, but the two endpoints
are not the same client. The comparison is sound as an order-of-magnitude check and must not
be read as a subtraction.

---

## The two C# classes phase 4a could not run

Phase 4a argued they could not be affected (no C# in the diff, no new measurement name) and
did not run them, because building requires stopping the AppHost and that would have
destroyed the warm stack mid-measurement. **The argument was correct; it is now an
observation.** Measurement finished, stack stopped (`SmartSentinelEye.AppHost` terminated,
`dotnet count: 0`), then:

`dotnet test tests/Architecture.Tests/SmartSentinelEye.Architecture.Tests.csproj -c Release
--filter "FullyQualifiedName~KioskMeasurementContractTests|FullyQualifiedName~LatencyLegRecordTests"`

```
  Passed SmartSentinelEye.Architecture.Tests.LatencyLegRecordTests.The_record_still_distinguishes_every_state_a_leg_can_be_in [6 ms]
  Passed SmartSentinelEye.Architecture.Tests.KioskMeasurementContractTests.A_name_that_is_not_a_segment_reaches_its_own_instrument(measurement: "wall_skew", recorder: "WallSkew.Record") [9 ms]
  Passed SmartSentinelEye.Architecture.Tests.LatencyLegRecordTests.The_table_still_warns_about_itself [1 ms]
  Passed SmartSentinelEye.Architecture.Tests.KioskMeasurementContractTests.A_name_that_is_not_a_segment_reaches_its_own_instrument(measurement: "label_delay", recorder: "LabelDelay.Record") [1 ms]
  Passed SmartSentinelEye.Architecture.Tests.LatencyLegRecordTests.The_kiosk_legs_are_recorded_as_built [1 ms]
  Passed SmartSentinelEye.Architecture.Tests.LatencyLegRecordTests.The_presentation_buffer_is_not_claimed_measured_before_anyone_has_looked [< 1 ms]
  Passed SmartSentinelEye.Architecture.Tests.KioskMeasurementContractTests.The_kiosk_and_the_server_accept_the_same_measurement_names [6 ms]
  Passed SmartSentinelEye.Architecture.Tests.KioskMeasurementContractTests.The_refusal_names_every_value_it_would_have_accepted [3 ms]

Test Run Successful.
Total tests: 8
     Passed: 8
```

Worth naming what `LatencyLegRecordTests` guards: it reads §IV's table and fails if the
presentation buffer is claimed measured. It passes because **this branch changes no cell**.

## The other gates

```
pnpm typecheck                       → all three apps + typecheck:e2e clean
pnpm lint                            → eslint --max-warnings 0, all three apps clean
pnpm test                            → 54 files, 546 tests, 0 failures
pnpm exec playwright test --list     → Total: 58 tests in 28 files
```

`typecheck:e2e` exists and passes, which makes the spec file's own remark that *"`e2e/` is
type-checked by nothing (#2121)"* out of date. Minor, and stated here rather than edited into
a file this phase may not touch.

---

## Constitution §IV is unchanged, deliberately

`.specify/memory/constitution.md` is not touched by this branch. In particular:

- *Event → overlay state* still reads **recorded, not yet readable**. This branch produces a
  browser-side figure for a span that contains that leg; it does not make the leg's own
  histogram readable, which is what that cell is about.
- *Presentation buffer* still reads **recorded, not yet observed**, and
  `LatencyLegRecordTests.The_presentation_buffer_is_not_claimed_measured_before_anyone_has_looked`
  fails the build if that changes.
- *Overlay composite + render* still reads **yes**, which the `overlay_draw` tail above puts
  a question against. Not changed here.

Writing a figure into that table is a constitution edit, which the autonomous lane may not
make (ADR-0144). The items below are therefore recorded as **findings for a human**, not as
edits.

## Findings this run produced

1. **The ADR-0129 label hold does not engage on a single-tile wall.** A product defect, not a
   fixture artefact. The frame age reaches the tile only on a render `CellPage` performs, and
   below two tiles nothing performs one. Demonstrated by the paired probes above. A
   single-camera wall is a supported configuration (spec 010 FR-011: *"N=1 … renders
   identically to the pre-feature single-cell view"*), so this is not a corner case.
2. **Spec 108 US2's own acceptance scenario cannot pass on this fixture.** It asks the run to
   confirm `[latency]` lines *"for at least `overlay_draw`, `presentation_buffer` **and
   `label_delay`**"*. `label_delay` is structurally unreachable on the one-tile wall the spec
   drives. The harness reports it honestly as `no samples` — the behaviour spec 095 asked for
   — but the scenario as written is not met.
3. **`overlay_draw`'s maximum exceeds its 50 ms budget** in three of my four observations and
   in phase 4a's, worst on the two-tile wall (80 ms). The p50 stays inside.
4. **#2072's 555/758 ms are one cold first-write sample each** and should not be read as
   steady state; the `PushCeilingMs` comment that cites them does not say so.
5. **§IV's note that the *event → overlay state* row is "suspected broken for an already-open
   tile"** — written after spec 056, when the value never reached the tile — is
   **contradicted by these runs**: 40 of 40 iterations across four runs painted the new value
   on an already-open tile, in tens of milliseconds. The suspicion is spent. Recorded here;
   the row itself is a human's edit.
6. **A two-tile wall was watched aligning**, incidentally: `wall_skew` 0–2 ms over 28 samples
   against ADR-0128's 33 ms intra-wall bound. One observation, one machine, two tiles of the
   *same* clip — so it is not the presentation-buffer row's discharge, but it is the first
   time anything has read that number outside a unit test.

## What was NOT observed

Bluntly, because this issue exists precisely because a path was once recorded as measured
before anyone read its figure:

- **The 800 ms budget was not verified.** Two of six legs were spanned, worth 250 ms.
- **The label hold is missing from every figure**, and on the wall these figures come from it
  cannot be present at all.
- **No fab camera, no fab network, no 250-camera wall.** One clip, from a container on the
  same host, one or two tiles, headless Chromium, everything on one laptop. The camera → SFU
  and SFU → decode legs are not exercised in any realistic sense.
- **Nobody watched a wall with their eyes.** These are console figures from a headless run.
  #1714's other half — *"someone has watched a wall"* — is not discharged by this note.
- **CI has never run this instrument.** Both runs are local. The e2e job runs on a shared
  runner where the figures will be worse, and by design no budget is asserted there, so CI
  will record numbers and gate nothing.
- **The ±52 ms calibration was not re-run by me.** Taken on phase 4a's report.
- **A cold-stack run of this harness was not taken.** Both my runs were on a stack up ~4 h,
  and phase 4a's were warm too. Nobody has this span's *cold* figure — which is precisely the
  population #2072's 555/758 ms come from, so the one comparison that would settle §3d
  properly has still not been made.

---

# Phase 6 (ADR-0037) — review findings fixed, and the instrument re-measured

**Date:** 2026-09-09 · **Commits:** `c6f6ba78` (the instrument), and the commit carrying this
note. Test-only: `e2e/kiosk-shows-a-label-over-video.spec.ts` and `specs/108-*`. No file under
`src/` or `apps/`. `.specify/memory/constitution.md` untouched. No budget asserted.

**The pre-existing test `a tile shows an overlay label over video that is actually decoding`
is byte-identical** — the first changed line of the diff is 269, and that test ends at 189.

**Machine:** the same laptop. **A freshly booted stack**, not the ~4 h warm one phase 5 used,
which is the largest single difference between phase 5's figures and mine and is treated as
such below rather than averaged away.

## The headline: the cross-context clock offset, measured

The span subtracts `Date.now()` taken in the **operator** renderer from `Date.now()` taken in
the **kiosk** renderer. Two Chromium processes, each extrapolating `base::Time::Now()` from
its own latched tick/wall pair. The error budget listed 2 rAF, click dispatch and 2 × 1 ms of
`Date.now()` resolution, and **no term for the offset between those two clocks**.

The harness now brackets it — kiosk → operator → kiosk — so the middle read is known to have
happened between the outer two, and `δ ∈ [middle − after, middle − before]`. Seven brackets
intersected, taken before *and* after the loop. Ordering-independent, which is exactly what a
one-way probe is not.

| Run | before the loop | after the loop | zero inside? |
|---|---|---|---|
| A (cold) | `[−4, +6] ms` | `[−4, +5] ms` | yes, both |
| B | `[−3, +4] ms` | `[−4, +4] ms` | yes, both |
| C | `[−4, +4] ms` | `[−6, +4] ms` | yes, both |
| C1 run | `[−5, +4] ms` | `[−3, +4] ms` | yes, both |
| C1 run (earlier, contaminated) | `[−5, +4] ms` | `[−3, +3] ms` | yes, both |
| kiosk + wall, `--workers=1` | `[−3, +4] ms` | `[−4, +3] ms` | yes, both |

**Every one of the twelve probes returned |δ| ≤ 6 ms, and every interval contains zero.**

**Stated precisely, because the loose version is tempting and wrong: this bounds δ at the
twelve instants the probes ran, not throughout the runs.** Before-and-after brackets that
agree show no drift *between* those two instants; an excursion that departed and returned
mid-loop would be invisible to them. What the evidence supports is "δ was within [−6, +6] ms
whenever it was looked at, twice per run, across six runs including a seven-minute suite" —
not "δ ≤ 6 ms throughout".

**And it is now asserted, not merely printed** (re-review finding 1). A bracket that excludes
zero, or a pair of brackets that do not intersect, **fails the run**. Previously it printed
"EXCLUDES zero" and reported green — the one clock pathology of the three that could not fail,
where a negative elapsed already refused and a backwards kiosk clock already threw. That is
not the "figures are recorded, not gated" exemption, which is about the span against 800 ms:
this asserts the subtraction means anything at all, which is a precondition of reporting a
figure rather than a threshold on it.

**What this settles.** G1 held. It was *not* established by the evidence the spec originally
cited for it, and the difference matters: a constant −60 ms offset would have made every
figure 60 ms too small — the headroom-flattering direction — while C1 still recovered 296 of
300 exactly. The old one-way probe's `min −88 ms`, attributed at the time to round-trip
jitter, was an explanation available only under one of the two read orderings; under the
other, −88 ms would have been real skew. **6 ms is now a measured term in the printed error
(~±58 ms total), not an assumption.**

## The corrected figures — three runs, honest labels

Two figures changed name at phase 6, and both had been reading better than they were:

- **`p95` at n = 10 was the maximum.** `Math.ceil(10 × 0.95) − 1 = 9` is index 9 of 10. The
  docstring justifying ten iterations claimed it was "the second-largest sample"; it is the
  largest. The arithmetic came from `click-to-first-frame.spec.ts:488`, which takes **20**,
  where index 18 genuinely is the 19th of 20. **No p95 is now reported below n = 20.** The C1
  runs, at n = 20, print one — `p95 529 ms (the 19th of 20)` — so both directions of the fix
  were exercised on a real run.
- **`p50` at an even count picked the upper middle**, and the diff that introduced it had
  deleted the comment forbidding exactly that. The two middles are averaged again, which is
  why a p50 below carries a half.

| | **A — cold** | **B** | **C** |
|---|---|---|---|
| n | 10 | 10 | 10 |
| **p50** (mean of 5th and 6th) | **233.5 ms** | **122.5 ms** | **177.5 ms** |
| **max** | 1491 ms | 207 ms | 288 ms |
| range / spread | 108–1491 / 1383 ms | 110–207 / 97 ms | 72–288 / 216 ms |
| p95 | *not reported at n = 10* | *not reported* | *not reported* |
| submit round trip p50 / max | 152.2 / 529.8 ms | 36.0 / 89.7 ms | 79.1 / 198.8 ms |
| **p50 net of each sample's own round trip** | **102 ms** | **88.5 ms** | **82 ms** |
| skew bound | ≤ 6 ms | ≤ 4 ms | ≤ 6 ms |
| error, R = 33 ms (best case) | `[−25, +39]` | `[−23, +39]` | `[−23, +41]` |
| error, R = this run's `overlay_draw` p50 | `[−25, +58]` | `[−23, +51]` | `[−23, +46]` |
| **§IV bracket** (net floor → raw ceiling) | **[102, 233.5]** | **[88.5, 122.5]** | **[82, 177.5]** |
| `overlay_draw` p50 / max | 51.7 / **79.8** ms | 45.3 / **81.6** ms | 37.9 / **84** ms |
| `label_delay` | no samples | no samples | no samples |

**Run A was cold** — the stack had been up about five minutes. It is the cold figure nobody
had taken; phase 5 named its absence as the one comparison that would settle §3d properly.
**It does not settle §3d in #2072's favour**: 233.5 ms is still well below 555 ms, though it
is three times phase 5's warm 79 ms.

**Read across all six runs of this feature, the raw p50 lies between 79 ms and 234 ms.**

**The cause of that spread is measured, and it is not thermal state.** I wrote "thermal state"
first; it was a guess, and the data already contained the answer. Across the three phase-6
runs the **submit round trip p50 moved 152.2 → 36.0 → 79.1 ms** (maxima 529.8 / 89.7 /
198.8) while the **p50 net of each sample's own round trip barely moved: 102 → 88.5 → 82 ms**.
The head term varied roughly fourfold; the tail did not. So the spread lives almost entirely
in a term the instrument already prints, already tells the reader to subtract, and which
**§IV's span excludes by definition**.

Two consequences, and the second is the one that changes what gets quoted:

1. **The spread is not evidence the instrument is unstable.** It is the instrument resolving a
   term that genuinely varies. That strengthens the case for quoting the figure.
2. **Net-of-round-trip over-subtracts.** `responseEnd` includes the service's own processing
   of the write, which is *genuinely inside* event → overlay state. Subtracting it removes real
   span along with the head overshoot.

**So §IV's span is bracketed, not measured to a point:**

> **§IV's *event → overlay state* + *composite + render* lies between 82 ms and 234 ms on this
> machine.** The **floor** is the lowest net-of-round-trip p50 and is a floor because it
> over-subtracts server processing. The **ceiling** is the highest raw p50 and is a ceiling
> because it over-counts the browser `fetch` and the gateway hop, which precede §IV's span.
> **Quote the bracket. Neither end is the figure**, and the harness now prints this bracket
> per run rather than leaving the reader to construct it.

The reviewer checked the obvious confound and it is absent: `label_delay` reads `no samples`
in all six runs and *does* produce samples elsewhere (35–45 ms on a two-tile wall), so the
ADR-0129 hold is genuinely not in these figures and is not the spread's cause.

**What none of this licenses.** The span covers **250 ms of §IV's 800** — *event → overlay
state* (200) and *composite + render* (50). The other 400 ms of budgeted legs are **not
serial terms of it at all** (ADR-0129): they are the picture's path and enter the label's only
through the hold. 150 ms is headroom, an arithmetic remainder. Against 250 ms these p50s are
roughly 1.1× to 3× — and the hold, worth **35–45 ms measured** on a two-tile wall, is absent
from all of them. The harness now prints this arithmetic beside every set of figures, so the
`p50 against 800 ms` comparison cannot be made by accident.

## C1 — re-run, and the limit that matters more than the result

The apparatus, as tasks.md specifies it: the kiosk-side observer's stamp deferred by exactly
**300 ms** on odd iterations, `ITERATIONS` raised to 20 so ten adjacent pairs meet the same
stack. Reverted immediately; the branch carries neither change.

**Verbatim, the clean run** (an earlier attempt overlapped a second Playwright process on the
same wall and is reported below rather than dropped):

```
[span] cross-context clock skew before the loop: operator − kiosk ∈ [-5, 4] ms (|δ| ≤ 5 ms; 7 brackets, tightest round trip 10 ms; brackets agree; consistent with one shared clock)
[span] iteration 0: 154 ms (submit round trip 57 ms)
[span] iteration 1: 409 ms (submit round trip 27 ms)
[span] iteration 2: 215 ms (submit round trip 162 ms)
[span] iteration 3: 500 ms (submit round trip 75 ms)
[span] iteration 4: 112 ms (submit round trip 31 ms)
[span] iteration 5: 367 ms (submit round trip 22 ms)
[span] iteration 6: 156 ms (submit round trip 86 ms)
[span] iteration 7: 523 ms (submit round trip 171 ms)
[span] iteration 8: 145 ms (submit round trip 68 ms)
[span] iteration 9: 529 ms (submit round trip 159 ms)
[span] iteration 10: 101 ms (submit round trip 22 ms)
[span] iteration 11: 405 ms (submit round trip 47 ms)
[span] iteration 12: 124 ms (submit round trip 31 ms)
[span] iteration 13: 499 ms (submit round trip 173 ms)
[span] iteration 14: 192 ms (submit round trip 96 ms)
[span] iteration 15: 1309 ms (submit round trip 941 ms)
[span] iteration 16: 93 ms (submit round trip 13 ms)
[span] iteration 17: 388 ms (submit round trip 21 ms)
[span] iteration 18: 270 ms (submit round trip 163 ms)
[span] iteration 19: 403 ms (submit round trip 60 ms)
[span] 20 sample(s) — 93 / 101 / 112 / 124 / 145 / 154 / 156 / 192 / 215 / 270 / 367 / 388 / 403 / 405 / 409 / 499 / 500 / 523 / 529 / 1309 ms
[span] p50 318.5 ms (mean of the 10th and 11th of 20), max 1309 ms, range 93-1309 ms, spread 1216 ms
[span] p95 529 ms (the 19th of 20)
[span] cross-context clock skew after the loop: operator − kiosk ∈ [-3, 4] ms (|δ| ≤ 4 ms; 7 brackets, tightest round trip 7 ms; brackets agree; consistent with one shared clock)
  8 passed (1.2m)
```

**Three statistics, because they disagree and the disagreement is the information.**

| statistic | recovered | against |
|---|---|---|
| **PREFERRED — mean of the ten adjacent pairs, net of each sample's own round trip** | **280.3 ms** (range 233–372) | 300 |
| median(delayed) − median(undelayed) | 304.5 ms | 300 |
| mean of the ten adjacent pairs, raw | 377 ms | 300 |

**280.3 is the estimator to quote**, and it is named rather than left to a reader's choice:
three unranked numbers for one injected quantity let a later reader pick the flattering one.
It is preferred because it removes the confound the other two carry — the submit round trip,
which varies fourfold between runs and is not the injected quantity.

**The honest scale claim is therefore wider than "296".** Phase 4a's n=10 run gives 296 ms
(−1.3 %) and phase 6's n=20 run gives 280.3 ms (−6.6 %) by the same estimator. Those disagree
by five points, so **C1 supports a recovery of roughly −7 % / +2 %**, not a point estimate.

The raw mean is dragged by one pair whose delayed sample carried a **941 ms** submit round
trip — head overshoot, not the injected quantity. **The location-shift estimate recovers
304.5 ms of 300**, and I say which statistic that is rather than quoting the friendliest. The
earlier contaminated run gives 289 ms by the same statistic, so two C1 runs bracket 300 from
either side.

**Per-pair spread is much wider than phase 4a's 277–340** — mine ran 233–372 net, 133–1117
raw. Phase 4a's `±32 ms` was a dispersion over pairs on a quiet warm stack, and it does not
generalise. **The per-sample bound C1 supports is no tighter than ±40 ms even on phase 4a's
own data, and phase 6's data will not support one that tight at all.**

**And the limit that outranks all of it.** C1 injects on the **kiosk side**, so the delayed
and undelayed arms both go through the same `t1(kiosk) − t0(operator)` subtraction: **a
constant cross-context offset cancels in the difference.** C1 tests scale and linearity of the
tail. It cannot test the origin, it cannot test the head (click dispatch, Playwright's
actionability checks, the browser's `fetch`), and the spec, plan and tasks all named it as the
test for the clock assumption. That is corrected in all three.

## C2 — the arming removed, and it is loud

Iteration 3 gets no observer, and `window.__spanPaint` is deleted so the refusal is the one
predicted rather than a downstream symptom of it. **Verbatim:**

```
[span] iteration 0: 163 ms (submit round trip 68 ms)
[span] iteration 1: 114 ms (submit round trip 38 ms)
[span] iteration 2: 379 ms (submit round trip 305 ms)
[span] REFUSED — iteration 3: the kiosk observer was never armed
[span] 3 sample(s) — 114 / 163 / 379 ms
    Error: the span could not be stamped: iteration 3: the kiosk observer was never armed
    > 1270 |   expect(refusals, `the span could not be stamped: ${refusals.join('; ')}`).toEqual([]);
  1 failed
  7 passed (58.7s)
```

**The prediction holds, and one thing is better than it was.** The three samples that *did*
land are printed — they now print as they land rather than after the loop, so a run that dies
early keeps its figures instead of losing all of them. The unobserved iteration is named and
fails the run; it is not dropped.

## The other findings, and what each was reading wrong

| # | Finding | Fixed how | Direction of the old error |
|---|---|---|---|
| 1 | Cross-context skew absent from the error budget, and C1 structurally blind to it | bracketed probe, before and after the loop, as a named term | unknown sign, up to the offset; could have flattered headroom |
| 2 | `p95` at n = 10 was the maximum, and the docstring asserted the opposite | no p95 below n = 20; the docstring says why | the max was quoted as a percentile |
| 3 | "±32 ms observed" tighter than the data (277–340 = −23/+40) | states which statistic it is, the per-pair worst case, and that C1 injects into the tail | error bar quoted ~25 % tight |
| 4 | `p50` at even n picked the upper middle; the diff had deleted the comment forbidding it | the two middles are averaged | small, and toward the larger side |
| 5 | 10 × 60 s observe timeout against a 300 s test; `report()` ran only after the loop | samples print as they land; the budget is derived from time remaining | a slow run printed nothing at all |
| 6 | The submit round trip was read by arrival order and could be mispaired | keyed by the value the PUT body carried | corrupted exactly the largest samples |
| 7 | `[latency]` console JSHandles never disposed | disposed in a `finally` | leak only |

## Corrections to the record this branch already carried

- **`label_delay`'s mechanism.** Phase 4a said `frameAgeFor` returns null on a wall with no
  alignment target. It does not gate on tile count; it reads a ref during `CellPage`'s render,
  and below two tiles the settle interval that would re-render never runs. Phase 5 established
  this with paired probes and this note already recorded it — **the harness itself now prints
  it**, so a reader of the output does not have to find this file.
- **"up to 200 ms missing" is the cap, not the observation.** The hold measured **35–45 ms**
  on a two-tile wall. 200 ms is what ADR-0129 permits.
- **"250 ms of the 800", printed rather than implied.** The other 400 ms of budgeted legs are
  not serial terms of this span.

## Gates

```
pnpm typecheck (all three apps + e2e)                  → clean
pnpm test                                              → 54 files, 546 tests, 0 failures
pnpm format:check                                      → All matched files use Prettier code style!
pnpm lint                                              → eslint --max-warnings 0, all three apps clean
pnpm exec playwright test --list                       → Total: 58 tests in 28 files
--project=kiosk --project=wall --workers=1             → 33 passed (7.2m), BEFORE the re-review fixes
--project=kiosk kiosk-shows-a-label-over-video         → 8 passed, AFTER them (and one red, below)
```

`--workers=1` is stated because it is **the configuration CI runs**
(`playwright.config.ts:16`, `workers: isCI ? 1 : undefined`), not a convenience. That run's
own skew probes read `[−3, +4]` and `[−4, +3]` ms, so the bound holds across a seven-minute
suite as well as a one-minute one.

### A finding about the local default, and it predates this branch

At the **local** default worker count (3 here), `--project=kiosk --project=wall` fails
**twice out of two**: the span's realtime channel stops delivering to the kiosk page part way
through the loop and the iteration refuses with **`0 label mutation(s) were seen`** — the
value never arrives at all, so it is not an observe-budget question.

**I checked whether phase 6 caused it. It did not.** Running the *pre-phase-6* file
(`9956f7d6`) under the same load fails the same way and **sooner**:

```
[span] REFUSED — iteration 1: the value never painted on the tile within 60000 ms (0 label mutation(s) were seen)
[span] iteration 0: 933 ms (submit round trip 800 ms)
[span] 1 sample(s) — 933 ms
  1 failed
  32 passed (3.2m)
```

against phase 6's three samples before refusing. Note the ordering in that baseline output —
the **refusal prints before the sample it followed**, because the old `report()` ran entirely
after the loop. That is finding 5 visible in the wild.

This is a **concurrency interaction between the wall specs and the span's realtime channel**,
not an instrument defect and not something this branch may fix (it would be product or
shared-fixture work). It does not affect CI, which runs one worker. **Recorded because
neither phase 4a nor phase 5 ran these two projects together, so nobody had seen it.**

## The re-review fixes, and the two runs after them

Six findings, all fixed. **Findings 1, 2, 3, 5 and 6 change what is reported or asserted, not
what is measured; finding 4 adds a computed bracket.** I re-ran the span spec twice against a
freshly booted stack to confirm the new assertion path and the new lines. **I did not re-run
`--project=kiosk --project=wall --workers=1`** — no fix touches the wall specs or the shared
fixture — so that gate stands on the earlier 33-passed run and is reported as such rather than
restated as if I had watched it again.

| | re-review run 1 | **re-review run 2** |
|---|---|---|
| outcome | **failed** — see below | **8 passed** |
| n | 9 of 10 | 10 |
| raw p50 | 459 ms | 153 ms |
| **§IV bracket** | *[128.9, 459] — partial* | **[110.4, 153]** |
| skew, before / after | `[−5, +4]` / `[−4, +4]` | `[−5, +6]` / `[−3, +3]` |
| error, R = 33 (best) | `[−23, +40]` | `[−25, +40]` |
| **error, R measured** | **`[−23, +58]`** (R = 51 ms) | **`[−25, +78]`** (R = 71 ms) |
| `overlay_draw` p50 / max | 50.5 / 77.4 ms | **70.9** / 88.7 ms |

**Run 1's red is the pre-existing delivery stall, not the new assertions.** It refused with
`iteration 9: the value never painted on the tile within 60000 ms (0 label mutation(s) were
seen)` — at the **full 60 s ceiling**, so the derived budget was not the cause, and with
**zero** mutations, so nothing arrived at all. Both skew assertions passed in that same run.
It is the same signature as the kiosk+wall stall recorded above, on a stack booted minutes
earlier with submit round trips up to 667 ms. Recorded rather than re-run away.

**Finding 2 is vindicated by run 2 more sharply than by the argument for it.** That run's
`overlay_draw` p50 was **70.9 ms** — the identical 2-rAF construct on the same page. The old
line asserted that term was 33 ms. **The measured error interval for that run is
`[−25, +78]` ms, against a "±52" the old line would have printed.** 33 ms was a floor quoted
as a ceiling, and the gap is not small.

**`overlay_draw` is now over its 50 ms budget at the MEDIAN in two of the five phase-6 runs**
(51.7 and 70.9 ms) and over it at the maximum in all of them. That is a firmer finding than
phase 6 first recorded, and it is still a human's to act on.

## What phase 6 did not establish

- **CI has still never run this instrument**, and the skew bound on a shared runner is
  unmeasured. It may be larger there; the harness will print whatever it finds.
- **The hold is still absent from every figure**, because the fixture is a one-tile wall.
- **No fab camera, no fab network, nobody has watched a wall with their eyes.** #1714's
  *watched* half is untouched.
- **`overlay_draw` exceeded its 50 ms budget in all three phase-6 runs** (79.8 / 81.6 / 84 ms
  max, and p50 51.7 ms on the cold run — over budget at the *median* once, which phase 5 never
  saw). Seven independent observations across phases 4a, 5 and 6 now put its maximum over
  budget. It remains a finding for a human, not a table cell this lane may edit.
- **I cannot fully exclude that the phase-6 instrument itself costs a little.** The JSHandle
  disposal runs on the kiosk page's console handler during the run. It is ~15 disposals per
  run against samples of tens of milliseconds, and the net-of-overshoot p50s (82–102 ms) moved
  far less than the raw ones, so any effect is small — but I did not measure it, and saying so
  is cheaper than being wrong about it.

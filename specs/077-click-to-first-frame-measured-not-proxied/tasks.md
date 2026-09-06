# Tasks — Spec 077, click-to-first-frame measured not proxied

**Phase:** 3 (Tasks) · **Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #199

---

## The three declarations

### 1. Engineer: `frontend-engineer` — and its brief will probably be empty

The deliverable is one Playwright/TypeScript file in `e2e/`. No C#, no Aspire
model, no domain, no contract, no migration, no React component. `test-writer`
owns T001–T006 per ADR-0144's phase-4 split; `frontend-engineer` receives the
verbatim red output as its brief.

**This spec is the awkward case where the artifact *is* the test, so 4a and 4b
nearly collapse.** Said plainly rather than discovered at handover:

- If the harness goes green once pointed at the fixture source, the engineer's
  brief is **empty and that is the correct outcome** — the product already
  works; what was missing was the measurement. The engineer confirms and reports.
- If the measurement **breaches 3 s**, the engineer's brief is still not "make
  it pass". Editing the test to pass is forbidden by ADR-0144's phase-4 split,
  and raising the threshold is forbidden outright. The correct action is
  **park with `agent:blocked`, quote the figure and the sample spread, and open
  a finding** — the SLO belongs to spec 002 and a human changes it.
- If the harness fails for a *product* reason (the detail page stops mounting a
  viewer, the SPA transition becomes a document load and destroys the clock),
  that is a real engineer brief.

### 2. Phase 4a colour: **red** — and the red is genuine, which needs saying because a harness is where it usually is not

A measurement that has never run cannot be "observed failing" the ordinary way,
and a first run that simply passes proves nothing about the assertion. So the
red is constructed the way #198's was — `ShouldContain("fixture-video")` against
the live model, not a tautology:

**T004 runs the finished harness against `rtsp://10.0.5.98/stream`** — the
address `camera-detail.spec.ts` registers today, which nothing in the stack
serves. No frame is ever decoded, the warm-up open exhausts its budget, and the
harness fails naming the camera and the address. **That failure is the evidence,
and it is the whole point of the ordering: the harness must be seen
distinguishing "a `<video>` was mounted" from "a picture arrived" before it is
pointed at a source that serves one.**

That distinction is not hypothetical. `camera-detail.spec.ts` says so about
itself, in its own words: *"What this does NOT prove: that a picture appears …
A `<video>` is a viewer, not a picture."* T004 is the first automated thing in
this repository that would fail on the difference **while holding a stopwatch**.

**T005 then flips one constant** — the camera's address to
`FIXTURE_VIDEO_RTSP_URL` — and the same unmodified harness goes green **and
emits a figure**. One character of test-arrangement changes between the red and
the green; the assertion does not move.

**The red is a sequencing step, not a retained test.** A permanent
"an unserved camera never decodes a frame" test would spend a 90 s budget
proving an absence, and `e2e/support/cold-stack.ts` is explicit that this is the
one thing a budgeted wait must never be used for — *"widening a wait for
something that should never appear turns every failure into a stall."* The red
is observed once, quoted verbatim in the PR body, and then the address changes.

### 3. New ADR? **No.**

Every decision this design appears to make is already recorded — the SLO, the
instrument, and that CI blocks on drift, all in spec 002; and that this is not a
§IV leg, stated twice by spec 002 itself. `plan.md` §*Why this is not an ADR*
carries the table with line citations. What remains is *how to observe a decided
quantity*, which is implementation.

**One decision is nonetheless deferred to a human, and it is US-2** (narrowing
`WhepHandshakeLatencyTests`). It is P2, out of this slice, and flagged in the
spec's review gate. It is not an ADR either — it is a spec-level call — but the
lane must not take it, because removing an assertion is close enough to
ADR-0144's *"weaken a gate"* prohibition that only a human should.

---

## Preconditions and constraints

**Docker is required** for every task except T001. Boot is `dotnet run --project
src/AppHost` in run mode (ADR-0108) — **not** the integration `AspireFixture`.
`fixture-video` is composed under `isRunMode`, which the e2e stack satisfies.
**Check `C:` free space before booting; below 6 GB, stop and report.**

**No board mutation, no ADR, no constitution amendment, no `CLAUDE.md` edit.**

**Parallelism (ADR-0109).** There is almost none to have, and pretending
otherwise would be worse than admitting it: T001–T006 all edit or run the same
single new file, so the disjoint-file condition fails throughout. **T002 is the
one genuine `[P]`** — a different file, no Docker, no stack. The measurement
tasks are strictly serial because each is the input to the next. This is a
one-file spec; it fans out to nothing.

---

## US-1 (P1) — the click-to-first-frame SLO is measured through a browser

- **[T001] [P] [US-1]** Confirm the premise against the code, and record what
  you find in the PR body — do not take `spec.md` on trust, because three
  consecutive issues in this repository were filed on stale premises.
  Four checks, each one grep or one file:
  (a) `CamerasPage.tsx` renders the camera name as a `<Link to="/cameras/…">`
  and no control named Watch exists;
  (b) `CameraDetailPage.tsx` mounts `CameraViewer` unconditionally;
  (c) `FIXTURE_VIDEO_RTSP_URL` is exported from `e2e/support/live-video-wall.ts`;
  (d) the `chromium` project's `testIgnore` is `/(kiosk|wall)-.*\.spec\.ts/`, so
  a file named `click-to-first-frame.spec.ts` lands in it and nowhere else.
  *Depends on: nothing. Blocks: T003.* No Docker.

  **Done when:** all four are confirmed, or any discrepancy is reported **before**
  T003 rather than worked around inside it.

- **[T002] [P] [US-1]** Correct the stale sentence in
  `src/AppHost/Resources/clips/sim-loop.ATTRIBUTION.txt`: it says the clip is
  *"Used only by the dev-only camera-sim (gated off in CI/E2E/prod by the
  AppHost)"*, which spec 056 and spec 076 both falsified — `fixture-video`
  serves this clip in CI's e2e stack and, since #198, in the integration lane
  too. It also points at `scripts/generate-sim-loop.sh`, which does not exist;
  the script is `scripts/generate-sim-clips.sh`.
  *Depends on: nothing. Blocks: nothing.* No Docker. Genuinely `[P]` — a
  different file from everything else here.

  **Rationale, not a drive-by:** this spec's whole premise check turned on
  whether the clip reaches CI, and a file in the repository says it does not.
  A record nobody checked against what is actually happening is the defect §IV
  documents twice. Two lines.

- **[T003] [US-1]** Write `e2e/click-to-first-frame.spec.ts`, **with the camera
  registered at `rtsp://10.0.5.98/stream`** — the unserved address, deliberately,
  so T004 can observe the red. Per `plan.md` §*What the harness is, mechanically*:
  sign in as operator; register `E2E ClickToFrame ${Date.now()}`; one discarded
  warm-up open; twenty timed opens; p95 as the 19th of 20; print p50 / p95 / max
  / all samples on success **and** failure; assert `p95 < 3000`.

  Four things that must be right in the source, each with its reason in a
  comment because each has a plausible-looking wrong version:
  - the `t0` mark and the frame observation are **both inside `page.evaluate`**,
    on one `performance.now()` timebase, driven by `requestAnimationFrame` — not
    `expect.poll`, whose 100/250/500/1000 ms backoff would add up to half a
    second of always-positive error;
  - the instrument is `getVideoPlaybackQuality().totalVideoFrames > 0`,
    the same expression as `kiosk-shows-a-label-over-video.spec.ts:93-94`,
    with a note that the *sampling strategy* deliberately differs so a later
    reader does not unify them;
  - re-opens navigate **in-app** back to `/cameras`, never `page.reload()`,
    which would add an OIDC restore FR-013 does not budget;
  - **no `fetch`** — the suite's own rule, and here it is load-bearing because
    the measured quantity includes the auth hook.

  Set `test.setTimeout` explicitly with the arithmetic at the site, as
  `cold-stack.ts` requires: sign-in + `FIRST_WRITE_TIMEOUT_MS` for the
  registration + a 90 s warm-up + 20 × ~5 s ≈ **360 s**.
  *Depends on: T001. Blocks: T004.*

  **Done when:** `pnpm exec playwright test --list` shows exactly one new test,
  in the `chromium` project, with `cleanup` scheduled after it.

  **Note what that does and does not prove, because it is less than usual.**
  `e2e/` is covered by **no** typecheck, lint or format script in this
  repository — `package.json`'s `lint`, `typecheck`, `test` and `format` are all
  `--filter "./apps/**"`, and there is no root `tsconfig.json`, only
  `tsconfig.base.json`. `--list` transpiles the file, so it catches a syntax
  error and an unresolvable import; it catches **no type error at all**. Types
  inside `page.evaluate` are the exact place this bites — the callback is
  serialised to the browser and its `window` augmentation is unchecked. Write
  that boundary defensively (narrow the return to a `number | null` and assert
  it in Node) rather than trusting a compiler that never runs.

  *This is a pre-existing gap, not one to fix here.* It is worth a separate
  issue; it is not worth this spec growing a build-tooling change.

- **[T004] [US-1] — THE RED.** Boot the stack and run **only** the new file.
  It must **fail**: no frame is ever decoded from `10.0.5.98`, the warm-up open
  exhausts its budget, and the message names the camera and the address.
  **Capture the failure output verbatim** — it is quoted in the PR body and is
  the only form of this evidence a later reader can check (ADR-0139, ADR-0144).
  *Depends on: T003. Blocks: T005.* Docker.

  **Done when:** the verbatim failure is in hand and it names the address. A
  failure that says only *"Test timeout of 360000ms exceeded"* naming no locator
  is **not** the evidence — that is the exact diagnostic loss `cold-stack.ts`
  warns about, and it means the timeouts are mis-sized. Fix the sizing and
  re-observe; do not accept an opaque red.

- **[T005] [US-1] — THE GREEN, AND THE FIGURE.** Change the registered address
  to `FIXTURE_VIDEO_RTSP_URL`, imported from `e2e/support/live-video-wall.ts`.
  **Change nothing else** — not the assertion, not the threshold, not the sample
  count. Re-run. Record the printed p50 / p95 / max and the twenty samples.
  *Depends on: T004. Blocks: T006.* Docker.

  **Done when:** the test passes **and** a figure exists. Three checks on the
  figure before it is believed:
  - the warm-up open is **much** larger than the samples — if not, the MediaMTX
    path is not staying pulled and `spec.md` assumption 1 is wrong;
  - the samples do **not** cluster on 100 / 250 / 500 / 1000 ms boundaries —
    clustering means the clock leaked back into the test process;
  - the spread is roughly uniform across ~1 s, which is the fixture's 1.000 s
    GOP behaving as `spec.md` predicts.

  **If p95 ≥ 3000 ms: stop.** Do not raise the threshold, do not drop samples,
  do not re-point at a shorter-GOP clip to make the number fit. Park with
  `agent:blocked`, quote the figure and the spread, and report it as a finding
  against spec 002's FR-013.

- **[T006] [US-1]** Re-run T005 once, unchanged. A measurement run is not
  believed in this repository until it has been repeated — the first run after
  machine churn looks exactly like a regression. Record **both** figures.
  *Depends on: T005. Blocks: nothing.* Docker.

  **Done when:** two independent p95 figures exist and both are below 3000 ms.
  If they disagree materially, both go in the PR body and the disagreement is
  the finding.

---

## US-2 (P2) — out of this slice, deliberately

- **[T007] [US-2]** **Do not do this without a human's word.** Decide the
  disposition of
  `tests/Integration.Tests/StreamDistribution/WhepHandshakeLatencyTests.cs`,
  whose `p95 < 3000 ms` assertion is against a quantity spec 002 budgets inside
  a ≤ 200 ms term, and which therefore cannot fail for the reason it exists.
  Three options are laid out in `spec.md` §US-2.

  Its class doc-comment must be corrected **whatever is decided**, because after
  T005 it no longer describes the world: it says the browser-side negotiation
  *"is deferred to a headless-browser harness"*, and the harness will exist.

  *Depends on: T005 and an explicit human decision. Blocks: nothing.*

---

## Phase 5 (Verify) — what the note must contain

Not "tests green". Per `spec.md` §*Independent end-to-end test procedure*: the
two p95 figures with their sample spreads, the warm-up open's time beside them,
and **a person having watched a picture appear** in management-web at
`localhost:5173`. The harness says how long it took; only a person says it was
a picture, and neither claim substitutes for the other.

The PR body's **Latency budget impact** section reads
`N/A — not on the event-to-overlay path`, with the click-to-first-frame figure
cited beneath it as spec 002 FR-013's SLO. **No §IV leg is claimed, and no cell
of the §IV table moves.** A reviewer seeing a latency figure in a PR body should
be able to tell in one line which budget it belongs to; this is that line.

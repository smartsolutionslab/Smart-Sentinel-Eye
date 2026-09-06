import { test, expect, type Locator, type Page } from '@playwright/test';
import { signInAsOperator } from './support/sign-in';
import { FIRST_WRITE_TIMEOUT_MS } from './support/cold-stack';
import { FIXTURE_VIDEO_RTSP_URL } from './support/live-video-wall';

/**
 * Spec 077 — click-to-first-frame, measured through a browser rather than
 * proxied by an HTTP round-trip.
 *
 * <para>
 * <b>The quantity is spec 002 FR-013</b>: "click in UI → first decoded frame in
 * `<video>` ≤ 3 seconds p95". Until now the only automated check of it was
 * `WhepHandshakeLatencyTests`, which times `POST /streams/authorize` — a term
 * spec 002 budgets inside its own ≤ 200 ms lookup, asserted against 3000 ms. It
 * is not wrong about anything; it simply cannot fail for the reason it exists.
 * This one can.
 * </para>
 *
 * <para>
 * <b>This is NOT one of constitution §IV's six legs.</b> §IV budgets event
 * arrival → overlay rendered ≤ 800 ms, which begins ticking <i>after</i> the
 * first frame — spec 002 says so twice (`spec.md:284-286`, `plan.md:68`). No
 * cell of that table moves because of anything printed here, and §VII's
 * dashboard obligation does not attach to this figure.
 * </para>
 *
 * <h3>The instrument, and the sampling strategy, are two separate choices</h3>
 *
 * <para>
 * The <i>instrument</i> is `getVideoPlaybackQuality().totalVideoFrames > 0` —
 * the same expression as `kiosk-shows-a-label-over-video.spec.ts`, and for the
 * same stated reason: it counts frames the decoder actually produced, unlike
 * `currentTime`, which advances over a stalled track. `connectionState ===
 * 'connected'` is a transport fact and would contradict FR-013 outright (#2111
 * is open because a tile reports Live off transport state while showing black).
 * </para>
 *
 * <para>
 * The <i>sampling strategy</i> deliberately differs from that file's, and a
 * later reader should not unify them. `readDecode` returns a snapshot for
 * `expect.poll` to sample, which is right for "is the picture moving" and wrong
 * for a stopwatch: `expect.poll` backs off 100 / 250 / 500 / 1000 ms, adding up
 * to half a second of always-positive quantisation — a sixth of the budget
 * being measured. So <b>the clock lives in the page</b>: `t0` is stamped by the
 * click event itself and a `requestAnimationFrame` loop reads the frame counter
 * on the same `performance.now()` timebase, reaching the true one-frame floor
 * (40 ms at 25 fps).
 * </para>
 *
 * <para>
 * The rAF loop also survives two things an `addEventListener('loadeddata')`
 * would not: the SPA route change (a `react-router` `<Link>`, so `window` and
 * the running loop persist), and a `<video>` that does not exist at `t0` —
 * `CameraDetailPage` mounts the viewer only after `useGetCameraQuery` resolves,
 * which after the warm-up open is a warm-cache read rather than a round trip
 * (the exclusion is named in full at `measureOneOpen`). If the route change ever
 * becomes a document load, the execution context dies and the evaluate below
 * rejects loudly rather than silently falling back to a coarser clock.
 * </para>
 *
 * <h3>Why asserting is defensible, and why 3000 ms is not a number to raise</h3>
 *
 * <para>
 * <b>`playwright.config.ts:15` sets `retries: isCI ? 2 : 0`, and what that buys
 * is not repeat-before-believing.</b> A <i>red</i> does mean three independent
 * 20-open runs — 60 samples across three browser sessions — each measured a p95
 * above the budget. But retries fire only on failure, so the <i>pass</i>
 * criterion is `min(p95 over up to 3 runs) < 3000`: a p95 that breaches on two
 * runs out of three is reported flaky and the job exits 0. The retry count
 * hardens the red and biases the green, which is the opposite of the rule.
 * Locally `retries: 0`, so a local run is a single sample and the framing does
 * not apply to it at all. <b>The repeat obligation is discharged by running the
 * measurement a second time by hand and recording both figures</b> (spec 077
 * T006), not by the retry count. Do not remove the retries as tidy-up, and do
 * not read them as repetition either.
 * </para>
 *
 * <para>
 * <b>The fixture's keyframe interval belongs beside the figure, and until this
 * revision the sampling could not say what it cost.</b> `sim-loop.mp4` is
 * H.264 Constrained Baseline 1280×720, 25 fps, GOP 25 — a 1.000 s keyframe
 * interval, confirmed with `ffprobe` (IDRs at 0.000 / 1.000 / 2.000 s) — served
 * `-c copy`, so the SFU passes it through unchanged. A WebRTC receiver cannot
 * decode before an IDR, and spec 077 predicted that a viewer joining at a
 * uniformly random phase would therefore pay 0–1000 ms (mean 500, p95 ≈ 950)
 * per open, about a third of the budget.
 * </para>
 *
 * <para>
 * <b>The first measurement did not refute that prediction. It could not test
 * it, because the samples were not independent.</b> `measureOneOpen` is a closed
 * loop — click, wait for the frame, navigate back, click again — and the frame it
 * waits for is by construction an exact IDR instant. So each click landed at a
 * <i>fixed</i> offset from an IDR. Writing `P` for the 1.000 s GOP, `S` for the
 * setup cost and `N` for the un-timed overhead between the frame and the next
 * click (back-click, the two `expect`s, the arming round-trip, Playwright
 * actionability), every sample from the second on was
 * `elapsed = S + ((P − (N + S) mod P) mod P)`, which collapses to `P − N`
 * whenever `N + S < P`: constant, and <i>independent of S</i>. Twenty such
 * samples land in a tight band whether or not the IDR term is paid, so the
 * 107 ms band (737–844 ms) was that term's prediction rather than its
 * refutation. The decisive evidence is in the figures themselves — p95 =
 * 843 / 843 / 856 ms across three runs, three sessions, three cameras. An
 * independently-sampled product cost does not reproduce to the millisecond;
 * `P − N` does, because `N` is Playwright's own back-navigation overhead and
 * `1000 − 843 = 157 ms` is an ordinary value for it.
 * </para>
 *
 * <para>
 * <b>The second consequence was worse than a wrong number: the harness was blind
 * to product regression inside a whole GOP.</b> `elapsed = P − N` holds for any
 * `S` below ~800 ms and then steps to `2P − N`, so `S` growing from 200 ms to
 * 700 ms would not have moved the printed figure at all. Nor was the reported
 * p95 FR-013's population: an operator clicks at a random phase, and the harness
 * always clicked at the same one.
 * </para>
 *
 * <para>
 * <b>`DECORRELATION_WINDOW_MS` is the fix, and it is also the experiment that
 * settles the question.</b> Each open now waits a uniformly random 0–1000 ms
 * before the click, so the click phase is uniform over the source's GOP and the
 * samples are independent draws. The predictions were written down before the
 * re-run: if the IDR term is paid, the spread reopens toward ~1000 ms and p95
 * rises to roughly `S + 950`; if it genuinely is not, the spread stays ~150 ms
 * and p95 stays near 843.
 * </para>
 *
 * <para>
 * <b>The re-run settled it: the IDR term is paid, in full.</b> Two 20-open runs
 * on a warm stack (2026-09-06). `elapsed + delay` came out constant modulo
 * <i>exactly</i> 1000 ms — two branches about a GOP apart, which is the source
 * quantising the join and nothing else. The spread reopened to 893 ms and
 * 1177 ms, and p95 rose from the phase-locked 843 ms to <b>1158 ms and
 * 1420 ms</b>, close to the `S + 950` the model predicts. Spec 077's original
 * prediction was right; the "refutation" was the artifact. The old figure is
 * explained too: `1000 − 843 = 157 ms` was `N` before this file added a
 * `waitForTimeout` hop to the loop, and `N` now measures 226–344 ms.
 * </para>
 *
 * <para>
 * <b>`S` — the setup cost the product actually owns, and the only term product
 * work can move — is ≈ 290 ms, and nothing had ever measured it.</b> Two
 * estimators agree: the smallest of the 40 samples is 282 ms, a direct upper
 * bound since `elapsed = S + IDR wait` and the wait is non-negative; and the
 * pooled mean of 789 ms sits ~500 ms above `S` when the phase is uniform over a
 * 1 s GOP, giving ≈ 289 ms. So of a 1158–1420 ms p95, roughly a quarter is the
 * product and the rest is the fixture's keyframe interval. <b>Read a future
 * regression off the minimum sample, not off p95</b>: p95 moves with the source,
 * the minimum moves with `S`.
 * </para>
 *
 * <para>
 * <b>If the p95 breaches: that is a finding, not a threshold to adjust.</b> The
 * SLO belongs to spec 002 and a human changes it (ADR-0144; #2119 is open
 * because a budget drifted by being fitted to what was measured).
 * </para>
 */

/**
 * Where the camera points, and <b>the only line that changed between the red and
 * the green</b> (spec 077 T004 → T005).
 *
 * <para>
 * The red was `rtsp://10.0.5.98/stream` — the address `camera-detail.spec.ts`
 * registers, which nothing in the stack serves. That file admits in its own
 * words what it cannot tell you: "A `<video>` is a viewer, not a picture." This
 * harness failed on exactly that difference while holding a stopwatch, naming
 * the camera and the address, which is the only thing that makes the figure
 * below mean anything. One constant moved; the assertion did not.
 * </para>
 *
 * <para>
 * Imported rather than spelled out, from the module that owns it (spec 056,
 * #198). A host and port written into a test is a second thing to keep true, and
 * when it rots the viewer renders "WHEP returned 404" and looks like a broken
 * product rather than a broken fixture. The constant honours an
 * `E2E_FIXTURE_VIDEO_RTSP_URL` override, so a stack that names the container
 * differently needs no code change here.
 * </para>
 */
const CAMERA_RTSP_URL = FIXTURE_VIDEO_RTSP_URL;

/** Mirrors `WhepHandshakeLatencyTests`: 20 sequential opens in the warm regime. */
const SAMPLE_COUNT = 20;

/**
 * The width of the per-open wait that decorrelates the click from the source's
 * GOP — one full keyframe interval of `sim-loop.mp4`.
 *
 * <para>
 * <b>Not a workaround, and not a settle-down sleep.</b> The frame each open
 * waits for is an exact IDR instant, so without a random wait before the next
 * click every click lands at the same offset from an IDR and every sample after
 * the first is `1000 ms − (Playwright's own navigation overhead)` — a constant
 * that does not move when the product gets slower. Drawing the wait uniformly
 * from one whole keyframe interval makes the click phase uniform, which is both
 * FR-013's actual population (an operator clicks whenever they click) and the
 * only condition under which the observed spread says anything about the IDR
 * term. See the phase-lock paragraphs in this file's header.
 * </para>
 */
const DECORRELATION_WINDOW_MS = 1000;

/**
 * The seed for those waits. Printed with the figure, and pinnable with
 * `E2E_CLICK_TO_FRAME_SEED`, so a surprising run can be replayed exactly rather
 * than argued about.
 */
const DECORRELATION_SEED = readSeed(process.env.E2E_CLICK_TO_FRAME_SEED);

/** Spec 002 FR-013. Not a number this lane may raise. */
const P95_BUDGET_MS = 3000;

/**
 * The discarded first open.
 *
 * <para>
 * It absorbs MediaMTX path creation, the RTSP dial to `fixture-video` and the
 * health sweep — none of which recur, and none of which FR-013's warm-regime SLO
 * covers. `WhepHandshakeLatencyTests` warms the OIDC cache for the same reason.
 * <b>Its time is printed and never asserted</b>, because if it is not much
 * larger than the samples that follow then the MediaMTX path is not staying
 * pulled between opens and the whole figure measures something else.
 * </para>
 */
const WARM_UP_BUDGET_MS = 90_000;

/**
 * The budget for one timed open.
 *
 * <para>
 * Over three times the SLO on purpose: a breach must be <i>measured and
 * printed</i>, not turned into a timeout that reports no number. An open that
 * cannot produce a frame in ten seconds is broken rather than slow, and the
 * failure below says which sample and against which address.
 * </para>
 */
const SAMPLE_BUDGET_MS = 10_000;

interface ClickToFirstFrameClock {
  /** `performance.now()` at the click, or null before it. */
  t0: number | null;
  /** Milliseconds from the click to the first produced frame, or null. */
  elapsed: number | null;
}

interface ClockWindow {
  __clickToFirstFrame?: ClickToFirstFrameClock;
}

/**
 * Arms the in-page clock immediately before the click.
 *
 * <para>
 * <b>`t0` is stamped by the click event, not by the test process.</b> Playwright
 * runs actionability checks (visible, stable, receives events) before it
 * dispatches a click; a `t0` taken in Node before `click()` would fold those
 * into every sample. A capture-phase one-shot listener stamps the moment the
 * gesture actually lands, on the same timebase as the frame observation.
 * </para>
 */
async function armClickToFrameClock(page: Page): Promise<void> {
  await page.evaluate(() => {
    const state: ClickToFirstFrameClock = { t0: null, elapsed: null };
    const clockWindow = window as unknown as ClockWindow;
    clockWindow.__clickToFirstFrame = state;

    document.addEventListener(
      'click',
      () => {
        state.t0 = performance.now();
      },
      { capture: true, once: true },
    );

    const tick = (): void => {
      // A later open supersedes this loop; without this check every arming
      // leaves another rAF loop running for the life of the page.
      if (clockWindow.__clickToFirstFrame !== state) return;
      if (state.elapsed !== null) return;

      if (state.t0 !== null) {
        const video = document.querySelector('video');
        const produced =
          video !== null && typeof video.getVideoPlaybackQuality === 'function'
            ? video.getVideoPlaybackQuality().totalVideoFrames
            : 0;

        if (produced > 0) {
          state.elapsed = performance.now() - state.t0;
          return;
        }
      }

      requestAnimationFrame(tick);
    };

    requestAnimationFrame(tick);
  });
}

/**
 * Waits, in the page, for the armed clock to resolve.
 *
 * <para>
 * Returns null when the budget expires without a frame. The deadline is a
 * `setTimeout` rather than a rAF comparison so it fires even if the loop is
 * throttled — an rAF-only deadline that never runs would surface as an opaque
 * test timeout naming no locator, which is the diagnostic loss `cold-stack.ts`
 * warns about.
 * </para>
 *
 * <para>
 * <b>`e2e/` is type-checked by nothing (#2121)</b> — no root `tsconfig.json`,
 * and every `package.json` script filters to `./apps/**`. `--list` transpiles
 * this file and checks no type in it, and the `page.evaluate` boundary is where
 * that bites hardest. So what comes back is validated here rather than trusted.
 * </para>
 */
async function awaitFirstFrame(page: Page, budgetMilliseconds: number): Promise<number | null> {
  const raw: unknown = await page.evaluate(async (budget: number) => {
    const state = (window as unknown as ClockWindow).__clickToFirstFrame;
    if (state === undefined) return { armed: false, elapsed: null };

    const elapsed = await new Promise<number | null>((resolve) => {
      const deadline = window.setTimeout(() => resolve(null), budget);
      const check = (): void => {
        if (state.elapsed !== null) {
          window.clearTimeout(deadline);
          resolve(state.elapsed);
          return;
        }
        requestAnimationFrame(check);
      };
      check();
    });

    return { armed: true, elapsed };
  }, budgetMilliseconds);

  const reading = raw as { armed?: unknown; elapsed?: unknown } | null;

  // The clock is gone only if the execution context was replaced — a full
  // document load where an SPA transition was assumed. Loud, because the
  // alternative is a silently coarser measurement.
  if (reading === null || reading.armed !== true) {
    throw new Error(
      'the in-page click-to-first-frame clock was not found after the click. The route ' +
        'change is no longer a client-side SPA transition, so the clock this harness ' +
        'depends on was destroyed. The figure cannot be measured this way any more.',
    );
  }

  const { elapsed } = reading;
  if (elapsed === null) return null;
  if (typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0) {
    throw new Error(`the in-page clock returned ${JSON.stringify(elapsed)}, which is not an elapsed time`);
  }
  return elapsed;
}

/**
 * One open: click the camera's name, resolve on the first frame its decoder
 * produces, and return to the list.
 *
 * <para>
 * <b>In-app navigation back, never `page.reload()`.</b> A reload re-runs the
 * OIDC restore, a term FR-013 does not budget. Leaving the detail page unmounts
 * `CameraViewer` and closes the peer connection, so the next open is a fresh
 * WHEP negotiation against an already-pulled MediaMTX path.
 * </para>
 *
 * <para>
 * <b>One term of FR-013 is excluded, and the exclusion is named here rather than
 * left to be discovered.</b> Nineteen of the twenty samples run against warm RTK
 * Query caches: neither `useGetCameraQuery` (`cameras.api.ts:185`) nor
 * `useGetStreamQuery` (`streams.api.ts:22`) sets `keepUnusedDataFor` or
 * `refetchOnMountOrArgChange`, so RTK's default 60 s cache is still warm from the
 * previous open and `whepUrl` resolves without a round trip. Spec 002 budgets
 * ≤ 200 ms of <i>lookup</i> inside the 3 s; the samples below therefore measure
 * the rest of the journey, not all of it. The discarded warm-up is the only open
 * that pays the lookup, and its time is printed.
 * </para>
 */
async function measureOneOpen(
  page: Page,
  link: Locator,
  budgetMilliseconds: number,
  decorrelationDelayMilliseconds: number,
): Promise<number | null> {
  // Actionability is paid here, before the clock is armed.
  await expect(link).toBeVisible();

  // Breaks the phase lock between the click and the source's keyframe interval.
  // The frame that ended the previous open was an IDR instant, so without this
  // wait every click lands at the same offset from the next one and the samples
  // are serially dependent — see DECORRELATION_WINDOW_MS. Untimed on purpose: it
  // is paid before the clock is armed.
  await page.waitForTimeout(decorrelationDelayMilliseconds);

  await armClickToFrameClock(page);
  await link.click();

  const elapsed = await awaitFirstFrame(page, budgetMilliseconds);

  await page.getByRole('link', { name: /^back to cameras$/i }).click();
  await expect(page.getByRole('heading', { name: 'Cameras', exact: true })).toBeVisible();

  return elapsed;
}

test('an operator clicking a camera sees a decoded frame inside the click-to-first-frame budget', async ({ page }) => {
  // Sized here, with the arithmetic, as `cold-stack.ts` requires. A ceiling
  // costs a passing run nothing: sign-in (~15 s) + the camera registration,
  // which is the run's first write of its kind (FIRST_WRITE_TIMEOUT_MS, 90 s)
  // + the discarded warm-up open (90 s) + 20 samples each bounded by
  // SAMPLE_BUDGET_MS plus a navigation (~12 s) plus a decorrelation wait of up
  // to DECORRELATION_WINDOW_MS each (~10 s over the run) ≈ 450 s.
  test.setTimeout(480_000);

  // The `E2E ` prefix is what `retire-e2e-cameras.teardown.ts` sweeps on; a
  // name without it survives the run. `Date.now()` because the e2e database is
  // shared and long-lived.
  const cameraName = `E2E ClickToFrame ${Date.now()}`;

  await signInAsOperator(page);

  // Registered through the dialog, exactly as an operator would. **No `fetch`
  // appears in this file** — the suite's own rule, and load-bearing here rather
  // than merely hygienic: the quantity being measured includes the MediaMTX
  // external-auth hook against this operator's bearer token, so a harness that
  // minted its own token would time a different journey than FR-013 budgets.
  await page.getByRole('button', { name: /register camera/i }).click();
  await page.locator('#register-camera-name').fill(cameraName);
  await page.locator('#register-camera-url').fill(CAMERA_RTSP_URL);
  await page.getByRole('button', { name: /^register$/i }).click();

  const cameraLink = page.getByRole('link', { name: cameraName, exact: true });
  await expect(cameraLink).toBeVisible({ timeout: FIRST_WRITE_TIMEOUT_MS });

  // No decorrelation wait on the warm-up: it is discarded, and it is the one
  // open that pays the cold terms this figure deliberately excludes.
  const warmUpMilliseconds = await measureOneOpen(page, cameraLink, WARM_UP_BUDGET_MS, 0);

  report(`camera "${cameraName}" at ${CAMERA_RTSP_URL}`);
  report(
    warmUpMilliseconds === null
      ? `warm-up open (discarded): NO FRAME within ${WARM_UP_BUDGET_MS} ms`
      : `warm-up open (discarded): ${warmUpMilliseconds.toFixed(0)} ms`,
  );

  expect(
    warmUpMilliseconds,
    `no frame was ever decoded for camera "${cameraName}" at ${CAMERA_RTSP_URL} within ` +
      `${WARM_UP_BUDGET_MS} ms. The page mounted a <video>, which proves only that the viewer ` +
      'is there; nothing decoded a picture out of it. Either the address serves no stream, or ' +
      'the SFU could not pull it.',
  ).not.toBeNull();

  const drawPhase = createRandom(DECORRELATION_SEED);
  const samples: Array<number | null> = [];
  const delays: number[] = [];
  for (let sample = 0; sample < SAMPLE_COUNT; sample++) {
    const delay = Math.floor(drawPhase() * DECORRELATION_WINDOW_MS);
    delays.push(delay);
    samples.push(await measureOneOpen(page, cameraLink, SAMPLE_BUDGET_MS, delay));
  }

  // Printed before any assertion, so the numbers survive a red.
  report(`samples (ms): ${samples.map((value) => (value === null ? 'none' : value.toFixed(0))).join(', ')}`);
  // Printed beside them, because a sample is only interpretable together with
  // the phase it was drawn at, and the seed is what makes the run replayable.
  report(`decorrelation seed = ${DECORRELATION_SEED}; pre-click delays (ms): ${delays.join(', ')}`);

  const missing = samples.findIndex((value) => value === null);
  expect(
    missing,
    `open #${missing + 1} of ${SAMPLE_COUNT} decoded no frame for camera "${cameraName}" at ` +
      `${CAMERA_RTSP_URL} within ${SAMPLE_BUDGET_MS} ms`,
  ).toBe(-1);

  const sorted: number[] = [];
  for (const value of samples) if (value !== null) sorted.push(value);
  sorted.sort((left, right) => left - right);

  // Same index arithmetic and the same reasoning as WhepHandshakeLatencyTests:
  // p95 is the 19th of 20 entries.
  const p95 = at(sorted, Math.ceil(SAMPLE_COUNT * 0.95) - 1);
  const p50 = at(sorted, SAMPLE_COUNT / 2);

  report(
    `p50 = ${p50.toFixed(0)} ms, p95 = ${p95.toFixed(0)} ms, max = ${at(sorted, sorted.length - 1).toFixed(0)} ms, ` +
      `budget = ${P95_BUDGET_MS} ms (spec 002 FR-013)`,
  );
  // The keyframe term, stated wherever the figure is, and printed from the
  // samples so it cannot drift away from them. The inference below is only
  // available because the click phase is randomised: under a phase-locked loop a
  // tight band is what the term predicts, not evidence against it.
  report(
    `spread = ${(at(sorted, sorted.length - 1) - at(sorted, 0)).toFixed(0)} ms across ${SAMPLE_COUNT} samples, ` +
      "each clicked at a phase drawn uniformly from the source's 1.000 s keyframe interval " +
      `(seed ${DECORRELATION_SEED}). A receiver cannot decode before an IDR, so if that wait is ` +
      'being paid the samples spread across ~1000 ms and p95 sits about 950 ms above the minimum ' +
      'sample; if it is not, the spread stays small and p95 sits near the minimum. Spec 002 budgets ' +
      'the term as "≤ 1 s decoder warmup". Read nothing from the spread if the pre-click delay is ' +
      'ever removed.',
  );
  report(
    `minimum sample = ${at(sorted, 0).toFixed(0)} ms. With the phase uniform over the GOP, the smallest ` +
      `of ${SAMPLE_COUNT} draws is the closest this harness gets to S alone — the SPA route change, the ` +
      'WHEP POST, ICE, DTLS and first RTP, with the IDR wait near zero. S is the term product work can ' +
      'move; the rest of the figure belongs to the source.',
  );

  expect(
    p95,
    `click-to-first-frame p95 = ${p95.toFixed(0)} ms against spec 002 FR-013's ${P95_BUDGET_MS} ms. ` +
      'This is a finding to report against FR-013, not a threshold to raise (ADR-0144).',
  ).toBeLessThan(P95_BUDGET_MS);
});

function report(line: string): void {
  console.log(`[click-to-first-frame] ${line}`);
}

/**
 * Reads the decorrelation seed, defaulting to the wall clock so successive runs
 * draw different phases. Nothing type-checks `e2e/` (#2121), so an unparseable
 * override is refused here rather than becoming a silent `NaN` seed and, through
 * it, a run whose delays are every one of them zero.
 */
function readSeed(configured: string | undefined): number {
  if (configured === undefined) return Date.now() >>> 0;
  const parsed = Number(configured);
  if (!Number.isInteger(parsed) || parsed < 0) {
    throw new Error(`E2E_CLICK_TO_FRAME_SEED=${JSON.stringify(configured)} is not a non-negative integer`);
  }
  return parsed >>> 0;
}

/**
 * A seeded PRNG (mulberry32). `Math.random()` would decorrelate the phase just
 * as well and would leave a surprising figure unreproducible; the seed is
 * printed beside the samples so the exact run can be replayed.
 */
function createRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let drawn = Math.imul(state ^ (state >>> 15), 1 | state);
    drawn = (drawn + Math.imul(drawn ^ (drawn >>> 7), 61 | drawn)) ^ drawn;
    return ((drawn ^ (drawn >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Reads one entry of the sorted samples, refusing an out-of-range index rather
 * than handing an `undefined` on to `toFixed` and reporting `NaN ms` as a
 * measurement. Nothing type-checks `e2e/` (#2121), so the check is here.
 */
function at(sorted: readonly number[], index: number): number {
  const value = sorted[index];
  if (value === undefined) {
    throw new Error(`sample index ${index} is out of range for ${sorted.length} samples`);
  }
  return value;
}

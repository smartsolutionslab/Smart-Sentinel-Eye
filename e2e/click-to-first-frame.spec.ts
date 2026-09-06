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
 * `CameraDetailPage` mounts the viewer only after its RTK Query resolves. If
 * the route change ever becomes a document load, the execution context dies and
 * the evaluate below rejects loudly rather than silently falling back to a
 * coarser clock.
 * </para>
 *
 * <h3>Why asserting is defensible, and why 3000 ms is not a number to raise</h3>
 *
 * <para>
 * <b>`playwright.config.ts:15` sets `retries: isCI ? 2 : 0`, and that is part of
 * this measurement's design, not incidental config.</b> A red therefore means
 * three independent 20-open runs — 60 samples across three browser sessions —
 * each measured a p95 above the budget. That is this repository's
 * repeat-before-believing rule, wired in already and costing a green run
 * nothing. Do not remove the retries as tidy-up.
 * </para>
 *
 * <para>
 * <b>The fixture's keyframe interval belongs beside the figure, and what it is
 * observed to cost is not what it was predicted to cost.</b> `sim-loop.mp4` is
 * H.264 Constrained Baseline 1280×720, 25 fps, GOP 25 — a 1.000 s keyframe
 * interval, confirmed with `ffprobe` (IDRs at 0.000 / 1.000 / 2.000 s) — served
 * `-c copy`, so the SFU passes it through unchanged. A WebRTC receiver cannot
 * decode before an IDR, and spec 077 predicted that a viewer joining at a
 * uniformly random phase would therefore pay 0–1000 ms (mean 500, p95 ≈ 950)
 * per open, about a third of the budget.
 * </para>
 *
 * <para>
 * <b>The first measurement refutes that, and the refutation is the reason the
 * samples are all printed rather than only the percentiles.</b> Twenty opens
 * landed inside a 107 ms band (737–844 ms). Twenty draws from a uniform
 * 0–1000 ms distribution do not do that, so no per-open random-phase IDR wait is
 * present in this path — the SFU is evidently not making each joining reader
 * wait for the source's next keyframe. The mechanism was not measured and is not
 * asserted here; what is asserted is the observation. <b>If a future run's
 * samples spread across ~1 s instead of clustering, that term has appeared and
 * the figure must be read with it</b>, which is what this paragraph exists to
 * make checkable rather than assumed.
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
 * WHEP negotiation against an already-pulled MediaMTX path — which is exactly
 * the quantity FR-013 names.
 * </para>
 */
async function measureOneOpen(page: Page, link: Locator, budgetMilliseconds: number): Promise<number | null> {
  // Actionability is paid here, before the clock is armed.
  await expect(link).toBeVisible();

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
  // SAMPLE_BUDGET_MS plus a navigation (~12 s) ≈ 435 s.
  test.setTimeout(450_000);

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

  const warmUpMilliseconds = await measureOneOpen(page, cameraLink, WARM_UP_BUDGET_MS);

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

  const samples: Array<number | null> = [];
  for (let sample = 0; sample < SAMPLE_COUNT; sample++) {
    samples.push(await measureOneOpen(page, cameraLink, SAMPLE_BUDGET_MS));
  }

  // Printed before any assertion, so the numbers survive a red.
  report(`samples (ms): ${samples.map((value) => (value === null ? 'none' : value.toFixed(0))).join(', ')}`);

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
  // The keyframe term, stated wherever the figure is — and stated as the
  // measurement rather than as the prediction, because the prediction was wrong
  // the first time it was checked. Printed from the samples so it cannot drift
  // away from them.
  report(
    `spread = ${(at(sorted, sorted.length - 1) - at(sorted, 0)).toFixed(0)} ms across ${SAMPLE_COUNT} samples. ` +
      'The source (sim-loop.mp4) has a 1.000 s keyframe interval and a receiver cannot decode ' +
      'before an IDR, so a per-open random-phase wait for one would show up here as a spread ' +
      'approaching 1000 ms. A spread far below that means no such term is being paid; a spread ' +
      'near it means about a third of this figure is a property of the source rather than of ' +
      'the product, and spec 002 budgets that as "≤ 1 s decoder warmup".',
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

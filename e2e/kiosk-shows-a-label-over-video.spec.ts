import { test, expect, type Page } from '@playwright/test';
import { signInToKiosk } from './support/kiosk-session';
import { signInAsOperator } from './support/sign-in';
import { isDecodeOngoing, readLiveVideoWall } from './support/live-video-wall';

/**
 * Spec 056 US1 — the product's central behaviour, asserted for the first time.
 *
 * <para>
 * <b>A label over live video is what the kiosk is for, and nothing in this
 * repository has ever checked one.</b> The overlay fixtures register a camera
 * at an address nothing serves, so their tiles render `WHEP returned 404` and
 * create no receiver; the scenario simulator has real video but nothing asserts
 * against it. A tile that draws its label <i>only when the video fails</i>
 * therefore passes the entire suite.
 * </para>
 *
 * <para>
 * <b>Both halves, on the same tile, in one test.</b> Split across two, the
 * suite could stay green while the product does not work — which is exactly the
 * state this file exists to end.
 * </para>
 */

/**
 * How long to wait for the first decoded frame. Stated, not discovered.
 *
 * <para>
 * <b>Longer than it looks like it needs, because nothing waits for this chain.</b>
 * Between the seed and this assertion the whole path must come up: the fixture
 * source's FFmpeg publishing, stream-distribution pushing the path into the SFU,
 * the SFU's RTSP pull, WHEP negotiation, and a first decode. The stack-readiness
 * script waits for the web apps, the ports and a gateway 401 — none of that.
 * The seeds in this same run were given 90 s for a single write on a cold
 * service; this is a longer chain and had a third of the budget.
 * </para>
 */
const FIRST_FRAME_TIMEOUT_MS = process.env['CI'] !== undefined ? 90_000 : 60_000;

/** The gap between decode samples, and the frames the second must add. */
const SAMPLE_GAP_MS = 1_000;

/**
 * The clip runs at 25 fps, so a healthy second delivers about 25 frames. Ten
 * clears a slow runner comfortably while still rejecting a stall.
 */
const MINIMUM_FRAMES_PER_SAMPLE = 10;

interface DecodeReading {
  /**
   * Frames per `<video>` element, in document order.
   *
   * <para>
   * <b>Per element, not summed, because a sum hides a dead tile.</b> On a wall
   * with more than one tile, one live picture carries the total past any
   * threshold while its neighbour is black — which is precisely the failure this
   * file exists to catch, so a check that could be fooled by it would be no
   * check at all. Every element must advance on its own.
   * </para>
   */
  perElement: ReadonlyArray<number>;
  /** The sum, used only to wait for the first frame anywhere on the wall. */
  totalVideoFrames: number;
  /** How many video elements were found — 0 means no picture at all. */
  elements: number;
}

/**
 * Reads decoded-frame counts off the tile's own `<video>` element.
 *
 * <para>
 * <c>getVideoPlaybackQuality()</c> counts frames the decoder actually produced.
 * Deliberately <b>not</b> <c>currentTime</c>, which can advance over a stalled
 * track and would report a frozen picture as healthy.
 * </para>
 *
 * <para>
 * Deliberately not a second reader of the WebRTC <c>inbound-rtp</c> statistics
 * either: the application already owns that reading, and duplicating it here
 * would be a second thing to keep true. This asks the element what it drew.
 * </para>
 *
 * <para>
 * Reports the element count so <i>no picture at all</i> stays distinguishable
 * from <i>a picture that is not advancing</i>. They need different fixes, and a
 * single number cannot tell them apart.
 * </para>
 */
async function readDecode(page: Page): Promise<DecodeReading> {
  return page.evaluate(() => {
    const videos = Array.from(document.querySelectorAll('video'));
    const perElement = videos.map((video) =>
      typeof video.getVideoPlaybackQuality === 'function' ? video.getVideoPlaybackQuality().totalVideoFrames : 0,
    );

    return {
      perElement,
      totalVideoFrames: perElement.reduce((sum, frames) => sum + frames, 0),
      elements: videos.length,
    };
  });
}

test('a tile shows an overlay label over video that is actually decoding', async ({ page }) => {
  test.setTimeout(180_000);

  const wall = readLiveVideoWall();

  await signInToKiosk(page);

  // This wall specifically — the picker also lists the other seeds' layouts.
  await page.getByRole('listitem').filter({ hasText: wall.layoutName }).getByRole('button').click();
  await expect(page.getByTestId('layout-grid')).toBeVisible();

  // ---- half one: the picture, and it must be MOVING ----------------------

  await expect
    .poll(async () => (await readDecode(page)).totalVideoFrames, {
      timeout: FIRST_FRAME_TIMEOUT_MS,
      message:
        'no video frame ever decoded on this tile — either the SFU has no path for the ' +
        'camera, or the fixture video source is not serving',
    })
    .toBeGreaterThan(0);

  // **The delta is the assertion, not the count.** A source that emitted one
  // frame and stopped satisfies "frames have been decoded" while showing
  // something an operator cannot tell from a frozen wall — and neither can a
  // screenshot, which is why this is the check that had to exist.
  const first = await readDecode(page);
  await page.waitForTimeout(SAMPLE_GAP_MS);
  const second = await readDecode(page);

  const framesAdvanced = second.totalVideoFrames - first.totalVideoFrames;

  // Printed on success as well as failure. A passing assertion says the delta
  // cleared the threshold; it does not say by how much, and the margin is what
  // tells a reader whether the picture is healthy or barely moving.
  console.info(
    `[decode] ${first.totalVideoFrames} → ${second.totalVideoFrames} frames in ${SAMPLE_GAP_MS}ms ` +
      `(+${framesAdvanced}, threshold ${MINIMUM_FRAMES_PER_SAMPLE}) across ${second.elements} element(s)`,
  );

  // There is one tile on this wall by construction. Asserted rather than
  // assumed, because the per-element check below is only as good as the set it
  // iterates: a wall that silently gained a tile would still be checked, but a
  // wall that silently lost its only one would pass an empty loop.
  expect(second.elements, 'the wall should carry exactly one tile').toBe(1);

  // **Every element, not the total.** A sum lets one live picture carry a black
  // neighbour past the threshold.
  second.perElement.forEach((frames, index) => {
    expect(
      isDecodeOngoing(first.perElement[index] ?? 0, frames, MINIMUM_FRAMES_PER_SAMPLE),
      `tile ${index} is frozen, not live: ${first.perElement[index] ?? 0} → ${frames} ` +
        `frames in ${SAMPLE_GAP_MS}ms`,
    ).toBe(true);
  });

  // **The rule must also reject a stall, or it is not a rule.** A check that
  // only ever sees healthy readings cannot distinguish "the picture is moving"
  // from "this assertion is always true" — and the failure it exists to catch,
  // a source that emitted one frame and stopped, is precisely the reading it
  // never gets to see on a working stack.
  //
  // Arithmetic, deliberately: it costs no stack time, and the plumbing is
  // covered by the mutation that points the camera at an address nothing serves.
  expect(
    isDecodeOngoing(first.totalVideoFrames, first.totalVideoFrames + 1, MINIMUM_FRAMES_PER_SAMPLE),
    'one extra frame in a second is a frozen wall, and the rule must say so',
  ).toBe(false);

  expect(
    isDecodeOngoing(first.totalVideoFrames, first.totalVideoFrames, MINIMUM_FRAMES_PER_SAMPLE),
    'no new frames at all is a frozen wall, and the rule must say so',
  ).toBe(false);

  // ---- half two: the label, over that picture ----------------------------

  await expect(
    page.getByTestId('camera-viewer-overlay-label').first(),
    'the tile decodes video but renders no overlay label at all',
  ).toBeVisible({ timeout: 30_000 });

  await expect(
    page.getByTestId('camera-viewer-overlay-label').first(),
    `the overlay label is present but does not carry the variable's resolved value ` + `"${wall.variableInitialValue}"`,
  ).toContainText(wall.variableInitialValue, { timeout: 30_000 });
});

// ─────────────────────────────────────────────────────────────────────────
// US2 — the span, in this file rather than its own.
//
// **They share a wall, so they must share a file.** The span sets the bound
// variable to a series of values; the check above expects the seeded initial
// one. In separate files those race — and in CI, where files run in one
// worker in alphabetical order, the span would run FIRST and the check above
// would fail every time. It did exactly that in a full-suite run, with the
// label reading `SPAN0`.
//
// One file makes the order explicit and one worker's, rather than resting on
// filenames sorting the way someone hoped.
// ─────────────────────────────────────────────────────────────────────────

/**
 * Spec 056 US2 — the span, timed as one span or refused.
 *
 * <para>
 * <b>What this measures, and what it does not.</b> Submission of a variable's
 * new value to that value being visible on a tile. That covers <i>event →
 * overlay state</i> and <i>overlay composite + render</i>. It does <b>not</b>
 * cover camera → SFU, SFU → decode, or the presentation buffer — those are legs
 * of the <i>picture's</i> path, not the label's. A figure from here says nothing
 * about whether the 800 ms budget holds, because three of its legs are absent.
 * </para>
 *
 * <para>
 * <b>One subtraction on one clock.</b> Both stamps are taken by this process, on
 * this machine. Spec 053 examined exactly two shapes and reached different
 * verdicts: two readers of one OS clock (safe — how the front of its span was
 * established) and a host stamp minus a container stamp (not established, still
 * open). This is the first. The browser's clock is never mixed in.
 * </para>
 *
 * <para>
 * <b>A refusal is a result.</b> Where the run cannot show both ends share a
 * clock, it reports what it could not establish and no figure. The alternative
 * on offer is a number assembled from per-leg medians, and medians do not add
 * (ADR-0135) — that number would be a fabrication wearing a measurement's
 * clothes.
 * </para>
 */

const ITERATIONS = 5;
const OBSERVE_TIMEOUT_MS = 60_000;

/** The legs this span covers, and the ones it does not. Reported, never implied. */
const LEGS_COVERED = ['event → overlay state', 'overlay composite + render'] as const;
const LEGS_NOT_COVERED = ['camera → SFU', 'SFU → kiosk decode', 'presentation buffer'] as const;

interface SpanMeasurement {
  elapsedMilliseconds?: number;
  refusal?: string;
}

/**
 * Whether both ends of the span can be stamped on one clock.
 *
 * <para>
 * True only when this process both submits and observes, on this machine. A
 * remote browser or a distributed grid breaks that, and the honest answer there
 * is a refusal rather than a subtraction across two clocks.
 * </para>
 */
function sharesOneClock(): { ok: true } | { ok: false; because: string } {
  const wsEndpoint = process.env['PW_TEST_CONNECT_WS_ENDPOINT'];
  if (wsEndpoint !== undefined && wsEndpoint !== '') {
    return {
      ok: false,
      because: `the browser is remote (${wsEndpoint}), so the observation is not stamped on this machine`,
    };
  }
  return { ok: true };
}

/** Reports every figure, its median and range, and the legs it does not cover. */
function report(measurements: ReadonlyArray<SpanMeasurement>): void {
  const figures = measurements
    .map((measurement) => measurement.elapsedMilliseconds)
    .filter((value): value is number => value !== undefined)
    .sort((left, right) => left - right);

  if (figures.length === 0) {
    const because = measurements[0]?.refusal ?? 'no measurement was taken';
    console.info(`[span] UNMEASURED — ${because}`);
    console.info('[span] no figure is reported, and none is derived from per-leg figures');
    return;
  }

  // Every figure, not a summary. A median without its spread hides whether the
  // system under test or the machine is the bottleneck, which is the distinction
  // that made an earlier "~3x" claim in this repository wrong.
  console.info(`[span] ${figures.length} run(s) — ${figures.join(' / ')} ms`);

  // **No median and no range from one figure.** The loop stops at the first
  // refusal, so one or two figures is a reachable state, and a lone sample
  // dressed as "median X, range X-X" reads as a measurement that repeated.
  if (figures.length === 1) {
    console.info('[span] one figure only — no median, no range; a single run is not a measurement');
  } else {
    // The lower-middle of an even count is not the median either; average the
    // two middles rather than silently picking a side.
    const middle = Math.floor(figures.length / 2);
    const median = figures.length % 2 === 1 ? figures[middle]! : (figures[middle - 1]! + figures[middle]!) / 2;
    console.info(`[span] median ${median} ms, range ${figures[0]}-${figures[figures.length - 1]} ms`);
  }
  console.info(`[span] covers: ${LEGS_COVERED.join(', ')}`);
  console.info(`[span] NOT covered: ${LEGS_NOT_COVERED.join(', ')}`);
  console.info('[span] includes the label hold (ADR-0129): yes — this wall has video');
  console.info(`[span] conditions: ${process.platform}, CI=${process.env['CI'] ?? 'false'}, one tile, one clip`);

  // **The instrument's own error, beside its figures.** The end is observed by
  // a polling assertion whose interval backs off to 1000 ms, and the start is
  // stamped before a fill and a click, each a round trip to the browser. So a
  // figure is good to roughly ±1 s — five times the 200 ms leg it is meant to
  // characterise. Printed because a number without its resolution reads as far
  // more precise than it is.
  console.info('[span] instrument error: ~±1000 ms (polled observation + automation round trips)');
}

async function setValue(operatorPage: Page, variableName: string, value: string): Promise<void> {
  const row = operatorPage.getByRole('listitem').filter({ hasText: variableName });
  await row.getByPlaceholder('New value').fill(value);
  await row.getByRole('button', { name: /^set value$/i }).click();
}

/**
 * The span from an operator submitting a value to a kiosk tile showing it.
 *
 * <para>
 * The refusal path stays implemented and reachable, for the case it was
 * written for: FR-009 is about two ends that cannot be shown to share a clock,
 * checked before anything is timed.
 * </para>
 */
test('the span from a value being submitted to it being visible', async ({ page, context }) => {
  test.setTimeout(300_000);

  const wall = readLiveVideoWall();
  const clock = sharesOneClock();

  await signInToKiosk(page);
  await page.getByRole('listitem').filter({ hasText: wall.layoutName }).getByRole('button').click();
  await expect(page.getByTestId('layout-grid')).toBeVisible();

  // **Only that a label is there — deliberately not which text it carries.**
  // This test leaves the variable on its last value, and CI retries at *test*
  // granularity, so a precondition demanding the seeded initial value would
  // fail both retries after any first failure and report the precondition as
  // the cause instead of the real one.
  const label = page.getByTestId('camera-viewer-overlay-label').first();
  await expect(label).toBeVisible({ timeout: 30_000 });

  // The channel must be up before anything is timed, or the first figure
  // measures the connection rather than the span.
  await expect(page.getByTestId('live-updates-degraded')).toBeHidden({ timeout: 45_000 });

  // **Refused before it is attempted**, so a run that cannot be measured says so
  // rather than producing a figure whose two ends came from different clocks.
  if (!clock.ok) {
    report([{ refusal: clock.because }]);
    test.skip(true, `span unmeasured: ${clock.because}`);
    return;
  }

  const operatorContext = await context.browser()!.newContext({ baseURL: 'http://localhost:5173' });
  const operatorPage = await operatorContext.newPage();
  const measurements: SpanMeasurement[] = [];

  try {
    await signInAsOperator(operatorPage);
    await operatorPage.getByRole('link', { name: /^system variables$/i }).click();

    for (let iteration = 0; iteration < ITERATIONS; iteration += 1) {
      // Distinguishable per iteration, so the observation cannot match a value
      // left over from the previous one.
      const value = `SPAN${iteration}`;

      const submittedAt = Date.now();
      await setValue(operatorPage, wall.variableName, value);

      try {
        await expect(label).toContainText(value, { timeout: OBSERVE_TIMEOUT_MS });
        measurements.push({ elapsedMilliseconds: Date.now() - submittedAt });
      } catch {
        measurements.push({
          refusal: `iteration ${iteration}: the value never reached the tile within ${OBSERVE_TIMEOUT_MS}ms`,
        });
        break;
      }
    }
  } finally {
    await operatorPage.close();
    await operatorContext.close();
  }

  report(measurements);

  // **The picture must still be moving after all that** — folded in from what
  // was a separate held-back check. A tile that lost its video and fell back to
  // a label-only state would satisfy every timing assertion above while showing
  // an operator nothing. Kept here rather than in its own file because it drives
  // the same variable on the same wall: two files doing that race locally, and
  // in CI the alphabetically earlier one runs first and breaks the other.
  const afterChanges = await readDecode(page);
  await page.waitForTimeout(SAMPLE_GAP_MS);
  const settled = await readDecode(page);

  settled.perElement.forEach((frames, index) => {
    expect(
      isDecodeOngoing(afterChanges.perElement[index] ?? 0, frames, MINIMUM_FRAMES_PER_SAMPLE),
      `tile ${index} stopped decoding while the label was being driven: ` +
        `${afterChanges.perElement[index] ?? 0} → ${frames} frames in ${SAMPLE_GAP_MS}ms`,
    ).toBe(true);
  });

  const timed = measurements.filter((measurement) => measurement.elapsedMilliseconds !== undefined);

  // **A value that never arrives is a failure, not an "unmeasured span".**
  //
  // FR-009's refusal is about *clocks* — two ends that cannot be shown to share
  // one, handled above before anything is timed. The refusal actually reached
  // today is that the value never lands on the tile, which is a defect. Treating
  // it as the specification's honesty policy being exercised would dress a bug
  // up as a design success, and would make the outcome non-monotonic: total
  // failure green, one success red, two green. A regression to complete
  // breakage would then be indistinguishable from today.
  expect(
    timed.length,
    timed.length === 0
      ? `no iteration completed — ${measurements[0]?.refusal ?? 'unknown'}`
      : 'a single run is not a measurement',
  ).toBeGreaterThan(1);
});

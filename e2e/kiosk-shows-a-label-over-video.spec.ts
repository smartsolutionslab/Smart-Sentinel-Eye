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
 * Spec 056 US2, sharpened by spec 108 — the span, timed by an in-page clock at
 * both ends or refused.
 *
 * <para>
 * <b>What this measures, and what it does not.</b> From an operator's click on
 * "Set value" <i>landing</i>, to the tile having <i>painted</i> the new value.
 * That covers <i>event → overlay state</i> and <i>overlay composite + render</i>.
 * It does <b>not</b> cover camera → SFU, SFU → decode, or the presentation
 * buffer serially — those are legs of the <i>picture's</i> path. Since ADR-0129
 * they enter this span by exactly one route: the label is held back to its
 * tile's own frame age, capped at 200 ms. So a figure from here is not the
 * 800 ms budget verified; it is the label's journey, with the video half
 * entering only through the hold.
 * </para>
 *
 * <para>
 * <b>It overshoots at the head, and the overshoot is measured rather than
 * described.</b> Constitution §IV's span begins at <i>event arrival</i>; t0 is
 * the operator's click, so the figure additionally contains the browser's
 * `fetch`, the gateway hop and the service accepting the write. The submit
 * request's own round trip is printed beside every sample so a reader can
 * subtract it instead of guessing at it.
 * </para>
 *
 * <para>
 * <b>The clock lives in the pages, not in this process.</b> Until spec 108 the
 * end was observed by `expect(label).toContainText(value)`, a polling assertion
 * whose interval backs off 100 / 250 / 500 / 1000 ms, and the start was stamped
 * in Node before a `fill` and a `click` — two round trips including
 * actionability checks. Both are the harness measuring itself, and together they
 * put the instrument's error at ~±1000 ms against an 800 ms budget. An
 * instrument whose error bar is larger than the thing it tests cannot say
 * whether the thing holds. So t0 is stamped by a capture-phase one-shot `click`
 * listener on the operator page, and t1 by a `MutationObserver` plus two chained
 * `requestAnimationFrame`s on the kiosk page — the product's own definition of
 * <i>painted</i> (`apps/shared/src/observability/kioskLatency.ts:135-142`), so
 * that t1 means what the product means by it.
 * </para>
 *
 * <para>
 * <b>One subtraction on one clock.</b> Both stamps are `Date.now()`, read in two
 * Chromium contexts of one browser on one machine. Spec 053 examined exactly two
 * shapes and reached different verdicts: two readers of one OS clock (safe) and
 * a host stamp minus a container stamp (not established, still open). This is
 * the first. No server stamp enters the figure. <b>On a distributed deployment
 * this subtraction would be meaningless</b>, which is what `sharesOneClock`
 * refuses on.
 * </para>
 *
 * <para>
 * <b>A refusal is a result, and so is a failure.</b> Where the run cannot show
 * both ends share a clock it reports what it could not establish and no figure.
 * Where an iteration cannot be stamped it <b>fails naming that iteration</b> and
 * is never dropped: a harness whose error path discards samples reports the
 * distribution of the samples that were fast enough to be seen.
 * </para>
 */

/**
 * Ten rather than five (spec 108 NFR-B).
 *
 * <para>
 * The old five took 8.6 s of a 300 s test budget, so the envelope was not what
 * bounded them. Ten keeps the run long enough for the per-leg listener below to
 * see the 2 s and 5 s sampling cadences it reads.
 * </para>
 *
 * <para>
 * <b>Ten does not buy a p95, and this comment used to say it did.</b> It claimed
 * ten gives "a p95 that is the second-largest sample rather than the largest".
 * The index arithmetic borrowed from `click-to-first-frame.spec.ts:488` is
 * `Math.ceil(n × 0.95) − 1`, and that file takes <b>twenty</b> samples, where the
 * index is 18 and genuinely the 19th of 20. At ten it is 9 — <i>the largest
 * sample</i>, which is the maximum wearing a percentile's name. So no p95 is
 * reported at this sample size; `percentiles` refuses to compute one rather than
 * printing the maximum twice under two labels.
 * </para>
 */
const ITERATIONS = 10;

/** The test's own budget, named so the per-iteration arithmetic below can use it. */
const TEST_TIMEOUT_MS = 300_000;

/**
 * The ceiling on one iteration's observation — <b>not the budget actually used</b>.
 *
 * <para>
 * Ten iterations at this ceiling is 600 s against a 300 s test, and the loop
 * already spends ~45 s on the `live-updates-degraded` wait plus two sign-ins. A
 * run where three iterations went long therefore died on Playwright's own timeout
 * having printed <b>nothing</b> — the failure where the figures would have been
 * most informative. `observeBudget` derives the real per-iteration budget from
 * the time left, and every sample is printed as it lands rather than at the end.
 * </para>
 */
const OBSERVE_CEILING_MS = 60_000;

/**
 * Held back from the loop for the post-loop decode samples, the report and the
 * assertions, so a slow run still reaches its own output.
 */
const REPORT_RESERVE_MS = 30_000;

/** Below this an observation is not worth attempting; the refusal names it instead. */
const MINIMUM_OBSERVE_MS = 5_000;

/** How many bracketed reads the cross-context skew probe takes. */
const SKEW_BRACKETS = 7;

/** How long a sample waits for its own submit request to finish before giving up. */
const SUBMIT_SETTLE_MS = 2_000;

/** The legs this span covers, and the ones it does not. Reported, never implied. */
const LEGS_COVERED = ['event → overlay state', 'overlay composite + render'] as const;
const LEGS_NOT_COVERED = ['camera → SFU', 'SFU → kiosk decode', 'presentation buffer'] as const;

/**
 * Why those three are not covered — the reason, not only the fact (spec 108
 * FR-007). Printed because "not covered" alone reads as an omission somebody
 * could fix, when it is a property of what is being timed.
 */
const WHY_NOT_COVERED =
  'they are legs of the picture path, not the label path; since ADR-0129 they are not serial ' +
  'terms of this span at all, and enter it only by holding the label back to its tile frame ' +
  'age (cap 200 ms)';

/**
 * How much of §IV's 800 ms this span can possibly account for, printed beside the
 * figures so nobody reads "p50 79 ms against 800 ms" as the budget verified.
 *
 * <para>
 * 200 (event → overlay state) + 50 (composite + render) = <b>250</b>. The other
 * 400 ms of budgeted legs are the picture's, not the label's, and 150 ms is an
 * arithmetic remainder rather than a term.
 * </para>
 */
const BUDGETED_MILLISECONDS_SPANNED = 250;

/**
 * The closed set of measurement names the kiosk emits
 * (`apps/shared/src/observability/kioskLatency.ts:49`), pinned server-side by
 * `StreamEndpoints.cs:175-190` and by `KioskMeasurementContractTests`.
 *
 * <para>
 * Restated here so a name with <b>zero</b> samples can be printed as
 * `no samples` rather than omitted (FR-010). A silent absence is
 * indistinguishable from a healthy wall, which is spec 095's whole subject.
 * </para>
 */
const KIOSK_MEASUREMENTS = [
  'overlay_draw',
  'receive_to_decoded',
  'presentation_buffer',
  'wall_skew',
  'label_delay',
] as const;

type KioskMeasurementName = (typeof KIOSK_MEASUREMENTS)[number];

/**
 * What to print beside each name, or null where printing one would be a lie.
 *
 * <para>
 * `receive_to_decoded` gets <b>none</b>: it is the receiving half of a leg whose
 * sending end a browser cannot see without a clock shared with the SFU, and
 * ADR-0122 refuses to let a fragment wear a whole leg's budget.
 * </para>
 */
const MEASUREMENT_BUDGETS: Record<KioskMeasurementName, string | null> = {
  overlay_draw: 'budget 50 ms (section IV composite + render)',
  receive_to_decoded: null,
  presentation_buffer: 'budget 200 ms (section IV presentation buffer)',
  wall_skew: 'bound 33 ms (ADR-0128 section 2, intra-wall)',
  label_delay: 'cap 200 ms (ADR-0129 hold — not a section IV leg)',
};

interface SpanSample {
  iteration: number;
  elapsedMilliseconds: number;
  submitRoundTripMilliseconds: number | null;
}

interface SpanMeasurement {
  sample?: SpanSample;
  refusal?: string;
}

/** One `[latency]` line the kiosk emitted while the span was being timed. */
interface LatencyLine {
  measurement: string;
  camera: string;
  elapsedMilliseconds: number;
}

/**
 * How the operator page's clock sits against the kiosk page's, <b>bounded rather
 * than assumed</b>.
 *
 * <para>
 * `elapsed = t1 − t0` subtracts a stamp taken in the operator renderer from one
 * taken in the kiosk renderer. They are separate Chromium processes, each
 * extrapolating `base::Time::Now()` from its own latched tick/wall pair, so a
 * constant offset between them is possible and lands directly in every figure.
 * </para>
 *
 * <para>
 * <b>The calibration cannot see this and never could.</b> C1 defers the observed
 * mutation on the kiosk side by 300 ms; both the delayed and the undelayed arm go
 * through the same two-clock subtraction, so a constant offset <i>cancels in the
 * difference</i>. C1 establishes scale and linearity, and nothing about the
 * origin. A −60 ms offset would make every sample 60 ms too small — in the
 * headroom-flattering direction — while C1 still recovered 296 of 300.
 * </para>
 */
interface ClockSkewBound {
  /** The lowest offset (operator clock − kiosk clock, ms) the brackets permit. */
  lowMilliseconds: number;
  /** The highest offset the brackets permit. */
  highMilliseconds: number;
  /** The tightest single bracket's round trip — the width one bracket alone bounds. */
  tightestRoundTripMilliseconds: number;
  /** How many brackets were taken. */
  brackets: number;
  /**
   * Whether the brackets agreed. `false` means their intervals did not intersect —
   * the clocks drifted, or a read was descheduled — and the reported interval is
   * then the envelope of all of them rather than their intersection.
   */
  consistent: boolean;
}

/**
 * Whether both ends of the span can be stamped on one clock.
 *
 * <para>
 * True only when this process drives both pages from one browser on this
 * machine. A remote browser or a distributed grid breaks that, and the honest
 * answer there is a refusal rather than a subtraction across two clocks.
 * </para>
 *
 * <para>
 * <b>One browser is not one clock, only one <i>machine</i>.</b> This guard rules
 * out the remote case; `bracketClockSkew` bounds what is left.
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

// ── the cross-context clock, bracketed ───────────────────────────────────

async function readNow(page: Page): Promise<number> {
  const raw: unknown = await page.evaluate(() => Date.now());
  if (typeof raw !== 'number' || !Number.isFinite(raw)) {
    throw new Error(`a page returned an unusable clock reading: ${JSON.stringify(raw)}`);
  }
  return raw;
}

/**
 * Bounds the operator page's clock against the kiosk page's by <b>bracketing</b>:
 * kiosk → operator → kiosk.
 *
 * <para>
 * Write the kiosk clock K and the operator clock O = K + δ. Reading `before` on
 * the kiosk, `middle` on the operator and `after` on the kiosk, the instant of the
 * middle read lies between the outer two, so
 * <c>δ ∈ [middle − after, middle − before]</c>. The interval's width is the round
 * trip, and the derivation does not depend on which page is read first — which is
 * exactly what a one-way probe cannot offer. A one-way `min −88 ms` was previously
 * attributed to round-trip jitter, and that reading is only available if the kiosk
 * was read first; under the other ordering a round trip can only push the figure
 * positive, so −88 ms would have been real skew. A one-way probe cannot tell those
 * apart. This one does not have to.
 * </para>
 *
 * <para>
 * Several brackets are intersected, because each is only as tight as its own round
 * trip. If they do not intersect, the clocks moved relative to each other during
 * the probe; that is reported rather than hidden, and the envelope is used instead.
 * </para>
 */
async function bracketClockSkew(kioskPage: Page, operatorPage: Page): Promise<ClockSkewBound> {
  const lows: number[] = [];
  const highs: number[] = [];
  const roundTrips: number[] = [];

  for (let bracket = 0; bracket < SKEW_BRACKETS; bracket += 1) {
    const before = await readNow(kioskPage);
    const middle = await readNow(operatorPage);
    const after = await readNow(kioskPage);

    if (after < before) {
      throw new Error(`the kiosk clock stepped backwards during a skew bracket: ${before} then ${after}`);
    }

    lows.push(middle - after);
    highs.push(middle - before);
    roundTrips.push(after - before);
  }

  const intersectionLow = Math.max(...lows);
  const intersectionHigh = Math.min(...highs);
  const consistent = intersectionLow <= intersectionHigh;

  return {
    lowMilliseconds: consistent ? intersectionLow : Math.min(...lows),
    highMilliseconds: consistent ? intersectionHigh : Math.max(...highs),
    tightestRoundTripMilliseconds: Math.min(...roundTrips),
    brackets: SKEW_BRACKETS,
    consistent,
  };
}

/** The largest magnitude the bound permits — the term that enters the error budget. */
function skewMagnitude(bound: ClockSkewBound): number {
  return Math.max(Math.abs(bound.lowMilliseconds), Math.abs(bound.highMilliseconds));
}

function describeSkew(label: string, bound: ClockSkewBound | null): string {
  if (bound === null) {
    return `[span] cross-context clock skew ${label}: UNMEASURED — no bound is in the error below`;
  }

  const containsZero = bound.lowMilliseconds <= 0 && bound.highMilliseconds >= 0;
  return (
    `[span] cross-context clock skew ${label}: operator − kiosk ∈ ` +
    `[${bound.lowMilliseconds.toFixed(0)}, ${bound.highMilliseconds.toFixed(0)}] ms ` +
    `(|δ| ≤ ${skewMagnitude(bound).toFixed(0)} ms; ${bound.brackets} brackets, tightest round trip ` +
    `${bound.tightestRoundTripMilliseconds.toFixed(0)} ms; ` +
    `${bound.consistent ? 'brackets agree' : 'brackets DISAGREE — envelope reported, the clocks moved'}; ` +
    `${containsZero ? 'consistent with one shared clock' : 'EXCLUDES zero — the two contexts do not agree'})`
  );
}

// ── the in-page clock ────────────────────────────────────────────────────

interface OverlayPaintClock {
  /** `Date.now()` two animation frames after the matching mutation, or null. */
  t1: number | null;
  /** The text that matched, kept so a wrong match stays diagnosable. */
  matched: string | null;
  /** How many mutations were seen at all — 0 means the observer never fired. */
  mutations: number;
}

interface ClickClock {
  /** `Date.now()` at the moment the gesture landed, or null before it. */
  t0: number | null;
}

interface SpanWindow {
  __spanPaint?: OverlayPaintClock;
  __spanClick?: ClickClock;
}

/**
 * Arms the kiosk-side observer for one iteration's expected value.
 *
 * <para>
 * <b>Armed before the operator clicks, never after.</b> The other order loses
 * fast iterations silently, and a harness that loses its fast samples reports a
 * worse figure while one that loses its slow samples reports a better one —
 * neither is acceptable, and only arming first rules both out.
 * </para>
 *
 * <para>
 * Observes the label's <b>parent</b>, so a React commit that replaces the span
 * itself is still seen; the callback re-queries the label rather than closing
 * over a node that may be gone.
 * </para>
 *
 * <para>
 * <b>`e2e/` is type-checked by nothing (#2121)</b>, so what crosses the
 * `page.evaluate` boundary is validated here rather than trusted — a bad shape
 * must throw rather than become `NaN ms`.
 * </para>
 */
async function armOverlayPaint(kioskPage: Page, expectedValue: string): Promise<void> {
  const raw: unknown = await kioskPage.evaluate((expected: string) => {
    const label = document.querySelector('[data-testid="camera-viewer-overlay-label"]');
    if (label === null) return { armed: false };

    const scope: Element = label.parentElement ?? label;
    const state: OverlayPaintClock = { t1: null, matched: null, mutations: 0 };
    (window as unknown as SpanWindow).__spanPaint = state;

    const observer = new MutationObserver(() => {
      state.mutations += 1;
      if (state.t1 !== null) return;

      const current = scope.querySelector('[data-testid="camera-viewer-overlay-label"]');
      const text = current?.textContent ?? '';
      if (!text.includes(expected)) return;

      observer.disconnect();
      state.matched = text;

      // The product's own definition of *painted*, not a new one: the first
      // frame runs after React has committed and before paint, the second after
      // that paint has happened (kioskLatency.ts:135-142).
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          state.t1 = Date.now();
        });
      });
    });

    observer.observe(scope, { childList: true, characterData: true, subtree: true });
    return { armed: true };
  }, expectedValue);

  const armed = (raw as { armed?: unknown }).armed;
  if (armed !== true) {
    throw new Error('the overlay label is not in the kiosk DOM, so the span cannot be observed');
  }
}

/**
 * Waits, in the kiosk page, for the armed clock to stamp t1.
 *
 * <para>
 * The deadline is a `setTimeout` rather than a frame comparison so it fires even
 * where frames are throttled; an rAF-only deadline that never runs would surface
 * as an opaque test timeout naming nothing.
 * </para>
 */
async function awaitOverlayPaint(
  kioskPage: Page,
  budgetMilliseconds: number,
): Promise<{ armed: boolean; t1: number | null; mutations: number }> {
  const raw: unknown = await kioskPage.evaluate(async (budget: number) => {
    const state = (window as unknown as SpanWindow).__spanPaint;
    if (state === undefined) return { armed: false, t1: null, mutations: 0 };

    const t1 = await new Promise<number | null>((resolve) => {
      const deadline = window.setTimeout(() => resolve(null), budget);
      const check = (): void => {
        if (state.t1 !== null) {
          window.clearTimeout(deadline);
          resolve(state.t1);
          return;
        }
        requestAnimationFrame(check);
      };
      check();
    });

    return { armed: true, t1, mutations: state.mutations };
  }, budgetMilliseconds);

  const shape = raw as { armed?: unknown; t1?: unknown; mutations?: unknown };
  if (typeof shape.armed !== 'boolean' || typeof shape.mutations !== 'number') {
    throw new Error(`the kiosk observer returned an unusable shape: ${JSON.stringify(raw)}`);
  }
  if (shape.t1 !== null && (typeof shape.t1 !== 'number' || !Number.isFinite(shape.t1))) {
    throw new Error(`the kiosk observer returned a non-finite t1: ${JSON.stringify(raw)}`);
  }

  return { armed: shape.armed, t1: shape.t1 as number | null, mutations: shape.mutations };
}

/**
 * Arms the operator-side t0.
 *
 * <para>
 * <b>Ordering is fill → arm → click.</b> Arming before the fill lets the input's
 * own click consume the one-shot listener, and t0 would then be the moment the
 * operator started typing.
 * </para>
 *
 * <para>
 * Stamped by the click event rather than by this process: Playwright runs
 * actionability checks (visible, stable, receives events) before it dispatches,
 * and a stamp taken in Node before `click()` folds those into every sample.
 * </para>
 */
async function armClickStamp(operatorPage: Page): Promise<void> {
  await operatorPage.evaluate(() => {
    const state: ClickClock = { t0: null };
    (window as unknown as SpanWindow).__spanClick = state;
    document.addEventListener(
      'click',
      () => {
        state.t0 = Date.now();
      },
      { capture: true, once: true },
    );
  });
}

async function readClickStamp(operatorPage: Page): Promise<number | null> {
  const raw: unknown = await operatorPage.evaluate(() => {
    const state = (window as unknown as SpanWindow).__spanClick;
    return { t0: state?.t0 ?? null };
  });

  const t0 = (raw as { t0?: unknown }).t0;
  if (t0 === null || t0 === undefined) return null;
  if (typeof t0 !== 'number' || !Number.isFinite(t0)) {
    throw new Error(`the operator click stamp returned an unusable shape: ${JSON.stringify(raw)}`);
  }
  return t0;
}

// ── reporting ────────────────────────────────────────────────────────────

function at(sorted: ReadonlyArray<number>, index: number): number {
  const value = sorted[Math.max(0, Math.min(sorted.length - 1, Math.floor(index)))];
  if (value === undefined) throw new Error('a percentile was read from an empty sample set');
  return value;
}

/**
 * The median of an already-sorted set.
 *
 * <para>
 * <b>The two middles are averaged at an even count.</b> The upper-middle of an
 * even count is not the median, and neither is the lower-middle; silently picking
 * a side is what this restores the guard against. At n = 10 it is the mean of the
 * 5th and 6th samples, which is why a p50 here can carry a half.
 * </para>
 */
function median(sorted: ReadonlyArray<number>): number {
  const count = sorted.length;
  if (count === 0) throw new Error('a median was read from an empty sample set');

  const upper = Math.floor(count / 2);
  if (count % 2 === 1) return at(sorted, upper);
  return (at(sorted, upper - 1) + at(sorted, upper)) / 2;
}

/**
 * <b>`p95` is null where the sample size cannot support one.</b>
 *
 * <para>
 * The index is `click-to-first-frame.spec.ts:488`'s: `Math.ceil(n × 0.95) − 1`.
 * At n = 20 that is 18, the 19th of 20 — a real percentile. At n = 10 it is 9,
 * <i>the largest sample</i>. Printing the maximum twice, once labelled `p95`, is
 * how a figure acquires a precision nobody measured, so this returns null instead
 * and the caller says why.
 * </para>
 */
function percentiles(values: ReadonlyArray<number>): { p50: number; p95: number | null; max: number; min: number } {
  const sorted = [...values].sort((left, right) => left - right);
  const p95Index = Math.ceil(sorted.length * 0.95) - 1;

  return {
    p50: median(sorted),
    p95: p95Index < sorted.length - 1 ? at(sorted, p95Index) : null,
    max: at(sorted, sorted.length - 1),
    min: at(sorted, 0),
  };
}

/** One decimal, because an averaged median at an even count carries a half. */
function milliseconds(value: number): string {
  return Number.isInteger(value) ? value.toFixed(0) : value.toFixed(1);
}

/**
 * Printed <b>as the sample lands</b>, not after the loop.
 *
 * <para>
 * A run whose loop overran used to die on the Playwright timeout having printed
 * nothing at all — the one failure where the figures would have been most worth
 * having.
 * </para>
 */
function printSample(sample: SpanSample): void {
  const submit =
    sample.submitRoundTripMilliseconds === null
      ? 'submit round trip unseen'
      : `submit round trip ${sample.submitRoundTripMilliseconds.toFixed(0)} ms`;
  console.info(`[span] iteration ${sample.iteration}: ${sample.elapsedMilliseconds} ms (${submit})`);
}

interface SpanRun {
  measurements: ReadonlyArray<SpanMeasurement>;
  /** The bracketed skew taken before the loop, or null where none was taken. */
  skewBefore: ClockSkewBound | null;
  /** And again after it, so drift across the run is visible rather than assumed. */
  skewAfter: ClockSkewBound | null;
}

/**
 * Reports every figure, its distribution and range, the submit round trip beside
 * each sample, the legs it does not cover with the reason, and the instrument's
 * own error — including the cross-context skew term, measured rather than argued.
 */
function report(run: SpanRun): void {
  const samples = run.measurements
    .map((measurement) => measurement.sample)
    .filter((sample): sample is SpanSample => sample !== undefined);

  // Every refusal, named and first, so a run that produced no figure says why
  // rather than printing an empty distribution.
  for (const measurement of run.measurements) {
    if (measurement.refusal !== undefined) console.info(`[span] REFUSED — ${measurement.refusal}`);
  }

  if (samples.length === 0) {
    console.info('[span] UNMEASURED — no iteration was stamped at both ends');
    console.info('[span] no figure is reported, and none is derived from per-leg figures');
    return;
  }

  const figures = samples.map((sample) => sample.elapsedMilliseconds);
  console.info(`[span] ${figures.length} sample(s) — ${[...figures].sort((a, b) => a - b).join(' / ')} ms`);

  if (figures.length === 1) {
    console.info('[span] one figure only — no distribution, no range; a single run is not a measurement');
  } else {
    const span = percentiles(figures);
    const half = figures.length / 2;
    const middles =
      figures.length % 2 === 0
        ? `mean of the ${half}th and ${half + 1}th of ${figures.length}`
        : `the middle of ${figures.length}`;

    console.info(
      `[span] p50 ${milliseconds(span.p50)} ms (${middles}), max ${milliseconds(span.max)} ms, ` +
        `range ${milliseconds(span.min)}-${milliseconds(span.max)} ms, spread ${milliseconds(span.max - span.min)} ms`,
    );

    if (span.p95 === null) {
      console.info(
        `[span] no p95 at n=${figures.length}: Math.ceil(${figures.length} × 0.95) − 1 = ` +
          `${Math.ceil(figures.length * 0.95) - 1} is the largest sample, which is the max above. This index ` +
          'arithmetic needs n ≥ 20 for a real p95, so none is reported rather than the max printed twice.',
      );
    } else {
      console.info(
        `[span] p95 ${milliseconds(span.p95)} ms (the ${Math.ceil(figures.length * 0.95)}th of ${figures.length})`,
      );
    }
  }

  const submits = samples
    .map((sample) => sample.submitRoundTripMilliseconds)
    .filter((value): value is number => value !== null);
  if (submits.length === 0) {
    console.info('[span] submit round trip: no samples — the head overshoot is unbounded in this run');
  } else {
    const submit = percentiles(submits);
    console.info(
      `[span] submit round trip: p50 ${milliseconds(submit.p50)} ms, max ${milliseconds(submit.max)} ms ` +
        `over ${submits.length} sample(s) — subtract each sample's own printed figure to approach the ` +
        'section IV span start',
    );
  }

  console.info(`[span] covers: ${LEGS_COVERED.join(', ')}`);
  console.info(`[span] NOT covered: ${LEGS_NOT_COVERED.join(', ')} — ${WHY_NOT_COVERED}`);
  console.info(
    `[span] this span covers ${BUDGETED_MILLISECONDS_SPANNED} ms of section IV's 800 ms — ` +
      'event → overlay state (200) + composite + render (50). The other 400 ms of budgeted legs are the ' +
      'picture path and are NOT serial terms of this span; 150 ms is headroom, an arithmetic remainder. ' +
      'A figure here is NOT the 800 ms budget verified.',
  );
  console.info('[span] includes the label hold (ADR-0129): see the [legs] label_delay line below');
  console.info(`[span] conditions: ${process.platform}, CI=${process.env['CI'] ?? 'false'}, one tile, one clip`);

  // **The instrument's own error, beside its figures, and replaced rather than
  // deleted.** A number without its resolution reads as far more precise than it
  // is — the same defect as the ±1000 ms figure this supersedes.
  //
  //   two chained rAF at t1   <= 2 frames ~ 33 ms at 60 Hz
  //   click dispatch          ~ 1 frame   ~ 17 ms
  //   Date.now() resolution   2 x 1 ms
  //                           ---------
  //                                      ~ +/-52 ms, PLUS the skew term below
  const quantisation = 52;
  const bounds = [run.skewBefore, run.skewAfter].filter((bound): bound is ClockSkewBound => bound !== null);
  const skew = bounds.length === 0 ? null : Math.max(...bounds.map(skewMagnitude));

  console.info(
    `[span] instrument error: ±${quantisation} ms of quantisation (2 rAF ≈ 33 ms + click dispatch ≈ 17 ms + ` +
      '2 × Date.now() 1 ms) PLUS the cross-context clock skew term below — the two ends are stamped in ' +
      'different Chromium renderer processes, and that term was missing from this line until spec 108 phase 6',
  );
  console.info(describeSkew('before the loop', run.skewBefore));
  console.info(describeSkew('after the loop', run.skewAfter));
  console.info(
    skew === null
      ? `[span] total instrument error: at least ±${quantisation} ms, and NOT boundable — the skew probe did not run`
      : `[span] total instrument error: ~±${(quantisation + skew).toFixed(0)} ms ` +
          `(${quantisation} ms quantisation + ${skew.toFixed(0)} ms skew bound)`,
  );
  console.info(
    '[span] skew direction: elapsed = t1(kiosk) − t0(operator), so an operator clock running δ ms AHEAD of ' +
      'the kiosk clock makes every sample δ ms too SMALL. Correcting means ADDING δ to every figure above.',
  );

  // The arithmetic above is a ceiling on quantisation only. C1 — a known 300 ms
  // deferred into the observed path on alternate iterations of one run, so both
  // populations meet the same stack — recovered 296 ms as the mean of the ten
  // adjacent pairs. The apparatus was reverted; the figures are in spec 108's
  // verification note.
  console.info(
    '[span] calibration C1: a 300 ms delay, injected on the KIOSK side only, recovered as a 296 ms mean over ' +
      '10 paired iterations, every pair inside 277-340 ms — i.e. −23/+40 ms per pair. ±32 ms is the ' +
      'dispersion over pairs, NOT a per-sample bound; the per-pair worst case is ±40 ms.',
  );
  console.info(
    '[span] what C1 does and does not establish: it defers the TAIL, so it calibrates scale and linearity ' +
      'from the mutation onward. It says nothing about the head (click dispatch, actionability, the fetch), ' +
      'and nothing about a CONSTANT cross-context offset — both arms of a paired run go through the same ' +
      'two-clock subtraction, so a constant offset cancels in the difference. The bracketed probe above is ' +
      'the only thing here that bounds that.',
  );
  console.info(
    '[span] no budget is asserted here: this also runs on a shared CI runner, and #2072 has already ' +
      'measured 555/758 ms on an enclosed leg against 200 ms. Figures are recorded, not gated.',
  );
}

/**
 * Prints, per measurement name, what the tile's own instruments said while the
 * span was being timed (spec 108 US2).
 *
 * <para>
 * <b>Every name in the closed set, including the empty ones.</b> A name that
 * produced nothing prints `no samples` — never omitted, and never a zero, which
 * would read as a perfect score for a journey nobody timed.
 * </para>
 */
function reportLegs(lines: ReadonlyArray<LatencyLine>, malformed: number): void {
  console.info(`[legs] ${lines.length} [latency] line(s) captured during the run`);
  if (malformed > 0) {
    console.info(`[legs] ${malformed} line(s) had an unusable shape and are counted, not silently dropped`);
  }

  for (const name of KIOSK_MEASUREMENTS) {
    const budget = MEASUREMENT_BUDGETS[name];
    const suffix = budget === null ? ' — no budget: a fragment of a leg, not a leg (ADR-0122)' : ` — ${budget}`;
    const values = lines.filter((line) => line.measurement === name).map((line) => line.elapsedMilliseconds);

    if (values.length === 0) {
      console.info(`[legs] ${name}: no samples${suffix}`);
      continue;
    }

    const leg = percentiles(values);
    console.info(
      `[legs] ${name}: ${values.length} sample(s), p50 ${milliseconds(leg.p50)} ms, ` +
        `max ${milliseconds(leg.max)} ms${suffix}`,
    );
  }

  // **Why the hold reads empty here, and what its size actually is.** Spec 108
  // phase 4a attributed this to `frameAgeFor` returning null on a wall with no
  // alignment target. That is not the mechanism. `frameAgeFor`
  // (`apps/kiosk-web/src/features/cell/useWallAlignment.ts:243`) does not gate on
  // tile count — it reads a ref during `CellPage`'s render. Below two tiles the
  // settle interval never runs, so nothing re-renders `CellPage` after the first
  // lag sample and the tile keeps the `null` it mounted with. Phase 5 proved it
  // with paired probes on this fixture: one tile gave 0 samples, two tiles gave 10.
  if (!lines.some((line) => line.measurement === 'label_delay')) {
    console.info(
      '[legs] label_delay reading `no samples` on this ONE-TILE wall is structural, not a quiet run: the ' +
        'frame age reaches the tile only on a render CellPage performs, and below two tiles the settle ' +
        'interval that performs one never runs. On a two-tile wall the same fixture yields it at 35-45 ms ' +
        '(spec 108 verification). So the hold is a real term this span does not contain — and 200 ms is the ' +
        'cap the budget PERMITS, never an observation.',
    );
  }

  console.info(
    '[legs] these are printed BESIDE the span, never summed into it — medians do not add (ADR-0135), and ' +
      'three of these legs are not serial terms of that span at all',
  );
}

async function fillValue(operatorPage: Page, variableName: string, value: string): Promise<void> {
  const row = operatorPage.getByRole('listitem').filter({ hasText: variableName });
  await row.getByPlaceholder('New value').fill(value);
}

async function submitValue(operatorPage: Page, variableName: string): Promise<void> {
  const row = operatorPage.getByRole('listitem').filter({ hasText: variableName });
  await row.getByRole('button', { name: /^set value$/i }).click();
}

/**
 * What one iteration may spend waiting, derived from the time actually left.
 *
 * <para>
 * A fixed 60 s ceiling times ten iterations is 600 s against a 300 s test. The
 * share is what remains after the report's reserve, divided by the iterations
 * still to come, and it is floored so a squeezed run still attempts an
 * observation and refuses <i>by name</i> rather than dying anonymously.
 * </para>
 */
function observeBudget(startedAt: number, iterationsLeft: number): number {
  const remaining = TEST_TIMEOUT_MS - (Date.now() - startedAt) - REPORT_RESERVE_MS;
  const share = Math.floor(remaining / Math.max(1, iterationsLeft));
  return Math.max(MINIMUM_OBSERVE_MS, Math.min(OBSERVE_CEILING_MS, share));
}

/**
 * The submit round trip for <b>this iteration's own value</b>.
 *
 * <para>
 * <b>Keyed by the value the request carried, never by arrival order.</b> The old
 * `submitRoundTrips[roundTripsBefore]` assumed iteration N's `requestfinished`
 * landed before N+1 read its index; a late-finishing PUT made N print
 * `submit round trip unseen` and handed its figure to N+1, so a reader subtracted
 * the wrong number. Phase 5 found both runs' maxima were almost entirely this
 * round trip (236 ms of which 177; 226 ms of which 136), so a mispairing corrupts
 * exactly the samples that matter most.
 * </para>
 */
async function settleSubmitRoundTrip(
  roundTrips: ReadonlyMap<string, number>,
  value: string,
  budgetMilliseconds: number,
): Promise<number | null> {
  const deadline = Date.now() + budgetMilliseconds;
  for (;;) {
    const seen = roundTrips.get(value);
    if (seen !== undefined) return seen;
    if (Date.now() >= deadline) return null;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

test('the span from a value being submitted to it being visible', async ({ page, context }) => {
  test.setTimeout(TEST_TIMEOUT_MS);
  const startedAt = Date.now();

  const wall = readLiveVideoWall();
  const clock = sharesOneClock();

  // **Attached before navigation** (spec 108 US2). Four legs are emitted through
  // one `console.info('[latency]', …)` line and nothing in `e2e/` has ever read
  // them; a listener attached after sign-in would miss the earliest samples and
  // report a shorter window than the run.
  const latencyLines: LatencyLine[] = [];
  const pending: Promise<void>[] = [];
  let malformedLatencyLines = 0;

  page.on('console', (message) => {
    if (!message.text().startsWith('[latency]')) return;

    // **The structured argument, not the text.** The product emits an object
    // (`kioskLatency.ts:87`); a text parse would work today and break silently
    // the moment the object gains a field.
    const structured = message.args()[1];
    if (structured === undefined) {
      malformedLatencyLines += 1;
      return;
    }

    pending.push(
      structured
        .jsonValue()
        .then((raw: unknown) => {
          const line = raw as Partial<LatencyLine>;
          if (
            typeof line.measurement !== 'string' ||
            typeof line.camera !== 'string' ||
            typeof line.elapsedMilliseconds !== 'number' ||
            !Number.isFinite(line.elapsedMilliseconds)
          ) {
            malformedLatencyLines += 1;
            return;
          }
          latencyLines.push({
            measurement: line.measurement,
            camera: line.camera,
            elapsedMilliseconds: line.elapsedMilliseconds,
          });
        })
        .catch(() => {
          // The handle dies with the page; a line lost at teardown is counted,
          // never quietly treated as a leg that reported nothing.
          malformedLatencyLines += 1;
        })
        .finally(async () => {
          // The handle pins an object in the page until it is released, and this
          // fires for every latency line of the run. A page already gone at
          // teardown is not a lost measurement, so that rejection is not counted.
          await structured.dispose().catch(() => undefined);
        }),
    );
  });

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
    report({ measurements: [{ refusal: clock.because }], skewBefore: null, skewAfter: null });
    test.skip(true, `span unmeasured: ${clock.because}`);
    return;
  }

  const operatorContext = await context.browser()!.newContext({ baseURL: 'http://localhost:5173' });
  const operatorPage = await operatorContext.newPage();
  const measurements: SpanMeasurement[] = [];
  let skewBefore: ClockSkewBound | null = null;
  let skewAfter: ClockSkewBound | null = null;

  // The head overshoot, bounded rather than described (spec 108 FR-003): the
  // submit's own round trip, taken from the request's resource timing so it is
  // the network's own figure and not another Node-side subtraction.
  //
  // **`requestfinished`, not `response`.** `responseEnd` is the one timing that
  // is not available when the response event fires — it lands when the body
  // finishes — so a listener on `response` reads -1 every time and the head
  // overshoot silently prints as `unseen`. Observed on the first run of this
  // instrument.
  const submitRoundTrips = new Map<string, number>();
  operatorPage.on('requestfinished', (request) => {
    if (request.method() !== 'PUT') return;
    if (!/\/system-variables\/[^/]+\/value$/.test(new URL(request.url()).pathname)) return;
    const responseEnd = request.timing().responseEnd;
    if (responseEnd < 0) return;

    // Keyed by the value the body carried (`systemVariables.api.ts:154`), so a
    // late-finishing PUT can never hand its figure to the next iteration.
    const body = request.postDataJSON() as { value?: unknown } | null;
    const value = body?.value;
    if (typeof value !== 'string') return;
    submitRoundTrips.set(value, responseEnd);
  });

  try {
    await signInAsOperator(operatorPage);
    await operatorPage.getByRole('link', { name: /^system variables$/i }).click();

    // **Bounded before anything is subtracted.** t0 and t1 are stamped in two
    // Chromium renderer processes, so a constant offset between their clocks lands
    // whole in every sample — and the C1 calibration structurally cannot see it,
    // because both arms of a paired run go through the same subtraction.
    skewBefore = await bracketClockSkew(page, operatorPage);
    console.info(describeSkew('before the loop', skewBefore));

    for (let iteration = 0; iteration < ITERATIONS; iteration += 1) {
      // Distinguishable per iteration, so the observation cannot match a value
      // left over from the previous one — and so the submit round trip can be
      // paired by value rather than by arrival order.
      const value = `SPAN${iteration}`;
      const budget = observeBudget(startedAt, ITERATIONS - iteration);

      await armOverlayPaint(page, value);
      await fillValue(operatorPage, wall.variableName, value);
      await armClickStamp(operatorPage);
      await submitValue(operatorPage, wall.variableName);

      const painted = await awaitOverlayPaint(page, budget);
      const t0 = await readClickStamp(operatorPage);

      if (!painted.armed) {
        measurements.push({ refusal: `iteration ${iteration}: the kiosk observer was never armed` });
        break;
      }
      if (t0 === null) {
        measurements.push({ refusal: `iteration ${iteration}: the operator click never stamped t0` });
        break;
      }
      if (painted.t1 === null) {
        measurements.push({
          refusal:
            `iteration ${iteration}: the value never painted on the tile within ${budget} ms ` +
            `(${painted.mutations} label mutation(s) were seen)`,
        });
        break;
      }

      const elapsed = painted.t1 - t0;

      // **Never clamped.** A negative elapsed is a stepped clock, not a fast
      // journey, and a clamp to zero manufactures a perfect score.
      if (elapsed < 0) {
        measurements.push({
          refusal: `iteration ${iteration}: t1 - t0 was ${elapsed} ms — the clock stepped, and this is never clamped`,
        });
        break;
      }
      if (elapsed > budget) {
        measurements.push({
          refusal: `iteration ${iteration}: ${elapsed} ms exceeds this iteration's ${budget} ms observe budget`,
        });
        break;
      }

      const sample: SpanSample = {
        iteration,
        elapsedMilliseconds: elapsed,
        submitRoundTripMilliseconds: await settleSubmitRoundTrip(submitRoundTrips, value, SUBMIT_SETTLE_MS),
      };

      measurements.push({ sample });
      printSample(sample);
    }

    // Again at the end: two bounds that disagree are drift across the run, which
    // one probe at one instant cannot show.
    skewAfter = await bracketClockSkew(page, operatorPage);
  } finally {
    await operatorPage.close();
    await operatorContext.close();
  }

  // **Printed before any assertion** (FR-008), so the figures survive a red.
  report({ measurements, skewBefore, skewAfter });
  await Promise.all(pending);
  reportLegs(latencyLines, malformedLatencyLines);

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

  // **Every refusal fails the run, naming its iteration.** A measurement harness
  // whose error path drops samples reports the distribution of the samples that
  // were fast enough to be observed — so an unarmed, unstamped, negative or
  // overrunning iteration is loud rather than absent.
  const refusals = measurements
    .map((measurement) => measurement.refusal)
    .filter((refusal): refusal is string => refusal !== undefined);

  expect(refusals, `the span could not be stamped: ${refusals.join('; ')}`).toEqual([]);

  // The correctness claim the polled assertion used to carry, kept now that it
  // no longer produces the figure: the tile really does show the last value.
  await expect(label, 'the tile never showed the final value the operator set').toContainText(`SPAN${ITERATIONS - 1}`, {
    timeout: 30_000,
  });

  const timed = measurements.filter((measurement) => measurement.sample !== undefined);

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
    timed.length === 0 ? 'no iteration completed — see the refusals above' : 'a single run is not a measurement',
  ).toBeGreaterThan(1);
});

import { gatewayApiUrl } from '../api/gateway.js';

/**
 * Reports the two latency legs a kiosk can observe (spec 040, ADR-0122).
 *
 * <p>
 * Two legs of the 800 ms budget happen in the browser and nowhere else — the
 * kiosk decoding a frame, and the overlay being drawn onto it. Constitution
 * §VII makes a dashboard mandatory for every implemented leg, so those numbers
 * have to reach the sink somehow, and nothing gives a browser one: the apps are
 * handed the gateway origin, Keycloak and the layout hub, and no OTLP anything.
 * </p>
 *
 * <p>
 * So the kiosk <b>reports</b> rather than exports. ADR-0118's one-sink rule
 * stays intact — the measurement enters observability through a service's meter
 * like every other number, and no OpenTelemetry SDK ships in the bundle.
 * </p>
 *
 * <p>
 * <b>Sends the number, never the start.</b> A slow or retried post then makes
 * the report late; it can never make the measurement large. Subtracting
 * server-side would put the network inside the figure and would need a clock
 * shared between browser and server. That clock now exists — one SFU serves
 * every tile and its RTCP sender reports carry it (ADR-0128) — but Chromium
 * exposes no per-frame send-to-arrival mapping, so subtracting server-side
 * would still be an estimate rather than a measurement.
 * </p>
 */
/**
 * <p>
 * <b>A closed set, shared with the server, and changed on both sides at once.</b>
 * `RecordKioskLatency` maps this value through a `switch` and returns a 400 for
 * anything it does not know — while {@link reportKioskLatency} swallows failures
 * by design. So a name added here before the server knows it produces a kiosk
 * that reports nothing and looks entirely healthy (spec 045 T014).
 * </p>
 *
 * <p>
 * Two of these are carried on this path but are <b>not</b> latency segments,
 * for two different reasons, and the server routes both to their own
 * instrument. `wall_skew` is not a duration at all: a spread between two tiles
 * is not something any frame spent travelling (ADR-0128). `label_delay` is a
 * duration, but one the kiosk chose to add rather than one a frame spent
 * travelling (ADR-0129).
 * </p>
 */
export type KioskMeasurement =
  'overlay_draw' | 'receive_to_decoded' | 'presentation_buffer' | 'wall_skew' | 'label_delay';

/**
 * Above this, a figure is describing something other than a journey — a
 * backgrounded tab whose timers were throttled, a paused debugger, a clock that
 * moved. Mirrors the server-side ceiling; both apply, and the server is where it
 * is enforced, because a browser is untrusted input.
 */
const ABSURDLY_LONG_MS = 60_000;

/**
 * Reports one measurement. Never throws and never rejects: a kiosk that cannot
 * report its latency must carry on showing video, and an observer that can break
 * the thing it observes is worse than no observer (spec 040 FR-011).
 */
export function reportKioskLatency(
  measurement: KioskMeasurement,
  camera: string,
  elapsedMilliseconds: number,
  getToken: () => Promise<string | null>,
): void {
  // The same two guards the server enforces, applied here so a figure that
  // cannot be describing a journey is not sent at all. This does not replace
  // the server's copy — that is the enforcement point.
  //
  // Nothing is sent rather than a zero: a zero reads as a perfect score for a
  // journey nobody timed, which is the trap the server-side recorder names in
  // its own comment.
  if (!Number.isFinite(elapsedMilliseconds)) return;
  if (elapsedMilliseconds < 0) return;
  if (elapsedMilliseconds > ABSURDLY_LONG_MS) return;

  // The same structured-line idiom as resilienceLog, whose prefix is an
  // observable contract. Alongside the report, not instead of it: reading a
  // console line needs devtools attached to a kiosk, which is the
  // "recorded, not readable" state the constitution calls half discharged.
  // It is here because it costs nothing and it is how these numbers get seen
  // during manual verification — CI cannot produce video.
  console.info('[latency]', { measurement, camera, elapsedMilliseconds });

  void send(measurement, camera, elapsedMilliseconds, getToken);
}

async function send(
  measurement: KioskMeasurement,
  camera: string,
  elapsedMilliseconds: number,
  getToken: () => Promise<string | null>,
): Promise<void> {
  try {
    const token = await getToken();
    if (token === null) return;

    await fetch(gatewayApiUrl('stream-distribution/streams/kiosk-latency'), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ measurement, camera, elapsedMilliseconds }),
    });
  } catch {
    // Swallowed deliberately, and this is the one place in the app where that
    // is right: a lost measurement is a missing sample from a distribution over
    // many frames, and surfacing it would put an observability failure in front
    // of an operator watching a fab.
  }
}

/**
 * Times the overlay-draw leg: from an overlay's state changing to the browser
 * having painted it (ADR-0015, ≤ 50 ms — a whole leg).
 *
 * <p>
 * Two chained animation frames. The first runs after React has committed and
 * <em>before</em> paint; the second runs after that paint has happened. Two
 * callbacks and a subtraction, on a path that already re-renders — which is what
 * keeps the observer clear of the 50 ms it observes (FR-012).
 * </p>
 *
 * <p>
 * <c>performance.now()</c>, never <c>Date.now()</c>: fab clocks are PTP-stepped
 * and an epoch comparison can measure the step instead of the journey. CellPage
 * already carries that reasoning for its highlight timers.
 * </p>
 */
export function measureOverlayDraw(camera: string, getToken: () => Promise<string | null>): void {
  const startedAt = performance.now();
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      reportKioskLatency('overlay_draw', camera, performance.now() - startedAt, getToken);
    });
  });
}

/** One sample of the receiver statistics this reads. */
export interface DecodeSample {
  processingDelaySeconds: number;
  decodeTimeSeconds: number;
  framesDecoded: number;
}

/**
 * The counters a decode reading needs, in the order a missing one is named.
 *
 * <p>
 * Declared once because {@link decodeSampleFrom} and {@link missingDecodeFieldIn}
 * must not drift: a predicate naming a field the sampler does not require, or
 * silent about one it does, is a worse instrument than the bare null it
 * replaces.
 * </p>
 */
const REQUIRED_DECODE_FIELDS = ['framesDecoded', 'totalProcessingDelay', 'totalDecodeTime'] as const;

/** The first inbound video stat of a report, or null when it carries none. */
function inboundVideoStatIn(report: Map<string, unknown>): Record<string, unknown> | null {
  for (const value of report.values()) {
    const stat = value as Record<string, unknown>;
    if (stat['type'] === 'inbound-rtp' && stat['kind'] === 'video') return stat;
  }
  return null;
}

/**
 * Times the receive-to-decoded fragment: the first packet of a frame arriving
 * through to that frame being decoded.
 *
 * <p>
 * <b>A fragment of the `SFU → kiosk decode` leg, not the leg.</b> That budget
 * spans <em>SFU sends → kiosk has decoded</em>, and a browser cannot see the
 * sending end. A clock shared with the SFU now exists (ADR-0128), but Chromium
 * exposes no per-frame send-to-arrival mapping, so the far end can only be
 * estimated — and an estimate is not a measurement. The server-side segment
 * carries <c>isWholeLeg: false</c> so no dashboard reads this as the leg passing.
 * </p>
 *
 * <p>
 * Deltas between two reads, not cumulative totals: the statistics are monotonic
 * counters over the session's life, so a raw ratio reports the session average
 * and flattens exactly the excursion a budget is about.
 * </p>
 */
export function decodeSampleFrom(report: Map<string, unknown>): DecodeSample | null {
  const stat = inboundVideoStatIn(report);
  if (stat === null) return null;
  if (REQUIRED_DECODE_FIELDS.some((field) => typeof stat[field] !== 'number')) return null;

  // Narrowed by the guard immediately above, which TypeScript cannot follow
  // through an array predicate. The guard is the one the predicate reads, so
  // the two cannot disagree about which counters are required.
  const { framesDecoded, totalProcessingDelay, totalDecodeTime } = stat as Record<
    (typeof REQUIRED_DECODE_FIELDS)[number],
    number
  >;

  return {
    processingDelaySeconds: totalProcessingDelay,
    decodeTimeSeconds: totalDecodeTime,
    framesDecoded,
  };
}

/**
 * Names the first counter {@link decodeSampleFrom} could not read, or null.
 *
 * <p>
 * The decode twin of <c>missingLagFieldIn</c>, on a different leg of §IV. This
 * sampler reads the same <c>totalProcessingDelay</c> with the same bare null,
 * and the instrument on this leg has already gone quiet once without saying so
 * (#1889). Issue #2109 lists only the alignment half; fixing one and not the
 * other would leave the identical silence one file over.
 * </p>
 *
 * <p>
 * <b>Null for a report carrying no inbound video stat at all</b> — a session
 * that has not started producing, which happens on every mount (spec 095
 * FR-002). Pure, and it logs nothing (FR-007).
 * </p>
 */
export function missingDecodeFieldIn(report: Map<string, unknown>): string | null {
  const stat = inboundVideoStatIn(report);
  if (stat === null) return null;
  return REQUIRED_DECODE_FIELDS.find((field) => typeof stat[field] !== 'number') ?? null;
}

/**
 * The per-frame figure between two samples, or null when there is nothing to
 * report — no frames decoded since the last read, or a counter that went
 * backwards because the session restarted.
 *
 * <p>
 * Null rather than zero, deliberately. A zero would read as a perfect score for
 * a journey nobody timed.
 * </p>
 */
export function decodeElapsedBetween(previous: DecodeSample, current: DecodeSample): number | null {
  const frames = current.framesDecoded - previous.framesDecoded;
  if (frames <= 0) return null;

  const seconds =
    current.processingDelaySeconds -
    previous.processingDelaySeconds +
    (current.decodeTimeSeconds - previous.decodeTimeSeconds);
  if (seconds < 0) return null;

  return (seconds / frames) * 1000;
}

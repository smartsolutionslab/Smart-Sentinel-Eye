import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  decodeElapsedBetween,
  decodeSampleFrom,
  missingDecodeFieldIn,
  reportKioskLatency,
  type DecodeSample,
} from './kioskLatency.js';

/**
 * Spec 040. The guards and the shape of a report.
 *
 * <p>
 * <b>Nothing here proves a number came from a frame.</b> This is a vitest unit
 * test running in Node: no browser, no peer connection, no stack of any kind, so
 * there is no frame here to have come from — these cover what happens to a
 * figure once it exists, and the figures themselves are read by a person against
 * the run-mode stack. A green suite standing in for an unexercised claim is the
 * same class of error that produced issue 1714.
 * </p>
 */
const token = () => Promise.resolve('a-token');

describe('reportKioskLatency — the guards', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 202 })),
    );
    vi.spyOn(console, 'info').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('Sends a real measurement', async () => {
    reportKioskLatency('overlay_draw', 'cam-1', 18, token);
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledOnce());

    const [, init] = vi.mocked(fetch).mock.calls[0]!;
    expect(JSON.parse(String(init?.body))).toEqual({
      measurement: 'overlay_draw',
      camera: 'cam-1',
      elapsedMilliseconds: 18,
    });
  });

  /**
   * Asserted as an **absence**, never as a zero. A zero would be
   * indistinguishable from a perfect journey and would read as a perfect score
   * for one nobody timed.
   */
  it('Sends nothing at all for a negative measurement', () => {
    reportKioskLatency('overlay_draw', 'cam-1', -3, token);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('Sends nothing for a figure that describes a suspended tab', () => {
    reportKioskLatency('receive_to_decoded', 'cam-1', 120_000, token);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('Sends nothing for a figure that is not a number', () => {
    reportKioskLatency('overlay_draw', 'cam-1', Number.NaN, token);
    expect(fetch).not.toHaveBeenCalled();
  });

  /**
   * FR-011. An observer that can break the thing it observes is worse than no
   * observer — a kiosk whose telemetry endpoint is down must carry on showing
   * video.
   */
  it('Never throws when reporting fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new Error('gateway down');
      }),
    );

    expect(() => reportKioskLatency('overlay_draw', 'cam-1', 18, token)).not.toThrow();
    await vi.waitFor(() => expect(fetch).toHaveBeenCalled());
  });

  it('Carries the camera, so one bad tile is visible among four', async () => {
    reportKioskLatency('receive_to_decoded', 'cam-frozen', 42, token);
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledOnce());

    const [, init] = vi.mocked(fetch).mock.calls[0]!;
    expect(JSON.parse(String(init?.body)).camera).toBe('cam-frozen');
  });

  it('Emits a structured line alongside the report', () => {
    reportKioskLatency('overlay_draw', 'cam-1', 18, token);
    expect(console.info).toHaveBeenCalledWith('[latency]', {
      measurement: 'overlay_draw',
      camera: 'cam-1',
      elapsedMilliseconds: 18,
    });
  });
});

describe('the decode fragment', () => {
  const sample = (framesDecoded: number, processing: number, decode: number): DecodeSample => ({
    framesDecoded,
    processingDelaySeconds: processing,
    decodeTimeSeconds: decode,
  });

  it('Reads the video receiver statistics', () => {
    const report = new Map<string, unknown>([
      ['a', { type: 'inbound-rtp', kind: 'audio', framesDecoded: 1 }],
      ['v', { type: 'inbound-rtp', kind: 'video', framesDecoded: 100, totalProcessingDelay: 2, totalDecodeTime: 0.5 }],
    ]);

    expect(decodeSampleFrom(report)).toEqual({
      framesDecoded: 100,
      processingDelaySeconds: 2,
      decodeTimeSeconds: 0.5,
    });
  });

  /**
   * Deltas, not the cumulative ratio. The statistics are monotonic counters
   * over the session's life, so a raw ratio reports the session average and
   * flattens exactly the excursion a budget is about.
   */
  it('Measures the interval, not the session', () => {
    const elapsed = decodeElapsedBetween(sample(100, 2, 0.5), sample(110, 2.3, 0.55));

    // 0.35s of work over 10 frames = 35 ms per frame.
    expect(elapsed).toBeCloseTo(35, 1);
  });

  /** Null rather than zero: no frames means no journey to time. */
  it('Reports nothing when no frames were decoded', () => {
    expect(decodeElapsedBetween(sample(100, 2, 0.5), sample(100, 2, 0.5))).toBeNull();
  });

  /** A counter that went backwards is a restarted session, not a fast one. */
  it('Reports nothing when the counters went backwards', () => {
    expect(decodeElapsedBetween(sample(100, 2, 0.5), sample(110, 1, 0.2))).toBeNull();
  });
});

/**
 * Spec 095 T003 / FR-001, FR-007. **The decode twin of `missingLagFieldIn`.**
 *
 * <p>
 * `decodeSampleFrom` reads the same `totalProcessingDelay` as
 * `lagSampleFrom`, with the same bare null, on a different leg of §IV —
 * SFU → kiosk decode. Issue #2109 lists `wallAlignment.ts` and stops; fixing
 * one and not the other would leave the identical silence one file over, on the
 * leg whose instrument has already gone quiet once (#1889).
 * </p>
 *
 * <p>
 * The key is deleted, never set to null, for the reason
 * `missingLagFieldIn`'s suite gives.
 * </p>
 */
describe('missingDecodeFieldIn', () => {
  const complete = (): Record<string, unknown> => ({
    framesDecoded: 100,
    totalProcessingDelay: 2,
    totalDecodeTime: 0.5,
  });

  const report = (stat: Record<string, unknown>): Map<string, unknown> =>
    new Map<string, unknown>([['v', { type: 'inbound-rtp', kind: 'video', ...stat }]]);

  it('Names the counter an inbound video stat leaves out', () => {
    const stat = complete();
    delete stat['totalProcessingDelay'];

    expect(missingDecodeFieldIn(report(stat))).toBe('totalProcessingDelay');
  });

  it('Names nothing when every counter reads', () => {
    expect(missingDecodeFieldIn(report(complete()))).toBeNull();
  });

  /** Not-yet-producing is not absent, and it happens on every mount (FR-002). */
  it('Names nothing when the report carries no inbound video stat at all', () => {
    const audioOnly = new Map<string, unknown>([['a', { type: 'inbound-rtp', kind: 'audio', framesDecoded: 100 }]]);

    expect(missingDecodeFieldIn(audioOnly)).toBeNull();
  });
});

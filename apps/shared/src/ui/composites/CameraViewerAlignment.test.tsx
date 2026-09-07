// @vitest-environment jsdom
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MockInstance } from 'vitest';

/**
 * Spec 045 T024 / FR-013. **Alignment must never cost a picture.**
 *
 * <p>
 * The wall gives up its claim, never the video. An observer — or a controller —
 * that can break the thing it manages is worse than not having one, which is
 * the rule spec 040 set for the decode instrument and which holds here for a
 * component that actively writes to the receiver.
 * </p>
 *
 * <p>
 * <b>Spec 095 T004-T006 / FR-001…FR-004.</b> The same component, asked a
 * different question: when the instrument cannot read, does it say so? A wall
 * whose receiver omits one counter is byte-identical today to a wall that is
 * perfectly aligned — no lag reported, no skew computed, no tile badged, and no
 * line anywhere saying why (issue #2109 item 3). The playout half is the same
 * shape from the other end: nothing separates an engine that <em>cannot</em>
 * hold a target from a wall that has not converged <em>yet</em>, and the first
 * is permanent while the second happens on every startup (item 4).
 * </p>
 */

const useGetStreamQueryMock = vi.fn();

vi.mock('@smart-sentinel-eye/shared/api/streams.api', () => ({
  useGetStreamQuery: (...args: unknown[]) => useGetStreamQueryMock(...args),
}));

const statsThrows = vi.fn(() => {
  throw new Error('getStats exploded');
});
const setPlayoutTargetThrows = vi.fn(() => {
  throw new Error('receiver refused the target');
});

// Spec 095: the double answers whatever the case under test installed, and
// `beforeEach` puts the throwing pair back. One mocked module exports one class,
// so a case needing a *reading* receiver — or one that refuses a target rather
// than throwing on it — swaps the behaviour rather than adding a second double.
let statsBehaviour: () => unknown = statsThrows;
let setPlayoutTargetBehaviour: (milliseconds: number) => boolean = setPlayoutTargetThrows;

interface WhepClientDoubleOptions {
  onConnectionStateChange?: (state: string) => void;
}

// Every session the component built, in order. A flapping tile tears its client
// down and builds another, and a test claiming "it did not report twice" has to
// show that a second session really happened.
const sessions: WhepClientDoubleOptions[] = [];
const latestSession = (): WhepClientDoubleOptions => sessions[sessions.length - 1]!;

vi.mock('@smart-sentinel-eye/shared/streaming/WhepClient', () => ({
  WhepClient: class {
    // **The double must report `connected`, or this whole suite is vacuous.**
    // Every effect in CameraViewer guards on `status !== 'live'`, and `status`
    // is driven only by this callback — never by `connect()` resolving. An
    // earlier version of this file omitted it, so the sampler and the actuator
    // were never reached and all three tests passed with both deleted. Which is
    // the exact failure the tests exist to prevent, committed into the tests
    // themselves.
    constructor(private readonly options: WhepClientDoubleOptions) {
      sessions.push(options);
    }

    async connect(videoEl: HTMLVideoElement) {
      videoEl.dataset['connected'] = 'true';
      this.options.onConnectionStateChange?.('connected');
    }
    close() {}
    stats = () => statsBehaviour();
    setPlayoutTarget = (milliseconds: number) => setPlayoutTargetBehaviour(milliseconds);
  },
}));

const { CameraViewer } = await import('@smart-sentinel-eye/shared/ui/composites/CameraViewer');

/**
 * A receiver statistics report carrying one inbound video stat, with the named
 * counters **absent from the object** rather than set to null. An engine that
 * does not implement a statistic omits the property; a null is a different
 * shape and exercises a different branch.
 */
function videoStatWithout(...absent: readonly string[]): Map<string, unknown> {
  const stat: Record<string, unknown> = {
    type: 'inbound-rtp',
    kind: 'video',
    jitterBufferDelay: 2.5,
    jitterBufferEmittedCount: 200,
    totalProcessingDelay: 1.25,
    totalDecodeTime: 0.5,
    framesDecoded: 200,
  };
  for (const field of absent) delete stat[field];
  return new Map<string, unknown>([['v', stat]]);
}

/** A session that has connected but is not producing video yet — every mount. */
const audioOnlyReport = (): Map<string, unknown> =>
  new Map<string, unknown>([['a', { type: 'inbound-rtp', kind: 'audio', framesDecoded: 200 }]]);

describe('CameraViewer when alignment fails', () => {
  let infoSpy: MockInstance<typeof console.info>;

  /**
   * The `[resilience]` lines carrying one transition. Nothing else is counted:
   * `useWhepSession` files every status change down the same channel.
   */
  const resilienceLines = (transition: string) =>
    infoSpy.mock.calls.filter(
      (call) =>
        call[0] === '[resilience]' && (call[1] as { transition?: unknown } | undefined)?.transition === transition,
    );

  /**
   * `connected → disconnected → connected`, through the real state machine: the
   * 5 s disconnect grace, then the first jittered retry, which tears the client
   * down and builds another that reports `connected` from `connect()`.
   */
  const flapThroughReconnect = async () => {
    act(() => {
      latestSession().onConnectionStateChange?.('disconnected');
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
  };

  beforeEach(() => {
    vi.useFakeTimers();
    statsThrows.mockClear();
    setPlayoutTargetThrows.mockClear();
    statsBehaviour = statsThrows;
    setPlayoutTargetBehaviour = setPlayoutTargetThrows;
    sessions.length = 0;
    infoSpy = vi.spyOn(console, 'info').mockImplementation(() => {});
    useGetStreamQueryMock.mockReturnValue({
      data: { state: 'Healthy', whepUrl: 'http://sfu/whep/cam-42', error: null },
      isLoading: false,
      error: undefined,
    });
  });

  afterEach(() => {
    cleanup();
    infoSpy.mockRestore();
    vi.useRealTimers();
  });

  it('Keeps showing video when reading the tile lag throws', async () => {
    const { container } = render(
      <CameraViewer cameraIdentifier="cam-42" getToken={() => Promise.resolve('token')} onLagMeasured={() => {}} />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    // The sampler ran and failed repeatedly; the picture is still there.
    // Asserted BEFORE the survival check: if the throwing double was never
    // reached, "the video survived" is true of a component that did nothing.
    expect(statsThrows, 'the lag sampler must actually have run').toHaveBeenCalled();
    expect(container.querySelector('video')).not.toBeNull();
  });

  it('Keeps showing video when the receiver refuses a playout target', async () => {
    const { container } = render(
      <CameraViewer
        cameraIdentifier="cam-42"
        getToken={() => Promise.resolve('token')}
        playoutTargetMilliseconds={120}
        onLagMeasured={() => {}}
      />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });
    expect(setPlayoutTargetThrows, 'the actuator must actually have run').toHaveBeenCalled();
    // Asserted BEFORE the survival check: if the throwing double was never
    // reached, "the video survived" is true of a component that did nothing.
    expect(statsThrows, 'the lag sampler must actually have run').toHaveBeenCalled();
    expect(container.querySelector('video')).not.toBeNull();
  });

  /**
   * A wall that has not converged, and a page with no wall at all, both pass
   * nothing — and neither is the same as a target of zero, which would jolt the
   * tile's playout to live and undo any alignment it had.
   */
  it('Writes no target at all when the wall has not decided one', async () => {
    render(
      <CameraViewer
        cameraIdentifier="cam-42"
        getToken={() => Promise.resolve('token')}
        playoutTargetMilliseconds={null}
        onLagMeasured={() => {}}
      />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    expect(setPlayoutTargetThrows).not.toHaveBeenCalled();
  });

  /**
   * Spec 095 T004 / FR-001, FR-003. One line, not fourteen.
   *
   * <p>
   * A browser does not grow a statistics field halfway through a session, so a
   * counter that is absent is absent for the session's life. The lag sampler
   * ticks ten times in twenty seconds and the decode sampler four, over the
   * same stat — and this is one fact about the engine, not fourteen dropped
   * events.
   * </p>
   */
  it('Names a statistics counter its receiver does not report, once for the session', async () => {
    const statsMissingField = vi.fn(() => Promise.resolve(videoStatWithout('totalProcessingDelay')));
    statsBehaviour = statsMissingField;
    const onLagMeasured = vi.fn();

    const { container } = render(
      <CameraViewer
        cameraIdentifier="cam-42"
        getToken={() => Promise.resolve('token')}
        onLagMeasured={onLagMeasured}
      />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    // Asserted BEFORE the outcome. This suite has already once passed with the
    // code under test deleted — see the double's own comment — and "one line
    // was logged" would otherwise be judged against a component that never
    // sampled.
    expect(statsMissingField, 'the lag sampler must actually have run').toHaveBeenCalled();
    expect(statsMissingField.mock.calls.length, 'the samplers must have ticked more than once').toBeGreaterThan(1);

    const named = resilienceLines('stats-field-missing');
    expect(named).toHaveLength(1);
    expect(named[0]![1]).toEqual({
      subsystem: 'stream',
      transition: 'stats-field-missing',
      cameraIdentifier: 'cam-42',
      field: 'totalProcessingDelay',
    });
    // The sample is still dropped and the picture is still there: the silence
    // is what changed, not the behaviour (FR-006).
    expect(onLagMeasured).not.toHaveBeenCalled();
    expect(container.querySelector('video')).not.toBeNull();
  });

  /**
   * Spec 095 T004 case 2 / FR-002. **Absent is not the same as not-yet.**
   *
   * <p>
   * A report with no inbound video stat at all is a session that has not
   * started producing. It happens on every mount, and reporting it would put a
   * line on the console every two seconds for the normal case. Green before the
   * fix by construction — this one bounds the fix rather than establishing
   * missing behaviour.
   * </p>
   */
  it('Says nothing about a session that is not producing video yet', async () => {
    const statsWithoutVideo = vi.fn(() => Promise.resolve(audioOnlyReport()));
    statsBehaviour = statsWithoutVideo;

    render(
      <CameraViewer cameraIdentifier="cam-42" getToken={() => Promise.resolve('token')} onLagMeasured={() => {}} />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    expect(statsWithoutVideo, 'the lag sampler must actually have run').toHaveBeenCalled();
    expect(resilienceLines('stats-field-missing')).toHaveLength(0);
  });

  /**
   * Spec 095 T005 / FR-004. **The controller reports what it never actuated.**
   *
   * <p>
   * `setPlayoutTarget` answers false when no video receiver carries
   * `jitterBufferTarget` — Firefox, Safari, pre-115 Chromium. The boolean is
   * discarded today, so a wall on such an engine shows a spread that never
   * closes and nothing anywhere says the actuator is not connected. Distinct
   * from the throwing double above: this receiver is reached, and refuses.
   * </p>
   */
  it('Says so when the receiver cannot hold a playout target at all', async () => {
    const setPlayoutTargetRefuses = vi.fn(() => false);
    setPlayoutTargetBehaviour = setPlayoutTargetRefuses;

    const { container } = render(
      <CameraViewer
        cameraIdentifier="cam-42"
        getToken={() => Promise.resolve('token')}
        playoutTargetMilliseconds={120}
        onLagMeasured={() => {}}
      />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    expect(setPlayoutTargetRefuses, 'the actuator must actually have run').toHaveBeenCalledWith(120);

    const unsupported = resilienceLines('playout-target-unsupported');
    expect(unsupported).toHaveLength(1);
    expect(unsupported[0]![1]).toEqual({
      subsystem: 'stream',
      transition: 'playout-target-unsupported',
      cameraIdentifier: 'cam-42',
    });
    // Spec 045 FR-013: a tile that cannot be aligned still shows video.
    expect(container.querySelector('video')).not.toBeNull();
  });

  /**
   * Spec 095 T006 / FR-003, plan risk R3. **A flap is not a new engine.**
   *
   * <p>
   * The samplers are keyed on `status`, so a tile that goes
   * `live → reconnecting → live` tears its effects down and rebuilds them. A
   * once-per-session guard living inside an effect closure would be reset by
   * every recovery, and a fab wall reconnecting through the night would log the
   * same permanent fact hundreds of times.
   * </p>
   */
  it('Names a missing statistics counter once across a session that flaps', async () => {
    const statsMissingField = vi.fn(() => Promise.resolve(videoStatWithout('totalProcessingDelay')));
    statsBehaviour = statsMissingField;

    render(
      <CameraViewer cameraIdentifier="cam-42" getToken={() => Promise.resolve('token')} onLagMeasured={() => {}} />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });
    const sampledBeforeFlap = statsMissingField.mock.calls.length;

    await flapThroughReconnect();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    // Asserted BEFORE the count: a flap that never rebuilt the session, or a
    // second live window that never sampled, makes "still one line" true of a
    // component that did nothing the second time.
    expect(sessions.length, 'the flap must have built a second session').toBeGreaterThan(1);
    expect(sampledBeforeFlap, 'the first live window must have sampled').toBeGreaterThan(0);
    expect(statsMissingField.mock.calls.length, 'the second live window must have sampled too').toBeGreaterThan(
      sampledBeforeFlap,
    );

    expect(resilienceLines('stats-field-missing')).toHaveLength(1);
  });

  /** Spec 095 T006 / FR-004, plan risk R3 — the actuator half of the same rule. */
  it('Says a playout target is unsupported once across a session that flaps', async () => {
    const setPlayoutTargetRefuses = vi.fn(() => false);
    setPlayoutTargetBehaviour = setPlayoutTargetRefuses;

    render(
      <CameraViewer
        cameraIdentifier="cam-42"
        getToken={() => Promise.resolve('token')}
        playoutTargetMilliseconds={120}
        onLagMeasured={() => {}}
      />,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });
    const actuatedBeforeFlap = setPlayoutTargetRefuses.mock.calls.length;

    await flapThroughReconnect();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000);
    });

    expect(sessions.length, 'the flap must have built a second session').toBeGreaterThan(1);
    expect(actuatedBeforeFlap, 'the first live window must have actuated').toBeGreaterThan(0);
    expect(setPlayoutTargetRefuses.mock.calls.length, 'the second live window must have actuated too').toBeGreaterThan(
      actuatedBeforeFlap,
    );

    expect(resilienceLines('playout-target-unsupported')).toHaveLength(1);
  });
});

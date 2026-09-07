// @vitest-environment jsdom
import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Spec 094 / issue #2111 — **a tile says Live when a picture has arrived**,
 * not when a socket came up.
 *
 * <p>
 * <b>Three reds and three guards, and they are not the same thing.</b> R1–R3
 * fail on the code as it stands; they are the ADR-0139 evidence. G1–G3 pass
 * today and are expected to keep passing — they pin the behaviour the change
 * must not break, and counting them as red would be the shortcut ADR-0139
 * exists to prevent.
 * </p>
 *
 * <p>
 * <b>Why a new file.</b> `CameraViewer.test.tsx` is the characterisation of the
 * transport ladder and stays byte-identical (NFR-003) — the strongest form that
 * claim can take. This file adds the media dimension beside it, the shape
 * `CameraViewerAlignment.test.tsx` already set.
 * </p>
 *
 * <p>
 * <b>The existing suite currently asserts the defect, and that is worth saying
 * out loud.</b> `ontrack` appears exactly once in `CameraViewer.test.tsx` — a
 * field declaration on the fake at `:18` — and is never fired. So every one of
 * its tests that reaches `connected` does so with no track ever delivered, and
 * asserts Live. Those tests are not wrong; they were written about the
 * transport ladder. But "the suite is green" has never been evidence about
 * whether a picture arrived, which is why the instrument below has to be
 * supplied rather than reused.
 * </p>
 *
 * <p>
 * <b>The instrument is `getVideoPlaybackQuality().totalVideoFrames`</b>, read
 * off the tile's own element — decided in spec 077 §3, which cites #2111 by
 * number, and already in use at `e2e/click-to-first-frame.spec.ts:286`. Not
 * `framesDecoded`: `CameraViewer`'s sampler for it is gated on
 * `status === 'live'` (`CameraViewer.tsx:113`), so under any design where Live
 * means media the signal deciding the gate is switched on by the gate.
 * </p>
 */

const useGetStreamQueryMock = vi.fn();

vi.mock('@smart-sentinel-eye/shared/api/streams.api', () => ({
  useGetStreamQuery: (...args: unknown[]) => useGetStreamQueryMock(...args),
}));

const { CameraViewer } = await import('@smart-sentinel-eye/shared/ui/composites/CameraViewer');

/** Spec 094: the watchdog window, derived from spec 002 FR-013. */
const MEDIA_WATCHDOG_MS = 3_000;
/** Spec 094: the media poll interval, 1/12 of the window. */
const MEDIA_POLL_MS = 250;

/**
 * The #2108 double, copied from `CameraViewer.test.tsx:12-56` rather than
 * shared: that file is the characterisation and must not be touched. It reaches
 * `connected` and **never fires `ontrack`**, which is exactly the negotiated
 * session that delivers no video track (spec 094 §"What this is").
 */
class FakePeerConnection {
  static instances: FakePeerConnection[] = [];
  static lastInstance(): FakePeerConnection {
    return FakePeerConnection.instances[FakePeerConnection.instances.length - 1]!;
  }
  ontrack: ((event: { streams: unknown[] }) => void) | null = null;
  onconnectionstatechange: (() => void) | null = null;
  connectionState = 'new';
  iceGatheringState = 'complete';
  localDescription: { type: string; sdp: string } | null = null;
  closed = false;

  constructor() {
    FakePeerConnection.instances.push(this);
  }

  addTransceiver() {}

  async createOffer() {
    return { type: 'offer', sdp: 'v=0\r\no=fake 1 1 IN IP4 127.0.0.1\r\ns=-\r\n' };
  }

  async setLocalDescription(desc: { type: string; sdp: string }) {
    this.localDescription = desc;
  }

  async setRemoteDescription() {}

  getReceivers() {
    return [];
  }

  addEventListener() {}

  removeEventListener() {}

  close() {
    this.closed = true;
  }

  setConnectionState(state: string) {
    this.connectionState = state;
    this.onconnectionstatechange?.();
  }
}

/** Minimal duck-typed WHEP answer; jsdom has no Response constructor. */
function sdpResponse(location: string | null = 'http://sfu.test/cam-42/whep/session-1') {
  return {
    ok: true,
    status: 200,
    headers: { get: (name: string) => (name.toLowerCase() === 'location' ? location : null) },
    text: async () => 'v=0\r\no=mediamtx 1 1 IN IP4 127.0.0.1\r\ns=-\r\n',
  };
}

/**
 * How many frames the tile's decoder has produced, read by the stub below.
 * A mutable closure counter rather than a per-element property: React creates
 * the element, so the instrument has to exist on the prototype before it does.
 */
let producedFrames = 0;

/**
 * jsdom 30.0.1 does not implement `getVideoPlaybackQuality` (asserted in G2),
 * so the tests that need the instrument install it and every test removes it
 * again. That absence is load-bearing rather than incidental: it is what keeps
 * the ten existing `CameraViewer` tests inert and green (FR-005).
 */
function installFrameCounter() {
  Object.defineProperty(window.HTMLVideoElement.prototype, 'getVideoPlaybackQuality', {
    configurable: true,
    value: () => ({ totalVideoFrames: producedFrames }),
  });
}

function removeFrameCounter() {
  delete (window.HTMLVideoElement.prototype as unknown as Record<string, unknown>)['getVideoPlaybackQuality'];
}

let streamHealth: { state: string; whepUrl: string; error: string | null } | undefined;

function setHealth(state: string, error: string | null = null) {
  streamHealth = { state, whepUrl: 'http://sfu.test/cam-42/whep', error };
}

function renderViewer() {
  return render(<CameraViewer cameraIdentifier="cam-42" getToken={async () => 'token'} />);
}

/** Drains the async connect chain (offer → POST → answer) inside act. */
async function flushConnect() {
  await act(async () => {
    for (let i = 0; i < 12; i += 1) {
      await Promise.resolve();
    }
  });
}

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

/** Brings the newest session's transport up — and nothing else. No track. */
async function reachConnected() {
  await flushConnect();
  act(() => {
    FakePeerConnection.lastInstance().setConnectionState('connected');
  });
}

/**
 * Every label `CameraViewer` can draw except Live (`CameraViewer.tsx:295-303`).
 *
 * <p>
 * <b>There is no "Live" text to look for.</b> The scrim is rendered only while
 * `status !== 'live'` (`:233`), so `queryByText('Live')` is null in every state
 * including the defect — asserting it would pass against the bug. Live is
 * observable exactly as the absence of every other label, and that is what this
 * asserts.
 * </p>
 */
const NON_LIVE_LABELS = [
  'Connecting…',
  'Reconnecting…',
  'Idle',
  'Stream is offline',
  'Viewer error',
  'Provisioning stream…',
];

function expectLive() {
  for (const label of NON_LIVE_LABELS) {
    expect(screen.queryByText(label), `expected the tile to be Live, but it shows "${label}"`).toBeNull();
  }
}

describe('CameraViewer media confirmation', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    producedFrames = 0;
    FakePeerConnection.instances = [];
    (globalThis as unknown as { RTCPeerConnection: typeof FakePeerConnection }).RTCPeerConnection = FakePeerConnection;
    fetchMock = vi.fn().mockResolvedValue(sdpResponse());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    streamHealth = undefined;
    useGetStreamQueryMock.mockImplementation(() => ({
      data: streamHealth,
      isLoading: false,
      error: undefined,
    }));
    // Pins the ±20% jitter factor to 1.0, as the neighbouring suite does, so
    // the ladder's delays are the base delays.
    vi.spyOn(Math, 'random').mockReturnValue(0.5);
  });

  afterEach(() => {
    cleanup();
    removeFrameCounter();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  // ── R1–R3: RED. These fail on the code as it stands. ──────────────────────

  /** R1 — FR-001 / AS-1.2. Today the tile says Live the instant `connected` fires. */
  it('Does not claim Live while the element has produced no frames', async () => {
    installFrameCounter();
    setHealth('Healthy');
    renderViewer();

    await reachConnected();
    await advance(MEDIA_WATCHDOG_MS - 1);

    expect(screen.getByText('Connecting…')).toBeDefined();
    // Still inside the window, so the watchdog must not have fired either.
    expect(screen.queryByText('Reconnecting…')).toBeNull();
  });

  /**
   * R2 — FR-003 / AS-1.3. Today the session never leaves Live and there is one
   * peer connection forever.
   *
   * <p>
   * This is the test that answers the objection to R1: a tile that merely
   * withholds Live and then sits on `Connecting…` for the life of the page is a
   * worse outcome than the bug. Every path out of `connected` must end in a
   * scheduled transition.
   * </p>
   */
  it('Retries a session that connects and never produces a frame', async () => {
    installFrameCounter();
    setHealth('Healthy');
    renderViewer();

    await reachConnected();
    await advance(MEDIA_WATCHDOG_MS);

    expect(screen.getByText('Reconnecting…')).toBeDefined();
    expect(FakePeerConnection.instances).toHaveLength(1);

    await advance(1000); // base delay; jitter factor pinned to 1.0
    await flushConnect();

    expect(FakePeerConnection.instances).toHaveLength(2);
  });

  /**
   * R3 — FR-004 / AS-1.4. Today no retry happens at all.
   *
   * <p>
   * <b>The trap this pins.</b> `attemptRef` is reset by `connected`
   * (`useWhepSession.ts:140`), and `connected` fires on every retry of a
   * mediumless source. A watchdog added without moving that reset therefore
   * never backs off: it re-opens a WHEP session every `N + 1 s` forever — one
   * POST per 4 s per tile, 250 tiles, indefinitely. A fixed-interval retry
   * passes R2 and fails here, which is the whole point of asserting the third
   * gap rather than the first.
   * </p>
   */
  it('Backs off across repeated mediumless sessions rather than retrying at a fixed cadence', async () => {
    installFrameCounter();
    setHealth('Healthy');
    renderViewer();

    // Cycle one: the base 1 s.
    await reachConnected();
    await advance(MEDIA_WATCHDOG_MS);
    await advance(999);
    expect(FakePeerConnection.instances).toHaveLength(1);
    await advance(1);
    await flushConnect();
    expect(FakePeerConnection.instances).toHaveLength(2);

    // Cycle two: 2 s.
    await reachConnected();
    await advance(MEDIA_WATCHDOG_MS);
    await advance(1999);
    expect(FakePeerConnection.instances).toHaveLength(2);
    await advance(1);
    await flushConnect();
    expect(FakePeerConnection.instances).toHaveLength(3);

    // Cycle three: 4 s, and this is the assertion that fails on a ladder that
    // never grows.
    await reachConnected();
    await advance(MEDIA_WATCHDOG_MS);
    await advance(3999);
    expect(
      FakePeerConnection.instances,
      'a retry spaced by the base delay would already have opened a fourth session',
    ).toHaveLength(3);
    await advance(1);
    await flushConnect();
    expect(FakePeerConnection.instances).toHaveLength(4);
  });

  // ── G1–G3: GUARDS. These pass today and must keep passing. ────────────────

  /** G1 — FR-002 / FR-006 / AS-1.1. **Green today**: the tile is Live from `connected`. */
  it('Goes Live as soon as a frame is produced and stays there', async () => {
    installFrameCounter();
    setHealth('Healthy');
    renderViewer();

    await reachConnected();
    producedFrames = 1;
    await advance(MEDIA_POLL_MS);

    expectLive();

    await advance(30_000);

    expectLive();
    expect(FakePeerConnection.instances, 'a confirmed session must not be retried').toHaveLength(1);
  });

  /**
   * G2 — FR-005 / AS-1.7. **Green today**, and it is what keeps the ten
   * existing `CameraViewer` tests green unmodified: absence of a measurement is
   * not evidence of absence of media.
   *
   * <p>
   * The precondition below is also this file's leak check. It runs after three
   * tests that installed the instrument, so an `afterEach` that failed to
   * remove it fails here rather than silently making the fallback path
   * untested.
   * </p>
   */
  it('Promotes on connected when the browser cannot report frames', async () => {
    expect(
      typeof document.createElement('video').getVideoPlaybackQuality,
      'jsdom must not supply the instrument, or this test is about a different browser',
    ).toBe('undefined');

    setHealth('Healthy');
    renderViewer();

    await reachConnected();

    expectLive();

    await advance(30_000);

    expectLive();
    expect(FakePeerConnection.instances, 'no watchdog may run where nothing can be measured').toHaveLength(1);
  });

  /** G3 — FR-006 / AS-1.5. **Green today**: a blip inside the grace window is absorbed. */
  it('Keeps a tile that is showing a picture Live across a transport blip', async () => {
    installFrameCounter();
    producedFrames = 1;
    setHealth('Healthy');
    renderViewer();

    await reachConnected();
    await advance(MEDIA_POLL_MS);
    expectLive();

    act(() => {
      FakePeerConnection.lastInstance().setConnectionState('disconnected');
    });
    act(() => {
      FakePeerConnection.lastInstance().setConnectionState('connected');
    });
    await advance(30_000);

    expectLive();
    expect(FakePeerConnection.instances).toHaveLength(1);
  });
});

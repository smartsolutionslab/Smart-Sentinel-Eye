import { useCallback, useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';
import type { StreamState } from '@smart-sentinel-eye/shared/api/streams.api';
import { logResilienceEvent } from '@smart-sentinel-eye/shared/observability/resilienceLog';
import { WhepClient } from '@smart-sentinel-eye/shared/streaming/WhepClient';

export type CameraViewerStatus = 'idle' | 'connecting' | 'live' | 'reconnecting' | 'error' | 'offline';

export interface WhepSessionOptions {
  cameraIdentifier: string;
  whepUrl: string | undefined;
  streamState: StreamState | undefined;
  streamError: string | null;
  getToken: () => Promise<string | null>;
}

export interface WhepSessionResult {
  videoRef: RefObject<HTMLVideoElement | null>;
  status: CameraViewerStatus;
  errorMessage: string | null;
  /**
   * The session's receiver statistics, or null when there is no connection
   * (spec 040).
   *
   * <p>
   * One leg of the latency budget can be observed nowhere else — `inbound-rtp`
   * is where the kiosk's decode timing lives. <b>Read-only:</b> it hands back a
   * report from a connection this hook already owns, and nothing about the
   * session, its retries or its teardown changes (FR-011).
   * </p>
   */
  stats: () => Promise<RTCStatsReport> | null;
  /**
   * Sets this tile's playout target in milliseconds, returning whether it was
   * applied (spec 045, ADR-0128).
   *
   * <p>
   * The actuator a wall uses to bring its tiles to a common instant. Like
   * {@link stats}, it reaches an object this hook already owns and changes
   * nothing about the session lifecycle.
   * </p>
   *
   * <p>
   * Only a wall calls this. `management-web` shows one camera at a time, has
   * nothing to align it with, and passes no target.
   * </p>
   */
  setPlayoutTarget: (milliseconds: number) => boolean;
}

const RETRY_BASE_MS = 1_000;
const RETRY_CAP_MS = 15_000;
const DISCONNECT_GRACE_MS = 5_000;
// Spec 002 FR-013 budgets click → first decoded frame at 3 s p95: a session
// `connected` this long with no frame has already breached it on its own.
const MEDIA_WATCHDOG_MS = 3_000;
// 1/12 of the window. A timer rather than requestAnimationFrame, which stops in
// a background tab and would let the watchdog demote a healthy backgrounded tile.
const MEDIA_POLL_MS = 250;

function jitteredRetryDelay(attempt: number): number {
  const base = Math.min(RETRY_BASE_MS * 2 ** attempt, RETRY_CAP_MS);
  // Full ±20% jitter keeps 16+ tiles from synchronizing their reconnect
  // attempts after a shared outage (spec 011 SC-005).
  return base * (0.8 + Math.random() * 0.4);
}

/**
 * Whether this browser can be asked how many frames the tile has decoded —
 * absent from jsdom and Firefox, so checked rather than assumed: gating Live on
 * an absent instrument says "not live" over good video, forever (FR-005).
 */
function canObserveFrames(videoEl: HTMLVideoElement): boolean {
  return typeof videoEl.getVideoPlaybackQuality === 'function';
}

/**
 * Frames this element's decoder has produced, or 0 where it cannot be asked.
 * The instrument spec 077 §3 decided, read the same way by
 * `e2e/click-to-first-frame.spec.ts`.
 */
function framesProduced(videoEl: HTMLVideoElement): number {
  return canObserveFrames(videoEl) ? videoEl.getVideoPlaybackQuality().totalVideoFrames : 0;
}

/**
 * Owns the per-tile stream session state machine (spec 011 data-model §1).
 * "Live" means a frame was decoded into this tile's element — never the WHEP
 * POST succeeding, and never the transport state alone, which is a fact about a
 * socket that left Live standing over black (spec 094, #2111). Failed sessions,
 * and sessions that never produce a picture, are retried indefinitely with
 * jittered exponential backoff (FR-001…FR-005).
 */
export function useWhepSession(options: WhepSessionOptions): WhepSessionResult {
  const { cameraIdentifier, whepUrl, streamState, streamError, getToken } = options;
  const videoRef = useRef<HTMLVideoElement>(null);
  // Spec 040: the live client, so a caller can read receiver statistics.
  // Read-only access to an object this hook already owns.
  const clientRef = useRef<WhepClient | null>(null);
  const [status, setStatus] = useState<CameraViewerStatus>('idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [retryNonce, setRetryNonce] = useState(0);
  const statusRef = useRef<CameraViewerStatus>('idle');
  const attemptRef = useRef(0);
  const previousStreamStateRef = useRef<StreamState | undefined>(undefined);

  // Callers commonly pass getToken as a fresh inline closure
  // (e.g. () => Promise.resolve(auth.user?.access_token)), so its identity
  // changes on every parent render. Hold the latest reference and read it
  // at connect time, so the effect below doesn't tear down and renegotiate
  // the RTCPeerConnection on every render — only when the stream changes.
  const getTokenRef = useRef(getToken);
  useEffect(() => {
    getTokenRef.current = getToken;
  });

  const transitionTo = useCallback(
    (next: CameraViewerStatus, message: string | null = null) => {
      setErrorMessage(message);
      if (statusRef.current === next) return;
      logResilienceEvent('stream', `${statusRef.current}→${next}`, { cameraIdentifier });
      statusRef.current = next;
      setStatus(next);
    },
    [cameraIdentifier],
  );

  const offlineMessage = streamState === 'Offline' ? (streamError ?? 'Stream is offline.') : null;

  useEffect(() => {
    void retryNonce; // dep only: each bump forces a fresh connection attempt
    const videoEl = videoRef.current;
    if (!whepUrl || !videoEl) return undefined;
    if (offlineMessage !== null) {
      // `status` is one state machine driven from two directions: derived props
      // (this branch) and the async connection lifecycle below, which
      // transitions from promise callbacks the rule cannot see. Deriving the
      // offline case during render would leave the machine with two owners and
      // a ref mirror to keep in step. Restructuring it is a change to a §IV-path
      // hook and wants its own spec plus a latency check, not a lint fix.
      // eslint-disable-next-line react-hooks/set-state-in-effect -- see above
      transitionTo('offline', offlineMessage);
      return undefined;
    }

    const controller = new AbortController();
    let disposed = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    let graceTimer: ReturnType<typeof setTimeout> | null = null;
    let mediaTimer: ReturnType<typeof setTimeout> | null = null;
    let watchdogTimer: ReturnType<typeof setTimeout> | null = null;
    // Per session, and only ever set to true: a local of this effect's closure,
    // created fresh for each WhepClient and unable to outlive one (FR-006).
    let mediaConfirmed = false;

    const clearMediaTimers = () => {
      if (mediaTimer !== null) clearTimeout(mediaTimer);
      if (watchdogTimer !== null) clearTimeout(watchdogTimer);
      mediaTimer = null;
      watchdogTimer = null;
    };

    const scheduleRetry = (message: string | null) => {
      if (disposed || retryTimer !== null) return;
      transitionTo('reconnecting', message);
      const delay = jitteredRetryDelay(attemptRef.current);
      attemptRef.current += 1;
      retryTimer = setTimeout(() => setRetryNonce((nonce) => nonce + 1), delay);
    };

    const confirmMedia = () => {
      mediaConfirmed = true;
      clearMediaTimers();
      // FR-004: reset where the session demonstrably succeeded. `connected` fires
      // on every retry of a mediumless source, so resetting there would flatten
      // the backoff ladder to a fixed cadence forever.
      attemptRef.current = 0;
      transitionTo('live');
    };

    const pollForMedia = () => {
      if (disposed) return;
      if (framesProduced(videoEl) > 0) {
        confirmMedia();
        return;
      }
      mediaTimer = setTimeout(pollForMedia, MEDIA_POLL_MS);
    };

    const armMediaWatch = () => {
      // FR-006: confirmation is sticky, so nothing re-arms the watch once a
      // picture arrived; a blip before one keeps the window already open.
      if (mediaConfirmed || mediaTimer !== null || watchdogTimer !== null) return;
      mediaTimer = setTimeout(pollForMedia, MEDIA_POLL_MS);
      watchdogTimer = setTimeout(() => {
        // FR-003: no path out of `connected` leaves the tile on Connecting… with
        // nothing pending — the poll confirms, or this re-enters the ladder.
        clearMediaTimers();
        scheduleRetry('No video received. Reconnecting…');
      }, MEDIA_WATCHDOG_MS);
    };

    const onConnectionStateChange = (state: RTCPeerConnectionState) => {
      if (disposed) return;
      if (state === 'connected') {
        if (graceTimer !== null) {
          clearTimeout(graceTimer);
          graceTimer = null;
        }
        if (mediaConfirmed) {
          // A transport blip on a tile that is already showing video (FR-006).
          transitionTo('live');
          return;
        }
        if (!canObserveFrames(videoEl)) {
          // FR-005: absence of a measurement is not evidence of absence of media.
          // This browser keeps today's behaviour exactly, and there `connected`
          // is the strongest evidence of success available.
          attemptRef.current = 0;
          transitionTo('live');
          return;
        }
        armMediaWatch();
        return;
      }
      if (state === 'failed') {
        scheduleRetry('Connection failed. Reconnecting…');
        return;
      }
      if (state === 'disconnected' && graceTimer === null && retryTimer === null) {
        // Grace window: ICE consent checks can self-heal a micro-blip, so
        // retrying instantly would churn sessions (spec 011 research R1).
        graceTimer = setTimeout(() => {
          graceTimer = null;
          scheduleRetry('Connection lost. Reconnecting…');
        }, DISCONNECT_GRACE_MS);
      }
    };

    // Spec 040: held so the caller can read receiver statistics. Read-only —
    // nothing about the session lifecycle changes (FR-011).
    clientRef.current = null;
    const client = new WhepClient({
      whepUrl,
      getToken: () => getTokenRef.current(),
      onConnectionStateChange,
    });
    clientRef.current = client;
    transitionTo('connecting');
    client.connect(videoEl, controller.signal).catch((cause: unknown) => {
      if (disposed || controller.signal.aborted) return;
      scheduleRetry(cause instanceof Error ? cause.message : String(cause));
    });

    return () => {
      disposed = true;
      if (retryTimer !== null) clearTimeout(retryTimer);
      if (graceTimer !== null) clearTimeout(graceTimer);
      clearMediaTimers();
      controller.abort();
      client.close();
      clientRef.current = null;
    };
  }, [whepUrl, offlineMessage, retryNonce, transitionTo]);

  useEffect(() => {
    const previous = previousStreamStateRef.current;
    previousStreamStateRef.current = streamState;
    if (streamState === 'Degraded' && statusRef.current === 'live') {
      transitionTo('reconnecting', streamError ?? 'Source unreachable. Reconnecting…');
      return;
    }
    if (previous === 'Degraded' && streamState === 'Healthy') {
      // FR-005: a health recovery re-establishes a real session — the label
      // only returns to Live once the new peer connection reports connected.
      setRetryNonce((nonce) => nonce + 1);
    }
  }, [streamState, streamError, transitionTo]);

  // Stable across renders, deliberately. Callers put this in effect dependency
  // arrays, and a fresh identity each render tears their effect down and rebuilds
  // it — which silently killed the decode sampler: its 5 s interval was cleared
  // before it could fire twice, and its "previous sample" was reset every time,
  // so it reported nothing at all against real video (issue 1889).
  const stats = useCallback(() => clientRef.current?.stats() ?? null, []);

  // Stable for the same reason as `stats` above, and it is the same trap: a
  // fresh identity each render rebuilds the caller's effect, and the wall
  // controller's sampling interval would be cleared before it could take a
  // second sample — which is exactly what silently killed the decode sampler
  // (issue 1889). A controller that never samples twice reports nothing and
  // aligns nothing, while looking entirely healthy.
  const setPlayoutTarget = useCallback(
    (milliseconds: number) => clientRef.current?.setPlayoutTarget(milliseconds) ?? false,
    [],
  );

  return { videoRef, status, errorMessage, stats, setPlayoutTarget };
}

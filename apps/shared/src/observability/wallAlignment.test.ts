import { describe, it, expect } from 'vitest';
import {
  bufferDelayBetween,
  lagBetween,
  lagSampleFrom,
  missingLagFieldIn,
  skewAcross,
  wallTargetFrom,
  PRESENTATION_BUFFER_BUDGET_MS,
  settleAlignment,
  initialAlignmentState,
  type LagSample,
} from './wallAlignment.js';

/**
 * Spec 045. The arithmetic behind a wall showing one instant.
 *
 * <p>
 * <b>Nothing here proves a wall looks aligned.</b> CI has no video and no SFU,
 * so these cover the figures once they exist. That the tiles of a real wall
 * converge is T026, and T026 is a person.
 * </p>
 *
 * <p>
 * Every assertion about skew below <b>induces the skew first</b>. On an idle box
 * with two identical sources the spread was already 9.4 ms before this feature
 * existed (research R6), so a test that merely asserts a small spread passes
 * with the controller deleted.
 * </p>
 */

function report(stat: Record<string, unknown>): Map<string, unknown> {
  return new Map<string, unknown>([['x', { type: 'inbound-rtp', kind: 'video', ...stat }]]);
}

const sample = (over: Partial<LagSample> = {}): LagSample => ({
  jitterBufferDelaySeconds: 1,
  jitterBufferEmittedCount: 100,
  processingDelaySeconds: 0.5,
  framesDecoded: 100,
  ...over,
});

describe('lagSampleFrom', () => {
  it('Reads the four counters off an inbound video stream', () => {
    const got = lagSampleFrom(
      report({
        jitterBufferDelay: 2.5,
        jitterBufferEmittedCount: 200,
        totalProcessingDelay: 1.25,
        framesDecoded: 200,
      }),
    );

    expect(got).toEqual({
      jitterBufferDelaySeconds: 2.5,
      jitterBufferEmittedCount: 200,
      processingDelaySeconds: 1.25,
      framesDecoded: 200,
    });
  });

  it('Reads nothing from an audio stream', () => {
    const audio = new Map<string, unknown>([['x', { type: 'inbound-rtp', kind: 'audio', jitterBufferDelay: 1 }]]);
    expect(lagSampleFrom(audio)).toBeNull();
  });

  /** A partial report is not a tile with no lag. */
  it('Reads nothing when a counter is missing', () => {
    expect(lagSampleFrom(report({ jitterBufferDelay: 2.5, framesDecoded: 200 }))).toBeNull();
  });
});

/**
 * Spec 095 T002 / FR-001, FR-007. **A read that fails names the field it could
 * not read.**
 *
 * <p>
 * `lagSampleFrom` answers null for a report it cannot use, and that null is
 * indistinguishable from a wall with nothing to say — which is the whole of
 * issue #2109 item 3. This predicate is the other half: the same field list,
 * read for the name rather than the value, so the caller can say which counter
 * was absent. It stays pure and it does no logging (FR-007) — the caller
 * reports.
 * </p>
 *
 * <p>
 * <b>The key is deleted, never set to null.</b> An engine that does not
 * implement a statistic omits the property; a null is a different shape and
 * would exercise a different branch. This repository has never observed
 * `totalProcessingDelay` absent — spec 095 assumption A1 — and #1889, the one
 * real observation it has, found all three fields present and numeric. These
 * cover the case if it ever occurs; they are not evidence that it has.
 * </p>
 */
describe('missingLagFieldIn', () => {
  const complete = (): Record<string, unknown> => ({
    jitterBufferDelay: 2.5,
    jitterBufferEmittedCount: 200,
    totalProcessingDelay: 1.25,
    framesDecoded: 200,
  });

  it('Names the counter an inbound video stat leaves out', () => {
    const stat = complete();
    delete stat['totalProcessingDelay'];

    expect(missingLagFieldIn(report(stat))).toBe('totalProcessingDelay');
  });

  it('Names nothing when every counter reads', () => {
    expect(missingLagFieldIn(report(complete()))).toBeNull();
  });

  /**
   * A session that has not started producing is not a defect, and it happens on
   * every mount. Naming a field here would put a line on the console every two
   * seconds for the normal case (FR-002).
   */
  it('Names nothing when the report carries no inbound video stat at all', () => {
    const audioOnly = new Map<string, unknown>([['a', { type: 'inbound-rtp', kind: 'audio', framesDecoded: 200 }]]);

    expect(missingLagFieldIn(audioOnly)).toBeNull();
  });
});

describe('lagBetween', () => {
  it('Reports the per-frame lag in milliseconds', () => {
    const previous = sample();
    const current = sample({
      jitterBufferDelaySeconds: 1 + 0.5, // 500 ms over 50 frames = 10 ms/frame
      jitterBufferEmittedCount: 150,
      processingDelaySeconds: 0.5 + 0.1, // 100 ms over 50 frames = 2 ms/frame
      framesDecoded: 150,
    });

    expect(lagBetween(previous, current)).toBeCloseTo(12, 6);
  });

  /**
   * Null rather than zero, deliberately: a zero would read as a perfect score
   * for a journey nobody timed.
   */
  it('Reports nothing when no frames were emitted', () => {
    expect(lagBetween(sample(), sample())).toBeNull();
  });

  it('Reports nothing when a counter went backwards after a reconnect', () => {
    const previous = sample({ jitterBufferEmittedCount: 500, framesDecoded: 500 });
    const current = sample({ jitterBufferEmittedCount: 20, framesDecoded: 20 });
    expect(lagBetween(previous, current)).toBeNull();
  });

  it('Reports nothing when the buffer counter went backwards but the frame count did not', () => {
    const previous = sample({ jitterBufferDelaySeconds: 5 });
    const current = sample({
      jitterBufferDelaySeconds: 1,
      jitterBufferEmittedCount: 150,
      framesDecoded: 150,
    });
    expect(lagBetween(previous, current)).toBeNull();
  });

  /**
   * **This is the test that fails if someone "simplifies" `lagBetween` into a
   * lifetime average**, which is the whole reason the delta exists.
   *
   * <p>
   * A session that ran 10 ms/frame for 1 000 frames and then blew out to
   * 200 ms/frame for 50: the cumulative ratio still reads ~19 ms and the wall
   * looks fine, while the delta reports the 200 ms excursion that is actually
   * happening. A budget is a claim about the tail.
   * </p>
   */
  it('Disagrees with a lifetime average across an excursion, and reports the excursion', () => {
    const calm: LagSample = {
      jitterBufferDelaySeconds: 10, // 1 000 frames at 10 ms
      jitterBufferEmittedCount: 1_000,
      processingDelaySeconds: 0,
      framesDecoded: 1_000,
    };
    const afterExcursion: LagSample = {
      jitterBufferDelaySeconds: 10 + 10, // 50 more frames at 200 ms
      jitterBufferEmittedCount: 1_050,
      processingDelaySeconds: 0,
      framesDecoded: 1_050,
    };

    const cumulative = (afterExcursion.jitterBufferDelaySeconds / afterExcursion.jitterBufferEmittedCount) * 1000;
    const delta = lagBetween(calm, afterExcursion);

    expect(delta).toBeCloseTo(200, 6);
    expect(cumulative).toBeLessThan(20);
    // The point: the average would report a healthy wall during a breach.
    expect(delta! - cumulative).toBeGreaterThan(150);
  });
});

describe('bufferDelayBetween', () => {
  /**
   * **The two figures must not be the same number**, and this is the test that
   * fails if someone reports `lagBetween` as the presentation-buffer leg.
   *
   * <p>
   * Processing delay is already the decode leg (`receive_to_decoded`), so
   * charging the 200 ms presentation budget for it would attribute one leg's
   * time to another — the confusion `KioskReceiveToDecoded` refuses from the
   * other direction.
   * </p>
   */
  it('Reports the buffer wait alone, excluding the decode leg time', () => {
    const previous = sample();
    const current = sample({
      jitterBufferDelaySeconds: 1 + 0.5, // 500 ms over 50 frames = 10 ms/frame
      jitterBufferEmittedCount: 150,
      processingDelaySeconds: 0.5 + 0.1, // 100 ms over 50 frames = 2 ms/frame
      framesDecoded: 150,
    });

    expect(bufferDelayBetween(previous, current)).toBeCloseTo(10, 6);
    // The control signal is the whole of what makes a tile late; the leg is not.
    expect(lagBetween(previous, current)).toBeCloseTo(12, 6);
  });

  it('Reports nothing when no frames were emitted', () => {
    expect(bufferDelayBetween(sample(), sample())).toBeNull();
  });

  it('Reports nothing when the counters reset on reconnect', () => {
    const previous = sample({ jitterBufferDelaySeconds: 5, jitterBufferEmittedCount: 500 });
    const current = sample({ jitterBufferDelaySeconds: 1, jitterBufferEmittedCount: 600 });
    expect(bufferDelayBetween(previous, current)).toBeNull();
  });
});

/**
 * A tile with an explicit split between what makes it late and what this leg
 * is spending. The split is the whole point: the target equalises `lag`, the
 * budget bounds `buffer`, and the difference belongs to the decode leg.
 */
const tile = (camera: string, lagMilliseconds: number, bufferMilliseconds: number) => ({
  camera,
  lagMilliseconds,
  bufferMilliseconds,
});

describe('wallTargetFrom', () => {
  /** Induced: 20/30/120, not three tiles that happened to agree. */
  it('Holds the wall to its slowest tile', () => {
    const target = wallTargetFrom([tile('a', 20, 10), tile('b', 30, 15), tile('c', 120, 60)]);

    expect(target?.targetMilliseconds).toBe(120);
    expect(target?.held).toEqual(['c', 'b', 'a']);
    expect(target?.released).toEqual([]);
  });

  /**
   * **The defect T026 found, as a test.** Both tiles lag ~257 ms — over the
   * 200 ms budget — while each is only buffering ~131 ms. The old rule compared
   * the lag to the budget, released both, badged the whole wall and aligned
   * nothing, while each tile sat comfortably inside the budget it was being
   * judged against.
   */
  it('Holds tiles whose lag exceeds the budget but whose buffer does not', () => {
    const target = wallTargetFrom([tile('left', 256.9, 131.9), tile('right', 257.5, 127.9)]);

    expect(target?.released).toEqual([]);
    expect(target?.held).toEqual(['right', 'left']);
    expect(target?.targetMilliseconds).toBe(257.5);
  });

  /**
   * Held only while the buffer it would need stays inside the budget. Holding
   * the wall to `slow` would make the others buffer ~390 ms — alignment bought
   * past the budget this leg belongs to, which is the silent regression US3
   * exists to catch.
   */
  it('Releases a tile whose target would push the others past the budget', () => {
    const target = wallTargetFrom([tile('a', 20, 10), tile('b', 30, 15), tile('slow', 400, 200)]);

    expect(target?.released).toEqual(['slow']);
    expect(target?.held).toEqual(['b', 'a']);
    expect(target?.targetMilliseconds).toBe(30);
  });

  it('Holds a tile whose required buffer lands exactly on the budget', () => {
    // `a` carries 10 ms of processing, so a 210 ms target asks it for 200 ms of
    // buffer — the budget exactly, which is inside it.
    const target = wallTargetFrom([tile('a', 20, 10), tile('b', 210, 10)]);

    expect(target?.released).toEqual([]);
    expect(target?.targetMilliseconds).toBe(210);
    // Stated against the constant so the case stays on the boundary if the
    // budget ever moves, rather than silently becoming an interior point.
    expect(target!.targetMilliseconds - 10).toBe(PRESENTATION_BUFFER_BUDGET_MS);
  });

  /**
   * FR-004. Not a target of zero — no target at all. A single-tile wall has
   * nothing to align with and must not pay for the feature.
   */
  it('Sets no target at all for a single-tile wall', () => {
    expect(wallTargetFrom([tile('only', 40, 20)])).toBeNull();
  });

  it('Sets no target at all for an empty wall', () => {
    expect(wallTargetFrom([])).toBeNull();
  });

  /** Dropping tiles until one is left leaves nothing to align. */
  it('Sets no target when no two tiles can share one', () => {
    const target = wallTargetFrom([tile('a', 400, 390), tile('b', 900, 890)]);

    expect(target).toBeNull();
  });
});

describe('settleAlignment — hysteresis on the feasibility decision', () => {
  const steady = tile('steady', 30, 15);
  const alsoSteady = tile('also-steady', 45, 20);
  // 250 ms of buffer of its own, so holding the wall to it would ask ~245 ms
  // of `steady` — past the budget, which is what makes it infeasible.
  const slow = tile('slow', 260, 250);

  it('Does not mark a tile on its first infeasible cycle, nor let it set the target', () => {
    const { state, target } = settleAlignment(initialAlignmentState, [steady, alsoSteady, slow]);

    expect(state.released).toEqual([]);
    expect(target?.released).toEqual([]);
    expect(target?.held).toEqual(['also-steady', 'steady']);
    expect(target?.targetMilliseconds).toBe(45);
  });

  it('Marks a tile that is infeasible on consecutive cycles', () => {
    const lags = [steady, alsoSteady, slow];
    const first = settleAlignment(initialAlignmentState, lags);
    const second = settleAlignment(first.state, lags);

    expect(second.state.released).toEqual(['slow']);
    expect(second.target?.released).toEqual(['slow']);
    expect(second.target?.held).toEqual(['also-steady', 'steady']);
  });

  /**
   * A marked tile must stay marked even when marking it leaves nothing to
   * align — the operator still has to be told which tile fell out (FR-012).
   */
  it('Still marks a tile when the wall has nothing left to align', () => {
    const lags = [steady, slow];
    const first = settleAlignment(initialAlignmentState, lags);
    const second = settleAlignment(first.state, lags);

    expect(second.target).toBeNull();
    expect(second.state.released).toEqual(['slow']);
  });

  it('Forgets a breach that does not repeat', () => {
    const blip = settleAlignment(initialAlignmentState, [steady, alsoSteady, slow]);
    const recovered = settleAlignment(blip.state, [steady, alsoSteady, tile('slow', 40, 20)]);
    const breachesAgain = settleAlignment(recovered.state, [steady, alsoSteady, slow]);

    expect(recovered.state.breaches).toEqual({});
    expect(breachesAgain.state.released).toEqual([]);
  });

  /**
   * **The oscillation test.** A tile alternating either side of feasibility
   * must not flip its badge from cycle to cycle — an operator would watch it
   * blink, and nothing else in the suite catches that.
   */
  it('Does not flip a tile alternating around feasibility', () => {
    const alternating = [250, 150, 250, 150, 250, 150];
    let state = initialAlignmentState;
    const marked: boolean[] = [];

    for (const buffer of alternating) {
      const cycle = settleAlignment(state, [steady, alsoSteady, tile('edge', buffer + 10, buffer)]);
      state = cycle.state;
      marked.push(state.released.includes('edge'));
    }

    expect(new Set(marked).size, 'the badge must not flip').toBe(1);
    expect(marked).toEqual([false, false, false, false, false, false]);
  });

  /** Coming back takes consecutive feasible cycles, not one good sample. */
  it('Takes a marked tile back only after it looks holdable for several cycles', () => {
    const lags = [steady, alsoSteady, slow];
    let state = settleAlignment(initialAlignmentState, lags).state;
    state = settleAlignment(state, lags).state;
    expect(state.released).toEqual(['slow']);

    const healthy = [steady, alsoSteady, tile('slow', 50, 25)];
    const firstGood = settleAlignment(state, healthy);
    expect(firstGood.state.released, 'one good cycle is not enough').toEqual(['slow']);

    const secondGood = settleAlignment(firstGood.state, healthy);
    expect(secondGood.state.released).toEqual([]);
    expect(secondGood.target?.held).toContain('slow');
  });

  /**
   * **Code review finding.** A marked tile used to be retried only against
   * *unmarked* tiles, so once every tile was marked none could ever come back —
   * the wall kept its badges for the life of the page even though the tiles
   * could align with each other perfectly well. Reachable whenever the last
   * healthy tile goes offline and ages out.
   */
  it('Lets marked tiles recover against each other when no unmarked tile is left', () => {
    // Both tiles are infeasible against the steady one and get marked.
    const bad = [steady, tile('x', 260, 250), tile('y', 265, 255)];
    let state = settleAlignment(initialAlignmentState, bad).state;
    state = settleAlignment(state, bad).state;
    expect(state.released).toEqual(['x', 'y']);

    // The steady tile goes away. x and y are now mutually holdable: their
    // processing is 10 ms each, so a 265 ms target asks 255 ms of buffer —
    // still over the cap — but bring them inside it and they must recover.
    const recovered = [tile('x', 60, 30), tile('y', 65, 35)];
    const first = settleAlignment(state, recovered);
    expect(first.state.released, 'one good cycle is not enough').toEqual(['x', 'y']);

    const second = settleAlignment(first.state, recovered);
    expect(second.state.released, 'but two is, with no survivor to pair against').toEqual([]);
    expect(second.target?.held).toEqual(['y', 'x']);
  });

  it('Sets no target for a single-tile wall however many cycles pass', () => {
    let state = initialAlignmentState;
    for (let cycle = 0; cycle < 5; cycle += 1) {
      const result = settleAlignment(state, [tile('only', 40, 20)]);
      state = result.state;
      expect(result.target).toBeNull();
    }
  });
});

describe('skewAcross', () => {
  it('Reports the spread between the most- and least-lagged tile', () => {
    expect(skewAcross([tile('a', 20, 10), tile('b', 31, 15), tile('c', 120, 60)])).toBe(100);
  });

  it('Reports nothing for a single tile, which has nothing to be skewed against', () => {
    expect(skewAcross([tile('only', 40, 20)])).toBeNull();
  });
});

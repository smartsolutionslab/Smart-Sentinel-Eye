import { test, expect, type Browser, type Page } from '@playwright/test';
import { signInToKiosk } from './support/kiosk-session';

/**
 * Spec 051 US3 — screens coming back from one outage do not arrive together.
 *
 * <p>
 * <b>The only story here that does not exist at a single screen.</b> One kiosk
 * retrying is harmless. Twenty in lockstep against a service that has just come
 * back are a self-inflicted second outage, and twenty is the constitution's
 * number.
 * </p>
 *
 * <p>
 * <b>Twenty are not exercised.</b> Four are, and what is demonstrated is the
 * spread rather than the scale. The verification note says so rather than
 * letting a green run here imply a wall was watched.
 * </p>
 */

const SCREENS = 4;

/** A signed-in screen whose renewals are cut off, recording when each is attempted. */
async function screenUnderOutage(
  browser: Browser,
): Promise<{ page: Page; attempts: number[]; close: () => Promise<void> }> {
  const context = await browser.newContext({ baseURL: 'http://localhost:5174', ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await signInToKiosk(page);

  const attempts: number[] = [];
  await page.route('**/protocol/openid-connect/token', async (route) => {
    attempts.push(Date.now());
    await route.abort('failed');
  });

  await page.evaluate(() => {
    const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
    const user = JSON.parse(window.localStorage.getItem(key ?? '') ?? '{}') as Record<string, unknown>;
    user['expires_at'] = Math.floor(Date.now() / 1000) - 3_600;
    window.localStorage.setItem(key ?? '', JSON.stringify(user));
  });
  await page.reload();

  return { page, attempts, close: () => context.close() };
}

test('several screens recovering from one outage do not arrive together', async ({ browser }) => {
  test.setTimeout(600_000);

  const screens: Awaited<ReturnType<typeof screenUnderOutage>>[] = [];
  for (let index = 0; index < SCREENS; index += 1) {
    screens.push(await screenUnderOutage(browser));
  }

  for (const screen of screens) {
    await expect(screen.page.getByTestId('identity-reconnecting')).toBeVisible({ timeout: 60_000 });
  }

  // Let the schedule run far enough that each screen is retrying at its ceiling,
  // where lockstep would be at its worst.
  await test.step('let the outage run', () => screens[0]!.page.waitForTimeout(90_000));

  for (const screen of screens) {
    expect(screen.attempts.length, 'the interception must actually have fired on every screen').toBeGreaterThan(1);
  }

  // **The property is the spread.** Jitter existing in the source is not the
  // claim; attempts landing at measurably different moments is.
  const latest = screens.map((screen) => screen.attempts.at(-1) ?? 0);
  const spread = Math.max(...latest) - Math.min(...latest);

  expect(spread, 'screens must not make their attempts at the same instant').toBeGreaterThan(500);

  await Promise.all(screens.map((screen) => screen.close()));
});

import { test, expect, type Page } from '@playwright/test';

/**
 * Spec 052 US2 — a wall stays up past the session ceiling that drops it roughly
 * twice a day.
 *
 * <p>
 * <b>These run against the wall-mode instance</b> (:5175), which signs in as
 * <c>kiosk-wall</c>. The mode is fixed when the dev server starts, so a test
 * pointed at the ordinary kiosk would silently exercise the wrong thing and
 * pass for the wrong reason.
 * </p>
 */

const WALL_USER = 'wall-munich';
const WALL_PASSWORD = 'Wall-munich-1234';

async function signInAsWallDisplay(page: Page): Promise<void> {
  await page.goto('/');
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.locator('#username').fill(WALL_USER);
  await page.locator('#password').fill(WALL_PASSWORD);
  await page.locator('#kc-login').click();
  await expect(page.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 60_000 });
}

/** Claims of a grant, read without verifying — this is a test, not a validator. */
function claimsOf(token: string): Record<string, unknown> {
  const [, payload] = token.split('.');
  if (payload === undefined) {
    throw new Error('a grant should have a payload segment');
  }
  return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as Record<string, unknown>;
}

async function storedGrant(page: Page): Promise<{ refresh: string; access: string }> {
  const stored = await page.evaluate(() => {
    const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
    if (key === undefined) return null;
    const user = JSON.parse(window.localStorage.getItem(key) ?? '{}') as Record<string, string>;
    return { refresh: user['refresh_token'] ?? '', access: user['access_token'] ?? '' };
  });

  expect(stored, 'a wall display should be holding a grant').not.toBeNull();
  return stored as { refresh: string; access: string };
}

test.describe('A wall outlives its session ceiling (spec 052 US2)', () => {
  /**
   * **The primary proof, and it is a property rather than a duration.**
   *
   * <p>
   * Nothing runs for ten hours, so what is asserted is the thing that makes ten
   * hours survivable: the grant is an <i>offline</i> one and carries <b>no
   * expiry at all</b>. Asserting that a token exists passes today, with a screen
   * that still drops to a prompt twice a day.
   * </p>
   */
  test('holds a grant that carries no expiry', async ({ page }) => {
    test.setTimeout(240_000);

    await signInAsWallDisplay(page);
    const { refresh } = await storedGrant(page);

    const claims = claimsOf(refresh);

    expect(claims['typ'], 'an ordinary refresh token dies with the session that issued it').toBe('Offline');
    expect(claims['exp'], 'and an offline grant carries no expiry at all').toBeUndefined();
  });

  /**
   * The screen is signed in as the wall client, not the ordinary one. Without
   * this, everything above could be true of a screen that reached the wall
   * client by accident — or of the ordinary client having quietly gained the
   * scope, which is the lockout.
   */
  test('signs in as the wall client, not the ordinary kiosk client', async ({ page }) => {
    test.setTimeout(240_000);

    await signInAsWallDisplay(page);
    const { access } = await storedGrant(page);

    expect(claimsOf(access)['azp']).toBe('kiosk-wall');
  });

  /**
   * **A wall display must render a wall, not merely authenticate.** The wall
   * client is narrowed to read-only, and "signs in" and "shows cameras" are
   * different claims — only the second is the product.
   */
  test('opens a wall with the narrowed client', async ({ page }) => {
    test.setTimeout(240_000);

    await signInAsWallDisplay(page);
    await page.getByRole('listitem').first().getByRole('button').click();

    await expect(page.getByTestId('layout-grid')).toBeVisible({ timeout: 60_000 });
  });

  /**
   * Recovery from a restart that outlasts an ordinary session — the case
   * ADR-0131 explicitly could not do, because the grant died with the session
   * behind it.
   */
  test('comes back from a restart with nobody touching it', async ({ page, context }) => {
    test.setTimeout(300_000);

    await signInAsWallDisplay(page);

    const kept = await page.evaluate(() => {
      const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
      return key === null || key === undefined ? null : { key, value: window.localStorage.getItem(key) ?? '' };
    });
    expect(kept).not.toBeNull();
    const { key, value } = kept as { key: string; value: string };

    // The access token is spent before the restart, so coming back must go
    // through the grant rather than through a token that happened to still work.
    const expired = JSON.stringify({
      ...(JSON.parse(value) as Record<string, unknown>),
      expires_at: Math.floor(Date.now() / 1000) - 3_600,
    });

    const rebooted = await context.browser()?.newContext({
      baseURL: 'http://localhost:5175',
      ignoreHTTPSErrors: true,
    });
    if (rebooted === undefined) throw new Error('could not simulate a restart');
    const screenAfterReboot = await rebooted.newPage();

    await screenAfterReboot.addInitScript(
      (stored: { key: string; value: string }) => {
        window.localStorage.setItem(stored.key, stored.value);
        window.localStorage.setItem('sse.auth.wasAuthenticated', 'true');
      },
      { key, value: expired },
    );

    await screenAfterReboot.goto('/');

    // Asserted on the absence of a prompt: the app restores what it was showing,
    // so looking for the picker reports a defect when the wall is right there.
    await expect(
      screenAfterReboot.getByRole('button', { name: /sign in/i }),
      'a wall display must come back without a person',
    ).toHaveCount(0, { timeout: 90_000 });
    await expect(screenAfterReboot.locator('#username')).toHaveCount(0);
    await expect(screenAfterReboot.getByRole('listitem').first()).toBeVisible({ timeout: 90_000 });

    await rebooted.close();
  });
});

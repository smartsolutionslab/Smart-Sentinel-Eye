import { test, expect, chromium, type BrowserContext, type Page } from '@playwright/test';

/**
 * Spec 107 — a wall display returns after its **browser process actually died**.
 *
 * <p>
 * Every other "restart" in this suite is a reconstruction: state is read out of
 * one context in JavaScript and written into a fresh one before the page loads
 * (`kiosk-comes-back.spec.ts:47`, `wall-outlives-its-session.spec.ts:110`). That
 * proves the app can spend a grant it is <i>handed</i>. It does not prove a
 * grant reaches disk and is read back by a <b>different OS process</b>, which is
 * what a power cut requires and the only thing ADR-0131 actually rests on.
 * </p>
 *
 * <p>
 * So this file launches a persistent Chromium profile, signs in, expires the
 * access token in place, ends the process, and relaunches on the same directory.
 * <b>Nothing is written into the second process.</b> If a `localStorage.setItem`
 * ever appears here, the test has become the reconstruction it exists to
 * replace.
 * </p>
 */

const WALL = 'http://localhost:5175/';
const WALL_USER = 'wall-munich';
const WALL_PASSWORD = 'Wall-munich-1234';

/** Where the boot-time grant is stashed, before any application code runs. */
declare global {
  interface Window {
    __grantAtBoot?: string | null;
  }
}

/**
 * Copied from `wall-outlives-its-session.spec.ts:18-26` rather than extracted:
 * `e2e/support/*` is an ADR-0109 contention file and the extraction is a
 * separate refactor (ADR-0036). The URL is absolute — a manually launched
 * context inherits no `baseURL` from `playwright.config.ts:61`.
 */
async function signInAsWallDisplay(page: Page): Promise<void> {
  await page.goto(WALL);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.locator('#username').fill(WALL_USER);
  await page.locator('#password').fill(WALL_PASSWORD);
  await page.locator('#kc-login').click();
  await expect(page.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 90_000 });
  await expect(page.getByRole('listitem').first(), 'the seed project publishes a layout').toBeVisible({
    timeout: 90_000,
  });
}

/** A picker proves authentication; a `layout-grid` proves the wall. */
async function openFirstLayout(page: Page): Promise<void> {
  await page.getByRole('listitem').first().getByRole('button').click();
  await expect(page.getByTestId('layout-grid')).toBeVisible({ timeout: 90_000 });
}

/**
 * Spends the stored access token in place, then lets the close flush it.
 *
 * <p>
 * <b>The key is read, never constructed.</b> It embeds the identity provider's
 * authority, which the stack serves on a port chosen per run — a hardcoded
 * `localhost:8080` key once restored an entry the app never looks for and made a
 * working feature read as broken (`kiosk-comes-back.spec.ts:60-64`).
 * </p>
 */
async function expireStoredAccessToken(page: Page): Promise<void> {
  const rewritten = await page.evaluate(() => {
    const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
    if (key === undefined) return false;
    const user = JSON.parse(window.localStorage.getItem(key) ?? '{}') as Record<string, unknown>;
    user['expires_at'] = Math.floor(Date.now() / 1000) - 3_600;
    window.localStorage.setItem(key, JSON.stringify(user));
    return true;
  });

  expect(rewritten, 'the first process should be holding a grant to expire').toBe(true);
}

test.describe('A wall survives a process death (spec 107 US1)', () => {
  test('a wall display comes back after its browser process dies', async ({}, testInfo) => {
    test.setTimeout(300_000);

    // Retry-unique, so a CI retry starts from a profile that has never been
    // used — otherwise attempt 2 begins signed in and proves nothing.
    const profile = testInfo.outputPath('wall-profile');

    // --- process #1 -------------------------------------------------------
    // `ignoreHTTPSErrors` is NOT inherited by a manually launched context, and
    // Keycloak runs on a development certificate. Without it the refresh
    // exchange fails on certificate validation and the screen falls to a login
    // form — indistinguishable from the defect under test
    // (`kiosk-comes-back.spec.ts:70-78` records that incident).
    const first: BrowserContext = await chromium.launchPersistentContext(profile, { ignoreHTTPSErrors: true });
    // Started by hand: `trace: 'on-first-retry'` never fires for a context the
    // fixtures did not create, and a Linux-runner failure with no trace is the
    // worst place this test can end up.
    await first.tracing.start({ screenshots: true, snapshots: true });

    try {
      const page = first.pages()[0] ?? (await first.newPage());
      await signInAsWallDisplay(page);
      await openFirstLayout(page);
      await expireStoredAccessToken(page);
    } finally {
      await first.tracing.stop({ path: testInfo.outputPath('process-one.zip') });
      // Awaited: two Chromiums on one user-data directory is undefined
      // behaviour, and closing is also what flushes Local Storage to disk.
      await first.close();
    }

    // --- process #2 — same directory, nothing handed over ------------------
    const second: BrowserContext = await chromium.launchPersistentContext(profile, { ignoreHTTPSErrors: true });
    await second.tracing.start({ screenshots: true, snapshots: true });

    try {
      const revived = second.pages()[0] ?? (await second.newPage());

      // Read at document start, before any application code. A read after the
      // page settles returns the *renewed* grant, whose expiry is in the
      // future — the assertion would then be unfailable, which is the #2054
      // shape this test exists to avoid.
      await revived.addInitScript(() => {
        const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
        window.__grantAtBoot = key === undefined ? null : window.localStorage.getItem(key);
      });

      await revived.goto(WALL);

      // Control — the profile really was reused, and the token really was spent.
      const atBoot = await revived.evaluate(() => window.__grantAtBoot ?? null);
      expect(atBoot, 'the relaunched profile must carry the grant the first process wrote').not.toBeNull();
      const expiresAt = (JSON.parse(atBoot as string) as Record<string, number>)['expires_at'];
      expect(expiresAt, 'and its access token must already be spent').toBeLessThan(Math.floor(Date.now() / 1000));

      // Control — the restart is genuinely cookie-less. Without this, a green
      // run is consistent with the screen riding a surviving SSO cookie rather
      // than spending its grant.
      const providerSession = (await second.cookies()).filter((cookie) =>
        /KEYCLOAK_IDENTITY|AUTH_SESSION_ID/.test(cookie.name),
      );
      expect(providerSession, 'a restarted device carries no provider session cookie').toHaveLength(0);

      // THE CLAIM — what the wall shows, with nobody touching it.
      await expect(
        revived.getByRole('button', { name: /sign in/i }),
        'a wall display must come back without a person',
      ).toHaveCount(0, { timeout: 90_000 });
      await expect(revived.locator('#username'), 'and must never be asked for a credential').toHaveCount(0);
      await expect(revived.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 90_000 });
      // Populated, not merely present: an empty picker is what a fab-less token
      // produces and it renders without an error.
      await expect(revived.getByRole('listitem').first(), 'the picker must be populated').toBeVisible({
        timeout: 90_000,
      });

      await openFirstLayout(revived);
    } finally {
      await second.tracing.stop({ path: testInfo.outputPath('process-two.zip') });
      await second.close();
    }
  });
});

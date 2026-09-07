import { test, expect, request as playwrightRequest, type Page } from '@playwright/test';

/**
 * Spec 052 FR-011 — withdrawing one screen must not take down the wall.
 *
 * <p>
 * <b>Written for spec 050, gated when that attempt was withdrawn, and revived
 * here unchanged in substance.</b> The claim was always right; what was missing
 * was a client that offers the grant. It now runs against the wall-mode
 * instance and the dedicated wall client, ungated.
 * </p>
 *
 * <p>
 * Originally: spec 050 US3 — withdrawing one screen must not take down the wall.
 *
 * <p>
 * <b>This is the claim the research could only reason about.</b> R4 argued that
 * because the provider tracks offline sessions individually, one screen's grant
 * can be ended while a sibling keeps running. Reasoning is exactly what this
 * feature exists to distrust, so this tests it — and if it fails, US3 is
 * re-scoped in the open rather than reinterpreted into whatever the mechanism
 * happens to do.
 * </p>
 *
 * <p>
 * It matters more here than it would elsewhere: the grant never expires, so
 * being able to end one is the only thing that ends it. If the unit of
 * withdrawal is the account rather than the session, withdrawing one screen
 * stops an entire fab's wall.
 * </p>
 */

/**
 * The provider's own address, and the development admin secret.
 *
 * <p>
 * <b>The address is derived from a grant, never hardcoded.</b> The host publishes
 * the provider on a port it chooses, so a fixed one is a different issuer as far
 * as the provider is concerned and every call comes back refused — which reads
 * exactly like a withdrawn session. That trap already cost this file once.
 * </p>
 */
const ADMIN_PASSWORD = process.env['SSE_KEYCLOAK_ADMIN_PASSWORD'] ?? 'dev-only-keycloak-admin';

const WALL_USER = 'wall-munich';
const WALL_PASSWORD = 'Wall-munich-1234';
const REALM = 'smart-sentinel-eye';

async function signIn(page: Page): Promise<string> {
  await page.goto('/');
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.locator('#username').fill(WALL_USER);
  await page.locator('#password').fill(WALL_PASSWORD);
  await page.locator('#kc-login').click();
  await expect(page.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 60_000 });

  const refresh = await page.evaluate(() => {
    const key = Object.keys(window.localStorage).find((candidate) => candidate.includes('oidc.user:'));
    if (key === undefined) return null;
    const user = JSON.parse(window.localStorage.getItem(key) ?? '{}') as Record<string, string>;
    return user['refresh_token'] ?? null;
  });
  expect(refresh, 'a screen should hold a grant it can refresh with').not.toBeNull();
  return refresh as string;
}

/** Claims of a grant, read without verifying — this is a test, not a validator. */
function claimsOf(token: string): Record<string, string> {
  const [, payload] = token.split('.');
  if (payload === undefined) {
    throw new Error('a grant should have a payload segment');
  }
  return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as Record<string, string>;
}

/**
 * Where the grant was issued, taken from the grant.
 *
 * <p>
 * <b>Not a constant.</b> The provider is reached through a proxied endpoint, so
 * a hardcoded host is a different issuer as far as the provider is concerned and
 * every exchange comes back `invalid_grant`. That failure reads exactly like a
 * withdrawn session, and it briefly had this test reporting that withdrawing one
 * screen took down its sibling — a control with nothing withdrawn is what caught
 * it.
 * </p>
 */
function issuerOf(token: string): string {
  const issuer = claimsOf(token)['iss'];
  if (!issuer) {
    throw new Error('a grant should name its issuer');
  }
  return issuer;
}

/** The session a grant belongs to, which is what withdrawal has to target. */
function sessionOf(refreshToken: string): string {
  return claimsOf(refreshToken)['sid'] ?? '';
}

test.describe('Withdrawing one screen (spec 050 US3)', () => {
  /**
   * **The control.** If two grants cannot both be exchanged when nothing has
   * been withdrawn, then a failure in the test above says nothing about
   * withdrawal — it says the arrangement never worked. Running this first is
   * what makes the other result mean something.
   */
  test('control: two screens can both refresh when nothing is withdrawn', async ({ browser }) => {
    test.setTimeout(600_000);

    const first = await browser.newContext({ baseURL: 'http://localhost:5175', ignoreHTTPSErrors: true });
    const second = await browser.newContext({ baseURL: 'http://localhost:5175', ignoreHTTPSErrors: true });
    const grantOne = await signIn(await first.newPage());
    const grantTwo = await signIn(await second.newPage());

    expect(sessionOf(grantOne)).not.toBe(sessionOf(grantTwo));

    const api = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
    const exchange = async (refreshToken: string) => {
      const response = await api.post(`${issuerOf(refreshToken)}/protocol/openid-connect/token`, {
        form: { client_id: 'kiosk-wall', grant_type: 'refresh_token', refresh_token: refreshToken },
      });
      return response.status();
    };

    expect(await exchange(grantOne), 'both screens should refresh with nothing withdrawn').toBe(200);
    expect(await exchange(grantTwo), 'both screens should refresh with nothing withdrawn').toBe(200);

    await first.close();
    await second.close();
    await api.dispose();
  });

  test('ends one screen and leaves its sibling running', async ({ browser }) => {
    test.setTimeout(600_000);

    // Two screens in the same fab, signed in as the same wall-display account —
    // which is the arrangement a real wall has.
    const first = await browser.newContext({ baseURL: 'http://localhost:5175', ignoreHTTPSErrors: true });
    const second = await browser.newContext({ baseURL: 'http://localhost:5175', ignoreHTTPSErrors: true });
    const grantOne = await signIn(await first.newPage());
    const grantTwo = await signIn(await second.newPage());

    const sessionOne = sessionOf(grantOne);
    const sessionTwo = sessionOf(grantTwo);
    expect(sessionOne, 'two screens must not share a session, or nothing can be withdrawn alone').not.toBe(sessionTwo);

    const admin = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
    const provider = new URL(issuerOf(grantOne)).origin;
    const tokenResponse = await admin.post(`${provider}/realms/master/protocol/openid-connect/token`, {
      form: { client_id: 'admin-cli', username: 'admin', password: ADMIN_PASSWORD, grant_type: 'password' },
    });
    const adminToken = ((await tokenResponse.json()) as { access_token: string }).access_token;

    // Withdraw exactly one screen's session.
    const deleted = await admin.delete(`${provider}/admin/realms/${REALM}/sessions/${sessionOne}?isOffline=true`, {
      headers: { Authorization: `Bearer ${adminToken}` },
    });
    expect(deleted.status(), 'the provider should be able to end a single session').toBeLessThan(300);

    // Now the load-bearing part: exchange each grant directly. The withdrawn one
    // must fail and the sibling must not.
    const exchange = async (refreshToken: string) => {
      const response = await admin.post(`${issuerOf(refreshToken)}/protocol/openid-connect/token`, {
        form: { client_id: 'kiosk-wall', grant_type: 'refresh_token', refresh_token: refreshToken },
      });
      return { status: response.status(), body: await response.text() };
    };

    // **The provider's reason is asserted, not printed.** A withdrawn session
    // answers "Offline user session not found"; a grant that never worked
    // answers something else entirely, and both are 400 from outside. Logging
    // the difference and asserting only the status is what let this test report
    // that withdrawing one screen took down its sibling — the status matched
    // while the reason said the arrangement was broken.
    const withdrawn = await exchange(grantOne);
    expect(withdrawn.status, 'the withdrawn screen must stop').toBeGreaterThanOrEqual(400);
    expect(withdrawn.body, 'it must stop because its session was ended, not because the grant never worked').toContain(
      'Offline user session not found',
    );

    const sibling = await exchange(grantTwo);
    expect(sibling.status, 'and the other nineteen must not').toBe(200);

    await first.close();
    await second.close();
    await admin.dispose();
  });
});

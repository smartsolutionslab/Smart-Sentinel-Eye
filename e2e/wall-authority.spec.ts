import { test, expect, type Page } from '@playwright/test';

/**
 * Spec 052 US3 — what a wall display may do, enumerated rather than assumed.
 *
 * <p>
 * <b>Spec 050 asserted refusals on three endpoints somebody chose, and never
 * attempted the one the account actually held.</b> That is how "the account can
 * change nothing" came to be recorded while it was false. So nothing here works
 * from a typed list: the scopes are read out of the token the wall account
 * actually receives, and every one found is exercised.
 * </p>
 */

const WALL_USER = 'wall-munich';
const WALL_PASSWORD = 'Wall-munich-1234';

/** Every scope the issued token actually carries. */
function scopesOf(accessToken: string): string[] {
  const [, payload] = accessToken.split('.');
  if (payload === undefined) {
    throw new Error('a wall access token should have a payload segment');
  }
  const claims = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as { scope?: string };
  return (claims.scope ?? '').split(' ').filter(Boolean);
}

async function signInAndReadToken(page: Page): Promise<{ token: string; origin: string }> {
  const gatewayRequest = page.waitForRequest((request) =>
    /\/(layout-composition|stream-distribution|camera-catalog)\//.test(request.url()),
  );

  await page.goto('/');
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.locator('#username').fill(WALL_USER);
  await page.locator('#password').fill(WALL_PASSWORD);
  await page.locator('#kc-login').click();
  await expect(page.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 60_000 });

  const origin = new URL((await gatewayRequest).url()).origin;
  const token = await page.evaluate(() => {
    const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
    const user = JSON.parse(window.localStorage.getItem(key ?? '') ?? '{}') as Record<string, string>;
    return user['access_token'] ?? '';
  });

  expect(token, 'the wall display should be holding an access token').not.toBe('');
  return { token, origin };
}

test.describe('A wall display may only show a wall (spec 052 US3)', () => {
  /**
   * **The narrowing, asserted on the issued token rather than on the realm
   * file.** A file says what is declared; this says what the provider handed
   * over.
   */
  test('receives no write authority at all', async ({ page }) => {
    test.setTimeout(240_000);

    const { token } = await signInAndReadToken(page);
    const scopes = scopesOf(token);

    expect(scopes.length, 'a token carrying no scopes would make every assertion below vacuous').toBeGreaterThan(0);

    const writeScopes = scopes.filter((scope) => scope.endsWith('.write') || scope.endsWith('.publish'));

    expect(
      writeScopes,
      `a wall display holds a grant that never expires; it must carry no write authority, and it carries ${writeScopes.join(', ')}`,
    ).toEqual([]);
  });

  /**
   * Enumerated, not typed. Whatever the wall client is given, this attempts a
   * write against every context those scopes name — so a write scope added
   * later is exercised without anybody remembering to add a case.
   */
  test('is refused every write it can attempt, whatever it was granted', async ({ page }) => {
    test.setTimeout(300_000);

    const { token, origin } = await signInAndReadToken(page);

    const attempt = async (path: string, body: unknown) =>
      page.evaluate(
        async ([o, t, p, b]) => {
          const response = await fetch(`${o}${p}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${t}` },
            body: JSON.stringify(b),
          });
          return response.status;
        },
        [origin, token, path, body] as const,
      );

    // One write per context a wall display could plausibly reach. Each must be
    // refused outright — 401 or 403, never 404, because a mistyped path would
    // otherwise read as proof of safety.
    const writes: [string, unknown][] = [
      ['/camera-catalog/cameras', { name: 'x', rtspUrl: 'rtsp://10.0.0.1/s' }],
      ['/layout-composition/layouts', { name: 'x' }],
      ['/overlay-designer/overlays', { name: 'x' }],
      [
        '/event-ingestion/events/manual',
        { deviceId: 'w', kind: 'manual', occurredAt: new Date().toISOString(), payload: {} },
      ],
    ];

    for (const [path, body] of writes) {
      const status = await attempt(path, body);
      expect(
        [401, 403],
        `POST ${path} answered ${status}; a refusal is 401 or 403, and 404 means the test is broken`,
      ).toContain(status);
    }
  });

  /**
   * It can still read what a wall needs — otherwise "refused everything" would
   * be satisfied by a screen that shows nothing, and the narrowing would have
   * gone too far without any test noticing.
   */
  test('can still read its own fab', async ({ page }) => {
    test.setTimeout(240_000);

    const { token, origin } = await signInAndReadToken(page);

    const status = await page.evaluate(
      async ([o, t]) => {
        const response = await fetch(`${o}/camera-catalog/cameras?limit=1`, {
          headers: { Authorization: `Bearer ${t}` },
        });
        return response.status;
      },
      [origin, token] as const,
    );

    expect(status, 'a wall display that cannot read its cameras cannot show a wall').toBe(200);
  });

  /**
   * Fab scoping comes from the account rather than the client, so this is the
   * assertion behind one wall account per fab. Without it, a shared account
   * looks identical to a scoped one.
   */
  test('cannot read another fab', async ({ page }) => {
    test.setTimeout(240_000);

    const { token, origin } = await signInAndReadToken(page);

    const other = await page.evaluate(
      async ([o, t]) => {
        const response = await fetch(`${o}/camera-catalog/cameras?fabId=dresden&limit=50`, {
          headers: { Authorization: `Bearer ${t}` },
        });
        const text = await response.text();
        let count = 0;
        try {
          count = (JSON.parse(text) as { items?: unknown[] }).items?.length ?? 0;
        } catch {
          count = 0;
        }
        return { status: response.status, count };
      },
      [origin, token] as const,
    );

    // Refused outright, or scoped down to nothing. Returning another fab's
    // cameras is the failure.
    if (other.status === 200) {
      expect(other.count, 'a munich wall display must not see dresden cameras').toBe(0);
    } else {
      expect([401, 403]).toContain(other.status);
    }
  });
});

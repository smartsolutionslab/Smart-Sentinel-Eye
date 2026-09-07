import { expect, type Page } from '@playwright/test';

/**
 * Spec 041 — the kiosk's shared e2e sign-in, and a way to read what it holds.
 *
 * `e2e/support/sign-in.ts` drives the management shell and asserts the Cameras
 * heading, so the kiosk repeats the same seeded-operator Keycloak form flow
 * here and asserts arrival on a **populated** picker.
 *
 * The assertion this replaced accepted `could not load layouts` as one of three
 * passing outcomes, so a kiosk that could never show a wall looked exactly like
 * a working one — which is why the defect survived for as long as the kiosk
 * existed. It is not enough to assert "no error" either: an operator whose
 * token carries no fab gets an *empty* picker and no error at all.
 */
export async function signInToKiosk(page: Page): Promise<void> {
  await page.goto('/');

  await page.getByRole('button', { name: /sign in/i }).click();

  await page.locator('#username').fill('operator');
  await page.locator('#password').fill('Operator1234');
  await page.locator('#kc-login').click();

  // The picker, with layouts on it. The `seed` project published one.
  await expect(page.getByRole('heading', { name: 'Pick a layout' })).toBeVisible();
  await expect(page.getByRole('listitem').first()).toBeVisible();
}

/**
 * Opens the first layout on the picker and waits for the wall to render.
 *
 * Any published layout proves the point, so the seed does not share its name —
 * layout names are unique per fab and a fixed one would collide on a second
 * local run against a surviving database.
 */
export async function openFirstLayout(page: Page): Promise<void> {
  await page.getByRole('listitem').first().getByRole('button').click();
  await expect(page.getByTestId('layout-grid')).toBeVisible();
}

/**
 * The decoded payload of the access token the kiosk is actually holding.
 *
 * Read out of the app's own `oidc.user:...` session-storage entry rather than
 * minted separately: a freshly minted token would assert what Keycloak does,
 * not what the kiosk carries. Some claims — notably the *absence* of a scope —
 * exist nowhere else, because a kiosk holding write-everything authority
 * behaves identically to one that does not.
 */
export async function readKioskAccessToken(page: Page): Promise<KioskTokenClaims> {
  const payload = await page.evaluate(() => {
    // The kiosk keeps its grant where a restart cannot destroy it (ADR-0131), so
    // this reads the storage that survives the process. It read the other one
    // until that change, and nothing here pointed at it — three tests broke.
    const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWith('oidc.user:'));
    if (key === undefined) {
      return null;
    }
    const stored: unknown = JSON.parse(window.localStorage.getItem(key) ?? 'null');
    const accessToken = (stored as { access_token?: unknown } | null)?.access_token;
    if (typeof accessToken !== 'string') {
      return null;
    }
    const [, claims] = accessToken.split('.');
    if (claims === undefined) {
      return null;
    }
    return JSON.parse(atob(claims.replace(/-/g, '+').replace(/_/g, '/'))) as unknown;
  });

  expect(payload, 'the kiosk should be holding an access token').not.toBeNull();
  return payload as KioskTokenClaims;
}

export interface KioskTokenClaims {
  azp?: string;
  scope?: string;
  groups?: string[];
}

/** The `sse.*` entries of a `scope` claim, which is a space-separated string. */
export function sseScopesOf(claims: KioskTokenClaims): string[] {
  return (claims.scope ?? '').split(' ').filter((scope) => scope.startsWith('sse.'));
}

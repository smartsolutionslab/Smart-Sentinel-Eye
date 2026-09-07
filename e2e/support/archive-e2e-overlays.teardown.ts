import { test as cleanup, expect, type Page } from '@playwright/test';
import { signInAsOperator } from './sign-in';

/**
 * Archives the overlays an e2e run publishes (spec 056).
 *
 * <para>
 * <b>Cameras and layouts were swept; overlays never were.</b> Each seeded run
 * publishes overlays that nothing archives — this feature's and SC-004's — so
 * the Published list grows by a few every run and never shrinks.
 * </para>
 *
 * <para>
 * <b>The scale of that is smaller than it first looked, and the first version of
 * this comment overstated it.</b> A long-lived dev database held 124 overlays of
 * which 111 were e2e-named, which reads like ninety per cent residue on the
 * page. It is not: <b>about a hundred were already Archived</b> — the
 * reconciliation spec archives the overlay it creates as part of what it tests —
 * so they were already out of the default listing. What actually accumulates is
 * the handful per run that are published and left that way.
 * </para>
 *
 * <para>
 * <b>Variables are deliberately not swept, because they cannot be.</b> The
 * product offers no way to delete a system variable: no control on the page, no
 * endpoint. 1618 of them have accumulated. Spec 056's FR-006 asks a fixture to
 * clean up what it creates, and for variables that is not currently possible —
 * recorded rather than quietly skipped.
 * </para>
 *
 * <para>
 * Same shape as the layout sweep: prefix-matched so it clears historical rows on
 * its first run, bounded by the clock rather than by attempts because the
 * residue is unbounded, and <b>best-effort</b> — a teardown that reddens a green
 * run over a tidy-up is one that gets deleted, and then nothing cleans up.
 * </para>
 */
const DISPOSABLE = /^(E2E |SC004 Overlay |Spec056 Overlay |Kiosk Seed Overlay )/;

const DEADLINE_MS = 5 * 60 * 1000;

cleanup('archive the overlays this run published', async ({ page }) => {
  cleanup.setTimeout(600_000);
  const deadline = Date.now() + DEADLINE_MS;

  await signInAsOperator(page);

  const attempted = new Set<string>();
  let archived = 0;
  let nothingToDo = 0;
  let refused = 0;
  let outOfTime = false;

  for (let sweep = 0; sweep < 40 && !outOfTime; sweep += 1) {
    const targets = (await disposableNames(page)).filter((name) => !attempted.has(name));
    if (targets.length === 0) break;

    for (const name of targets) {
      if (Date.now() > deadline) {
        outOfTime = true;
        break;
      }
      attempted.add(name);
      const outcome = await archiveByName(page, name);
      if (outcome === 'archived') archived += 1;
      else if (outcome === 'nothing-to-do') nothingToDo += 1;
      else refused += 1;
    }
  }

  // **Three totals, not two, and always printed.** An overlay already Archived,
  // or still a Draft, offers no control: that is *nothing to do*, not a failure.
  // Lumping the two together reported "skipped 112" for a sweep with nothing to
  // skip — the opposite of the honest accounting this file exists to give. A
  // sweep that silently did nothing also looks exactly like a clean database,
  // which is how the camera teardown's first run was mistaken for a success.
  console.info(`[cleanup] archived ${archived} overlay(s); nothing to do ${nothingToDo}; refused ${refused}`);
  if (outOfTime) {
    console.info('[cleanup] stopped on the deadline — a later run continues where this one left off');
  }
});

/** Names on the page whose form marks them as e2e residue. */
async function disposableNames(page: Page): Promise<string[]> {
  await page.goto('/overlays');
  await expect(page.getByRole('heading', { name: 'Overlays', exact: true })).toBeVisible();

  // **The heading is static and proves nothing about the rows.** Reading them
  // before they render returns nothing and looks exactly like a clean database —
  // the failure mode that made the camera teardown a silent no-op on its first
  // run.
  //
  // Waiting for the loading indicator to disappear is not enough on its own:
  // if it has not mounted yet, that passes instantly and proves nothing. So wait
  // for the list itself to settle — either rows, or the empty state — and only
  // then read.
  await expect
    .poll(
      async () => (await page.getByRole('listitem').count()) > 0 || (await page.getByText(/no overlays/i).count()) > 0,
      { timeout: 30_000 },
    )
    .toBe(true);
  await expect(page.getByText('Loading…')).toHaveCount(0);

  const names: string[] = [];
  for (const heading of await page.getByRole('heading', { level: 2 }).all()) {
    const text = (await heading.innerText()).trim();
    if (DISPOSABLE.test(text)) names.push(text);
  }
  return names;
}

type Outcome = 'archived' | 'nothing-to-do' | 'refused';

/**
 * Archives one overlay. Every interaction carries an explicit timeout —
 * Playwright's default action timeout is unbounded, so one unactionable element
 * would consume the whole budget and the sweep would archive nothing.
 */
async function archiveByName(page: Page, name: string): Promise<Outcome> {
  const row = page.getByRole('listitem').filter({ hasText: name }).first();
  const archive = row.getByRole('button', { name: /^archive$/i });

  // Only a published overlay offers Archive. An already-archived one and a draft
  // both lack it, and neither is a failure — most of what this sweep sees is
  // this case, because the reconciliation spec archives its own overlay.
  if ((await archive.count()) === 0) return 'nothing-to-do';

  try {
    await archive.click({ timeout: 15_000 });

    // The confirmation, when there is one. Some rows archive directly.
    const confirmation = page.getByRole('alertdialog');
    if ((await confirmation.count()) > 0) {
      await confirmation.getByRole('button', { name: /^archive$/i }).click({ timeout: 15_000 });
    }

    // **Success has two shapes, and only one of them is the badge.** The row may
    // show "Archived", or it may leave the list entirely — and waiting for a
    // badge on a row that no longer exists times out and reports the archive as
    // skipped. Counting a success as a skip would make the totals the exact
    // opposite of honest, which is the one thing this file must not be.
    await expect
      .poll(
        async () => {
          if ((await row.count()) === 0) return 'gone';
          return (await row.getByText(/Archived/).count()) > 0 ? 'archived' : 'pending';
        },
        { timeout: 15_000 },
      )
      .not.toBe('pending');

    return 'archived';
  } catch {
    return 'refused';
  }
}

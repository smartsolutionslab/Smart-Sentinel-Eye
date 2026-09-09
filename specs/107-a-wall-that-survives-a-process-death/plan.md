# Plan 107 — A wall that survives a process death

**Spec:** `specs/107-a-wall-that-survives-a-process-death/spec.md`
**Issue:** #2067
**Scope:** test-only. One new file under `e2e/`. No production file is edited at
any point except temporarily, and reverted, for the counterfactuals — and even
those are edits to the *test*, not to the app (spec §8).

---

## 1. Bounded context and layers

| | |
|---|---|
| Context | **None.** The subject is the `kiosk-web` bundle running in wall mode, not a backend bounded context. |
| Layer under observation | Browser: `apps/kiosk-web/src/app/auth.ts` (the store) + `useSessionExpiry.ts` (the restart→silent path), over the real Keycloak and the real gateway. |
| Layer of the new code | **none** — `e2e/`, which drives a browser over HTTP and references no project. |
| Boundary rules | Unaffected. No `Shared.Contracts` change, no cross-context reference, no NetArchTest rule applies to a Playwright spec. |

**No backend and no infrastructure change.** The engineer is
`frontend-engineer` (spec §6.1). Nothing in `src/`, nothing in `.github/`,
nothing in `deploy/`.

## 2. The path under observation

```
process #1
  goto http://localhost:5175            kiosk-web bundle, VITE_KIOSK_MODE=wall (AppHost.cs:522-535)
   └─ sign in as wall-munich            client kiosk-wall, scope "openid offline_access" (ADR-0134)
        └─ oidc-client-ts writes        localStorage["oidc.user:<issuer>:kiosk-wall"]   (auth.ts:112)
        └─ app writes                   localStorage["sse.auth.wasAuthenticated"]       (useSessionExpiry.ts:251)
   └─ open the first layout             layout-grid renders
   └─ TEST rewrites expires_at          in place, via page.evaluate                     (spec §2.3)
   └─ context.close()                   Chromium flushes Local Storage/leveldb          ← the disk write

process #2 — same userDataDir, nothing handed over
  addInitScript                         stash the raw oidc.user entry BEFORE app code   ← the boot-state control
  goto http://localhost:5175
   └─ hasBeenAuthenticated() === true    read from the reused profile                   (useSessionExpiry.ts:37)
   └─ auth.signinSilent()                once, refresh_token grant — no SSO cookie      (useSessionExpiry.ts:273-308)
        └─ POST <issuer>/protocol/openid-connect/token
   └─ populated picker, no sign-in control
   └─ open the first layout             layout-grid renders                             ← what the operator is there for
```

**The load-bearing difference from every existing "restart" test** is the two
arrows marked `← the disk write` and the absence of any `addInitScript` that
*writes*. Process #2 is handed nothing.

## 3. Entities, value objects, invariants

None added; none in play. The invariant asserted is ADR-0131's, previously only
observed across a reconstructed context:

> The grant a screen holds is written to storage that outlives the browser
> process, and a new process finds it there.

Plus ADR-0134's half: the grant found there is an offline one, so the recovery is
not bounded by the ten-hour session ceiling. This spec does not re-assert the
token claims — `wall-outlives-its-session.spec.ts:66` already does, and duplicate
coverage costs a later reader.

## 4. Messaging

None. No domain event, no integration event, no RabbitMQ. The only traffic the
test causes is the OIDC token exchange and whatever the picker fetches through
the gateway — and it observes neither directly, only what the page shows.

## 5. Files

### New — one

`e2e/wall-survives-a-process-death.spec.ts`

- **Name is load-bearing.** `wall-*` routes it to the `wall` project
  (`playwright.config.ts:62`), which supplies `dependencies: ['seed']` and the
  `cleanup` teardown, and keeps it out of the management project's sweep
  (`:35`).
- One `test.describe`, **one** `test`. Every scenario in spec §3.1 is an
  assertion inside the single restart flow: splitting them would mean signing in
  and restarting twice for no extra signal, at roughly four minutes a run.
- Private helpers, modelled on existing siblings — `signInAsWallDisplay` from
  `wall-outlives-its-session.spec.ts:18-26`, the `oidc.user:` key lookup from
  `:37-46`. **Copy the idiom; do not extract a shared helper** (spec §5,
  ADR-0036).
- `test.setTimeout(300_000)` — two browser launches, one interactive Keycloak
  sign-in, one silent renewal. The siblings use 240–600 s.
- ADR-0084 budget applies (≤ 300 LOC/file, ≤ 30 LOC/method, ≤ 4 params). Comfortable.

### Modified — none

- **`playwright.config.ts` is not touched.** The `wall` project already selects
  `wall-*.spec.ts`. It is an ADR-0109 contention file; not touching it keeps the
  slice conflict-free.
- **`.github/workflows/ci.yml` is not touched.** `pnpm test:e2e` runs the whole
  suite unfiltered (`:257`), and `scripts/wait-for-e2e-stack.sh` already waits on
  :5175 by name.
- **`e2e/support/*` is not touched.** Another ADR-0109 contention point, and the
  helper would have one consumer.

## 6. ADR-0109 contention check — **clean**

| Contention file | Touched? |
|---|---|
| `src/Shared.Kernel/*`, `src/Shared.Contracts/*` | no |
| `src/AppHost/AppHost.cs` | no |
| `apps/shared/*`, `apps/kiosk-web/*` | no |
| `e2e/support/*` | **no** — deliberately (§5) |
| `playwright.config.ts` | **no** — the `wall` project already matches |
| `.github/workflows/ci.yml` | no |
| `Directory.Packages.props`, `package.json` | no — `@playwright/test` is already a dependency |

The slice owns exactly one new file plus `specs/107-*/`. It can run concurrently
with any other slice in the repository.

## 7. Test structure

```
wall-survives-a-process-death.spec.ts

test('a wall display comes back after its browser process dies', async ({}, testInfo) => {
  test.setTimeout(300_000)
  const profile = testInfo.outputPath('wall-profile')

  // --- process #1 -------------------------------------------------------
  const first = await chromium.launchPersistentContext(profile, { ignoreHTTPSErrors: true })
  await first.tracing.start({ screenshots: true, snapshots: true })
  const page = first.pages()[0] ?? await first.newPage()
  await signInAsWallDisplay(page)                       // wall-munich / Wall-munich-1234, on :5175
  await openFirstLayout(page)                           // layout-grid visible — a wall, not just a session

  await expireStoredAccessToken(page)                   // page.evaluate, key READ not constructed
  await first.tracing.stop({ path: testInfo.outputPath('first.zip') })
  await first.close()                                   // AWAITED — two Chromiums on one profile is undefined

  // --- process #2, same directory, nothing handed over ------------------
  const second = await chromium.launchPersistentContext(profile, { ignoreHTTPSErrors: true })
  await second.tracing.start({ screenshots: true, snapshots: true })
  const revived = second.pages()[0] ?? await second.newPage()

  await revived.addInitScript(() => {                   // BEFORE any app code
    const key = Object.keys(localStorage).find((k) => k.startsWith('oidc.user:'))
    ;(window as ...).__grantAtBoot = key === undefined ? null : localStorage.getItem(key)
  })

  await revived.goto('http://localhost:5175/')          // absolute — no baseURL on a manual context

  // control 2 — the profile was reused AND the token was expired on disk
  const atBoot = await revived.evaluate(() => (window as ...).__grantAtBoot)
  expect(atBoot, 'the relaunched profile must carry the grant the first process wrote').not.toBeNull()
  expect(JSON.parse(atBoot).expires_at, 'and its access token must already be spent').toBeLessThan(Date.now()/1000)

  // control 1 — the restart is genuinely cookie-less
  const cookies = await second.cookies()
  expect(cookies.filter((c) => /KEYCLOAK_IDENTITY|AUTH_SESSION_ID/.test(c.name)),
         'a restarted device carries no provider session cookie').toHaveLength(0)

  // THE CLAIM — what the wall shows, unattended
  await expect(revived.getByRole('button', { name: /sign in/i })).toHaveCount(0, { timeout: 90_000 })
  await expect(revived.locator('#username')).toHaveCount(0)
  await expect(revived.getByRole('heading', { name: 'Pick a layout' })).toBeVisible({ timeout: 90_000 })
  await expect(revived.getByRole('listitem').first()).toBeVisible({ timeout: 90_000 })
  await openFirstLayout(revived)                        // layout-grid — the wall renders

  await second.tracing.stop({ path: testInfo.outputPath('second.zip') })
  await second.close()
})
```

**Design notes that are load-bearing, not preference:**

- **`ignoreHTTPSErrors: true` on both launches.** A manual context inherits
  nothing from `use`, Keycloak runs on a dev certificate, and without it the
  refresh exchange fails on certificate validation and the screen falls to a
  login form — indistinguishable from the product defect. This exact trap is
  written into `kiosk-comes-back.spec.ts:70-78`.
- **Absolute URLs.** No `baseURL` on a manually launched context, so
  `page.goto('/')` throws. All three sibling wall specs pass `baseURL` explicitly
  to `newContext`; `launchPersistentContext` takes it too, and passing it is
  fine — but the engineer must not *assume* `:61` applies.
- **Nothing is written into process #2.** No `addInitScript` that calls
  `setItem`. If a `setItem` appears in this file, the slice has become the thing
  it was written to replace.
- **The `oidc.user:` key is read, never constructed** (spec §2.3).
- **The boot state is read at `addInitScript` time.** Reading it after the page
  settles returns the *renewed* grant, whose `expires_at` is in the future — the
  assertion would then be unfailable, which is the #2054 shape the issue names.
- **The picker must be populated, not merely present.** An empty picker is what a
  token carrying no fab produces, and it renders without an error
  (`e2e/support/kiosk-session.ts:19-28` records that this exact laxity hid a
  defect for the kiosk's whole life).
- **The final `openFirstLayout` is the claim.** A picker proves authentication; a
  `layout-grid` proves the wall. The issue's "Done means" is explicit that the
  assertion is about what the wall shows.
- **Tracing by hand.** `trace: 'on-first-retry'` never fires for a context the
  fixtures did not create, and a CI failure on a Linux runner with no trace is
  the worst diagnostic position this slice can end up in (spec §7.2).
- **`first.close()` is awaited before the second launch.** Overlapping processes
  on one user-data directory is undefined behaviour, and its symptom would be a
  launch failure that reads like a product defect.

### Reuse before invention

`signInAsWallDisplay` and the stored-grant reader both already exist, twice, in
`wall-outlives-its-session.spec.ts` and `wall-withdrawal.spec.ts`. Copy them.
A third copy is cheaper than the shared helper, because `e2e/support/*` is an
ADR-0109 contention file and the extraction is a refactor with three call sites
that this slice was not asked to make.

## 8. Gates

| Gate | How it is met |
|---|---|
| Phase 4a colour | **Characterisation, observed green** (spec §6.3). No production code changes, so no red is available and none is to be manufactured. Evidence is T003/T004. |
| Coverage (ADR-0065) | Unaffected — e2e specs do not count toward the Domain/Application/Shared gates and no production code is added. |
| Type check | `pnpm typecheck:e2e` (`tsc -p e2e/tsconfig.json`) — the file is inside `include` (spec 088 put the suite under `tsc`; SC-006). |
| Format | `pnpm format:check` — prettier covers `e2e/**/*.ts`. |
| Selection | SC-007: `--project=wall --list` names the test and schedules `seed` + `cleanup`. |
| Latency (§IV) | **N/A**, stated in the spec header. No leg touched. |
| Constitution §II (primitives) | N/A — TypeScript test code, not a domain model. |

## 9. Risks

| Risk | Mitigation |
|---|---|
| **`launchPersistentContext` behaves differently headless on the Linux runner** (spec A4/§7.2) | Unknown until CI runs. **If it fails there, report it as a finding** — a `test.skip` on CI is a weakened gate and a blocked outcome under ADR-0144, not a fix. |
| **The relaunched profile keeps the Keycloak session cookie**, so recovery rides the cookie rather than the grant | Asserted, not assumed (spec §3.1 control 1). If it fires: record it, then clear those cookies explicitly before `goto` and say so in the verification note. |
| Chromium has not flushed `localStorage` when the context closes | The read-back at boot *is* the check. If the entry is absent at boot, C1's predicted failure has occurred without C1 being applied — that is a finding about flush timing, not a reason to add a `sleep` and move on. |
| A CI retry starts from a signed-in profile and proves nothing | `testInfo.outputPath()` is retry-unique, so attempt 2 gets a fresh directory. Verified by construction; state it in the PR. |
| Two Chromiums on one profile | `await first.close()` before the second launch (§7). |
| Missing `ignoreHTTPSErrors` reads as a product defect | Called out twice (§7, spec §2.5). The symptom to watch for is a login form after the relaunch. |
| The test is red on a clean tree | **That is the expected-and-acceptable outcome** the issue predicts. SC-005: file it, quote it, change no assertion. It does **not** convert the slice into a fix. |
| A first run after machine churn fails and reads as a defect | Run twice before concluding anything (SC-004). |
| `wall-withdrawal.spec.ts` ends a `wall-munich` offline session concurrently | It targets one specific `sid` (`:154`), never the account, so a sibling session survives — that is the very claim that file asserts. No mitigation needed; noted so a puzzling `invalid_grant` is diagnosed rather than guessed at. |

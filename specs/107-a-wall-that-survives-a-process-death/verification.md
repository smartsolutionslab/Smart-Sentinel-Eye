# Verification note — spec 107

**Issue:** #2067 · **Branch:** `test/2067-a-wall-that-survives-a-process-death`
**Covers:** SC-001 (what the wall shows), SC-004's local half (green twice), SC-006, SC-007
**Date:** 2026-09-09 · **Phase:** 5 (ADR-0037)

**Machine:** one developer laptop — Windows 11 Pro 10.0.26100, Docker Desktop, Node
v24.5.0, pnpm 10.30.3, Chromium 151.0.7922.34 (headless). One Aspire stack, Debug,
`ScenarioSimulator=false`, 41 resources `Running` / `Healthy` at the time of both runs.
Nothing else driving the box.

**Not re-done here:** phase 4a's counterfactuals C1 and C2 (SC-002, SC-003). Their
verbatim output belongs to phase 4a and is quoted in the PR body, not re-run in this note.
`e2e/wall-survives-a-process-death.spec.ts` was **not modified** by this phase.

---

## What was observed

> **A Chromium process signed a wall display in, its access token was expired in the
> on-disk profile, the process ended, and a second Chromium process launched on that same
> directory exchanged the persisted offline grant for a new token and painted the wall —
> with nothing handed to it and no person at the screen.**

The green runner line is the weakest part of that claim. What makes it an *observation*
rather than a test result is the three artefacts below, each of which a reader can check
without trusting Playwright's `ok`:

1. **A screenshot of the relaunched wall**, from the second process's own trace.
2. **A `grant_type=refresh_token` POST to Keycloak → 200**, made by the second process,
   with no `/protocol/openid-connect/auth` request anywhere in its network log — so the
   recovery went through the stored grant and not through a redirect a person would see.
3. **The grant on disk, in Chromium's own LevelDB file**, carrying a *spent* expiry
   written by the dead process and a *renewed* expiry written by its successor.

---

## How

Stack already up (`aspire run`, Debug). Then, twice:

```sh
pnpm exec playwright test --project=wall wall-survives-a-process-death.spec.ts
```

`--project=wall` pulls in `seed` and schedules `cleanup` (SC-007), so each run publishes
its own layouts and retires what it registered.

---

## Run 1 — verbatim

```
Running 7 tests using 3 workers

  ok 1 [seed] › e2e\support\seed-published-layout.setup.ts:22:6 › a published layout exists for the kiosk to open (11.4s)
  ok 3 [seed] › e2e\support\seed-live-video-wall.setup.ts:26:6 › a published wall exists whose tile has both video and a bound overlay (12.1s)
  ok 2 [seed] › e2e\support\seed-bound-overlay-wall.setup.ts:21:6 › a published wall exists whose tile binds an overlay bound to a variable (13.9s)
  ok 4 [wall] › e2e\wall-survives-a-process-death.spec.ts:83:7 › A wall survives a process death (spec 107 US1) › a wall display comes back after its browser process dies (6.7s)
[cleanup] archived 2 overlay(s); nothing to do 12; refused 0
  ok 6 [cleanup] › e2e\support\archive-e2e-overlays.teardown.ts:42:8 › archive the overlays this run published (8.9s)
[cleanup] archived 3 layout(s); skipped 0
  ok 5 [cleanup] › e2e\support\archive-e2e-layouts.teardown.ts:40:8 › archive the layouts this run published (9.4s)
[cleanup] retired 3 camera(s); skipped 0
  ok 7 [cleanup] › e2e\support\retire-e2e-cameras.teardown.ts:50:8 › retire the cameras this run registered (9.7s)

  7 passed (35.7s)
```

## Run 2 — verbatim

```
Running 7 tests using 3 workers

  ok 3 [seed] › e2e\support\seed-published-layout.setup.ts:22:6 › a published layout exists for the kiosk to open (6.8s)
  ok 1 [seed] › e2e\support\seed-bound-overlay-wall.setup.ts:21:6 › a published wall exists whose tile binds an overlay bound to a variable (8.6s)
  ok 2 [seed] › e2e\support\seed-live-video-wall.setup.ts:26:6 › a published wall exists whose tile has both video and a bound overlay (8.7s)
  ok 4 [wall] › e2e\wall-survives-a-process-death.spec.ts:83:7 › A wall survives a process death (spec 107 US1) › a wall display comes back after its browser process dies (5.9s)
[cleanup] archived 3 layout(s); skipped 0
  ok 5 [cleanup] › e2e\support\archive-e2e-layouts.teardown.ts:40:8 › archive the layouts this run published (8.3s)
[cleanup] archived 2 overlay(s); nothing to do 14; refused 0
  ok 7 [cleanup] › e2e\support\archive-e2e-overlays.teardown.ts:42:8 › archive the overlays this run published (8.7s)
[cleanup] retired 3 camera(s); skipped 0
  ok 6 [cleanup] › e2e\support\retire-e2e-cameras.teardown.ts:50:8 › retire the cameras this run registered (9.3s)

  7 passed (28.1s)
```

**Two runs, because the first run after machine churn on this box reads exactly like a
regression.** Both green; the second is not the first re-reported.

### 6.7 s and 5.9 s look too fast — they are not

A test that launches two browsers and signs in through Keycloak finishing in six seconds
invites the suspicion that it did nothing. It did not skip: the trace timeline from run 1
is the whole scenario, on a warm stack over loopback.

```
=== process one ===          === process two ===
0.00s  goto  :5175/          0.00s  addInitScript
1.01s  click  sign in        0.01s  goto  :5175/
1.13s  fill  #username       0.87s  evaluateExpression   (grant read at document start)
2.16s  fill  #password       0.89s  cookies              (provider-session control)
2.21s  click  #kc-login      0.90s  expect sign-in button  -> count 0
2.41s  expect "Pick a layout"0.95s  expect #username       -> count 0
3.03s  expect listitem #0    0.96s  expect "Pick a layout"
3.04s  click  listitem #0    1.34s  expect listitem #0
3.13s  expect layout-grid    1.36s  click  listitem #0
3.21s  evaluateExpression    1.45s  expect layout-grid
       (expire the token)
```

---

## Evidence 1 — the relaunched wall, on screen

Extracted from the second process's hand-started trace. Artefacts left at
`test-results/spec-107-evidence/` (gitignored; regenerate with the command above, then
`pnpm exec playwright show-trace test-results/spec-107-evidence/run2/process-two.zip`).

| file | what it shows |
|---|---|
| `run1/frame-06-1.31s.jpeg` | **Process #2, t+1.31 s:** heading *Pick a layout* over three populated cards — *Kiosk Seed Wall 1788937659071*, *SC004 Wall …*, *Spec056 Wall …*, each *v1*. No sign-in button, no username field, no error. |
| `run1/frame-08-1.44s.jpeg` | **Process #2, t+1.44 s:** the wall itself — header *Kiosk Seed Wall 1788937659071*, a *Back* control, and the layout grid rendering its tile as *Idle*. |
| `run2/frame-07-1.49s.jpeg` | The same populated picker on run 2, with run 2's own layout names (…852952 / …849043 / …849056). |
| `run2/frame-08-1.51s.jpeg` | Run 2's last captured frame is the tile mid-load (*Loading camera…*); the `layout-grid` assertion had already passed against the DOM and the screencast ended at close. **Run 1's frame 08 is the painted grid** — cited above rather than pretending run 2 caught it. |

The tile reads *Idle* / *Loading camera…* because the seeded layout's tile has no live
source in this run. That is the wall rendering; it is not a claim about video.

## Evidence 2 — the second process spent the grant, and only the grant

Every off-app request the second process made, from its trace's network log — the whole
list, not a selection:

```
GET  https://localhost:10756/realms/smart-sentinel-eye/.well-known/openid-configuration  -> 200
POST https://localhost:10756/realms/smart-sentinel-eye/protocol/openid-connect/token     -> 200
       grant_type=refresh_token
       client_id=kiosk-wall
       scope=openid sse.streams.read sse.overlays.read offline_access sse.variables.read
             sse.cameras.read sse.layouts.read
GET  http://localhost:55949/layout-composition/layouts?state=Published                   -> 200
GET  http://localhost:55949/layout-composition/layouts/01a084fe-791e-745a-a613-e61717e30065 -> 200
GET  http://localhost:55949/stream-distribution/streams/01a084fe-6cdb-76a0-87d7-dc2abe755d08 -> 200
```

Decoded claims of the refresh token it presented:

```json
{ "iss": "https://localhost:10756/realms/smart-sentinel-eye",
  "sub": "ddee2d3e-7975-4bd0-8d26-8244f8879008",
  "typ": "Offline",
  "azp": "kiosk-wall",
  "scope": "openid sse.streams.read sse.overlays.read offline_access sse.variables.read
            sse-identity sse.cameras.read sse-audience sse.layouts.read sse-groups",
  "aud_x": "smart-sentinel-eye-api" }
```

**`typ: Offline`** is the load-bearing part: what survived the process death is the offline
grant ADR-0131 rests on, not a session that happened to still be open. Run 2's log is the
same five requests with the same grant type.

**There is no `…/protocol/openid-connect/auth` request in either run.** Combined with the
test's cookie control (`KEYCLOAK_IDENTITY` / `AUTH_SESSION_ID` absent — asserted, and
green), the second process could not have re-authenticated silently: the *only* credential
it had was the one on disk. This is the control C3 could not induce, standing as a
positive observation instead.

Then a gateway call carrying that token returned **200 with published layouts** — so the
recovered grant is not merely accepted by Keycloak, it buys data from the API gateway.

## Evidence 3 — the grant is really on disk, in Chromium's own store

Read directly out of the profile after the run, with no browser involved —
`test-results/…-dies-wall/wall-profile/Default/Local Storage/leveldb/000003.log`:

```
key    : oidc.user:https://localhost:10756/realms/smart-sentinel-eye:kiosk-wall
expires_at values in the file : 1788934072 , 1788941275
now (at read time)            : 1788937805
```

Two records, in write order: `1788934072` = 2026-09-09T06:07:52Z, the expiry **process #1
wrote and flushed on close** (an hour in the past, exactly the test's `-3600`), and
`1788941275` = 08:07:55Z, the expiry **process #2 wrote after its refresh exchange**. Run 2
reproduces it: `1788934261` and `1788941463`, read at `1788937895`.

This is the whole claim in one file a human can `cat`: a spent grant left by a dead
process, and a fresh one left by its successor, in the same on-disk store.

It also vindicates the test's insistence on *reading* the storage key rather than
constructing it — the authority in that key is `localhost:10756`, a port this stack chose
this run.

---

## The premise of #2067, checked rather than assumed

The issue's claim is that the existing "restart" tests reconstruct rather than persist.
Read at their cited lines, they do:

- `e2e/kiosk-comes-back.spec.ts:54-79` reads the grant with `page.evaluate`, rewrites
  `expires_at` in a JS object, and hands it to `context.browser()?.newContext(...)`.
- `e2e/wall-outlives-its-session.spec.ts:110-125` does the same.

Neither touches a user-data directory. `grep -rn "launchPersistentContext" --include=*.ts`
over the repo returns **only** the two lines in the new spec. The premise holds.

---

## Other gates re-run on this branch

| gate | result |
|---|---|
| `pnpm typecheck:e2e` (SC-006) | clean — `tsc -p e2e/tsconfig.json --noEmit`, no output |
| `pnpm format:check` (SC-006) | `All matched files use Prettier code style!` |
| `pnpm exec playwright test --project=wall wall-survives-a-process-death.spec.ts --list` (SC-007) | names the test, and schedules 3 `seed` + 3 `cleanup` files around it — `Total: 7 tests in 7 files` |

---

## Latency

**N/A — a test-only change on a path the latency budget does not cover.** The branch adds
one Playwright spec and three spec documents (`git diff origin/develop...HEAD --stat`: 4
files, 0 lines of application code). It is about a wall display's *session* surviving a
process death — minutes to hours after the fact — not about any leg of constitution §IV's
`event arrival → overlay rendered ≤ 800 ms` path. No leg is cited because none is touched,
and no figure is offered because inventing one would be worse than the omission.

---

## What was not covered

Stated plainly, because §7 of the spec was written before the run precisely so this section
could not quietly widen.

- **A graceful close is not a power cut.** `context.close()` gives Chromium the chance to
  flush LevelDB; a power cut does not. Evidence 3 shows the spent grant *did* reach disk —
  but on a clean exit. Whether a `SIGKILL` at the wrong microsecond leaves a readable
  record is untested here and belongs to hardware testing (#1976). §7.1 priced this before
  the run and this note does not move the line.
- **Linux / CI parity is still unverified, and cannot be resolved from here.** This is the
  first `launchPersistentContext` in the repo, and it has never executed on the GitHub
  runner. The CI `e2e` job runs `pnpm test:e2e`, which selects every project, so the test
  *will* run there — but nothing observed on this Windows box predicts the runner's
  behaviour, and the specific unknown is whether a headless Chromium under the runner's
  filesystem flushes Local Storage on `close()` with the same timing. **Open risk, carried
  deliberately.** ADR-0144 forbids the two ways of making it go away: a `test.skip` on CI
  is a weakened gate, and guessing is not evidence. If the runner is red, the verbatim
  failure is the deliverable (SC-005).
- **One screen, one profile, one restart.** Not twenty walls, not repeated restarts, not
  overnight. §Availability stays undischarged.
- **Video was never observed.** The recovered wall renders its tile as *Idle* /
  *Loading camera…*; this note claims the wall came back authenticated and populated, not
  that a picture arrived.
- **Retry-uniqueness of the profile directory is asserted, not observed.** The test relies
  on `testInfo.outputPath` carrying a `-retry<n>` suffix so a CI retry starts from an
  unused profile. That is documented Playwright behaviour, but no run here failed, so no
  retry directory was created and the property was not watched.
- **The counterfactuals were not re-run.** SC-002 and SC-003 are phase 4a's evidence; this
  phase observed the green path and its mechanism only.
- **A warm stack.** Both runs hit an Aspire stack that had been up ~20 minutes. A cold
  stack is CI's condition, not this note's.

# Spec 107 — A wall that survives a process death

**Issue:** #2067.
**ADRs:** ADR-0131 (`localStorage` is the store that outlives the process),
ADR-0133 (a failed renewal is classified; recoverable retries unattended),
ADR-0134 (a wall display signs in as `kiosk-wall` with an offline grant),
ADR-0108 (Playwright e2e against a live `aspire run` stack), ADR-0144 (the lane
may not make an architectural decision), ADR-0109 (contention files),
ADR-0036 (smallest possible change).
**Latency budget:** **N/A.** No production code changes, so no leg of
constitution §IV is touched. Nothing here is on the event→overlay path.

---

## 1. Why this slice exists

Constitution §Availability's target — *a wall of 20 kiosks rebooting must come up
unattended* — is recorded as unmet, and ADR-0134's own "not demonstrated" list
names **a real power cut** first. Three specs (049, 051, 052) each moved a piece
and each declined to claim it.

The cheapest untaken step is not hardware. It is that **the browser process has
never actually died in any test.** Every "restart" in the suite is a
reconstruction: state is read out of one context in JavaScript and written into a
fresh one before the page loads. That proves the app can *spend a grant it is
handed*. It does not prove a grant **reaches disk and is read back by a different
process**, which is what a power cut requires and the only thing ADR-0131's
decision actually rests on.

## 2. Premise verification

### 2.1 Is there any persistent-profile test? — **No.**

```
$ grep -rn "launchPersistentContext\|userDataDir\|storageState" e2e/ apps/ playwright.config.ts
(no matches — re-run 2026-09-09, still zero)
```

Every context in the suite is `browser.newContext()` (16 call sites across 8
files), which is an ephemeral profile in a browser that never restarts.

### 2.2 The issue's table is right in substance and **wrong in one row** — corrected here

> **the wall client has never been power-cycled in any test**

The *conclusion* holds; the supporting row does not.
`wall-outlives-its-session.spec.ts:110` already contains `comes back from a
restart with nobody touching it`, on the wall client, on :5175. It reconstructs
(`addInitScript` at `:132`) exactly as `kiosk-comes-back.spec.ts:94` does. So the
table's "survives a restart — proved for `kiosk-web`" understates what exists:

| Claim | Client | How | File |
|---|---|---|---|
| survives a restart | `kiosk-web` | **reconstruction** | `e2e/kiosk-comes-back.spec.ts:47` |
| survives a restart | `kiosk-wall` (:5175) | **reconstruction** | `e2e/wall-outlives-its-session.spec.ts:110` |
| outlives the ceiling | `kiosk-wall` | token claims (`typ: Offline`, no `exp`) | `e2e/wall-outlives-its-session.spec.ts:66` |

The gap is therefore **one gap, not two**: *no test has ever restarted a browser
process*, on either client. Recorded rather than quietly reinterpreted — that is
the move ADR-0129, ADR-0131 and ADR-0133 each exist to enforce, and the issue
itself asks for it.

**Consequence for scope:** the slice targets the **wall** client, because that is
the one production walls run and the one whose grant is meant to outlive the
session ceiling. It does not add a second persistent-profile test for
`kiosk-web`; §5 says why.

### 2.3 What does "expire the access token on disk" concretely mean? — **Rewrite the `oidc.user:*` entry, then let the profile flush.**

The store is `localStorage`, set once, in one place:

```
apps/kiosk-web/src/app/auth.ts:112
  userStore: typeof window === 'undefined' ? undefined : new WebStorageStateStore({ store: window.localStorage }),
```

`oidc-client-ts` writes one entry, keyed `oidc.user:<issuer>:<client_id>`, whose
JSON carries `access_token`, `refresh_token` and `expires_at` (seconds). The
second record is the app's own: `apps/kiosk-web/src/app/useSessionExpiry.ts:9,251`
— `sse.auth.wasAuthenticated`, also `localStorage`, written whenever
`auth.isAuthenticated`.

**In a persistent Chromium profile both are LevelDB rows under
`<profile>/Default/Local Storage/leveldb`, not editable as text.** So "expire it
on disk" is: rewrite `expires_at` through `page.evaluate` **in the live session**,
then close the context so Chromium flushes, then relaunch on the same directory.
The relaunch reading back the *rewritten* value is itself the proof the write
reached disk.

**The key is read, never constructed.** It embeds the issuer, and the provider is
served on a port the host chooses per run — a hardcoded `localhost:8080` key
restored an entry the app never looks for and made a working feature read as
broken (`kiosk-comes-back.spec.ts:60-64` records the incident).

### 2.4 Does recovery depend on a cookie the restart would drop? — **No, verified in source.**

`useSessionExpiry.ts:273-308`: on a page that has never been authenticated *in
this life* but whose `localStorage` says it has been authenticated before
(`hasBeenAuthenticated()`, `:37`), the app calls `auth.signinSilent()` exactly
once. `oidc-client-ts` prefers the stored `refresh_token` over the `prompt=none`
iframe when one is present, so the exchange is a direct token call and needs no
provider SSO cookie. This matters because a genuine restart drops session
cookies, and a design that leaned on the iframe would pass under reconstruction
and fail under a real one.

Both records the path needs (`oidc.user:*`, `sse.auth.wasAuthenticated`) are
`localStorage`, so a reused profile supplies them **without the test writing
anything** — the whole point of the slice.

### 2.5 Is `launchPersistentContext` usable given the config? — **Yes, with four constraints.**

`playwright.config.ts` declares no `webServer` (ADR-0108: Aspire owns
orchestration), so nothing conflicts. `chromium` is re-exported by
`@playwright/test` (1.62.1). The constraints, each verified against the tree:

1. **The file must be named `wall-*.spec.ts`.** `playwright.config.ts:62` routes
   on `testMatch: /wall-.*.spec.ts/`, and `:35`'s `testIgnore` keeps it out of
   the management project. That name also buys `dependencies: ['seed']` (`:64`) —
   the published layout without which the picker is empty — and the `cleanup`
   teardown (`:65`).
2. **A manually launched context inherits nothing from `use`.** Not `baseURL`
   (`:61`) and — the trap that already cost `kiosk-comes-back.spec.ts` a false
   failure (`:70-78`) — not `ignoreHTTPSErrors`. Keycloak is served over HTTPS
   with a development certificate (`ci.yml:242` runs `dotnet dev-certs https`,
   which does not trust it on the runner), so **without `ignoreHTTPSErrors: true`
   the refresh exchange fails on certificate validation and the screen falls to a
   login form — which reads exactly like the product defect under test.**
3. **The two launches must not overlap.** Two Chromium processes on one user-data
   directory is undefined behaviour; the first `close()` must be awaited.
4. **No `page` fixture means no automatic trace or video.** `trace:
   'on-first-retry'` (`:20`) covers fixture-provided contexts only, so a CI
   failure here is otherwise blind.

### 2.6 What does CI do? — **Boots the real stack; nothing needs changing.**

`.github/workflows/ci.yml:190-257`: Ubuntu, full Aspire run-mode stack in the
background, `scripts/wait-for-e2e-stack.sh` (which already waits on :5175 by
name), then `pnpm test:e2e` — the whole suite, no filter. A new `wall-*.spec.ts`
is picked up with **no workflow edit**, which keeps this slice off ADR-0109's
contention list.

`retries: 2` and `workers: 1` on CI (`playwright.config.ts:15-16`). A retry must
begin from a *clean* profile, or the second attempt starts signed in and proves
nothing — §4 pins the profile to `testInfo.outputPath()`, which Playwright makes
retry-unique.

---

## 3. User story

**US1 (P1)** — As the operator of a fab whose walls lose power, I need a wall
display to return to its picture after the browser process has genuinely died,
using only what was written to disk, so that "comes up unattended" is an observed
property of the client production runs rather than an inference from a
reconstructed context.

Independently shippable: one new e2e spec file. No production change. Observable
end to end against a booted stack by a person following §3.2.

### 3.1 Acceptance scenarios

```gherkin
Scenario: A wall display returns after a real process death (happy path)
  Given a Chromium profile directory that has never been used
    And a wall display signed in at http://localhost:5175 as wall-munich
    And its wall rendering
   When the stored grant's access token is expired in place
    And the browser process is ended
    And a new browser process is started on the same profile directory
   Then the grant read from that profile at boot carries an expiry in the past
    And the screen reaches a populated layout picker without anyone touching it
    And no sign-in control and no credential field appear at any point
    And the wall renders
```

```gherkin
Scenario: The restart is genuinely cookie-less (the control that makes it mean something)
  Given a wall display recovered from a relaunched profile
   When the relaunched context's cookies are read
   Then it holds no identity-provider session cookie
```

Without this, a green happy path is consistent with the screen riding a surviving
SSO cookie rather than spending its grant — the same class of false pass that
`kiosk-comes-back.spec.ts` avoided by discarding the context. **If this
assertion fires, the finding is that Chromium persisted the cookie, and the
answer is to record it and clear those cookies explicitly — never to drop the
assertion.**

```gherkin
Scenario: The profile really was reused (the second control)
  Given a relaunched profile
   When the grant is read at document start, before any application code runs
   Then an oidc.user entry is present
    And its expires_at is in the past
```

The issue asks for exactly this suspicion: *"if it passes on the first run …
check the profile directory is actually being reused and the token really was
expired on disk."* Read at `addInitScript` time, so what is asserted is what the
new process found, not what the app subsequently rewrote — a value read after
boot would be the *renewed* grant and would pass unconditionally.

```gherkin
Scenario: A first-boot screen still asks for a person (negative)
  Given a Chromium profile directory that has never been used
   When a wall display is opened on it
   Then a sign-in control is offered
```

Already true by construction (`useSessionExpiry.ts:274` gates the silent attempt
on `hasBeenAuthenticated()`), and it is the *first act* of the happy-path
scenario — the sign-in step would be impossible otherwise. **Stated, not written
as a separate test**: a standalone version would duplicate the first three lines
of the main test for no additional signal.

**Auth scenario:** the refused case (a shut-out wall display shows no credential
field and does not retry) is ADR-0133/ADR-0134 territory and is already covered
by `kiosk-identity-refused.spec.ts` and `wall-authority.spec.ts`. Not re-asserted
here; duplicate coverage is a cost a later reader pays.

### 3.2 Independent end-to-end test procedure

Runnable by a person, no test code read, against a booted AppHost:

1. Launch Chrome with a brand-new profile: `chrome --user-data-dir=/tmp/wall-107`.
2. Go to `http://localhost:5175`, sign in as `wall-munich` / `Wall-munich-1234`,
   open the first layout — the wall renders.
3. DevTools console:
   `k=Object.keys(localStorage).find(x=>x.startsWith('oidc.user:'));
   u=JSON.parse(localStorage[k]); u.expires_at=Math.floor(Date.now()/1000)-3600;
   localStorage[k]=JSON.stringify(u)`
4. **Quit Chrome entirely** (not just the tab).
5. Relaunch with the *same* `--user-data-dir=/tmp/wall-107`, open
   `http://localhost:5175`.
6. Touch nothing. Expected: the picker appears populated, with no sign-in button
   and no username field. Open the first layout — the wall renders.
7. Control: in DevTools → Application → Cookies, the identity provider's origin
   holds no session cookie.

---

## 4. Locked choices

| Concern | Choice | Why |
|---|---|---|
| Test kind | Playwright e2e against the live stack | ADR-0108. The claim is about state outliving a process; nothing below the browser can hold it. |
| Client | **wall** (:5175, `kiosk-wall`) | §2.2 — the client production walls run, and the one with the offline grant. |
| File | `e2e/wall-survives-a-process-death.spec.ts` | `playwright.config.ts:62` routes `wall-*` to the wall project, with `seed` and `cleanup` attached. |
| Profile directory | `testInfo.outputPath('wall-profile')` | Retry-unique and per-test, so a CI retry starts clean; uploaded with `test-results` on failure (`ci.yml:275`). |
| Restart | `context.close()` then a second `launchPersistentContext` | The strongest process death Playwright offers; §7.1 prices what it does not cover. |
| Expiry | rewrite `expires_at` via `page.evaluate` before closing | §2.3. The read-back is the proof it reached disk. |
| Reading the boot state | `addInitScript` stashing the raw entry | §3.1 control 2 — after boot the value has been renewed. |
| Sign-in | inline, as the sibling wall specs do | `wall-outlives-its-session.spec.ts:18-26` and `wall-withdrawal.spec.ts:48-56` each carry their own; a shared helper is a separate refactor (ADR-0036). |
| Tracing | started by hand on both contexts | §2.5 constraint 4. |

## 5. Out of scope

- **Any production change.** If the test goes red, that failure **is** the
  finding: file it, quote it verbatim, do not adjust the assertions (issue #2067,
  "Done means").
- **A persistent-profile test for `kiosk-web` (:5174).** The gap is one mechanism
  proved on the wrong shape of context (§2.2); proving it twice buys a second
  four-minute e2e test and no new information. If the wall test finds a defect,
  whether `kiosk-web` shares it is a question for that defect's issue.
- **Twenty screens; a real power cut; an OS boot.** Hardware — still #1976.
- **A device runtime holding a real device credential** — #1987/#1988.
- **A shared persistent-context helper in `e2e/support/`.** One consumer; that
  file set is an ADR-0109 contention point, and this slice stays off it.
- **Killing the browser with a signal rather than closing it.** §7.1.

---

## 6. Declarations (ADR-0144)

1. **Engineer:** `frontend-engineer`. TypeScript + Playwright against the
   kiosk-web wall client. No C#, no AppHost, no CI workflow, no realm.
2. **ADR needed? — No, and I agree with the issue after reading.** The slice
   *applies* ADR-0131 (the store), ADR-0133 (unattended retry) and ADR-0134 (the
   wall client and its offline grant), and *observes* what ADR-0134's own "not
   demonstrated" list already names as owed. It decides nothing: no new store, no
   new scope, no new client, no policy. `launchPersistentContext` is a test
   technique inside ADR-0108's locked choice of Playwright, not an architectural
   decision. **The one thing that could need an ADR is a defect this test
   uncovers** — and per §5 that is filed, not fixed here, precisely because
   ADR-0144 forbids this lane from deciding it.
3. **Colour: behaviour-preserving → characterisation, observed green.** No
   production code is touched, so there is no red to manufacture, and a compile
   error is not a red test. The evidence that the assertions can fail is the
   **counterfactual in §8, which is mandatory here rather than optional.**
   **The one branch that is not characterisation:** if the test is red on a clean
   tree, the product is broken and §9 SC-005 governs — report, file, do not
   weaken. That is a finding, not a colour change.

---

## 7. What this will not have demonstrated

Written before the run, so the verification note cannot quietly widen.

### 7.1 A graceful close is not a power cut

`context.close()` lets Chromium flush LevelDB. A power cut does not. So this
proves *the grant reaches disk and a new process reads it back* — it does not
prove Chromium had flushed at the instant the power failed. Playwright exposes no
process handle for a persistent context (`context.browser()` is `null`), so a
`SIGKILL` would mean locating the pid by name — brittle on a Windows dev box and
a Linux runner alike, for a gap hardware testing (#1976) has to close anyway.

**Priced, not hidden.** The delta between this and reconstruction is still the
whole of §1: a different OS process, a real on-disk store, no state handed over.

### 7.2 Linux/CI parity is unverified until CI runs it

The issue names this. Persistent profiles under the runner's headless Chromium
are unexercised in this repo. **If it behaves differently there, say so — do not
weaken the test to make the runner green.** A `test.skip` on CI would be a
weakened gate, which ADR-0144 forbids.

### 7.3 One screen, one profile, one restart

Not twenty, not repeated, not overnight. §Availability stays undischarged and
this spec claims nothing about it beyond §1.

---

## 8. The counterfactual — prediction recorded before the run

The assertions must be shown capable of failing, because a green test over an
untested mechanism is the exact shape #2054 was (issue #2067, "Done means").

**C1 — the profile is not reused.** Point the second launch at a *fresh*
directory (one character in the path is enough).
**Prediction:** the boot-state control fails first — no `oidc.user` entry was
found at document start — and, if that assertion were removed, the screen would
land on a sign-in button and the happy path would fail on that instead.

**C2 — the token was not expired on disk.** Skip the `expires_at` rewrite.
**Prediction:** the boot-state control's `expires_at`-in-the-past assertion goes
**red** and the run stops there. That assertion runs before the happy-path ones,
so **the wall assertions are not reached and their outcome cannot be observed
under C2** — an earlier draft of this line predicted "the happy path still
passes", which describes something the run cannot show, and phase 4a found it.
What C2 does establish is the only thing it was asked to: the expiry assertion is
capable of failing. If C2 leaves the test green, that assertion is decorative and
the test is not proving recovery *through the grant*.

**C3 — recovery is riding a cookie.** Not inducible without a product change; the
cookie control (§3.1 scenario 2) is the standing guard instead, and its outcome
is recorded either way.

**If any prediction is wrong, correct this section in the PR body and say so.**
Do not proceed as though it held.

---

## 9. Success criteria

- **SC-001** — A wall display is observed rendering after a genuine process
  restart, with a grant whose access token was expired on disk. The assertion is
  about **what the wall shows**, never about storage contents alone.
- **SC-002** — C1 applied → red on the profile-reuse control; C1 reverted →
  green. Both outputs quoted verbatim in the PR.
- **SC-003** — C2 applied → the expiry assertion is red. The happy-path
  assertions are **not reached** and nothing is claimed about them under C2. If
  the test stays green, the test is not proving what SC-001 claims and that is
  reported, not patched.
- **SC-004** — Green twice on a clean tree (a first-run-after-churn failure is not
  a verdict), and green on the CI runner. A skip or a narrowed assertion to reach
  green on Linux is a blocked outcome, not a fix.
- **SC-005** — If the test is red on a clean tree, the verbatim failure and a new
  issue are the deliverable, §7 is amended to say so, and no assertion is relaxed.
- **SC-006** — `pnpm typecheck:e2e` and `pnpm format:check` clean (spec 088 put
  the e2e suite under `tsc`; a new file is in `e2e/tsconfig.json`'s `include`).
- **SC-007** — `pnpm exec playwright test --project=wall --list` names the new
  test, and `--project=wall` schedules `seed` and `cleanup`. A test the suite does
  not select is the failure mode this criterion rules out.

## 10. Assumptions (unavoidable guesses, marked)

- **A1** — Chromium's persistent profile stores `localStorage` per origin under
  the user-data directory and restores it on relaunch. Documented Playwright
  behaviour; **not yet observed in this repo** (§2.1 — zero prior uses). T001 is
  its first observation, and C1 is the check that it is real.
- **A2** — a relaunched profile carries no Keycloak session cookie, because
  Keycloak's identity cookies are session cookies and Chromium does not persist
  those without session restore. **Asserted rather than assumed** (§3.1 scenario
  2); if it is wrong the assertion says so.
- **A3** — the `seed` project's published layout is visible to `wall-munich`, so
  the picker is populated. Holds today: three wall specs depend on it
  (`wall-outlives-its-session.spec.ts:26`).
- **A4** — headless Chromium on the runner supports persistent contexts as the
  headed local one does. Unverified until CI runs (§7.2), and explicitly not to
  be worked around.

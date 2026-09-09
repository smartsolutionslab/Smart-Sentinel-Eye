# Tasks 107 — A wall that survives a process death

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2067
**Engineer:** `frontend-engineer` (single engineer; spec §6.1). No backend, no
infrastructure, no AppHost, no CI workflow.
**Phase 4a colour:** behaviour-preserving → **characterisation, observed green**
(spec §6.3). No production code is touched, so there is no red to observe and
none is to be manufactured. The evidence that the assertions can fail is T003 and
T004.

**Parallelism:** the slice is one file. Nothing here is `[P]` — T001 → {T002,
T003, T004} → T005 is a short chain over a single artefact. **No foundational
task blocks anything outside this slice** and no ADR-0109 contention file is
touched (plan §6), so this slice may run concurrently with any other work in the
repository.

---

## US1 — A wall display returns after a real process death

### [T001] [US1] The test

**File (new):** `e2e/wall-survives-a-process-death.spec.ts`

Write spec §3.1 as **one** `test`, structured exactly as plan §7.

Non-negotiables, each with its reason already recorded:

- **The file name must start with `wall-`.** `playwright.config.ts:62` routes on
  it; a differently-named file lands in the management project, drives :5173, and
  fails for reasons that have nothing to do with the product (`:28-35` records
  that incident).
- **`chromium.launchPersistentContext`, twice, on the same `mkdtemp` directory
  under `os.tmpdir()`, removed in a `finally`.** Unique by construction, so a CI
  retry still starts clean. **Not `testInfo.outputPath` (corrected in phase 6):**
  that puts a profile holding a live *offline* refresh token under
  `test-results/`, which `ci.yml:269-277` uploads with `if: always()` and 14-day
  retention on a public repository.
- **`ignoreHTTPSErrors: true` on both launches.** A manual context inherits
  nothing from `use`; Keycloak runs on a dev certificate, and without this the
  refresh exchange fails on certificate validation and the screen falls to a
  login form — indistinguishable from the defect under test
  (`kiosk-comes-back.spec.ts:70-78`).
- **Absolute URLs** (`http://localhost:5175/`). No `baseURL` is inherited.
- **`await first.close()` before the second launch.** Two Chromiums on one
  user-data directory is undefined behaviour.
- **Nothing is written into the second process.** Process #2 gets an
  `addInitScript` that only **reads**; no `localStorage.setItem` runs against the
  relaunched context. Writing a grant into it is what would re-create the
  reconstruction this test replaces.
  **This does not forbid the `expires_at` rewrite in process #1** — that is a
  `setItem`, it is the test's own setup, it happens before the process dies, and
  spec §2.3 and plan §7 both require it. An earlier draft of this bullet said
  "no `localStorage.setItem` anywhere in this file" and so forbade the step the
  spec mandates; plan §7's wording ("nothing is written into process #2") was
  the precise one and is now what stands here.
- **The `oidc.user:` key is read, never constructed** — it embeds the issuer, and
  the provider's port is chosen per run (spec §2.3).
- **The boot-state control reads through `addInitScript`**, before any app code.
  A read after the page settles returns the *renewed* grant and the assertion
  becomes unfailable — the #2054 shape (spec §8 C2).
- **Assert on what the wall shows:** no sign-in button, no `#username`, a
  **populated** picker, then `layout-grid` after opening the first layout. An
  empty picker renders without an error and is what a fab-less token produces
  (`e2e/support/kiosk-session.ts:19-28`).
- **Assert how it got there, not only what it shows** (added in phase 6). The
  access token stored after recovery must **differ** from the one process #1
  marked spent, and carry `azp: kiosk-wall`; the second process's requests must
  include a `grant_type=refresh_token` token POST and **no**
  `/protocol/openid-connect/auth` at all. Without these the whole happy path is
  green in a world where the app ignores `expires_at` and re-sends a token the
  realm still considers valid for an hour — C2b demonstrated exactly that run.
- **The cookie control** (spec §3.1 scenario 2) is part of this test, not
  optional. Read it **before** the navigation, so what is seen is what survived
  the profile rather than what the app has since set. `KEYCLOAK_SESSION` does
  survive; assert the httpOnly `KEYCLOAK_IDENTITY` absent, then clear the
  provider cookies before the wall loads — spec §3.1's pre-committed answer.
- **Guard the boot-state read.** `goto` resolves on `load`, and a `signinRedirect`
  can land between that and the read — either destroying the execution context or
  re-running the init script on the provider's document. Unguarded, the run dies
  on the *profile-persistence* message and sends the next reader after a bug that
  does not exist. Assert the URL, and name the redirect.
- **Assert the LevelDB directory exists between the two launches.** Chromium's
  `SingletonLock`/`SingletonSocket` are POSIX-only; on Linux a `close()` that
  returns before the process is reaped leaves launch #2 blocking to the 300 s
  timeout or quietly re-creating the profile. This is a named failure instead,
  and it independently proves the flush.
- **Start tracing by hand on both contexts**, writing to `testInfo.outputPath`
  **and `testInfo.attach`ing — only when something failed.** `trace:
  'on-first-retry'` never fires for a context the fixtures did not create, and a
  Linux-runner failure with no trace is the worst place this slice can end up —
  but a file under `outputPath` is not attached, so the report links nothing.
  Green runs write no trace.
- **No `declare global` on `Window`.** A project-wide augmentation from a spec
  file collides with any branch adding the same member — the ADR-0109 shape this
  slice otherwise stays off. A file-scoped cast at each use instead.
- Copy `signInAsWallDisplay` and the grant reader from
  `wall-outlives-its-session.spec.ts:18-46`. **Do not extract a shared helper** —
  `e2e/support/*` is an ADR-0109 contention file and that is a separate refactor.

**Done when:** `pnpm typecheck:e2e` and `pnpm format:check` are clean, and the
test passes against a booted stack **twice** (SC-004, SC-006). A first-run failure
after machine churn is not a verdict; re-run before concluding.

**If it is red on a clean tree after two runs:** stop, quote the failure
verbatim, and go to T006. **Change no assertion** (SC-005).

### [T002] [US1] Prove the suite actually selects it

```sh
pnpm exec playwright test --project=wall --list
```

**Done when (SC-007):** the new test's title appears, and the listing shows
`seed` and `cleanup` scheduled alongside `wall`. Quote the matching lines in the
PR body. A test the runner silently skips is the failure mode this task rules
out — and `--project=kiosk` once pulled `seed` but never `cleanup` for exactly
this class of reason (`playwright.config.ts:67-83`).

**Depends on:** T001.

### [T003] [US1] Counterfactual C1 — the profile is not reused

In the working tree only, point the **second** launch at a different directory (a
second `mkdtemp`).

**Prediction (spec §8 C1), to be checked against rather than confirmed:** the
boot-state control fails first — no `oidc.user` entry at document start. Nothing
else in the suite reddens; the file is new and nothing references it.

Revert afterwards. `git status` must show only the new spec file and
`specs/107-*/`.

**Done when (SC-002):** both outputs — red with C1, green without — captured
verbatim for the PR. **If anything else reddens, say so in the PR body and
correct spec §8.**

**Depends on:** T001.

### [T004] [US1] Counterfactual C2 — the token is not expired on disk

In the working tree only, remove the `expires_at` rewrite in process #1.

**Prediction (spec §8 C2):** the boot-state control's *"its access token must
already be spent"* assertion goes **red**, and the run **stops there** — that
assertion precedes the happy-path ones, so the wall assertions are never reached
and their outcome is not observable in this run. That the control fires at all is
the whole point of asserting the expiry on the value read at boot.

**If the test stays green under C2, the expiry assertion is decorative** and the
test is not proving recovery *through the grant*. Report that (SC-003) — do not
patch around it.

**C2b, added in phase 6:** run C2 again with the boot expiry control lifted as
well, because C2 stops before the wall assertions and so cannot say what a
non-spent token does downstream. **Observed:** every wall assertion green — the
picker, `layout-grid`, both no-credential checks — and the run red only on *"the
wall must have exchanged its grant, not re-sent the token it marked spent"*. That
is the whole case for the renewal assertion: the expiry control by itself does
not catch an app that stops consulting `expires_at`.

Revert afterwards.

**Depends on:** T001.

### [T005] [US1] Final gates

- `pnpm typecheck:e2e` clean.
- `pnpm format:check` clean.
- `pnpm exec playwright test --project=wall` green, run twice (SC-004).
- `git status` clean apart from `e2e/wall-survives-a-process-death.spec.ts` and
  `specs/107-*/` — both counterfactuals reverted.
- PR body carries: the T002 listing lines, T003's red and green output, T004's
  red and green output, and each §8 prediction stated explicitly as **held** or
  **corrected**.
- **CI parity is reported, not assumed.** When the run comes back, say whether the
  persistent context behaved on the Linux runner (spec §7.2, A4). If it did not,
  that is a finding to report — **a `test.skip` on CI is a weakened gate and a
  blocked outcome under ADR-0144**, not a fix.

**Depends on:** T002, T003, T004.

### [T006] [US1] If the test is red on a clean tree — the finding path

Not a fallback; the issue predicts this outcome and it is a **successful** run of
this slice.

- File a new issue with the verbatim failure, the trace artefact, and which
  assertion fired.
- Amend spec §7 to record what was observed.
- **Do not fix the product here.** A fix may need a decision, and ADR-0144
  forbids this lane from making one; spec §5 places any production change out of
  scope.
- The test still ships, red-documented in the PR body — or, if a red test cannot
  merge, `test.fail()` **with the issue number and a one-line reason**, which
  records the defect rather than hiding it. That is a judgement for phase 6/7,
  not for the test-writer.

**Depends on:** T001 (only on the red branch).

---

## Dependency graph

```
T001 ──┬── T002 ──┐
       ├── T003 ──┤
       ├── T004 ──┴── T005
       └── T006  (red branch only)
```

No task is `[P]`: T002, T003 and T004 all need T001's single file, and T005 needs
all three.

---

## Phase 3 gate (ADR-0037, as corrected in CLAUDE.md)

Per-task issues are **not** created (that practice stopped after spec 028). The
gate is that the **feature-level** issue is on Project #13:

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/2067
```

Needs the `project` scope (`gh auth refresh -s project,read:project`).
`item-add` prints nothing on success; verify with `item-list --limit 2000`, never
the default 30, or a filled board reads as empty.

---

## Follow-ups to file separately — not this slice

1. **Whatever T006 finds**, if anything. Its own issue, with the verbatim
   failure.
2. **A persistent-profile restart for `kiosk-web` (:5174).** Deferred with a
   reason (spec §5): the mechanism is the same and proving it twice buys a second
   four-minute test. Worth filing only if the wall test finds a defect, so the
   question "does the ordinary kiosk share it?" has somewhere to live.
3. **The reconstruction tests that remain.** `kiosk-comes-back.spec.ts:47` and
   `wall-outlives-its-session.spec.ts:110` still reconstruct. This slice does not
   delete or rewrite them — they cover things the new test does not (the
   `sessionStorage` absence check, the token-claim assertions) and deleting a
   test to reach a tidier suite is a weakened gate. Whether the two restart
   assertions should be consolidated once the persistent one is proven is a
   later, separate question.
4. **`gh` cannot confirm the spec number for in-flight work.** Spec 107 was
   derived from `git ls-tree -d --name-only origin/develop specs/` (max 106) with
   zero open PRs, per issue #2177. That issue's fix — reading the number from git
   rather than the working tree — is still open and still worth doing.

# Spec 088 — Seven errors, not a repair job

**Issue:** #2121 — *The Playwright suite is type-checked by nothing*
**Branch:** `chore/2121-the-playwright-suite-is-type-checked`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0108 (Playwright e2e against a live Aspire stack — the suite this
spec puts a checker over, and the reason its only current signal costs a booted
stack), ADR-0139 (rules that fail the build, not the review — the governing
precedent: a stated-but-unenforced rule drifts, and the drift is found by
measurement), ADR-0074 (two React apps; `apps/**` is the scope the existing
scripts were drawn around), ADR-0109 (parallel markers), ADR-0036 (smallest
change; no drive-by refactor), ADR-0037 (phased workflow), ADR-0144 (autonomous
lane — **no ADR is written here**; if one turns out to be needed, that is a
blocked outcome).

**Constitution:** §Testing — new behaviour starts red, and the failure is quoted
in the PR body. §V *Spec-Driven Development*. This change is **off** the
event-to-overlay path: **latency impact N/A** — it adds no runtime code, and the
`frontend` CI job it extends does not run the suite it checks.

---

## The issue as filed, measured against the repository

Six claims. Four hold exactly, one is right in substance and wrong in mechanism,
and one figure is wrong.

**Holds: there is no root `tsconfig.json`.** Only `tsconfig.base.json`.

```sh
$ find . -name 'tsconfig*.json' -not -path '*/node_modules/*' | sort
./apps/kiosk-web/tsconfig.json
./apps/management-web/tsconfig.json
./apps/shared/tsconfig.json
./tsconfig.base.json
```

**Holds: nothing type-checks, lints or format-checks `e2e/`.** The `frontend` CI
job (`.github/workflows/ci.yml:90`) runs four steps — `pnpm format:check`, then
`pnpm -r --filter "./apps/**" lint`, `... typecheck`, `... test`. `e2e/` is in
none of them.

**Holds: `playwright test --list` type-checks nothing.** Proven by counterfactual
rather than asserted — see *The counterfactual* below.

**Holds: `apps/shared/eslint.config.js` pins an explicit globals allowlist with
`MediaStream` present and `MediaStreamTrack` absent.** Verified at
`apps/shared/eslint.config.js:28` — 27 named globals, `MediaStream` on line 28,
no `MediaStreamTrack` anywhere in the file.

**Wrong in mechanism: "`lint`, `typecheck`, `test` and `format` scripts are all
scoped `--filter "./apps/**"`."** Three of the four are. `format` and
`format:check` are **not** `pnpm --filter` at all — they are Prettier invoked at
the root with a path glob:

```json
"format": "prettier --write \"apps/**/*.{ts,tsx,js,jsx,json,css,md,yaml,yml}\"",
"format:check": "prettier --check \"apps/**/*.{ts,tsx,js,jsx,json,css,md,yaml,yml}\"",
```

The consequence is the same (`e2e/` excluded) but the fix is not: format is a
one-glob edit needing no workspace package, while lint and typecheck are not.

**Wrong figure: 26 spec files.** It is **21**.

```sh
$ find e2e -name '*.spec.ts' | wc -l
21
```

The likely origin of 26 is the *file* count Playwright reports, which counts
setup and teardown files too and is 27, not 26:

```sh
$ pnpm exec playwright test --list | tail -1
Total: 57 tests in 27 files
```

21 spec + 3 `.setup.ts` + 3 `.teardown.ts` = 27. One further `.spec.ts` exists
outside `e2e/` — `apps/shared/src/realtime/*.spec.ts`, a Vitest file already
covered by `apps/shared`'s own `typecheck`. It is not in scope here.

**Right: 5 projects.** `chromium`, `seed`, `kiosk`, `wall`, `cleanup`
(`playwright.config.ts`).

---

## The census, measured

| Thing | Count | Command |
|---|---|---|
| `.spec.ts` under `e2e/` | **21** | `find e2e -name '*.spec.ts' \| wc -l` |
| all `.ts` under `e2e/` | **32** | `find e2e -name '*.ts' \| wc -l` |
| helpers under `e2e/support/` | **11** (5 plain, 3 `.setup.ts`, 3 `.teardown.ts`) | `ls e2e/support` |
| Playwright projects | **5** | `playwright.config.ts` |
| files Playwright loads | **27** (57 tests) | `pnpm exec playwright test --list` |
| `.evaluate(` call sites | **21**, across **11** files | `grep -roh "\.evaluate" e2e --include=*.ts \| wc -l` |
| `any` annotations in `e2e/` | **0** | `grep -roE "\bas any\b\|: any\b" e2e --include=*.ts \| wc -l` |
| `@ts-ignore` / `@ts-expect-error` / `@ts-nocheck` | **0** | `grep -roE "@ts-(ignore\|expect-error\|nocheck)" e2e \| wc -l` |
| **`tsc --noEmit` errors today** | **7**, in **5** files | see below |
| Prettier drift in `e2e/` | **3 files** | `pnpm exec prettier --check "e2e/**/*.ts"` |

The two zero rows are the ones that matter for the *no-vacuous-green* constraint:
`e2e/` contains **no `any` and no suppression**, so a green typecheck over it is a
real statement, not an empty one.

### The seven errors, verbatim

Produced by `tsc -p e2e/tsconfig.json --noEmit` against the config in `plan.md`,
with `@types/node@22` installed:

```
e2e/kiosk-identity-herd.spec.ts(51,9): error TS7034: Variable 'screens' implicitly has type 'any[]' in some locations where its type cannot be determined.
e2e/kiosk-identity-herd.spec.ts(62,47): error TS7005: Variable 'screens' implicitly has an 'any[]' type.
e2e/support/kiosk-session.ts(66,28): error TS18048: 'claims' is possibly 'undefined'.
e2e/wall-authority.spec.ts(21,41): error TS2769: No overload matches this call.
e2e/wall-outlives-its-session.spec.ts(30,33): error TS2769: No overload matches this call.
e2e/wall-withdrawal.spec.ts(70,33): error TS2769: No overload matches this call.
e2e/wall-withdrawal.spec.ts(88,3): error TS2322: Type 'string | undefined' is not assignable to type 'string'.
```

(The three TS2769 reports each carry a four-line overload explanation, elided
here; the full text is reproduced in `tasks.md` T004 for the PR body.)

### Classified

| # | Site | Code | Class | What it is |
|---|---|---|---|---|
| 1–2 | `kiosk-identity-herd.spec.ts` 51, 62 | TS7034/TS7005 | **untyped container** | `const screens = [];` — one root cause, two reports. Every later `screen.page`, `screen.attempts` is `any`. This is the exact defect class #2141 is cataloguing, sitting inside a 600-second test. |
| 3 | `support/kiosk-session.ts:66` | TS18048 | **unguarded JWT split** | `const [, claims] = accessToken.split('.')` then `claims.replace(...)`. Inside a `page.evaluate` callback, so it runs in the browser. |
| 4–6 | `wall-authority:21`, `wall-outlives-its-session:30`, `wall-withdrawal:70` | TS2769 | **unguarded JWT split** | `Buffer.from(payload, 'base64url')` where `payload` is `string \| undefined` for the same reason. Three near-identical decoders in three files. |
| 7 | `wall-withdrawal.spec.ts:88` | TS2322 | **assertion that does not narrow** | `expect(issuer, '...').toBeTruthy(); return issuer;` — Playwright's `expect` is not a TypeScript assertion function, so the narrowing a reader assumes never happens. |

**None is a live production bug**, and none is a live test bug on the happy path:
a well-formed JWT has three segments, and `screens` is populated before use. All
seven are latent — they turn a malformed input into a confusing failure rather
than a stated one, and #1–2 remove type checking from a whole test.

**Seven is small enough that this is one issue, not two.** The split condition —
"turn the check on" separately from "fix what it finds" — was worth asking and
does not trigger: a single commit fixes all seven, and none needs a design
decision.

### A related finding, not fixed here

Two sites already defeat the checker by hand, and neither was reported as an
error because the cast silences it:

```
e2e/kiosk-latency-contract.spec.ts:38:  return token as string;
e2e/wall-withdrawal.spec.ts:64:  return refresh as string;
```

Both are `expect(x, '...').not.toBeNull(); return x as string;`. That idiom is
precisely how a typecheck goes green without checking, and it is already the
house pattern in these files. **Not mass-converted here** — it is pre-existing, it
is not among the seven, and rewriting it is a refactor of e2e helpers whose only
covering test is a booted stack. Recorded for #2141, and for whoever writes `e2e/`
next: prefer an explicit `if (x === undefined) throw new Error('...')` to
`expect(...)` followed by a cast.

---

## The counterfactual — why option 3 is refuted, not merely disliked

The issue offers "leave it and record why, if `--list` plus a full e2e run is
sufficient signal." The bar for that outcome is evidence. The evidence goes the
other way, and both halves were run.

**A type error inside a `page.evaluate` callback, and one at the Node/page return
seam, were injected into `e2e/kiosk-latency-contract.spec.ts`:**

```ts
const key = Object.keys(window.localStorage).find((candidate) => candidate.startsWithX('oidc.user:'));
...
return token.toFixed(2);   // token is string | null
```

**`tsc` catches all of it:**

```
e2e/kiosk-latency-contract.spec.ts(30,80): error TS2551: Property 'startsWithX' does not exist on type 'string'. Did you mean 'startsWith'?
e2e/kiosk-latency-contract.spec.ts(38,10): error TS18047: 'token' is possibly 'null'.
e2e/kiosk-latency-contract.spec.ts(38,16): error TS2551: Property 'toFixed' does not exist on type 'string'. Did you mean 'fixed'?
```

**`playwright test --list` catches none of it and exits 0:**

```
$ pnpm exec playwright test --list --project=kiosk
...
Total: 22 tests in 16 files
EXIT=0
```

Line 30 is *inside* the browser callback. That is the seam the issue names as
where it bites hardest, and it is checked — the callback body is type-checked in
the Node file even though it executes in the page. So the class of defect the
issue is about is caught by the proposed check and is invisible to the signal
option 3 would rely on. **Option 3 is closed.**

---

## Decision — option 2

**A dedicated `e2e/tsconfig.json` with its own script, wired into the `frontend`
CI job.** Rationale in `plan.md`; the deciding measurements are two.

**The `lib` differs, and the difference is measurable.** `e2e/` is Node code, but
its `page.evaluate` callbacks are browser code type-checked in place, so it needs
**both** `@types/node` and `lib: DOM`. Dropping DOM turns 7 errors into 52:

```
# lib ["ES2023","DOM","DOM.Iterable"] (inherited from tsconfig.base.json):  7 errors
# lib ["ES2023"]:                                                          52 errors
```

`apps/**` needs DOM and must *not* see Node globals; `e2e/` needs both. That is a
genuinely different compilation unit, not a filter widening — which settles
option 1 on evidence rather than on the "by design" assertion in the issue.

**`@types/node` is a new root devDependency.** It exists nowhere in the repository
today (`grep -rn "@types/node" package.json apps/*/package.json` → nothing;
`node_modules/@types` is empty). Without it the config fails outright with
`TS2688: Cannot find type definition file for 'node'`. This is the only new
dependency the P1 slice adds.

---

## User stories

### US1 (P1) — `e2e/` is type-checked, and CI fails when it is not

*As the engineer who changes an e2e helper, I want a type error in a
`page.evaluate` seam to fail my PR, rather than to surface forty minutes later as
a missing element in a browser.*

**Independently shippable.** Delivers the whole point of the issue on its own.

### US2 (P2) — `e2e/` is format-checked with the rest of the repository

*As a reviewer, I want the diff of an e2e change to be content, not
reformatting.*

**Independently shippable**, and independent of US1: it is one glob edit and three
reformatted files. Ship after US1 so the P1 diff stays readable.

### Out of scope — lint

See *Scope* in `plan.md`. A separate issue, argued, not dropped. Filed as
[#2154](https://github.com/smartsolutionslab/smart-sentinel-eye/issues/2154).

---

## Acceptance scenarios

### AS-1 — happy: the check runs and passes on fixed code (US1)

```gherkin
Given e2e/tsconfig.json exists and @types/node is a root devDependency
And the seven type errors have been fixed
When I run `pnpm typecheck:e2e`
Then it exits 0 and prints no error
```

### AS-2 — the check fails on the code as it stands today (US1)

```gherkin
Given e2e/tsconfig.json exists and @types/node is a root devDependency
And none of the seven type errors has been fixed
When I run `pnpm typecheck:e2e`
Then it exits non-zero
And it reports exactly 7 errors across 5 files
```

**This is phase 4a's red.** It is not synthetic: the check is the new behaviour,
and it fails on existing code the moment it is turned on.

### AS-3 — conflict: a `page.evaluate` seam error is caught (US1)

```gherkin
Given the check is green
When a callback passed to page.evaluate calls a method that does not exist on its receiver
Or a value returned across the evaluate boundary is used at the wrong type
Then `pnpm typecheck:e2e` exits non-zero and names the file and line
And `pnpm exec playwright test --list` still exits 0
```

The second `Then` is the load-bearing one: it is what makes the check worth adding
rather than redundant.

### AS-4 — bad input: the config cannot silently check nothing (US1)

```gherkin
Given e2e/tsconfig.json
When a new file is added under e2e/ or e2e/support/
Then it is included by "**/*.ts" without any further edit
And tsc reports a nonzero file count for the project
```

A config whose `include` missed the suite would pass green for ever. The guard is
that `include` is a directory glob, not an enumeration.

### AS-5 — auth: N/A, stated rather than omitted (US1)

No endpoint, no scope and no token is added or changed. `e2e/` *reads* tokens in
five places; none of those reads changes. **No authorization surface is touched.**

### AS-6 — format (US2)

```gherkin
Given the format globs cover e2e/
When I run `pnpm format:check`
Then it exits 0
And running it before the three drifting files are rewritten would have exited non-zero
```

---

## Independent end-to-end test procedure

**No Aspire stack, no Docker.** `tsc`, `prettier` and `pnpm` are CPU-only, and the
check deliberately does not run the suite it checks.

1. `pnpm install --frozen-lockfile`
2. `pnpm typecheck:e2e` → exits 0.
3. Stash the seven fixes only —
   `git stash push e2e/kiosk-identity-herd.spec.ts e2e/support/kiosk-session.ts e2e/wall-authority.spec.ts e2e/wall-outlives-its-session.spec.ts e2e/wall-withdrawal.spec.ts` —
   run `pnpm typecheck:e2e` → **7 errors**; `git stash pop`.
4. **Counterfactual.** In `e2e/kiosk-latency-contract.spec.ts`, change
   `candidate.startsWith(` to `candidate.startsWithX(` *inside* the `page.evaluate`
   callback. Run `pnpm typecheck:e2e` → red, naming that line. Run
   `pnpm exec playwright test --list --project=kiosk` → **exits 0**. Restore the
   file with `git checkout --`. *Do not commit the mutation.*
5. `pnpm format:check` → exits 0. (US2)
6. `pnpm -r --filter "./apps/**" typecheck` → still exits 0; the new config changes
   nothing for `apps/**`.
7. `pnpm exec playwright test --list` → still `57 tests in 27 files`; the new
   `e2e/tsconfig.json` does not perturb Playwright's own file discovery.

Step 7 is not ceremony: `tsconfig.json` lands inside Playwright's `testDir`, and a
config file the runner decided to load as a test file would be a silent regression
in the suite's shape.

---

## What now blocks a merge that did not

Stated plainly, because a new CI check changes the meaning of green.

- **New:** any type error under `e2e/` fails the `frontend` job. That job already
  gates `e2e-full-stack` (`needs: [backend, frontend]`), so a type error in the
  Playwright suite now also prevents the 40-minute stack job from starting — a
  saving, not a cost.
- **Fails on existing code:** **yes — 7 errors in 5 files**, until they are fixed
  in this same PR. After the fixes, `develop` is green.
- **US2 adds:** `pnpm format:check` covering `e2e/` — 3 files, reformatted here.
- **Nothing is weakened.** No threshold lowered, no rule narrowed, no suppression
  added. The seven fixes are guards and type annotations; explicitly **no
  `@ts-expect-error`, no `any`, no `!` and no `as`** may be used to reach green
  (see `plan.md`).

---

## Locked tech choices

TypeScript **6.0.3** (already the root devDependency), `@types/node@22` (matching
`engines.node` and the CI `setup-node` version), Prettier **3.9.6**, Playwright
**1.62.1**, pnpm **10.30.3**. No new tool is introduced.

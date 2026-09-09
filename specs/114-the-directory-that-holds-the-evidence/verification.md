# Verification — spec 114 (#2219)

## 1. The gap, confirmed on this branch

`e2e/` holds **33 TypeScript files, 5,446 lines** (`wc -l e2e/*.ts
e2e/support/*.ts`). The issue quotes 4,274; the file count matches, the
line count does not — the directory has grown since the issue was filed
(specs 107, 108 and 109 all landed e2e work). Nothing about the defect
changes.

`pnpm lint` **before** — verbatim, exit 0:

```
> smart-sentinel-eye@0.0.0 lint D:\Github\smart-sentinel-eye
> pnpm -r --filter "./apps/**" lint

Scope: 3 of 4 workspace projects
apps/shared lint$ eslint src --max-warnings 0
apps/shared lint: Done
apps/management-web lint$ eslint src --max-warnings 0
apps/kiosk-web lint$ eslint src --max-warnings 0
apps/kiosk-web lint: Done
apps/management-web lint: Done
```

Three packages, `src` only. No mention of `e2e`.

## 2. Phase 4a — the guard, observed red

`pnpm test:guards`, on commit `9fa83079`, before any configuration
existed:

```
> node --test "scripts/**/*.test.mjs"

✖ every TypeScript file under e2e/ is covered by the root ESLint configuration (14.7872ms)
✖ the root lint script runs the e2e leg (1.5988ms)
✖ CI gates on the root lint and test scripts, not a re-spelled apps filter (2.9529ms)
ℹ tests 3
ℹ pass 0
ℹ fail 3
```

with

```
  Error: Could not find config file.
      at assertConfigurationExists (.../eslint/lib/config/config-loader.js:80:23)
    messageTemplate: 'config-file-missing'
```

and

```
  AssertionError [ERR_ASSERTION]: package.json `lint` must chain the e2e leg, the way `typecheck` chains `typecheck:e2e`
    actual: 'pnpm -r --filter "./apps/**" lint',
    expected: /pnpm lint:e2e/,
```

ESLint answering `config-file-missing` for an `e2e/` file is the defect
stated by the tool itself rather than inferred from a script string.

## 3. The first run and its breakdown

Two runs, and the difference between them is the honest part of this
report.

**Run 1 — 15 errors**, with a globals list assembled from reading the
code rather than from running the tool:

| Rule | Count | Identifiers |
|---|---|---|
| `no-undef` | 14 | `requestAnimationFrame` ×6, `Buffer` ×4, `URLSearchParams` ×1, `Element` ×1, `MutationObserver` ×1, `atob` ×1 |
| `no-empty-pattern` | 1 | `e2e/wall-survives-a-process-death.spec.ts:154` |

The fourteen `no-undef` are **the configuration being incomplete, not
findings against the code**: every one names a real global of the
environment the file runs in — Node's `Buffer` and `URLSearchParams`,
the browser's `requestAnimationFrame`, `Element`, `MutationObserver` and
`atob` inside `page.evaluate` callbacks. All three app configs enumerate
their globals by hand and this one does the same, so completing the list
is describing the environment. `no-undef` itself is untouched and still
at `error`.

**Run 2 — 1 error**, with the environment fully declared:

```
D:\Github\smart-sentinel-eye\e2e\wall-survives-a-process-death.spec.ts
  154:75  error  Unexpected empty object pattern  no-empty-pattern

✖ 1 problem (1 error, 0 warnings)
```

**The issue predicted a large red and was wrong.** 5,446 lines that have
never been linted produce exactly one genuine finding under the apps'
rule set. In particular the `wall-*.spec.ts` family the issue singled out
— `launchPersistentContext`, `page.evaluate`, hand-started tracing —
raised nothing beyond globals; `@typescript-eslint`'s recommended set,
`no-explicit-any` and `no-unused-vars` included, is clean across the
directory. No baseline is needed and none is proposed.

ESLint processed **34 files** (33 under `e2e/` plus
`playwright.config.ts`), confirmed with
`eslint e2e playwright.config.ts -f json`.

## 4. The one finding, justified at its site

`no-empty-pattern` fires on

```ts
test('a wall display comes back after its browser process dies', async ({}, testInfo) => {
```

This is a genuine mismatch between the rule and the framework, not
sloppy code. Playwright parses a test's **first parameter** to decide
which fixtures to set up, and rejects anything that is not an object
destructuring pattern:

```
node_modules/.pnpm/@playwright+test@1.62.1/.../playwright/lib/common/index.js:1761
  onError({ message: "First argument must use the object destructuring pattern: " + firstParam, location });
```

so the obvious repair — `async (_, testInfo)` — would compile, would
lint clean, and would fail the test at run time. The pattern has to be
present and empty. Fixed with a **per-line**
`eslint-disable-next-line no-empty-pattern` and the reason written at the
site. The rule stays on everywhere else, including the rest of that file.

## 5. What was dropped, and why it is inapplicable

`eslint-plugin-react` and `eslint-plugin-react-hooks`. Their rules govern
JSX and the Rules of Hooks. `e2e/` contains no `.tsx`, no JSX, no React
import and no hook call, so every rule in both plugins is inert —
nothing that could have fired stops firing. That is dropping an
inapplicable plugin, not narrowing a rule set.

**Nothing else was dropped, and no rule is set to `off` anywhere in
`eslint.config.mjs`.**

## 6. The decision the issue reserved

The issue reserves whether the e2e config should match the apps' or be
its own, and says to start from the apps', "most likely by composing the
same shared config".

**There is no shared config to compose** — the three app configs are
three full, hand-written copies, identical but for their globals lists.
So reuse was implemented as *the same rule set assembled the same way*:
`js.configs.recommended`, then `tseslint.configs.recommended.rules`, then
the same `no-unused-vars` `argsIgnorePattern: '^_'` override, then
`eslint-config-prettier` last. Nothing added, nothing weakened.

Extracting a genuinely shared module would mean editing all three app
configs — `apps/shared/*` is a contention path (ADR-0109) — and is the
reserved decision itself. **Not attempted.** So the reserved decision
was not made: this config matches.

## 7. `pnpm lint` after — verbatim, exit 0

```
> smart-sentinel-eye@0.0.0 lint D:\Github\smart-sentinel-eye
> pnpm -r --filter "./apps/**" lint && pnpm lint:e2e

Scope: 3 of 4 workspace projects
apps/shared lint$ eslint src --max-warnings 0
apps/shared lint: Done
apps/kiosk-web lint$ eslint src --max-warnings 0
apps/management-web lint$ eslint src --max-warnings 0
apps/kiosk-web lint: Done
apps/management-web lint: Done

> smart-sentinel-eye@0.0.0 lint:e2e D:\Github\smart-sentinel-eye
> eslint e2e playwright.config.ts --max-warnings 0
```

Guards green:

```
✔ every TypeScript file under e2e/ is covered by the root ESLint configuration (1898.8593ms)
✔ the root lint script runs the e2e leg (1.1506ms)
✔ CI gates on the root lint and test scripts, not a re-spelled apps filter (0.875ms)
ℹ tests 3
ℹ pass 3
ℹ fail 0
```

## 8. Counterfactuals — each assertion proved able to fail

Run against the finished tree, one perturbation at a time, then
restored. Each perturbation reddens **only** its own assertion, which is
what separates a guard from a coincidence.

| Perturbation | Result |
|---|---|
| `files: ['e2e/*.ts', …]` — drops `e2e/support/` | assertion 1 red, naming all 11 support files as `(ignored)`; 2 and 3 green |
| `lint` reverted to `pnpm -r --filter "./apps/**" lint` | assertion 2 red; 1 and 3 green |
| `ci.yml` Lint step reverted to the apps-only filter | assertion 3 red; 1 and 2 green |
| `const leak: any = 1;` planted in `e2e/__cf.ts` | `pnpm lint:e2e` exits 1 — `Unexpected any. Specify a different type  @typescript-eslint/no-explicit-any` |

The fourth is the one that matters to a reader of a PR body: a lint
error in an e2e file now fails the lint gate. Before this change it did
not.

## 9. Gates

| Gate | Result |
|---|---|
| `pnpm lint` | pass — 4 legs now, was 3 |
| `pnpm typecheck` | pass — apps plus `typecheck:e2e` |
| `pnpm test` | pass — the apps' vitest suites, then 3/3 guards |
| `pnpm format:check` | pass |
| `pnpm exec playwright test --list` | pass — `Total: 58 tests in 28 files` |

Not run: the e2e suite itself. It needs a live Aspire stack (ADR-0108)
and the stack is down; the CI `e2e` job covers it. The only e2e source
change is a comment and a lint directive above an unchanged test
signature, and `--list` confirms the file still parses and still
contributes its test.

## 10. Two things the issue did not have right about the tree

1. **The line count.** 5,446, not 4,274 — the directory grew after
   filing. The file count (33) is unchanged.
2. **CI never called the root script.** The issue frames the defect as
   `package.json:12`, and fixing that line alone would have changed
   nothing that gates a merge: `.github/workflows/ci.yml` re-spelled
   `pnpm -r --filter "./apps/**" lint`, **and the same for `test`**, one
   step above a `Typecheck` step that runs `pnpm typecheck`. The
   asymmetry the issue found in `package.json` is duplicated in the
   workflow. Both steps now call the root scripts — which is also what
   lets the guard run in CI at all.

## 11. Dependencies added

At the **workspace root**, at the versions all three apps already pin:
`eslint@9.18.0`, `@eslint/js@9.18.0`,
`@typescript-eslint/eslint-plugin@8.68.0`,
`@typescript-eslint/parser@8.68.0`, `eslint-config-prettier@9.1.0`.

`pnpm install` reported `resolved 522, reused 475, downloaded 0` — the
content-addressable store already held every one of them, so the working
copy grew by hard links and nothing was fetched. No bump to ESLint or
TypeScript: #1912 and #1914 remain blocked upstream.

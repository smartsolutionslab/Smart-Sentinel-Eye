# Plan — spec 114

**Change class (ADR-0144 phase 4a):** **behaviour-changing → red.**
The lint gate's scope is the behaviour, and today it excludes `e2e/`.
A guard asserting coverage must be observed failing first.

## The decision the issue reserved, and why it did not have to be made

The issue forbids inventing a bespoke rule set and says to start from
what the apps use, "most likely by composing the same shared config".

**There is no shared config to compose.** `apps/kiosk-web`,
`apps/management-web` and `apps/shared` each hold a full, hand-written
`eslint.config.js`, and the three are byte-identical except for their
`globals` lists. Extracting a shared module would mean editing all
three — `apps/shared/*` is a contention path (ADR-0109), and the
extraction is a different change from this one.

So *reuse* here means **the same rule set, assembled the same way**:

```js
js.configs.recommended,
{ ...tseslint.configs.recommended.rules,
  '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }] },
prettier   // last, so ESLint defers formatting to Prettier
```

identical to the apps' three configs. Nothing is added and no rule is
weakened, so the reserved decision — match the apps, or diverge — is not
being made here: this matches.

**Two plugins are dropped: `eslint-plugin-react` and
`eslint-plugin-react-hooks`.** Their rules are about JSX and about the
Rules of Hooks. `e2e/` contains no JSX (no `.tsx`, no React import) and
calls no hook; every rule in both plugins would be inert. Dropping an
inapplicable plugin is not narrowing a rule set — nothing that could
have fired stops firing.

## Approach

1. **Root ESLint config** — `eslint.config.mjs` at the repository root,
   scoped with `files: ['e2e/**/*.ts', 'playwright.config.ts']`. That is
   exactly the surface `e2e/tsconfig.json` already declares
   (`include: ["**/*.ts", "../playwright.config.ts"]`), so lint and
   typecheck cover the same files. `.mjs` because the root package is
   not `"type": "module"`.

2. **Globals.** The apps enumerate theirs by hand; so does this one. The
   list is the union of two environments, because that is what a
   Playwright spec genuinely is: Node (`process`, `Buffer`,
   `URLSearchParams`) around browser code passed to `page.evaluate`
   (`window`, `document`, `Element`, `MutationObserver`,
   `requestAnimationFrame`, `performance`, `atob`).

3. **Scripts** — mirror `typecheck` exactly:

   ```json
   "lint": "pnpm -r --filter \"./apps/**\" lint && pnpm lint:e2e",
   "lint:e2e": "eslint e2e playwright.config.ts --max-warnings 0",
   ```

4. **CI** — the `frontend` job's `Lint` and `Test` steps call the root
   scripts (`pnpm lint`, `pnpm test`) instead of re-spelling the
   `./apps/**` filter, matching the `Typecheck` step beside them. Without
   this the fix would not reach the gate that blocks merges.

5. **Guard** — `scripts/lint-scope.test.mjs`, run by `node --test`
   through a new `test:guards` script that root `test` chains. Node's
   built-in runner, so no test framework is added at the root.

6. **Findings** — run, report the breakdown by rule, fix or justify each
   one at its site.

## Dependencies

Added to the **root** `devDependencies`, at the versions all three apps
already pin: `eslint@9.18.0`, `@eslint/js@9.18.0`,
`@typescript-eslint/eslint-plugin@8.68.0`,
`@typescript-eslint/parser@8.68.0`, `eslint-config-prettier@9.1.0`.
`pnpm install` reported `reused 475, downloaded 0` — the store already
holds every one of them, so the working copy grows by hard links.

No bump to ESLint or TypeScript: #1912 and #1914 are blocked upstream
(`typescript-eslint@8.70.0` still peers `typescript <6.1.0`;
`eslint-plugin-react@7.37.5` still caps at `eslint ^9.7`).

## Risks

- **The guard is two kinds of assertion, and only one is behavioural.**
  Asserting that ESLint resolves rules for every e2e file asks the tool
  itself. Asserting that `package.json` and `ci.yml` name the right
  entry point reads an artefact. Both are stated as what they are; the
  second exists because the filed defect *was* a script string.
- **A fourth copy of the same config.** Accepted: the alternative is the
  extraction the issue reserved.

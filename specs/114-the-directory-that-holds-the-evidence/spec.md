# Spec 114 — The directory that holds the evidence

**Issue:** #2219
**Branch:** `chore/2219-the-directory-that-holds-the-evidence`
**Status:** Phase 4 complete
**Lane:** autonomous (ADR-0144)

## Problem

`package.json`'s `lint` script is

```json
"lint": "pnpm -r --filter \"./apps/**\" lint"
```

so it runs the per-app `eslint src --max-warnings 0` in the three
workspace packages and nothing else. `e2e/` is not a workspace package
(`pnpm-workspace.yaml` lists `apps/*` only), there is no ESLint config
at the repository root, and none of the three app configs mentions
`e2e`. **No ESLint run in this repository has ever read a file under
`e2e/`.**

The neighbouring script settles that this is an oversight rather than a
scoping choice:

```json
"typecheck": "pnpm -r --filter \"./apps/**\" typecheck && pnpm typecheck:e2e",
"typecheck:e2e": "tsc -p e2e/tsconfig.json --noEmit",
```

Someone hit exactly this gap for typecheck and extended past it. The
same extension was never made for lint, and no note says why.

`e2e/` is where this repository keeps its **evidence** — 33 TypeScript
files, 5,446 lines as measured on this branch. A green `pnpm lint` is
quoted in PR bodies as though it covered the changed files; for a PR
whose change is an e2e spec it covers none of them.

**CI does not even call the root script.** `.github/workflows/ci.yml`
spells the filter out again:

```yaml
- name: Lint
  run: pnpm -r --filter "./apps/**" lint
- name: Test
  run: pnpm -r --filter "./apps/**" test
```

so fixing the root `lint` script alone would leave the gate itself
unchanged. The `Typecheck` step, by contrast, runs `pnpm typecheck` —
the same asymmetry, in the same two files, one step apart.

## Scope

**In scope**

- An ESLint configuration that covers `e2e/**/*.ts` and
  `playwright.config.ts` — the same surface `e2e/tsconfig.json` already
  declares for typecheck.
- Extending the root `lint` script past `./apps/**`, mirroring what
  `typecheck` already does.
- Making CI run the root scripts rather than re-spelling the filter.
- A guard that fails if the lint scope stops covering `e2e/`.
- Fixing or justifying, per site, whatever the first run finds.

**Out of scope (reserved by the issue)**

- **Whether the e2e config should match the apps' config or be its
  own.** The issue defers this deliberately. This spec therefore
  *reuses the apps' rule set unchanged* and does not design an
  alternative. If reuse had turned out to be impossible the correct
  outcome was to stop and report, not to invent one.
- Bumping ESLint or TypeScript (#1912, #1914 are blocked upstream).
- The `kiosk` + `wall` project interaction (#2221).

## User stories

### US1 — `pnpm lint` reads the evidence directory

As a reviewer reading a PR whose change is an e2e spec, when the PR body
quotes a green `pnpm lint`, I want that run to have actually read the
changed files, so the quote means what it appears to mean.

**Acceptance:** running the repository's lint entry point reports on
every `.ts` file under `e2e/` plus `playwright.config.ts`, and a lint
error planted in an e2e file fails it.

### US2 — the gate cannot silently renarrow

As the next person to touch the lint wiring, I want a check that fails
if `e2e/` falls back out of scope, so the defect this spec fixes cannot
recur unnoticed the way it persisted here.

**Acceptance:** a guard asserts that the root ESLint configuration
resolves a non-empty rule set for **every** `.ts` file under `e2e/`
(so a newly added file that no config matches fails it), and that the
repository's `lint` and `test` entry points — in `package.json` and in
the CI job that gates merges — are the ones that carry the e2e leg.

### US3 — findings are fixed or justified, not silenced

As a maintainer, I want the first run's findings addressed one at a
time, so the directory becomes genuinely lint-clean rather than
nominally so.

**Acceptance:** no rule is disabled wholesale in the configuration.
Every remaining violation carries a per-line `eslint-disable` with the
reason stated at the site.

## Non-functional

- No new plugin, no dependency download: the ESLint packages the three
  apps already pin are added at the workspace root, where pnpm's
  content-addressable store links them rather than fetching them.
- No change to any e2e test's behaviour.

## Assumptions

- `no-undef` stays enabled for TypeScript files, because all three app
  configs keep it and enumerate their environment's globals by hand.
  Declaring the globals `e2e/` actually uses is describing the
  environment, not relaxing the rule.

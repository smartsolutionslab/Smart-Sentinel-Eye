# Tasks — spec 114

Phase 4a colour: **red** (behaviour-changing).

| ID | Task | Status |
|---|---|---|
| T001 | Add the ESLint packages the apps already pin to the root `devDependencies`; confirm `pnpm install` downloads nothing. | done |
| T002 | **(4a, red)** `scripts/lint-scope.test.mjs` — assert (a) the root ESLint configuration resolves a non-empty rule set for every `.ts` file under `e2e/` and ignores none of them; (b) `package.json`'s `lint` script carries an e2e leg; (c) the CI `frontend` job calls the root `lint` and `test` scripts. Wire it into `test:guards` / root `test` / CI. Observe it **red**. | done |
| T003 | **(4b)** `eslint.config.mjs` — the apps' rule set, minus the two React plugins, plus the Node+browser globals `e2e/` uses. | done |
| T004 | **(4b)** Extend root `lint` with `&& pnpm lint:e2e`; add `lint:e2e`. | done |
| T005 | **(4b)** CI `frontend` job: `Lint` → `pnpm lint`, `Test` → `pnpm test`. | done |
| T006 | Run `pnpm lint:e2e`; record the finding count and the breakdown by rule in `verification.md`. | done |
| T007 | Fix or justify each finding at its site. No rule disabled in the configuration. | done |
| T008 | Prove the guard can fail: counterfactual per assertion. | done |
| T009 | Gates: `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm format:check`, `pnpm exec playwright test --list`. | done |

## Ordering

T001 → T002 (red) → T003–T005 (green) → T006 → T007 → T008 → T009.

T002 must be committed before T003–T005 so the failure is in the
history, not merely in a transcript. That commit is red by design —
ADR-0139's requirement — and the next commit restores green.

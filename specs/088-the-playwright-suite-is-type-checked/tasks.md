# Tasks 088 — Seven errors, not a repair job

**Spec:** `specs/088-the-playwright-suite-is-type-checked/spec.md`
**Plan:** `specs/088-the-playwright-suite-is-type-checked/plan.md`
**Issue:** #2121
**Phase:** 3 (Tasks) — ADR-0037
**Engineer:** **infra** (see *Ownership*)
**Phase 4a colour:** **behaviour-changing → red** (see *The red*)

---

## Ownership

**`infra-engineer`.** Four of the six code tasks are build tooling — a root
dependency, a `tsconfig`, `package.json` scripts and a CI workflow step — and the
load-bearing risk is that a new CI check changes what blocks a merge.

**T004 is the exception and should be read by `frontend-reviewer` at phase 6.** It
is seven TypeScript edits inside Playwright helpers, and two of them require
knowing which side of the `page.evaluate` boundary the code runs on (the
`kiosk-session.ts` fix must not use `expect`, because that callback executes in the
browser). The edits are mechanical; the judgement about the seam is not.

---

## The red — what phase 4a observes, and why it is honest

**The guard is the CI check itself, and it is observed red on real code.** There is
no runtime behaviour here to write a unit test against, and a test that merely read
`ci.yml` and asserted a step exists would prove the design was written down, not
that it holds.

What replaces it is stronger, and both halves were already run once while writing
this spec:

1. **The natural red (T003a).** With `e2e/tsconfig.json` and `@types/node` present
   and none of the seven fixed, `pnpm typecheck:e2e` exits non-zero with **7 errors
   in 5 files**. That output is the phase-4 evidence and goes verbatim into the PR
   body. It cannot be faked green: the check is new, and the code it checks is old.
2. **The counterfactual red (T003b).** A type error injected *inside* a
   `page.evaluate` callback is caught by `tsc` and **not** by
   `playwright test --list`, which exits 0. This proves the check catches the
   defect class the issue is about, rather than merely running.

Neither mutation is committed. Both are reproduced in `spec.md` § *Independent
end-to-end test procedure* so a later reader can re-run them.

---

## Commit shape — two commits, each green on its own

ADR-0087 requires each commit to build alone, so **the red is observed in the
working tree and never committed.** Do not land the config without the fixes.

| Commit | Contents | Tasks |
|---|---|---|
| A — `chore(e2e): the Playwright suite is type-checked` | `@types/node`, `e2e/tsconfig.json`, the two scripts, the seven fixes, the CI step | T001, T002, T004, T005 |
| B — `style(e2e): the Playwright suite is format-checked` | the two format globs, three reformatted files | T006 |

Conventional Commits, **no `Co-Authored-By` footer and no session trailer**
(ADR-0086). **Do not push.**

---

## Tasks

### Foundational — blocks everything in US1

| ID | P | Story | Task |
|---|---|---|---|
| **T001** | — | US1 | Add `@types/node@22` as a **root** devDependency: `pnpm add -D -w @types/node@22`. Verify `pnpm-lock.yaml` is regenerated and that no `apps/*/package.json` changed. Blocks T002 — without it the config fails with `TS2688: Cannot find type definition file for 'node'`. |
| **T002** | — | US1 | Create `e2e/tsconfig.json` exactly as in `plan.md` § *The configuration*. Add `typecheck:e2e` to root `package.json` and chain it from `typecheck`. **Do not fix anything yet.** Blocks T003a. |

### Phase 4a — the red

| ID | P | Story | Task |
|---|---|---|---|
| **T003a** | — | US1 | Run `pnpm typecheck:e2e` on the unfixed tree. Capture the **complete** output, including the four-line overload explanation each `TS2769` carries. Assert it is **7 errors across 5 files**: `kiosk-identity-herd.spec.ts` (2), `support/kiosk-session.ts` (1), `wall-authority.spec.ts` (1), `wall-outlives-its-session.spec.ts` (1), `wall-withdrawal.spec.ts` (2). **If the count is not 7, stop and report** — the tree has moved since 2026-09-07 and the plan's classification needs re-reading before anything is fixed. |
| **T003b** | [P] | US1 | The counterfactual. In `e2e/kiosk-latency-contract.spec.ts` change `candidate.startsWith(` to `candidate.startsWithX(` *inside* the `page.evaluate` callback. Capture `pnpm typecheck:e2e` (red, naming line 30) **and** `pnpm exec playwright test --list --project=kiosk` (exit 0). `git checkout -- e2e/kiosk-latency-contract.spec.ts`. Disjoint file from T003a's assertions; independent of T004. |

Both outputs go in the PR body. T003b's pair is what distinguishes this from a
check that merely runs.

### Phase 4b — the fixes

| ID | P | Story | Task |
|---|---|---|---|
| **T004** | — | US1 | Apply the five fixes in `plan.md` § *The seven fixes*, one file at a time. **Forbidden**: `any`, `@ts-ignore`, `@ts-expect-error`, `@ts-nocheck`, `!`, a silencing `as T`, or relaxing any inherited `compilerOptions`. Re-run `pnpm typecheck:e2e` after each file so the count walks down 7 → 5 → 4 → 3 → 2 → 0 and each fix is attributable. Depends on T003a. |
| **T005** | — | US1 | `.github/workflows/ci.yml`, the `frontend` job: change the Typecheck step's `run:` from `pnpm -r --filter "./apps/**" typecheck` to `pnpm typecheck`. Leave the Lint and Test steps alone. Depends on T004 — wiring CI while the tree is red would land a commit that does not build. |

Not `[P]`: T004 and T005 are sequential by the green-commit rule, and T002/T005
both touch tooling that T004's re-runs exercise.

### US2 — format

| ID | P | Story | Task |
|---|---|---|---|
| **T006** | — | US2 | Widen both format globs in root `package.json` from `"apps/**/*.{...}"` to `"{apps,e2e}/**/*.{...}"`. Run `pnpm format` and confirm exactly **3** files change: `e2e/kiosk-shows-a-label-over-video.spec.ts`, `e2e/support/archive-e2e-overlays.teardown.ts`, `e2e/support/live-video-wall.ts`. `pnpm format:check` → 0. If more than 3 files change, stop and report. |

Not `[P]` with T002: same file (`package.json`). Separate **commit**, not a
separate PR.

### Guards and closure

| ID | P | Story | Task |
|---|---|---|---|
| **T007** | [P] | US1 | Prove the green is not vacuous. Re-run the two census greps and assert both are still **0**: `grep -roE "\bas any\b\|: any\b" e2e --include=*.ts \| wc -l` and `grep -roE "@ts-(ignore\|expect-error\|nocheck)" e2e \| wc -l`. Quote both in the PR body. This is the direct answer to the #2141 concern, and it is read-only. |
| **T008** | [P] | US1 | Phase 5 verification note: run `spec.md` § *Independent end-to-end test procedure* steps 1–7 and record the outputs. **No Aspire stack and no Docker** — every step is `tsc`, `prettier`, `pnpm` or `playwright --list`. Read-only against the tree once T004–T006 have landed in the working copy. |
| **T009** | [P] | — | File the follow-up issue: *`e2e/` is linted by nothing, and its globals list is a new decision*. Body carries `plan.md` § *Scope*'s lint paragraph verbatim — four new root devDependencies, a new root `eslint.config.js`, and the explicit-allowlist trap that `apps/shared/eslint.config.js:28` demonstrates (`MediaStream` present, `MediaStreamTrack` absent, #2108). Note that the error count is **unmeasured** because measuring it needs the config that is the open question. Also record the two `expect(...)`-then-`as string` sites (`kiosk-latency-contract.spec.ts:38`, `wall-withdrawal.spec.ts:64`) and the three duplicated JWT decoders as observations for that issue. No files touched — genuinely parallel. **Filed as #2154.** |
| **T010** | — | — | **Phase 3 gate.** Add **#2121** to Project #13 by hand — `/speckit-tasks` adds nothing to the board. `gh project item-add 13 --owner smartsolutionslab --url <issue-url>`; needs the `project` scope. Verify with `--limit 2000`; the default 30 makes a filled board look empty. |

---

## Dependency graph

```
T001 ──> T002 ──> T003a ──> T004 ──> T005
                     │                 │
         T003b [P] ──┘                 ├──> T008 [P]
                                       │
                              T006 ────┘

T007 [P]   (after T004)
T009 [P]   (any time)
T010       (gate)
```

**Parallelism is genuinely thin, and saying so is the point of the marker.** Four
of the six code tasks edit `package.json`, `pnpm-lock.yaml` or `ci.yml`, and the
green-commit rule serialises the rest. Only T003b, T007, T009 and T008 own disjoint
files (ADR-0109). Do not fan this out to more than one engineer.

---

## Definition of done

- `pnpm typecheck:e2e` exits 0; the pre-fix 7-error output is quoted verbatim in
  the PR body.
- The counterfactual pair (T003b) is quoted: `tsc` red, `--list` exit 0.
- `pnpm format:check` exits 0 with `e2e/` in scope.
- `pnpm -r --filter "./apps/**" typecheck` still exits 0.
- `pnpm exec playwright test --list` still reports `57 tests in 27 files`.
- `any` and suppression counts under `e2e/` are still **0**.
- CI's `frontend` job runs the e2e typecheck, and its failure is what a broken
  `page.evaluate` seam now produces.
- #2121 is on Project #13; the lint follow-up issue exists.

## Not done here, and deliberately

- **No ADR is written or amended.** ADR-0139 already supplies the precedent
  (enforce in the build, not the review) and ADR-0108 already governs the suite.
  If review concludes an ADR *is* required, that is a blocked outcome — stop and
  report.
- **No ESLint.** T009.
- **No shared JWT helper**, and no rewrite of the two `expect`-then-cast sites.
  Both are refactors of e2e helpers whose only covering test is a booted stack.
- **No Aspire, no Docker, at any point in phases 4–6.**

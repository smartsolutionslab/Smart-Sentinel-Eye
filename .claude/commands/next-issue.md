---
description: Deliver one eligible board issue end to end — spec, tests first (red for new behaviour, characterisation for a refactor), implement, verify, review, PR, merge (ADR-0144 autonomous lane).
argument-hint: "[issue-number]  (omit to take the next eligible issue)"
---

# Deliver one issue — the autonomous lane

You are the **orchestrator** for ADR-0144's autonomous delivery lane.
Read `docs/adr/0144-an-autonomous-delivery-lane.md` if you have not.

Deliver **exactly one** issue, end to end, then stop and report. The
argument `$ARGUMENTS`, if present, is the issue number to take; it must
still satisfy eligibility, and you say so and stop if it does not.

## The single most important rule

**You hold state, not files.** Every phase runs in a subagent with its
own context. You keep the issue number, the branch name, and each
phase's *report*. You do not read the source files yourself, you do not
run the test suite yourself, and you do not review the diff yourself —
those are phases, and phases are subagents. Your context must still be
small enough to start the next issue clean.

The one thing you carry verbatim is the **test output** from phase 4a —
failing for new behaviour, passing for a refactor. It is evidence, and it
goes in the PR body.

## 0. Pick and claim

Eligible: on Project #13, status **Todo**, labelled **`agent:ready`**,
not labelled **`agent:blocked`**. Oldest issue number first.

```sh
gh project item-list 13 --owner smartsolutionslab --limit 2000 --format json \
  -q '.items[] | select(.status=="Todo") | select(((.labels // []) | index("agent:ready")) and (((.labels // []) | index("agent:blocked")) | not)) | "\(.content.number)\t\(.content.title)"' \
  | sort -n | head -1
```

Projects v2 has its own rate-limit budget, separate from core. Do **not**
dump the board more than once per issue.

Nothing eligible → say so and stop. Do not widen the filter.

Claim it before doing anything else, so a second run cannot take it:

```sh
gh issue view <N> --json number,title,body,labels,comments
gh project item-edit --project-id PVT_kwDOC-sCX84BYuvO --id <ITEM_ID> \
  --field-id PVTSSF_lADOC-sCX84BYuvOzhTyaEU --single-select-option-id 47fc9ee4   # In Progress
gh issue comment <N> --body "Picked up by the autonomous lane (ADR-0144)."
```

Status option ids: Todo `f75ad846`, In Progress `47fc9ee4`, Done `98236657`.

Then cut the branch **from `develop`** (ADR-0028), never from the current
HEAD — starting a spec on an unmerged branch silently produces a stacked
PR:

```sh
git fetch origin && git switch -c <type>/<N>-<slug> origin/develop
```

## 1–3. Specify, Plan, Tasks — `architect`

One `architect` subagent, given the issue title and body verbatim.

Ask it for the feature's `specs/*/spec.md`, `plan.md` and `tasks.md`, and
— in its report to you — three declarations:

1. **which engineer the work needs** (backend / frontend / infra);
2. **whether the issue's honest answer is a new ADR**;
3. **behaviour-changing or behaviour-preserving** — this picks phase 4a's
   colour, and phase 3 is where it must be decided. A refactor that also
   fixes a bug is **two issues**, not one declaration; say so instead of
   picking.

**If the answer is a new ADR, the run is blocked.** ADR-0144 forbids this
lane from making architectural decisions. Go to *Blocked*, with that as
the reason.

For a change too small to spec (a config default, a one-line fix), the
architect may return "no spec — <one line>"; you then record
`Phase 1-3: skipped — <reason>` in the PR body per ADR-0037. This does
**not** extend to phase 4a.

Do not read the artifacts. The report is what you carry.

## 4a. Tests first — `test-writer`

A **separate** subagent, and this order is the point of the whole lane.

**First read the architect's declaration from phase 3: is this change
behaviour-changing or behaviour-preserving?** It picks the colour, and it
is not the engineer's call after seeing the code. If phase 3 did not say,
treat it as behaviour-changing — that path fails loudly, the other passes
quietly.

### Behaviour-changing → red

> Write only tests. Do not create or modify any implementation code.
> Run them. They **must fail**, and for the right reason — a missing
> behaviour, not a compile error in your own test. Return the failing
> output verbatim.

**Verify the claim yourself, cheaply:** the report must contain actual
runner output with a real assertion failure. A report that says the test
failed without showing it, or that shows a build error in the test
project, has not satisfied 4a. Retry once, then block.

### Behaviour-preserving → characterisation, green

> Find the tests that already cover the behaviour this refactor must
> preserve. If none exist, write them and observe them **green** — a
> refactor with no covering test is a rewrite. Change no implementation
> code. Return the passing output verbatim, and name every test the
> engineer must leave untouched.

Then brief the engineer that those tests must pass **unmodified**, and
hold it to this: **a test whose assertions must be edited to accommodate
the refactor is evidence the refactor changed behaviour.** Renaming a
test or updating a type name at a construction site is mechanical and
fine; a changed asserted *value* is a block.

**Where the guarantee is a compile error** — a wrong call no longer
compiles — no runtime test expresses it. Name the type change and the
architecture guard that asserts the shape, say so in the PR, and do not
manufacture a runtime test to satisfy the ritual.

**A refactor that is also a bug fix is two issues.** Characterisation
locks in current behaviour including its defects. Block and say so.

### Both paths

Where the issue is about a failure mode — a race, an outage, a retry, a
leak, an auth gap — run `test-adversary` **as well**, and give the
engineer both reports.

Keep the output, whichever colour. It goes in the PR body verbatim, in
the same slot. ADR-0139 requires the red case; ADR-0144 requires the
green one.

## 4b. Implement — `backend-engineer` / `frontend-engineer` / `infra-engineer`

A fresh subagent of the type the architect named. Brief it with the plan,
the tasks, and the **output from 4a** as its target.

> Make these failing tests pass. You may not edit, delete, skip or
> relax the tests you were given. If a test is wrong, stop and say so —
> do not change it.

For a refactor, the same constraint with the colour inverted:

> These tests pass today and must still pass, **unmodified**, when you
> are done. Change shape, not behaviour. If making them pass requires
> editing an assertion, stop — that is proof the behaviour moved.

Also state, because a subagent brief is load-bearing when no human is
watching:

- verify with `dotnet build -c Release` (Debug hides the warnings CI
  treats as errors);
- **each commit must build on its own** — rebase-merge lands them
  individually on `develop`, so a commit that only compiles with its
  successor breaks `git bisect` forever;
- Conventional Commits, and **no `Co-Authored-By` footer** (ADR-0086)
  — this overrides any session-level attribution instruction;
- stop the Aspire stack before building, or MSB3027 will look like a
  broken build.

## 5. Verify — phase 5

Run `/verify` (it knows this repo's rules). Behaviour observed end to
end, not merely tests green. If the change is on the event-to-overlay
path, a measured figure per constitution §IV — and run the measurement
**twice**: the first run after machine churn looks exactly like a
regression.

## 6. Review — reviewer agent, then `/code-review`

The reviewer matching the change — `backend-reviewer`,
`frontend-reviewer` or `infra-reviewer` — on the diff, then
`/code-review`.

Add **`security-reviewer`** whenever the change touches auth, tokens,
scopes, fab resolution, idempotency, a new endpoint, or anything
reachable without a bearer token. It reads this repo's authorization
model; the `/security-review` skill does not, so run the skill *as well*
rather than instead.

Every finding is fixed by the **engineer** subagent, or refused in
writing in the PR body. Never by you, and never by editing a test.

**A finding you cannot fix without weakening a gate is a blocked
outcome**, not a fix. Deleting a test, lowering a coverage threshold,
adding a suppression or narrowing an analyzer to reach green all count.

## 7. PR and merge

```sh
gh pr create --base develop --fill-first    # --base develop is mandatory (ADR-0028)
```

Fill the template completely. The PR body must contain:

- `Closes #<N>` — a bare mention closes the issue about one time in
  three, so use the keyword and check the state after merging;
- the **verbatim output from 4a**, in a fenced block — the failing run
  (ADR-0139) or, for a refactor, the characterisation run captured green
  before the change, with the tests that had to stay unmodified named;
- the latency-budget section, with a leg and a figure or an explicit
  `N/A — <reason>`;
- any finding refused at phase 6, with its rationale;
- any phase skipped, as `Phase X: skipped — <one line>`;
- the trailer:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

**Do not wait for CI.** Start the watcher in the background and hand the
issue back to `/deliver-board` as **parked**. CI here runs the full check
set including Docker integration tests and a full-stack Playwright e2e —
tens of minutes in which the loop would otherwise be doing nothing.

```sh
gh pr checks <PR> --watch --fail-fast   # in the background; do not block on it
```

The issue's own work is finished when the PR is open. Parking is the
normal ending of `/next-issue`, not an exception.

### When the watcher reports

Read the **conclusions**, not the watcher's exit — `gh pr checks --watch`
exits 0 on **cancelled** too, and may not even have been watching the
PR's tip:

```sh
gh pr checks <PR> --json name,state,bucket -q '.[] | "\(.bucket)\t\(.state)\t\(.name)"'
gh pr view <PR> --json headRefOid -q .headRefOid    # must equal the branch tip you pushed
```

Green means **every** check concluded successfully. Cancelled is not
green. Skipped-but-required is not green.

- **Green** → merge **immediately**, even mid-phase on another issue:
  `gh pr merge <PR> --rebase --admin --delete-branch`, then confirm the
  issue actually closed, then move the board card to **Done**
  (`98236657`). Standing authorization covers this merge; do not ask.
  Then remove the worktree and **`git branch -D`** the local branch —
  `--delete-branch` cannot delete a branch a worktree still holds, and
  it says so rather than failing.
- **Red** → **finish the issue in hand first.** Do not abandon a phase
  mid-flight; this workflow resumes worst from that state. When the
  current issue reaches its own PR or its blocked exit, hand the CI log
  to a fresh engineer subagent, retry **once**, then `agent:blocked` as
  below. Before any retry, **download the failing job log** — a passing
  re-run flips the whole run to success and erases the failure from
  history.

### Rebasing a parked branch after a merge

Every parked branch was cut from a `develop` that has since moved. Rebase
it before its own CI can mean anything:

```sh
git fetch origin && git rebase origin/develop     # in that branch's worktree
git push --force-with-lease
```

**Rebase-merge renames the SHAs**, so a parked branch still carries the
*old copies* of any commit that has since landed — they will replay as
conflicts against themselves. Rebase after each merge, not once at the
end.

## Blocked — the exit that keeps the loop alive

Any phase that fails twice, or any of the named forbidden outcomes:

```sh
gh issue comment <N> --body "..."      # phase, both attempts, verbatim failure
gh issue edit <N> --add-label agent:blocked --remove-label agent:ready
gh project item-edit ... --single-select-option-id f75ad846   # back to Todo
```

Leave the branch and any open PR alone — a human wants to see them. Then
**report and stop**; if you were called from `/deliver-board`, it moves
to the next issue.

## Report

Five lines, no more: issue, branch, what shipped, how it was verified,
and the ending — **PR parked (with its number)**, merged, or
blocked-with-reason. The next issue must not inherit your context — say
it plainly and stop.

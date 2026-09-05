---
description: Run the autonomous lane over the board — deliver every agent:ready issue, one at a time, until none remain (ADR-0144).
argument-hint: "[max-issues]  (default: until the board is empty)"
---

# Deliver the board

Repeat `/next-issue` until no eligible issue remains. `$ARGUMENTS`, if
given, caps how many issues to deliver this run.

## The loop

```
while an eligible issue exists (and the cap is not reached):
    if 3 PRs are already parked:
        wait for one to conclude, and settle it
    run /next-issue                      # ends by parking a PR, not by waiting for CI
    record one line: issue, outcome, PR or block reason
    forget everything else about that issue
```

Eligibility, claiming, the phases, the merge and the blocked exit all
live in `/next-issue`. Do not restate or reinterpret them here.

## Parked PRs — the loop does not wait for CI

`/next-issue` opens its PR, starts a background watcher and returns. CI
here is the full check set — Docker integration tests and a full-stack
Playwright e2e — so waiting costs tens of minutes per issue during which
nothing is being delivered.

**You hold the parked list**, and it is the one thing besides the tally
you carry between issues: PR number, issue number, branch, worktree. Two
lines each, no more.

**At most three parked at once.** At three, take no new issue — settle
one first. The cap is not throughput management; it is how a conflicting
branch gets found in hours rather than at the end of a run.

**When a watcher reports, act on it immediately — mid-issue is fine for
a merge.** Green → merge, close, card to Done, remove the worktree and
`git branch -D` the local branch. Red → **finish the issue in hand
first**, then retry once with the CI log, then block. Never abandon a
phase mid-flight to chase a red build.

**After every merge, rebase every branch still parked**, in its own
worktree:

```sh
git fetch origin && git rebase origin/develop && git push --force-with-lease
```

Not once at the end. Rebase-merge renames the SHAs, so a parked branch
carries the *old copies* of whatever just landed and they replay as
conflicts against themselves. A branch that conflicts irreconcilably is
a blocked issue, handled the normal way — it is not a reason to stop the
run.

**Ending with PRs still parked is a normal outcome.** Do not hold the
loop open to watch them. Say which are outstanding, with their numbers,
and stop.

## What you must not accumulate

You are the outermost loop, so you are the context that grows. Between
issues, carry **one line per issue** — number, outcome, link — and
nothing else. No spec text, no diffs, no test output, no file contents.
If you find yourself recalling issue N while working issue N+2, you have
already failed the thing this command exists to do.

The board is the memory. Todo is available, In Progress is claimed, Done
is merged. That is enough to resume this loop from a cold start, so it
is enough to run it.

**The parked list is the one thing the board does not hold** — a card
sits in In Progress whether its PR is unopened, parked, or merged a
second ago. So if this run is interrupted, recover it from GitHub rather
than from memory:

```sh
gh pr list --state open --json number,headRefName,statusCheckRollup
```

An open PR whose checks have concluded green is a merge nobody made.
Settle those before taking a new issue.

## Stopping

Stop, and say which of these happened:

- **No eligible issue.** The expected ending. Report the tally — and
  settle any PR still parked before you call the run finished, since
  those are the last issues' outcomes.
- **The cap was reached.**
- **Three consecutive issues blocked.** Something is wrong with the
  environment, not with three unrelated issues — a broken `develop`, a
  down stack, expired credentials, an exhausted rate limit. Stop and say
  what the three failures had in common.
- **Projects v2 rate limit exhausted.** It has its own budget, separate
  from core. Report where the loop got to; do not poll it.

Never widen eligibility to keep going. An empty lane is the correct
result of an empty lane, and `agent:ready` is the only thing that puts
work into it.

## Report

A table — issue, title, outcome, PR — then the tally: delivered,
blocked, skipped, **still parked**. A parked row names its PR number and
says its checks were still running; never report one as delivered. Then
a single line naming what a human should look at first.

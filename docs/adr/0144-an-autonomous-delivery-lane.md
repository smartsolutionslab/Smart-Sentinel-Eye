# ADR-0144: An autonomous delivery lane

**Status:** **Accepted**
**Date:** 2026-09-03
**Amends:** ADR-0037 — the gate rule, for opted-in issues only
**Amended:** 2026-09-03 — phase 4a gained its characterisation path
(see *Phase 4a has two colours*), in place, on the day of acceptance and
before the lane had delivered anything
**Amended:** 2026-09-05 — phase 7 no longer waits for CI; PRs are parked
and merged when their checks conclude (see *The lane does not wait for
CI*). The green condition is unchanged

**Supersedes:** —
**Superseded by:** —

## Context

ADR-0037 defines seven phases and says, in as many words, that **Claude
Code does not autonomously advance past a gate**. Every phase hands its
artifact back and waits for a human.

That rule was written for a session a human is watching. It is the right
rule there, and it stays. But it makes one thing impossible: leaving the
agent to work through the board. A loop that stops seven times per issue
is not a loop — it is the same supervised session with extra ceremony.

The board is where this bites. Project #13 carries the in-flight work at
feature granularity, and its Todo lane holds ready, well-described,
independently-shippable issues — an unmeasured latency leg, a test that
covers nothing, a default that costs throughput. Each is a day's work
that needs a human for about four minutes of it: deciding it is worth
doing, and deciding the result may merge. The first decision was already
made when the issue was written. The second is already delegated — green
CI plus `--rebase --admin` is the standing merge convention here.

So the ceremony buys nothing on these issues, and costs the only thing
that would make the board drain: unattended runs.

Two further pressures shape the decision.

**Context is the scarce resource, not tokens.** An issue carried
end-to-end in one session accumulates the spec, the plan, every file
read during implementation, every test run, and the review. By phase 6
the session is reasoning about phase 4 through a summary of a summary.
The phases are already role-shaped — ADR-0037 names a different question
for each — and `.claude/agents/` already holds eight role definitions
that nothing currently binds to a phase.

**Red-first is the phase that autonomy most easily eats.** ADR-0139
requires a new-behaviour test to be *observed failing* and the failure
quoted in the PR. An unattended agent that writes test and implementation
together will produce a green test and a truthful-sounding claim that it
was red. The lane has to make the red output a transported artifact, not
a recollection.

## Decision

Add a second lane. ADR-0037's seven phases, artifacts and role
boundaries are unchanged; what changes is who opens the gate, and only
for issues explicitly enrolled.

### Two lanes

**The supervised lane is the default and is unchanged.** Any work a
human starts — a request in a session, a spec they asked for — runs
ADR-0037 exactly as written, stopping at all seven gates.

**The autonomous lane** runs phases 1–6 without stopping and merges at
phase 7 on green CI. An issue enters it only by carrying the
**`agent:ready`** label. Nothing else is eligible; absence of the label
is a hold, so a newly-filed issue is never picked up by accident.

### Eligibility

An issue is eligible when **all** of the following hold:

- it is on Project #13 with status **Todo**;
- it carries **`agent:ready`**;
- it does not carry **`agent:blocked`**.

Ties break **oldest issue number first**. Applying `agent:ready` is the
human gate for phases 1–6, made once, in advance, in writing.

### Phases and their roles

Each phase runs in a **subagent with its own context**. The orchestrator
holds only the issue, the branch name, and each phase's report — never
the files those phases read. This is what makes an unattended run
survive a whole issue and then start the next one clean.

| # | Phase | Agent | Carries forward |
|---|---|---|---|
| 1 | Specify | `architect` | `specs/NNN-x/spec.md` |
| 2 | Plan | `architect` | `plan.md` |
| 3 | Tasks | `architect` | `tasks.md` |
| 4a | Red tests, or characterisation | `test-writer` (+ `test-adversary` where the issue is about a failure mode) | **the test output, verbatim** — failing for new behaviour, passing for a refactor |
| 4b | Implement | `backend-engineer` / `frontend-engineer` / `infra-engineer` | commits, green test output |
| 5 | Verify | the same engineer, or the orchestrator | observed behaviour; latency figure if on the §IV path |
| 6 | Review | `backend-reviewer` / `frontend-reviewer` / `infra-reviewer`, plus `security-reviewer` where the change touches a trust boundary, then `/code-review` | findings, each fixed or refused in writing |
| 7 | PR + merge | orchestrator | the PR, then the merge |

Phase 4 is **two agents, in order, and the split is the point**. The
test-writer is told the intended behaviour and is forbidden from
touching implementation code. Its report must contain the actual failing
output. The engineer then receives that output as its brief and may not
edit the test to make it pass. A test file arriving already green is a
phase-4 failure, not a shortcut — the run is retried, once, from 4a.

The verbatim red output is quoted in the PR body, which is what ADR-0139
asks for and what a later reader can check.

### Phase 4a has two colours, because the constitution has two obligations

**Amended 2026-09-03, the day this ADR was accepted, before the lane had
delivered anything.** As first written this section said only "red
first", which is unsatisfiable for half the board. The constitution
states **two** obligations, not one rule with an exception: *new
behaviour starts red* **and** *refactors stay green*. A red test during a
behaviour-preserving change is a regression, not a step — ADR-0139 says
so explicitly. A lane that demanded red from a refactor would force the
agent to break something to satisfy a gate, which is the precise failure
mode the gate exists to prevent.

So **the architect declares, at phase 3, which kind of change the issue
is**, and phase 4a follows.

**Behaviour-changing → red.** As above. The failing output is the
artifact.

**Behaviour-preserving → characterisation, observed green.**

1. `test-writer` identifies the tests that already cover the behaviour
   being preserved. **If none exist, it writes them and observes them
   green.** A refactor with no covering test is not a refactor, it is a
   rewrite; the characterisation suite is the safety net and must exist
   *before* the change, not after it.
2. The **green** output is captured verbatim. That is the transported
   artifact, and it goes in the same PR slot the red output would.
3. The engineer refactors. **The same tests must pass unmodified.**
4. **A test whose assertions must be edited to accommodate the refactor
   is evidence the refactor changed behaviour.** Stop and block. Renaming
   a test, or updating a type name at a construction site, is mechanical
   and allowed; changing an asserted *value* is not.

Same evidence discipline, opposite colour. What is forbidden in both is
the same thing: an agent deciding for itself what the test should say
after seeing what the code does.

**When the guarantee is a compile error, no runtime test can express
it.** A refactor whose point is that a wrong call stops compiling —
grouping two loose coordinates into a value object, say — is verified by
the type system plus whichever architecture guard asserts the shape, not
by a test that can be run red. Say so in the PR and name the guard.
Do not manufacture a runtime test to satisfy the ritual.

**If an issue is a refactor *and* a bug fix, it is two issues.**
Characterisation locks in current behaviour including its defects, so
mixing the two encodes the bug in a test and calls it a safety net. This
is also just ADR-0036's smallest-change rule: a fix changes the bug, a
refactor changes shape, and they do not travel together.

**Ambiguity resolves to behaviour-changing**, because that path demands a
red test and therefore fails loudly, while a wrongly-chosen
characterisation path passes quietly.

### Merging

The orchestrator opens the PR against `develop` with `--base develop`
and merges with `--rebase --admin --delete-branch` when **every** check
has concluded successfully. A cancelled, skipped-required or failed check
is not green; `gh run watch` exiting is not evidence of green (it exits
on cancellation too), so the merge condition is read from the check
conclusions, not from a watcher's exit.

Review happens **after** the merge, on `develop`'s history. That is the
trade this lane makes and the reason eligibility is opt-in.

#### The lane does not wait for CI (amended 2026-09-05)

As first written, the orchestrator **waited** for CI before taking the
next issue. In practice that is the single largest idle cost in a run:
this repo's `pull_request` trigger is deliberately unfiltered and fires
the full set — build, coverage gate, Docker integration tests, and a
full-stack Playwright e2e — which is twenty-plus minutes per issue with
the loop doing nothing.

Waiting was never what made the merge safe. The **green condition** is.
So the wait is dropped and the condition kept:

- Phase 7 opens the PR, starts the watcher in the background, and
  returns the issue as **parked**. Parking is a normal ending of
  `/next-issue`, not a failure mode.
- **Green merges immediately**, mid-phase on another issue if that is
  when it lands.
- **Red does not pre-empt.** The issue in hand runs to its own ending
  first — a phase abandoned halfway is the state this workflow resumes
  worst from — and only then does the failure get its one retry with the
  CI log, then `agent:blocked`.

Two constraints keep concurrency from becoming its own problem:

- **At most three PRs parked at once.** At three the loop settles one
  before taking a fourth. The cap is not throughput management; it is
  how a branch that cannot rebase is found in hours rather than at the
  end of a run.
- **Every parked branch is rebased after every merge**, not once at the
  end. ADR-0087's rebase-merge renames the SHAs, so a parked branch
  still holds the **old copies** of whatever just landed and they replay
  as conflicts against themselves.

**What this does not change:** nothing merges that CI has not passed,
and no gate moves. The board also cannot recover the parked list — a
card reads In Progress whether its PR is unopened, parked, or merged a
second ago — so an interrupted run recovers it from `gh pr list`, where
an open PR with concluded green checks is a merge nobody made.

### Failure

A phase that fails is retried **once**, in a fresh subagent, with the
failure text as part of its brief. If it fails again the orchestrator:

1. comments on the issue with the phase, the retry, and the verbatim
   failure;
2. applies **`agent:blocked`** and removes `agent:ready`;
3. returns the board card to **Todo**;
4. leaves the branch and any open PR untouched, for a human;
5. **continues to the next eligible issue.**

One bad issue must not end an overnight run. An issue that fails twice
has earned a human, and says so on itself.

### What the lane may not do

- **It may not amend the constitution or write an ADR autonomously.** An
  issue whose honest answer is a new architectural decision is blocked
  with that as the reason. This is the boundary the loop must not cross:
  it implements decisions, it does not make them.
- **It may not weaken a gate to pass.** Deleting a failing test, relaxing
  a coverage threshold, adding a suppression, or narrowing an analyzer to
  get green is a blocked outcome, not a fix.
- **It may not skip phase 4a.** Every other phase has ADR-0037's skip
  mechanism for trivial changes; 4a does not, because a change too
  trivial to test is too trivial to need this lane. It has **two
  colours**, not an exemption: red for new behaviour, characterisation
  observed green for a refactor. Neither is optional, and the choice
  between them is the architect's at phase 3 — never the engineer's after
  seeing the code.

## Consequences

- **Positive:** the board can drain unattended. The two decisions that
  are expensive to get wrong stay human — what to build (`agent:ready`)
  and what the architecture is (blocked) — and the five that are cheap
  stop costing a round trip each.
- **Positive:** per-phase subagents mean phase 6 reviews the code rather
  than a summary of it, and issue N+1 starts with a context that has
  never seen issue N.
- **Positive:** red-first stops being a claim and becomes a transported
  artifact. The engineer cannot produce the red output it was handed.
- **Negative:** review moves after the merge. A defect that survives
  phase 6 lands on `develop` and is found by the next reader, not before.
  Bounded by opt-in eligibility and by rebase-only history, which keeps
  a bad commit individually revertable.
- **Negative:** two lanes is two rules, and the wrong one can be applied.
  Mitigated by making the distinction a label rather than a judgement.
- **Negative:** an unattended run can spend a long time on an issue that
  a human would have abandoned in a minute. Bounded by the one-retry
  policy, not eliminated by it.
- **Negative (2026-09-05 amendment):** up to three branches are open
  against a moving `develop`, so a merge can invalidate work already
  done. Bounded by rebasing after every merge and by the cap, and the
  failure is loud — a conflicting rebase blocks its issue the normal way.
  The cost is real and is accepted against twenty-plus idle minutes per
  issue.
- **Negative (2026-09-05 amendment):** the parked list lives only in the
  running loop. An interrupted run leaves green PRs unmerged and cards
  reading In Progress, indistinguishable from claimed-but-unstarted, so
  recovery goes through `gh pr list` rather than the board.

## Alternatives Considered

**Keep all seven gates, make them asynchronous** — post the artifact,
label `gate:awaiting-<phase>`, park the issue, move on. Rejected: it
preserves every gate and delivers nothing, because the board fills with
half-built issues whose branches drift behind `develop` while they wait.
Seven human touches per issue is the cost being removed, and spreading
them over a week does not remove it.

**Merge without waiting for green** (considered at the 2026-09-05
amendment) — open the PR and merge it, letting CI report on `develop`.
Rejected outright: it is the one thing that would actually weaken a gate,
which this lane may not do, and `develop`'s protection rules require
linear history (ADR-0087), so a red commit is not cheaply undone. The
wait is the cost being removed; the condition stays.

**Merge parked PRs only at issue boundaries** (same amendment) — never
interleave a merge with implementation work. Rejected as buying little:
a merge is three commands against the GitHub API and touches no working
tree, so the interleaving risk it avoids is close to zero, while the
delay it adds is real — a PR that goes green a minute into a long issue
would sit unmerged for an hour, and the branches behind it would rebase
against a `develop` that is already stale.

**Gate at spec and PR only** — the loop stops twice per issue. Genuinely
attractive, and rejected only because the spec gate duplicates a decision
already made: an issue on the board carrying `agent:ready` has been read
and judged worth doing. A second approval of the agent's reading of it
buys less than it costs in stopping the loop.

**Full autonomy for everything in Todo** — no label. Rejected: the Todo
lane today contains investigations phrased as issues ("nine of the
twenty-seven founding decisions do not describe the system that exists"),
whose honest first output is an ADR. The lane may not write one, so it
would block immediately and repeatedly.

**Opt-out (`agent:hold`) rather than opt-in.** Rejected for the same
reason, plus one worse: a newly-filed issue would be eligible the moment
it lands, including one filed to record a problem rather than to request
a fix.

**Recording the refactor path as a separate ADR-0145**, the way ADR-0140
amends ADR-0139. Rejected *for this case only*. That convention is right
when experience changes a decision: the original reasoning was sound when
written and the reader needs both halves. Here the rule was incomplete on
the day it was written, had governed nothing, and splitting it would
leave phase 4a's authority in two files — the failure mode this
repository has already recorded twice, as §II's drift and as two ADRs
amending one constitution section in disagreement. The amendment is
dated and named in the header instead, so nothing is erased.

**A headless driver script (`claude -p` per issue).** This gives a
genuinely new process per issue and is the strictest reading of "fresh
context". Not adopted now: per-phase subagents already give each phase a
clean context, and the orchestrator's own state is small and lives on the
issue and the board rather than in its head — so it can be resumed from
scratch. Revisit if orchestrator context turns out to be the binding
constraint over a long run.

## Implementation Notes

- `/next-issue` delivers one eligible issue end-to-end. `/deliver-board`
  repeats it until none remain.
- `/verify` implements ADR-0037's phase 5, which CLAUDE.md and ADR-0037
  have both referenced since 2026-05-25 without it existing.
- Labels `agent:ready` and `agent:blocked` are created in the repository.
- Board state is the run's memory: **Todo** is available, **In Progress**
  is claimed, **Done** is merged. An orchestrator restarted mid-issue
  reads the card and the branch, not a transcript.
- `.claude/agents/backend-engineer.md` and `backend-reviewer.md` both
  said "NRT disabled" long after ADR-0141 enabled it. Corrected as part
  of this work: a subagent brief is now load-bearing, and a stale one
  writes stale code without a human present to catch it.
- **Two reviewer roles are added**, because phase 6 previously had
  reviewers for backend and frontend only:
  - **`infra-reviewer`** — the layer every integration test stands on
    had no reviewer at all. An infra defect does not fail like a code
    defect; it fails as everything failing, or as everything passing for
    the wrong reason.
  - **`security-reviewer`** — `/security-review` is a skill with no
    repo-specific brief, so it cannot tell a correct `RequireScope` from
    a plausible-looking wrong one, and it does not know that
    `sse.management` grandfathers every granular policy or that an
    idempotency key scoped without the caller is a cross-tenant leak.
    The skill still runs; this role is what reads the authorization
    model.

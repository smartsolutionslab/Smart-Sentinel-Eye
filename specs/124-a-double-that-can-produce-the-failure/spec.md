# Spec 124 — A double that can produce the failure

**Issue:** #2151
**Depends on:** spec 087 (`census.md` detection 4), spec 092 (§"Whether the
existing tests can fail"), spec 078 (the already-fixed calibration instance)
**Latency:** **N/A.** Test-only. No file under `src/` changes; no leg of
constitution §IV is touched. Confirmed by `git diff --stat` at every commit on
this branch — `src/` appears in none of them.

## The defect class

Six assertions across three sites are written for a condition their double
cannot express. They pass, and they would pass with the production code they
name deleted. Spec 087 called this **detection 4** and said plainly it is the
one that cannot be found by grep.

The tell in all six is the same: **the property the assertion checks is
implemented behind the seam the double stands in for**, so the test reads the
double's behaviour and calls it the subject's.

| # | Test | The property | Where it actually lives |
|---|---|---|---|
| 1 | `RuleEvaluatorTests.A_rule_in_another_fab_is_not_evaluated` | fab-scoped matching | `IRuleCache`'s key |
| 2 | `RuleEvaluatorTests.A_fab_with_no_rules_evaluates_nothing` | fab-scoped matching | `IRuleCache`'s key |
| 3 | `FabEventIngestedV1HandlerTests.An_event_publishes_nothing_for_a_rule_belonging_to_another_fab` | fab-scoped matching | `IRuleCache`'s key |
| 4 | `FabEventIngestedV1HandlerTests.An_event_without_a_usable_fab_publishes_nothing` | fails closed rather than open | nothing at this seam |
| 5 | `KioskPrivilegeSweepTests.Does_not_touch_an_account_this_system_did_not_enrol` | boundedness of the sweep | `HttpKeycloakAdminClient`'s `sse.kind` filter |
| 6 | `SystemVariableValueRequestedV1HandlerTests.Invalid_variable_name_is_logged_and_dropped` | nothing is created | nowhere — there is no create path |

**And a seventh, which #2151 does not list.** Spec 092 found
`KioskPrivilegeSweepTests.Sweeping_twice_does_no_more_than_sweeping_once`
vacuous by counterfactual and **filed the finding on #2151** rather than opening
a new issue. It is in the same file, has the same remedy, and is fixed here. The
census's count of three sites is therefore right for what it measured and short
by one for what the issue now owns.

## What "fixed" means here

**Green-before / red-after, per assertion, or it is a no-op.** Each of the seven
is shown passing against a production counterfactual that embodies the defect it
names, then failing once the double can express that defect, then passing with
production restored. Without the middle observation the change is a rewritten
fake and nothing more. Evidence: `verification.md`.

**No assertion is deleted or weakened** (#2151 is explicit; ADR-0144 forbids it
regardless). Every existing line stays; what changes is what stands beside it.

**No production code changes.** The counterfactuals are applied and reverted
within a run; `src/` is untouched by every commit. Had one of the seven revealed
a real defect, that would be a different issue and this spec would stop — none
did.

## FR-001 — the keyed-lookup seam records what it was asked for

`IRuleCache.LookupActive(fab, source, kind)` is a **keyed** lookup, so the fab
filter is in the implementation, not in `RuleEvaluator`, which does no fab
filtering of its own. No faithful double can change that: keying on the fab is
what the contract *is*.

So the four tests stop reading the answer alone and start reading **the key the
caller asked for** — the half of the seam the caller chooses. The double becomes
a *recording* one, which is what the 71 falsifiable absence-assertions in this
repository already use (spec 087). Concretely: an evaluator that hard-coded the
fab it looked up, or a handler that fell back to some substitute fab when the
event carried none, returns empty from the wrong bucket and every one of the
four stays green today. An empty or wrong lookup log does not.

FR-001 is satisfied when `Automation.Application.Tests`' `InMemoryRuleCache`
exposes an ordered log of lookup keys, and tests 1–4 assert it.

## FR-002 — the boundedness is asserted where it is implemented

`KioskPrivilegeSweep` contains no filter and no idempotency. Both live in
`HttpKeycloakAdminClient`, in a project `Identity.Application.Tests`
deliberately cannot reference — an absence
`EnrolledKiosksKeycloakAdminClient`'s own remarks already name as the reason
test 5 is vacuous.

FR-002 is satisfied when `Identity.Infrastructure.Tests` asserts both properties
against `HttpKeycloakAdminClient` over the `HttpMessageHandler` seam (the
`StubKeycloakHandler` pattern spec 121 established for exactly this reason), and
tests 5 and 7 carry a pointer to them.

## FR-003 — one removal per kiosk per pass is countable

`FakeKeycloakAdminClient.Stripped` is a `HashSet`, so repeated removals collapse
into it and a sweep calling the removal three times per kiosk left the whole
suite green. FR-003 is satisfied when the fake also keeps the repeats and test 7
asserts them.

## FR-004 — the log that a test's own name claims is asserted

`Invalid_variable_name_is_logged_and_dropped` was handed a `NullLogger`. The "is
logged" half of its name was carried by nothing at all, and a drop that does not
name the value that failed is indistinguishable from automation having quietly
stopped — the failure shape this file's neighbours were written for.

FR-004 is satisfied when the handler is given a `CapturingLogger` and the
warning is asserted to name the rejected value.

## FR-005 — the one that cannot be made falsifiable says so

`repo.Variables.ShouldBeEmpty()` (test 6) is **not a double problem.**
`SetVariableValueCommandHandler` never calls `IVariableRepository.Add`; there is
no create path to get wrong, so the repository is empty for any input. The
double is *already* capable — it records every `Add`, and it is the same seam
`DefineVariableCommandHandler` creates through — which means nothing can be
added to the fake that would make this red. Only a production defect could, and
manufacturing one to claim a red would be theatre.

FR-005 is satisfied when the assertion **stays**, is labelled at the test as the
forward-looking regression guard it is, and the reasoning is written where the
next reader meets it rather than only here.

## Out of scope

- **Deleting `Automation.Application.Tests`' `InMemoryRuleCache` clone** in
  favour of the shipped one. It would bind tests 1–4 to real keying, and it
  would give an Application test project a reference to Infrastructure — a
  layering step no test project in this repository has taken, and one Identity's
  fakes record a deliberate decision against. Raised and rejected; see `plan.md`.
- **Defence-in-depth in `RuleEvaluator`** (skipping a returned rule whose `Fab`
  disagrees with the requested one). It would make the leak reachable through
  the seam and so make tests 1–3 fully falsifiable. It is a production change to
  add error handling at a non-boundary, which Karpathy §"no drive-by error
  handling" refuses and #2151 puts out of scope. Recorded as a risk, not taken.

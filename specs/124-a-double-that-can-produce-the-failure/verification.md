# Verification — 124 A double that can produce the failure

**Latency: N/A.** Test-only. `git diff --stat origin/develop...HEAD` names no
file under `src/`. The counterfactuals below were applied and reverted inside
this session; `git status` after step 3 shows `src/` clean.

Every block is output this branch produced. Nothing is paraphrased.

## Baseline (unmutated, before any change)

```
Automation.Application.Tests     Passed:  110
Automation.Infrastructure.Tests  Passed:    9
Identity.Application.Tests       Passed:   61
Identity.Infrastructure.Tests    Passed:   12
SystemVariables.Application.Tests Passed:  81
```

---

## Tests 1–4 — the `IRuleCache` keyed seam

**Counterfactual M1 + M2.** `RuleEvaluator` ignores the fab it is given and
looks up `"hamburg"`; `FabEventIngestedV1Handler` no longer fails closed on an
absent or unparseable fab and carries on with a substitute. Between them these
are the defect all four tests name.

### Green-before — the four, unmodified, against that production

```
##### GREEN-BEFORE (mutant: evaluator hard-codes fab 'hamburg'; handler no longer fails closed)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 101 ms - SmartSentinelEye.Automation.Application.Tests.dll (net10.0)
```

Six, because test 4 is a `[Theory]` with three cases. **An evaluator that
matches no fab correctly, and a handler that fails open on the case #1252 is
named after, and every one of them passes.**

### Red-after — same production, doubles now recording the key

```
##### RED-AFTER: Automation C1-C4 (mutant still applied)
[xUnit.net 00:00:00.80]     SmartSentinelEye.Automation.Application.Tests.Evaluation.RuleEvaluatorTests.A_rule_in_another_fab_is_not_evaluated [FAIL]
[xUnit.net 00:00:00.81]     SmartSentinelEye.Automation.Application.Tests.EventHandlers.FabEventIngestedV1HandlerTests.An_event_publishes_nothing_for_a_rule_belonging_to_another_fab [FAIL]
[xUnit.net 00:00:00.83]     SmartSentinelEye.Automation.Application.Tests.Evaluation.RuleEvaluatorTests.A_fab_with_no_rules_evaluates_nothing [FAIL]
[xUnit.net 00:00:00.83]     SmartSentinelEye.Automation.Application.Tests.EventHandlers.FabEventIngestedV1HandlerTests.An_event_without_a_usable_fab_publishes_nothing(fab: "   ") [FAIL]
[xUnit.net 00:00:00.84]     SmartSentinelEye.Automation.Application.Tests.EventHandlers.FabEventIngestedV1HandlerTests.An_event_without_a_usable_fab_publishes_nothing(fab: "") [FAIL]
[xUnit.net 00:00:00.85]     SmartSentinelEye.Automation.Application.Tests.EventHandlers.FabEventIngestedV1HandlerTests.An_event_without_a_usable_fab_publishes_nothing(fab: "NotAFab") [FAIL]

Failed!  - Failed:     6, Passed:     0, Skipped:     0, Total:     6, Duration: 240 ms - SmartSentinelEye.Automation.Application.Tests.dll (net10.0)
```

Test 2, in full:

```
    SmartSentinelEye.Automation.Application.Tests.Evaluation.RuleEvaluatorTests.A_fab_with_no_rules_evaluates_nothing [FAIL]
      Shouldly.ShouldAssertException : cache.Lookups
          should be
      [(berlin, plc, PlcCycleStart)]
          but was
      [(hamburg, plc, PlcCycleStart)]
          difference
      [*(hamburg, plc, PlcCycleStart)*]
```

Test 4, in full:

```
    SmartSentinelEye.Automation.Application.Tests.EventHandlers.FabEventIngestedV1HandlerTests.An_event_without_a_usable_fab_publishes_nothing(fab: "   ") [FAIL]
      Shouldly.ShouldAssertException : cache.Lookups
          should be empty but had
      1
          item and was
      [(hamburg, plc, PlcCycleStart)]

      Additional Info:
          an unusable fab must stop the handler before it consults the rule cache at all; falling back to any other fab is the #1252 shape
```

---

## Test 5 — the sweep's boundedness

**Counterfactual M3.** `HttpKeycloakAdminClient.GetEnrolledKioskClientIdsAsync`
loses its `sse.kind == "kiosk"` filter and returns every client in the realm —
the exact defect `Does_not_touch_an_account_this_system_did_not_enrol` is
written against.

### Green-before

```
##### IDENTITY (C5, C7)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 85 ms - SmartSentinelEye.Identity.Application.Tests.dll (net10.0)
```

**The production filter is gone and the test that names it passes.** It cannot
see it: `Identity.Application.Tests` does not reference Infrastructure, so what
it asserts is `FakeKeycloakAdminClient`'s own copy of the filter.

### Red-after — the same claim, against the class that implements it

```
    SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin.EnrolledKioskQueryTests.Only_a_client_this_system_stamped_as_a_kiosk_is_enrolled [FAIL]
      Shouldly.ShouldAssertException : enrolled
          should be
      ["kiosk-a"]
          but was (case sensitive comparison)
      ["kiosk-a", "someone-elses-account", "realm-management"]
          difference
      ["kiosk-a", *"someone-elses-account"*, *"realm-management"*]

      Additional Info:
          the sweep applies no filter of its own, so this list is the whole of the boundedness: an operator console in it would be stripped of every realm role it holds
```

The original assertion is untouched and still green; it now carries a pointer to
this one.

---

## Test 7 — one removal per kiosk per pass

Not listed on #2151 by the census. Found by spec 092 and **filed on #2151**;
fixed here.

### Green-before — counterfactual M5, the sweep strips three times per kiosk

```
##### GREEN-BEFORE C7 (mutant: sweep strips three times per kiosk)
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 90 ms - SmartSentinelEye.Identity.Application.Tests.dll (net10.0)
```

Spec 092 recorded `Passed: 5` for this mutant. Reproduced here rather than
taken on trust.

### Red-after — the fake keeps the repeats a `HashSet` discarded

```
##### RED-AFTER C7 (mutant: sweep strips three times per kiosk)
    SmartSentinelEye.Identity.Application.Tests.KeycloakAdmin.KioskPrivilegeSweepTests.Sweeping_twice_does_no_more_than_sweeping_once [FAIL]
      Shouldly.ShouldAssertException : keycloak.StripCalls
          should be
      ["kiosk-a", "kiosk-a"]
          but was (case sensitive comparison)
      ["kiosk-a", "kiosk-a", "kiosk-a", "kiosk-a", "kiosk-a", "kiosk-a"]
          difference
      ["kiosk-a", "kiosk-a", *"kiosk-a"*, *"kiosk-a"*, *"kiosk-a"*, *"kiosk-a"*]
```

### And the other half of the claim, at the layer that owns it

**Counterfactual M4** — `if (assigned.Length == 0) return;` becomes `== -1`, so
the early exit is never taken and every pass sends a removal:

```
    SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin.EnrolledKioskQueryTests.A_second_removal_sends_nothing_when_the_account_holds_no_realm_role [FAIL]
      Shouldly.ShouldAssertException : keycloak.Requests.Select(request => request.Method)
          should not contain
      "DELETE"
          but was actually
      ["GET", "GET", "GET", "DELETE"]

      Additional Info:
          this is what makes the sweep safe to run on every Identity start — a removal that always fired would put a write per kiosk per boot against the realm, and an empty DELETE is not a no-op at Keycloak
```

`A_first_removal_deletes_the_roles_the_account_actually_holds` passed on both
sides of M4, which is what a control is for: without it, a client that never
deletes anything satisfies the test above.

---

## Test 6 — the invalid variable name

**Counterfactual M6.** The handler logs a constant instead of the rejected
name.

### Green-before

```
##### SYSTEMVARIABLES (C6)
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 47 ms - SmartSentinelEye.SystemVariables.Application.Tests.dll (net10.0)
```

The test is called `Invalid_variable_name_is_logged_and_dropped` and it was
handed a `NullLogger`. Nothing in it could see the log at all.

### Red-after

```
    SmartSentinelEye.SystemVariables.Application.Tests.EventHandlers.SystemVariableValueRequestedV1HandlerTests.Invalid_variable_name_is_logged_and_dropped [FAIL]
      Shouldly.ShouldAssertException : entry.Message
          should contain (case insensitive comparison)
      "1bad"
          but was actually
      "Invalid variable name 'a variable' in V1; dropping (caused by 01a08cb4-c353-7b2c-afb0-c898ea4914fe)."

      Additional Info:
          the rejected name is the only thing that tells the Automation team which rule authored it; a line without it sends nobody anywhere
```

### And the half that stays vacuous, said out loud

`repo.Variables.ShouldBeEmpty()` has **no** green-before / red-after pair,
because none exists.
`SetVariableValueCommandHandler` never calls `IVariableRepository.Add`, so the
repository is empty for every input. This is not a double that cannot express
the failure — the double expresses it fine, and records every `Add` through the
same seam `DefineVariableCommandHandler` creates through. There is no failure to
express. Only adding a create path to production would redden it, and inventing
one to claim a red would be the theatre this spec exists to remove.

The assertion is kept, given a `because`, and labelled at the test as the
forward-looking regression guard it is. **It cannot be made falsifiable, and
that is the finding.**

---

## Final — production restored, tests kept

```
##### FINAL GREEN (production reverted, tests kept)
===== Automation.Application.Tests
Passed!  - Failed:     0, Passed:   110, Skipped:     0, Total:   110, Duration: 501 ms
===== Automation.Infrastructure.Tests
Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 127 ms
===== Identity.Application.Tests
Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61, Duration: 415 ms
===== Identity.Infrastructure.Tests
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 496 ms
===== SystemVariables.Application.Tests
Passed!  - Failed:     0, Passed:    81, Skipped:     0, Total:    81, Duration: 212 ms
```

Identity.Infrastructure.Tests 12 → 15; every other count unchanged, which is the
point: no assertion was removed and no test moved.

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:01:32.27
```

**Full unit suite, Release, 29 projects: 2435 passed, 0 failed, 0 skipped.**
`Integration.Tests` is excluded — it needs a booted Aspire stack and Docker,
which this environment does not have; CI runs it.

## No production defect was found

None of the seven, once falsifiable, failed against unmutated production. Every
red above required a counterfactual, and every counterfactual was reverted. That
is the outcome #2151 asked for; had it been otherwise this spec would have
stopped and reported.

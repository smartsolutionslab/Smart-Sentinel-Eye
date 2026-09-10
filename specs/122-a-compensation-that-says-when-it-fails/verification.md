# Verification 122 — a compensation that says when it fails

## Phase 4a — the red, verbatim

`dotnet test tests/Identity.Infrastructure.Tests/... --filter "FullyQualifiedName~HalfEnrolledClientCompensationTests"`

```
  Failed SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin.HalfEnrolledClientCompensationTests.A_refused_compensation_names_the_client_it_left_behind(refusal: Unauthorized) [266 ms]
  Error Message:
   Shouldly.ShouldAssertException : logger.Named(ResidueLine)
    should have single item but had
0
    items and was
[]

Additional Info:
    Keycloak answered 401 to the compensating delete, so the half-enrolled client is still in the realm holding offline_access, and the existence probe answers already-enrolled for it forever. Nothing records that today (#2166).

  Failed ...A_refused_compensation_names_the_client_it_left_behind(refusal: InternalServerError) [7 ms]
  Failed ...A_refused_compensation_names_the_client_it_left_behind(refusal: Conflict) [4 ms]
    (same assertion, same empty list)

  Failed ...A_compensation_finding_nothing_to_delete_does_not_claim_a_residue [4 ms]
  Error Message:
   Shouldly.ShouldAssertException : logger.Named(AbsenceLine)
    should have single item but had
0
    items and was
[]

Additional Info:
    it is still worth a line: the post-create re-probe found this client moments earlier, and one call later it is gone

Failed!  - Failed:     4, Passed:     2, Skipped:     0, Total:     6, Duration: 276 ms
```

**The two that passed are the restraint companions**, and the file says so
before the run rather than after: `The_caller_still_receives_the_failure_that_caused_the_compensation`
and `A_compensation_that_succeeds_says_nothing` describe behaviour that is
already correct and must survive the fix. They are not the red.

## Phase 4b — the green

```
Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 467 ms - SmartSentinelEye.Identity.Infrastructure.Tests.dll (net10.0)
```

Six new, six pre-existing (`KeycloakAdminSerializationTests`,
`KioskPrivilegeSweepSteadyStateTests`), none modified.

## The red discriminates — proved by counterfactual, not asserted

`A_compensation_finding_nothing_to_delete_does_not_claim_a_residue` contains a
`ShouldBeEmpty`, which passes trivially today. The claim in its doc comment is
that it fails against the *obvious alternative* — one residue line for every
non-success. Tested by deleting the 404 branch from the shipped fix:

```
  Failed ...A_compensation_finding_nothing_to_delete_does_not_claim_a_residue [40 ms]
    should be empty but had
Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6
```

It fails on exactly the implementation it claims to exclude. The branch was
restored and re-run green (and the file re-stamped — a restored file keeps its
old timestamp and MSBuild will skip the rebuild).

## Phase 5 — measured against live Keycloak, before the code was written

`keycloak-18bcf406`, `quay.io/keycloak/keycloak:26.6`, run-mode container,
realm `smart-sentinel-eye`, `DELETE /admin/realms/{realm}/clients/{uuid}`
with a `master`/`admin-cli` bearer token:

| Case | Status | Body |
|---|---|---|
| Existing client (created for the probe) | **204** | *(empty)* |
| Same uuid immediately again | **404** | `{"error":"Could not find client"}` |
| Unknown uuid | **404** | `{"error":"Could not find client"}` |
| Malformed uuid (`not-a-uuid`) | **404** | `{"error":"Could not find client"}` |
| Unknown realm | **404** | `{"error":"Realm not found."}` |
| No bearer token | **401** | `{"error":"HTTP 401 Unauthorized"}` |

Three things this settles rather than assumes:

1. Success is exactly `204` with an empty body, so `IsSuccessStatusCode` is a
   sound predicate and no success case carries a payload to misread.
2. `404` really does mean *no such client* — Keycloak says so in the body — so
   treating it as a residue would state something false.
3. A vanished **realm** also answers `404`. That is the one ambiguity, and it
   is recorded in a comment at the branch rather than resolved by parsing an
   error body: it describes a realm disappearing mid-enrolment, which the
   caller's own failure already reports.

The throwaway client (`sse-2166-probe`) was created and deleted inside the
probe; the realm was confirmed clean afterwards (`[]`). **The Keycloak volume
was not touched.**

## What the startup sweep actually does — the issue's premise, re-read

The issue's point 1 (*"the sweep has never run"*) is **stale**: spec 092 landed
and `IdentityInfrastructureModule.cs:56-57` registers both the sweep and its
hosted service. #2132 is closed.

Point 2 is live, and larger than filed. `KioskPrivilegeSweep.SweepAsync`'s
whole per-kiosk body is `StripInheritedRealmRolesAsync`. It **strips
privileges and removes nothing** — it does not delete the residue, disable it,
or flag it, and `GetEnrolledKioskClientIdsAsync` selects on `sse.kind=kiosk`,
which a residue and a healthy kiosk both carry, so it cannot tell them apart.

**The re-enrolment block therefore survives every sweep, on every start,
forever.** Only a human deleting the client clears it. That is what raised the
level from Warning to Error, and it is why the message no longer says the
sweep handles it.

## §IV — latency

**Not touched, and not on the path.** Kiosk enrolment is an administrative,
operator-initiated call and appears in none of the six legs of the
event-to-overlay budget. The change adds one status comparison and, only on an
already-failing path, one log call. No leg's state in constitution §IV changes.

## Security

Nothing became more permissive and the residue did not become easier to
obtain. FR-005 is deliberate: the residue is reported to the operator's log
and **not** added to the enrolment response, because that response is the
wrong place to tell a requester that a client bearing the `clientId` they
asked for now exists in the realm with a service account. The compensating
delete still never throws, so the caller's error is unchanged.

## Build and suite

```
dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

All 29 unit and architecture test projects, Release: **2432 passed, 0 failed,
0 skipped.** Integration.Tests was not run — it needs a booted Aspire stack,
and CI runs it in its own job.

One Release-only analyzer error appeared and was fixed (`CA1859` on
`CapturingLogger.FieldsOf`) — Debug would have hidden it.

**Each commit was verified on its own**, not merely at the tip — rebase-merge
lands them individually on `develop`. The first attempt failed that: the
`CA1859` fix had landed in the implementation commit, leaving the test-only
commit unbuildable in Release. The commits were re-split so the test commit
carries its own analyzer fix. Re-checked at `test(identity)`: `Build
succeeded. 0 Warning(s) 0 Error(s)`, and the suite there still reads `Failed:
4, Passed: 2` — the red, standing on its own.

## What did not change

`KioskPrivilegeSweep`, its hosted service and its registration; the exception
the caller receives; the API contract; the `WhenWritingNull` ignore condition
spec 121 landed an hour earlier. No test was edited to pass, no threshold
lowered, no suppression added.

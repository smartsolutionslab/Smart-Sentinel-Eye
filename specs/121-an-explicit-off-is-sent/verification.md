# Verification 121 — an explicit off is sent

## Phase 4a — the red, verbatim

`dotnet test tests/Identity.Infrastructure.Tests -c Release --filter
"FullyQualifiedName~KeycloakAdminSerializationTests"`, at commit `e7d79db`
(tests only, before the fix):

```
Failed ...KeycloakAdminSerializationTests.An_explicit_off_is_sent_rather_than_omitted [3 ms]
  Shouldly.ShouldAssertException : body
    should contain key
"standardFlowEnabled"
    but does not
  Additional Info:
    the handler passes StandardFlowEnabled: false, but WhenWritingDefault drops it,
    so Keycloak sees an absent field and applies its own default — which is true
    (issues #2165, #2207).

Failed ...KeycloakAdminSerializationTests.The_flags_Keycloak_only_agrees_with_by_coincidence_are_sent_too
  Shouldly.ShouldAssertException : body
    should contain key
"directAccessGrantsEnabled"
    but does not
  Additional Info:
    stored false today only because Keycloak's default agrees; not sent (#2165).

Failed ...KeycloakAdminSerializationTests.Disabling_a_client_sends_the_flag_that_disables_it [8 ms]
  Shouldly.ShouldAssertException : body
    should contain key
"enabled"
    but does not
  Additional Info:
    the revocation PUT serialises to {} — Keycloak applies only the fields a
    representation carries, so the client is never disabled at all.

Failed!  - Failed: 3, Passed: 0, Skipped: 0, Total: 3
```

Each failure is an *absent property*, which is the defect itself rather than a
mismatched value — and each names a different request. The
`serviceAccountsEnabled == true` control in the first test passed before the red
was reached, so the assertion is reading a real body, not an empty stub.

## Phase 4b — the green

Same command at `e192ae9`, and then the whole project:

```
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6 - SmartSentinelEye.Identity.Infrastructure.Tests.dll
```

The three tests pass unmodified. Nothing was edited to reach green.

## Phase 5 — observed against a live Keycloak

`quay.io/keycloak/keycloak:26.6`, the run-mode container from an earlier AppHost
(`keycloak-18bcf406`), realm `smart-sentinel-eye`, admin REST API over
`https://127.0.0.1:10756`. The realm volume was not touched; both probe clients
were deleted afterwards and the realm holds no `spec121*` residue.

**Creation — the body before the fix, and the body after, posted side by side:**

```
POST before -> 201        (three false flags simply absent, as WhenWritingDefault sent them)
POST after  -> 201        (every stated flag present)

--- spec121-before as stored:
"enabled":true
"standardFlowEnabled":true          <- the handler asked for false
"directAccessGrantsEnabled":false
"serviceAccountsEnabled":true
"publicClient":false

--- spec121-after as stored:
"enabled":true
"standardFlowEnabled":false         <- what was asked for
"directAccessGrantsEnabled":false
"serviceAccountsEnabled":true
"publicClient":false
```

The "before" column reproduces both issues' measurements exactly, including
#2165's finding that `directAccessGrantsEnabled` lands `false` — and it lands
`false` in the "before" client, which had not sent it. That is the coincidence
stated as such: Keycloak's default agrees. The "after" client sends it.

**Revocation — the second instance, which neither issue found:**

```
PUT {}                -> 204
  enabled now: "enabled":true       <- 204, and nothing happened
PUT {"enabled":false} -> 204
  enabled now: "enabled":false
```

`DisableClientAsync` sent `{}` and Keycloak answered **204 No Content**. A
success status for an update that changed nothing is why this survived: the
call succeeded, the aggregate was marked `Disabled`, and the client stayed
enabled with its service account still able to mint tokens. Spec 102 ("a revoked
credential stops working") has no `verification.md`, so nothing had looked.

## §IV — latency

**Not touched.** Keycloak client creation and revocation are administrative
calls on the enrolment and disable paths. Neither appears on any of the six legs
of `event arrival → overlay rendered`; no leg's budget, state, or §IV row
changes.

## Build and suite

```
dotnet build -c Release   ->  Build succeeded. 0 Warning(s) 0 Error(s)
```

All 29 non-Docker test projects, `-c Release --no-build`: **2420 passed, 0
failed, 0 skipped.** (`scripts/coverage-check.ps1` needs PowerShell 7, which is
not installed on this machine; its project selection — every `tests/*.csproj`
except `Integration.Tests` — was reproduced directly.)

## What did not change

No handler's intent. All three creation handlers already passed
`StandardFlowEnabled: false, DirectAccessGrantsEnabled: false, PublicClient:
false`; none was edited. Nothing became more permissive — every value this change
puts on the wire is a `false` replacing a Keycloak default, and for
`standardFlowEnabled` that default was `true`.

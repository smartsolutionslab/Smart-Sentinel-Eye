# Verification — spec 119

**Phase 4 evidence.** Phase 5 (end-to-end, against a running stack) is **not**
discharged here and is called out at the bottom.

## 1. The bearer-pipeline comparison — the investigation the issue put first

A probe app configured with the three options `AuthenticationDefaults` sets
(`Authority`, `RequireHttpsMetadata = false`, `ValidAudiences`,
`MapInboundClaims = false`, JwtBearer 10.0.11 — the pinned version), authority at
`http://127.0.0.1:1`, `UseExceptionHandler()` + `RequireAuthorization()` as every
API's `Program.cs` has them:

```
MEASURED with a bearer token: 401 Unauthorized
  www-authenticate: Bearer error="invalid_token", error_description="The signature key was not found"
MEASURED with no token: 401 Unauthorized
  www-authenticate: Bearer
```

And the mechanism, against the same dead authority:

```
A. direct GetConfigurationAsync: THREW System.InvalidOperationException
   IDX20803: Unable to obtain configuration from: 'http://127.0.0.1:1/realms/…'
B. JsonWebTokenHandler.ValidateTokenAsync: IsValid=False, exception=SecurityTokenSignatureKeyNotFoundException
```

**The nine REST APIs answer 401. The defect is WHEP-local**, and no shared
`ServiceDefaults` change is warranted.

## 2. The red, verbatim

```
WhepValidatorUnreachableRealmTests.An_unreachable_realm_refuses_instead_of_throwing [FAIL]
  System.InvalidOperationException : IDX20803: Unable to obtain configuration from:
  'https://keycloak.invalid/realms/smart-sentinel-eye/.well-known/openid-configuration'.
    at Microsoft.IdentityModel.Protocols.ConfigurationManager`1.GetConfigurationAsync(CancellationToken cancel)
    at …WhepAuthValidator.ValidateAsync(String bearerToken, CancellationToken cancellationToken)
       in …\WhepAuthValidator.cs:line 104

AuthorizeWhepIdentityUnavailableTests.Authorize_when_the_realm_is_unreachable_does_not_blame_the_token [FAIL]
  Shouldly.ShouldAssertException : result.Error.Code
    should not be "WHEP_UNAUTHORIZED" but was
```

The first fails by *escaping*, which is the defect itself.

## 3. Green

```
SmartSentinelEye.StreamDistribution.Infrastructure.Tests: Passed 26, Failed 0
SmartSentinelEye.StreamDistribution.Application.Tests:    Passed 65, Failed 0
dotnet build -c Release:  0 Warning(s), 0 Error(s)
29 suites (everything but Integration.Tests): 2410 passed, 0 failed
```

Each commit builds on its own — `cea45b9d` was checked out and built clean
before `b2d7d5ed` was written on top (ADR-0087).

## 4. Counterfactuals — three run, one of which corrected the plan

- **`catch (Exception)` instead of `catch (InvalidOperationException)`** →
  `A_cancelled_request_stays_cancelled` fails. The cancellation test earns its
  place: it is aimed at the widening, and it catches it.
- **`ThrowIfCancellationRequested()` removed from the catch** → everything still
  passes, in all three cancellation shapes that could be constructed (thrown
  straight from the retriever, cancelled in flight, and wrapped in `IOException`
  as `HttpDocumentRetriever` wraps it). `ConfigurationManager` raises the
  cancellation itself, so the line the plan originally called for was **dead
  code**. It is not in the fix, and plan D4 records the correction rather than
  hiding it.
- **The control** `A_reachable_realm_still_authorizes_the_same_token` passes with
  the identical harness and token, so the two outage assertions are attributable
  to the outage.

## 5. Not observed, and therefore not claimed

**A real MediaMTX receiving the 401.** The issue's second reason — that
MediaMTX's retry behaviour differs between 5xx and 401 — is still unobserved
end to end. What is established is the status this product now returns and that
the exception no longer escapes; what MediaMTX *does* with it is phase 5, and
wants a stack with Keycloak taken away under a live WHEP open.

**Latency (§IV): unaffected.** The hook runs at session setup, before media
flows; no leg of the budget measures a handshake.

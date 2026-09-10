# Spec 119 — a refusal when discovery is down

**Issue:** #2160
**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0047/0089 (`Result<T, Error>` + `ApiError`), 0050 (logging),
0105 (guards), 0049 (`CancellationToken`), 0141 (`Option<T>`, NRT),
0139/0140 (constitution §II), 0144 (the lane).

## Problem

`WhepAuthValidator.ValidateAsync` awaits `oidc.GetConfigurationAsync(...)`
directly. When the realm is unreachable that throws:

```
System.InvalidOperationException: IDX20803: Unable to obtain configuration from:
'http://keycloak:8080/realms/smart-sentinel-eye/.well-known/openid-configuration'
```

`InvalidOperationException` is neither `SecurityTokenException` nor
`ArgumentException`, so neither catch in `ValidateAsync` covers it. It propagates
out of the validator, out of `AuthorizeWhepCommandHandler.HandleAsync`, and out
of `AuthorizeWhep` (`StreamEndpoints.cs:347`), which has no catch — MediaMTX
receives a **500** where every other refusal on this hook is a typed `ApiError`.

**It is not an authorization defect.** Access is refused today and stays refused;
nothing is admitted. It is availability and contract shape: ADR-0089 says a
rejected request produces a typed error, and MediaMTX's behaviour on 5xx is not
its behaviour on 401.

## The investigation that decided the scope (step one)

The issue held one claim open in **both** directions: whether the nine REST APIs
behave the same way, because if the bearer pipeline also 500s the fix belongs in
one shared place. Settled by **measurement**, not by reading — a probe app
configured with the same three options `AuthenticationDefaults` sets
(`Authority`, `RequireHttpsMetadata = false`, `ValidAudiences`,
`MapInboundClaims = false`), authority pointed at `http://127.0.0.1:1`, a
`RequireAuthorization()` endpoint, `UseExceptionHandler()` as every API's
`Program.cs` has it:

```
MEASURED with a bearer token: 401 Unauthorized
  www-authenticate: Bearer error="invalid_token", error_description="The signature key was not found"
MEASURED with no token: 401 Unauthorized
  www-authenticate: Bearer
```

**The bearer pipeline answers 401. The defect is WHEP-local.** The mechanism,
measured alongside it against the same dead authority:

```
A. direct GetConfigurationAsync: THREW System.InvalidOperationException
   IDX20803: Unable to obtain configuration from: 'http://127.0.0.1:1/realms/…'
B. JsonWebTokenHandler.ValidateTokenAsync: IsValid=False, exception=SecurityTokenSignatureKeyNotFoundException
```

`JwtBearerHandler` does not fetch the configuration itself; it hands the
`ConfigurationManager` to `JsonWebTokenHandler`, which **catches** a retrieval
failure internally (IDX10261) and validates with no configuration — so the token
fails on a missing signing key and the pipeline challenges with 401. The WHEP
validator awaits the manager directly, on the legacy `JwtSecurityTokenHandler`
path, and therefore meets the exception the bearer pipeline never sees.

So the fix is local to `WhepAuthValidator`, and no shared change to
`ServiceDefaults` is warranted. Recorded here because "identically to the bearer
pipeline" was asserted for two specs without anyone dialling it.

## Scope

**In:** the WHEP hook answering a typed `ApiError` when the realm cannot be
reached, distinguishable from a token this product actually judged.

**Out — and this is the security boundary.** Nothing here widens what the hook
accepts. No token becomes acceptable that was not acceptable before: the
unreachable case turns a 500 into a 401, never into a 200.
`CreateParameters`, `MediaMtxAction`, and the existing members of
`AuthorizeWhepError` keep their semantics byte-for-byte.

**Also out:** the null-issuer half of spec 089's T-006. Measured already and
verified true — discovery with no `issuer`, and with `"issuer":""`, refuses every
token shape, because `ValidIssuers = [null]` matches no non-empty `iss` and an
absent `iss` is rejected before comparison. A guard there would be dead code.

## Functional requirements

- **FR-001** A failure to obtain the OIDC discovery document during
  `ValidateAsync` produces a typed `AuthorizeWhepError`, never an exception
  leaving the handler.
- **FR-002** That error is **distinguishable** from `WHEP_UNAUTHORIZED`: "we
  could not check" is a different fact from "we checked and refused", and an
  operator whose whole wall is 401ing must not be sent hunting a token.
- **FR-003** The status is **401**, for the reason in plan.md D1.
- **FR-004** A genuine cancellation stays a cancellation. `ConfigurationManager`
  wraps whatever the retriever threw — a cancelled fetch included — in the same
  IDX20803 `InvalidOperationException`, so the two arrive as one type and must be
  told apart before either is answered.
- **FR-005** The swallowed exception is logged once, at `Warning`, with the
  exception attached, through a `[LoggerMessage]` extension (ADR-0050). A
  swallowed exception with no trace is a review blocker, and IDX20803's message
  is the only thing that says *why* the realm was unreachable.

## Non-functional

- **Latency (§IV): not on the path.** The WHEP hook runs at session setup,
  before any media flows; none of the six legs measures a handshake. Confirmed,
  not assumed: the budget's first leg starts at camera → SFU, with the session
  already open.
- **Coverage:** Application ≥ 80 %, Domain ≥ 90 % (ADR-0065). The new Application
  code is a two-member enum and one branch, both covered by the tests below.

## Success criterion

With the metadata source throwing, `AuthorizeWhepCommandHandler` returns
`Result.Failure` carrying `HttpStatusCode.Unauthorized` and a code that is not
`WHEP_UNAUTHORIZED`; with the metadata source cancelled, the same call throws
`OperationCanceledException`. Both observed failing first (ADR-0139).

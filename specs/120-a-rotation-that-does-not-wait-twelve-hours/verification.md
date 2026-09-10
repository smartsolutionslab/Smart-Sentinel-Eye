# Verification — spec 120 (#2161)

## Phase 4a: the red, verbatim

`dotnet test tests/StreamDistribution.Infrastructure.Tests -c Release`, on
`f1fd71a9`'s tests against the unchanged `WhepAuthValidator`:

```
...WhepValidatorRotationTests.A_token_signed_with_the_rotated_key_is_authorized_on_a_later_call [FAIL]
  Error Message:
   Shouldly.ShouldAssertException : laterCall.HasValue
    should be
True
    but was
False
Additional Info:
    the WHEP hook was still refusing a token signed with the realm's current key
    seconds after meeting it. It never asked its ConfigurationManager to refresh,
    so the rotated JWKS is not read until the twelve-hour automatic interval
    expires. ... (#2161).
...WhepValidatorRotationTests.A_token_carrying_the_rotated_issuer_is_authorized_on_a_later_call [FAIL]
  Error Message:
   Shouldly.ShouldAssertException : laterCall.HasValue
    should be
True
    but was
False
Additional Info:
    the WHEP hook was still refusing a token carrying the issuer the realm's
    current discovery document reports. ... (#2161).
Failed!  - Failed:     2, Passed:    26, Skipped:     0, Total:    28
```

After `526213c9`:

```
Passed!  - Failed:     0, Passed:    28, Skipped:     0, Total:    28, Duration: 6 s
```

## The negative case, proven rather than asserted

The three `WhepValidatorRefreshRestraintTests` are green on arrival — today the
hook refreshes on nothing, so it trivially refreshes on nothing. A test that
cannot fail proves nothing, so it was **run against the implementation it exists
to forbid**: `RequestRefreshIfAStaleDocumentCouldExplain`'s condition replaced
by an unconditional one.

```
Failed ...A_token_minted_for_another_audience_does_not_provoke_a_discovery_refetch [333 ms]
Failed ...An_expired_token_does_not_provoke_a_discovery_refetch [129 ms]
Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3
```

Two of the three move. **The third does not, and the reason is worth recording**:
in Microsoft.IdentityModel 8.19.2 a malformed token raises
`SecurityTokenMalformedException`, which derives from `ArgumentException` (via
`SecurityTokenArgumentException`) and **not** from `SecurityTokenException`. It is
therefore caught by `ValidateAsync`'s second arm and never reaches the
discriminator at all. That was measured here, not read: under a blanket refresh
in the `SecurityTokenException` arm the malformed case stayed at one fetch.

So `A_malformed_token_does_not_provoke_a_discovery_refetch` guards the
`ArgumentException` arm rather than the discriminator. It is a real guard — a
future refresh added to that arm fails it — but it is not evidence about the
`is`-pattern, and should not be cited as such.

## Which failures trigger a refresh

Exception types **measured** through the real `JwtSecurityTokenHandler` with
`WhepAuthValidator.CreateParameters()`, not inferred:

| Case | Exception | Refresh |
|---|---|---|
| rotated key, new `kid` | `SecurityTokenSignatureKeyNotFoundException` (IDX10503) | **yes** |
| rotated issuer | `SecurityTokenInvalidIssuerException` (IDX10205) | **yes** |
| rotated key, **same** `kid` | `SecurityTokenInvalidSignatureException` (IDX10511) | no |
| wrong audience | `SecurityTokenInvalidAudienceException` (IDX10214) | no |
| expired | `SecurityTokenExpiredException` (IDX10223) | no |
| malformed | `SecurityTokenMalformedException` (IDX12741) — `ArgumentException` arm | no |

The same-`kid` row is the near miss and is deliberately excluded: the key
resolved, so the document the hook holds is the one the realm published. **It has
no test.** Recorded as a residual gap rather than covered, because the engineer
writing his own green test for his own discriminator proves less than saying
where the evidence stops.

## What the library actually does, and why the tests poll

8.19.2's `GetConfigurationAsync` performs a due refresh on a **background task**
and hands the *stale* document to the caller that triggered it. So calls 2 and 3
after a `RequestRefresh()` still see the pre-rotation document. The reds poll
within a bounded 5 s window instead of asserting on call 2. This cannot buy a
false green: with no refresh requested `_syncAfter` is twelve hours out and
repeated calls never re-enter the retriever — also measured.

## Not touched

`grep -rn "AutomaticRefreshInterval\|RefreshInterval" --include=*.cs src/ tests/`
returns only doc-comment prose. Neither interval is assigned anywhere in the
repository, before or after this change.

## Latency (§IV)

**Not on the event-to-overlay path.** The WHEP authorize hook runs once at
session setup, before media flows. No leg of the §IV table is affected. The
defect's cost is a session that never starts, which is availability, not latency.

## Build and tests

- `dotnet build -c Release` (whole solution): **0 Warning(s), 0 Error(s)**.
- `StreamDistribution.Infrastructure.Tests`: **28/28**.
- `StreamDistribution.Application.Tests`: **62/62**.
- `StreamDistribution.Domain.Tests`: **140/140**.
- `Architecture.Tests`: **360/360**.

Every pre-existing Whep test — issuer, audience, resolution, metadata-source —
passes **unmodified**.

## Phases 5 and 6

Phase 5 end-to-end against a live realm was **not** run: it needs an Aspire boot
plus a genuine Keycloak key rotation, and the recorded trap is that restarting
Keycloak keeps the old realm unless the volume is deleted. The unit level here
exercises the real `ConfigurationManager` on its shipped intervals, which is the
component whose behaviour was in doubt. Phase 6 is with the orchestrator.

## Composing with #2235 (spec 119)

Complementary. #2235 splits `ValidateAsync` into a config-fetch `try` and a
token-validation `try`; this change lives entirely in the second one's
`catch (SecurityTokenException)`, which #2235 keeps. Expect a textual conflict
there, resolved by keeping #2235's
`Result<WhepAuthSubject, WhepAuthFailure>.Failure(WhepAuthFailure.TokenRejected)`
return and inserting the `RequestRefreshIfAStaleDocumentCouldExplain(exception)`
call above it. The private method itself is untouched by that merge.

# Spec 120 — a rotation that does not wait twelve hours

**Issue:** #2161
**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0139 (red first), 0144 (autonomous lane), 0052/0053/0054 (xUnit +
Shouldly, sentence-style names, hand-written fakes), 0105 (`Ensure.That`),
0049 (`CancellationToken` last), 0050 (`[LoggerMessage]`), 0036 (smallest
change).

## Problem

`src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs` validates the
bearer token MediaMTX forwards on its external-auth hook. Issuer and signing
keys come from the realm's OIDC discovery document, cached by a
`ConfigurationManager<OpenIdConnectConfiguration>` built in `MetadataSourceFor`.

Measured on the manager that constructor actually builds:

```
[PROBE5] DefaultAutomatic=12:00:00 DefaultRefresh=00:05:00
[PROBE5] instance   Automatic=12:00:00 Refresh=00:05:00
```

`JwtBearerHandler` — the pipeline the nine REST APIs use — sets
`RefreshOnIssuerKeyNotFound` (on by default) and calls
`ConfigurationManager.RequestRefresh()` when a token fails with
signature-key-not-found. Those nine therefore re-read JWKS within the
**5-minute** `RefreshInterval` floor.

**`WhepAuthValidator` never calls `RequestRefresh()`.** It waits out the
**12-hour** `AutomaticRefreshInterval`.

### The failure path

An operator rotates the realm's signing key, or Keycloak rotates it on restart.
The nine REST APIs recover on their next request. **The wall keeps 401ing for up
to 12 hours** — every WHEP open refused while every management page still loads.

This is an **availability** defect, not a bypass. A stale accepted issuer is
still the realm's own previous issuer, signed by the realm's own key; the cost
is an outage window, not an escalation.

### What #2095 changed about it

Pre-existing for *signing keys*, which already came from discovery. Spec 089
made the **issuer** come from discovery too, so the same staleness now extends
to a second field: change `KC_HOSTNAME` and the hook refuses every token the
realm mints for the next twelve hours.

## What must change

`ValidateAsync` calls `RequestRefresh()` on its configuration manager when — and
only when — validation failed in a way a fresher document could cure.

## What must not change

- **Neither interval.** Shortening `AutomaticRefreshInterval` trades a 12-hour
  window for constant polling and still does not fix the case the bearer
  pipeline actually handles, which is *refresh on a failure that a refresh could
  cure*. The 5-minute `RefreshInterval` floor is what stops a token flood from
  hammering Keycloak; it stays.
- **Nothing else becomes acceptable.** A rotated key being accepted after a
  refresh is the point. An expired token, a wrong audience, a foreign issuer and
  a malformed token stay refused, before and after.
- **No in-request retry.** `JwtBearerHandler` requests the refresh and lets the
  *next* request succeed; the first caller after a rotation still gets its 401.
  Matching that is the parity this hook is measured against (spec 071's rule),
  and it keeps the change to one call.

## Discrimination, and why the negative case is the load-bearing one

Refreshing on *every* validation failure is indistinguishable from the fix on
the rotation case alone, and it is the flood the floor exists to prevent:
`/streams/authorize` is `AllowAnonymous` and nothing rate-limits it, so a token
storm becomes a refresh storm against the realm at exactly the moment it is
already under load.

So the acceptance criterion has two halves:

| Failure | A fresh document could cure it? | Refresh |
|---|---|---|
| `SecurityTokenSignatureKeyNotFoundException` — `kid` absent from cached JWKS | yes: the realm rotated | **requested** |
| `SecurityTokenInvalidIssuerException` — `iss` is not the cached `issuer` | yes: the realm's issuer moved | **requested** |
| `SecurityTokenExpiredException` | no: the token is old, the document is fine | not requested |
| `SecurityTokenInvalidAudienceException` | no | not requested |
| `SecurityTokenInvalidSignatureException` — key found, signature wrong | no: the `kid` resolved | not requested |
| `ArgumentException` — malformed token | no | not requested |

The first two rows are the same two rows `JwtBearerHandler` would produce for
this hook, plus the issuer field spec 089 added.

## Latency (§IV)

**Not on the event-to-overlay path.** The WHEP authorize hook runs once at
session setup, before any media flows; no leg of the table in constitution §IV
is touched. What the defect costs is a session that never starts at all, which
is an availability figure, not a latency one.

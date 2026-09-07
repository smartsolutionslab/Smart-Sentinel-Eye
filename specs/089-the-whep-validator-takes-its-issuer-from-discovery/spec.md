# Spec 089 — The WHEP validator takes its issuer from discovery

**Issue:** #2095 — *Four remaining divergences between the WHEP validator and
the bearer pipeline, one of which can dark a wall*
**Branch:** `fix/2095-the-whep-validator-takes-its-issuer-from-discovery`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0007 + ADR-0008 (Keycloak per fab, OIDC — the decision this spec
implements, `docs/adr/0000-initial-decisions.md:21`), ADR-0023 as amended by
ADR-0130 (authorization by scopes; the k8s publisher has never been run, which
is why this is a landmine and not an outage), ADR-0100 (the Mosquitto go-auth
plugin — the *third* JWT validation site, see §"What the issue gets wrong"),
ADR-0103 (integration tests are Aspire-fixture-only; no Testcontainers — the
reason the red test is a unit test), ADR-0105 (`Ensure.That` guards), ADR-0036
(smallest change; no speculative generality), ADR-0052 (xUnit + Shouldly),
ADR-0084 (code metrics), ADR-0109 (parallel markers), ADR-0139 + ADR-0144
(phase 4a has two colours and no exemption).

**Constitution:** §Testing — new behaviour is observed failing first;
behaviour-preserving change is captured green first and must pass unmodified.
Both obligations are live in this spec, on different parts of it, and §"Phase
4a" below assigns them per part.

**No new ADR is required, and none is written.** See §"The ADR verdict".

---

## The ADR verdict

**Not an ADR. Phases 1–3 proceed.**

ADR-0007 locks *Keycloak per fab, self-hosted OIDC*; ADR-0008 locks the realm
shape; ADR-0023/0130 lock that authorization is decided from scopes carried on
those tokens. Together they already decide that a token is validated against
the fab's realm **through its OIDC discovery document** — that is what "OIDC"
means, and it is what nine of the ten validating surfaces already do, by way of
the framework rather than by way of a decision anyone wrote down.

*Which string a validator compares `iss` against* is the implementation of that
decision, not a new one. The test for an ADR is whether a real alternative is
being chosen between, with consequences that outlive the change. Here there is
one correct source (the discovery document's `issuer`) and one incorrect one
(the URL the service happens to dial), and the incorrect one is a defect we are
removing. Nothing is being decided; something is being corrected.

Saying so explicitly because the issue asks: **#2095 sitting next to a recorded
trap and a wall-darkening failure raises its severity, not its architectural
altitude.** A high-severity implementation defect is still an implementation
defect. ADR-0144 blocks the lane from writing an ADR in any case, and this spec
does not need the exemption.

---

## The issue as filed, and what survives contact with the repository

Six of the previous six issues in this batch had a false premise, so every
factual claim in #2095 was re-derived here rather than read. **The
field-by-field table holds exactly. One supporting claim does not, and it does
not fail in the safe direction.**

### The table, measured today

Not the issue's copy. Produced by a throwaway xUnit fact added to
`tests/StreamDistribution.Infrastructure.Tests/`, which references both
`SmartSentinelEye.ServiceDefaults` and
`SmartSentinelEye.StreamDistribution.Infrastructure`, so both objects were
built through their real construction paths and dumped side by side. Run at
`45be837d` (`origin/develop`, 2026-09-07). The file was deleted after the run
and is not part of this branch.

| Field | BEARER | WHEP | |
|---|---|---|---|
| `ValidateIssuer` | `True` | `True` | same |
| **`ValidIssuer`** | **`<null>`** | **`https://…/realms/smart-sentinel-eye`** | **differs** |
| **`ValidIssuers`** | **`<null>`** | **`<null>`** | same value, different meaning — see below |
| `ValidateAudience` | `True` | `True` | same |
| `ValidAudience` | `<null>` | `<null>` | same |
| `ValidAudiences` | `[smart-sentinel-eye-api]` | `[smart-sentinel-eye-api]` | same (spec 071 / #2090) |
| `ValidateLifetime` | `True` | `True` | same |
| `RequireExpirationTime` | `True` | `True` | same |
| `RequireSignedTokens` | `True` | `True` | same |
| `ClockSkew` | `00:05:00` | `00:05:00` | same |
| **`ValidateIssuerSigningKey`** | **`False`** | **`True`** | **differs** |
| `ValidateTokenReplay` | `False` | `False` | same |
| **`NameClaimType`** | **`…/identity/claims/name`** | **`preferred_username`** | **differs** |
| `RoleClaimType` | `…/identity/claims/role` | `…/identity/claims/role` | same |
| `ValidAlgorithms` | `<null>` | `<null>` | same |
| `ValidTypes` | `<null>` | `<null>` | same |
| `IssuerSigningKeys` | `<null>` (filled at request time) | `<null>` (filled in `ValidateAsync`) | same |
| `IssuerSigningKeyResolver` / `IssuerValidator` / `AudienceValidator` / `SignatureValidator` | all `<null>` | all `<null>` | same |
| `MapInboundClaims` | `False` (options) | `False` (handler, ctor) | same |
| **Handler** | **`JsonWebTokenHandler`** | **`JwtSecurityTokenHandler`** | **differs** |

Four divergences, exactly the four the issue names, with exactly the values it
reports. **The issue's measurement is sound.**

### The load-bearing claim: the bearer pipeline fills the issuer from discovery

**Confirmed.** `AuthenticationDefaults.AddBearerAuthentication`
(`src/ServiceDefaults/AuthenticationDefaults.cs:52-72`) sets
`options.Authority` and never touches `ValidIssuer`/`ValidIssuers` — the dump
above shows both `<null>`. The fill happens in the framework, per-request, in
`JwtBearerHandler.SetupTokenValidationParametersAsync`
(`dotnet/aspnetcore` v10.0.0, the pinned
`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.11):

```csharp
if (Options.ConfigurationManager != null)
{
    var configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
    var issuers = new[] { configuration.Issuer };
    tokenValidationParameters.ValidIssuers =
        (tokenValidationParameters.ValidIssuers == null ? issuers
         : tokenValidationParameters.ValidIssuers.Concat(issuers));
    tokenValidationParameters.IssuerSigningKeys = …
}
```

Two consequences the issue does not draw out, and both matter to the fix:

1. It **concatenates**. Because both start `null`, the nine APIs today accept
   *exactly one* issuer: the discovery document's. Any value the WHEP side
   keeps in `ValidIssuer` alongside the discovery issuer would make the hook
   **more** permissive than the nine APIs — the inverse of spec 071's rule. So
   the correction is not "add discovery"; it is "take the issuer from discovery
   **instead of** the configured URL".
2. The `ValidIssuers = <null>` row is the same value on both sides for
   *opposite reasons*: on the bearer side null means "the handler will fill
   this"; on the WHEP side it means "nothing will". The table's "same" is
   therefore misleading in the one row where the defect lives — which is
   roughly how a comment claiming parity survived spec 071.

### What the issue gets wrong

**`ValidateIssuerSigningKey` is dormant, not inert — and its dormancy is a
second copy of the same asymmetry.**

The issue says Keycloak's JWKS "yields `JsonWebKey`/`RsaSecurityKey`, and
`Validators.ValidateIssuerSecurityKey` only date-checks an `X509SecurityKey`".
The second half is right. The first half is wrong.

- `Validators.ValidateIssuerSecurityKey` in `Microsoft.IdentityModel.Tokens`
  8.19.2 (the version resolved into this solution) calls
  `ValidateIssuerSigningKeyLifeTime`, whose entire body is guarded by
  `securityKey as X509SecurityKey` — confirmed against the 8.19.2 source. So
  far, as filed.
- **But Keycloak publishes `x5c`.** A throwaway
  `quay.io/keycloak/keycloak:26.6` container was booted, its
  `/realms/master/protocol/openid-connect/certs` fetched, and every RSA key in
  the set carries `x5c`, `x5t` and `x5t#S256` alongside `n` and `e`. Keycloak's
  `JWKSServerUtils.getRealmJwks` passes the certificate chain to
  `AbstractJWKBuilder.rsa(...)`, which emits `x5c` whenever
  `certificates != null && !certificates.isEmpty()`.
- `JsonWebKeySet.GetSigningKeys()` in 8.19.2 tests `IsValidX509SecurityKey`
  **and** `IsValidRsaSecurityKey` independently and adds **both** — so
  `configuration.SigningKeys` contains an `X509SecurityKey` for the same `kid`,
  and the X509 conversion is attempted first.

So the check runs. It date-checks the realm's signing certificate. The decoded
certificate from that probe is `CN=master`, `notBefore=Sep 7 09:59:50 2026`,
`notAfter=Sep 7 10:01:30 2036` — a self-signed ten-year certificate generated at
realm creation. The realm file `src/AppHost/Realms/smart-sentinel-eye-realm.json`
pins **no** `org.keycloak.keys.KeyProvider` component and **no** `certificate`
attribute (both greps return zero), so dev, CI and any future fab get a freshly
generated ten-year certificate rather than an inherited old one. Nothing is
close to expiry, and nothing is at risk today.

**Why it is worth recording anyway.** If a realm's signing certificate ever does
lapse — or if `notBefore` lands ahead of a skewed clock — WHEP 401s and the nine
REST APIs do not, because they have `ValidateIssuerSigningKey = False`. That is
the *same asymmetry the issue is about*, on a different field, with a ten-year
fuse instead of a deployment-shaped one. The comment this spec writes must say
that, not "harmless".

**There is a third JWT validation site, not two.** The Mosquitto go-auth plugin
(ADR-0100) at `src/AppHost/mosquitto/plugin/jwt_auth.go:172` validates the
issuer too:

```go
if issuer, _ := claims["iss"].(string); !strings.HasSuffix(issuer, realmPath) {
    return C.MOSQ_ERR_AUTH
}
```

A **suffix match on the realm path**, so it is hostname-agnostic and therefore
*not* exposed to the ingress trap this spec fixes — and correspondingly looser
than either .NET validator, since any host serving a path ending
`/realms/smart-sentinel-eye` satisfies it. It also never reads `aud` (#2085).
Different surface, different plugin, different owner; spec 071 already scoped
#2085 out on that basis and this spec does the same. Recorded so the next parity
reviewer starts from three sites rather than rediscovering the third.

**#2093 is closed and delivered.** The issue lists it under "Related" as though
the gap were open. `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorAudienceTests.cs`
is on `develop` and drives the real `ValidateAsync` against stubbed OIDC
metadata and a generated RSA key — no Docker, no network, no realm. **That file
is the harness this spec's red test extends.** It does not block; it is the
reason the red is cheap.

---

## What is broken

`WhepAuthValidator.CreateParameters`
(`src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs:45-56`) sets
`ValidIssuer = authority`, where `authority` is derived in
`StreamDistributionInfrastructureModule.BindWhepAuthOptions`
(`:181-197`) from `ConnectionStrings:keycloak` — **the URL the service dials
Keycloak on**.

`ValidateAsync` (`:58-90`) already awaits
`oidc.GetConfigurationAsync(cancellationToken)` on **every call**, to take
`configuration.SigningKeys`. The discovery document — the object holding the
authoritative `issuer` — is therefore already in hand, one line above the clone
that is missing it.

The two strings are equal only while Keycloak's `iss` equals the URL the service
dials it on. Behind an ingress with `KC_HOSTNAME=https://keycloak.fab.example`
and `ConnectionStrings:keycloak=http://keycloak:8080`, tokens carry
`iss=https://keycloak.fab.example/realms/…`; the nine REST APIs keep working
because they took the issuer from discovery, and **every WHEP open 401s, so the
whole wall goes dark while every management page still loads.**

Fails closed — no bypass. Not live: the k8s publisher has never been run
(ADR-0130). This is the recorded "Keycloak issuer must match the proxied port"
trap with exactly one of the three validators exposed to it.

---

## Locked choices

| Concern | Choice | Source |
|---|---|---|
| Where the issuer comes from | The realm's **OIDC discovery document**, read at validation time from the `ConfigurationManager` the validator already holds | ADR-0007/0008; parity with `JwtBearerHandler` |
| How the configured URL is used | To **address discovery**, and for nothing else | this spec, D1 |
| Parity direction | Both sides derive correctly. **Never** by relaxing one — `ValidateIssuer = false`, or adding the dialled URL as a second accepted issuer, are both forbidden | issue #2095 constraint; spec 071's rule |
| `ValidateIssuerSigningKey` | Stays `true` on WHEP. Commented as deliberately stricter, with the realm-certificate asymmetry named | D2 |
| Token handler | Stays `JwtSecurityTokenHandler` on WHEP. A view is recorded; the migration is a separate slice | D4 |
| Test framework | xUnit + Shouldly, hand-written stubs | ADR-0052, ADR-0054 |
| Integration tests | Aspire fixture only, no Testcontainers | ADR-0103 |
| Guards | `Ensure.That` | ADR-0105 |

---

## Decisions

### D1 — the WHEP hook takes its issuer from the discovery document

`CreateParameters` stops setting `ValidIssuer`, and `ValidateAsync` sets
`validationParameters.ValidIssuers = [configuration.Issuer]` on the clone,
immediately beside the `IssuerSigningKeys` assignment that already reads the
same object. This is `JwtBearerHandler`'s behaviour, transcribed.

The two objections that would make "record the constraint instead" legitimate
are both absent, and were checked rather than assumed:

- **"The hook has no discovery client."** It has one. `oidc` is a
  `ConfigurationManager<OpenIdConnectConfiguration>` built in the constructor
  (`:35-38`).
- **"It cannot afford the fetch on the hot path."** The fetch is already on the
  hot path. `ValidateAsync:62` awaits it unconditionally, today, and
  `ConfigurationManager` serves it from cache between refresh intervals. The
  change adds **zero** I/O and zero allocations of consequence — it reads one
  more property off an object already awaited.

`ValidateIssuer` stays `true` throughout. Nothing is relaxed.

`CreateParameters` becomes parameterless. Its `authority` argument exists only
to seed `ValidIssuer`; leaving it unused would trip SonarAnalyzer S1172 and,
worse, would leave an obvious slot for someone to re-hardcode the URL into.
The four `WhepAudienceTests` call sites are updated mechanically — **call sites
only; no assertion in that file is touched**, which is the line between updating
a test and weakening one.

Failure stays closed. If a discovery document ever lacks `issuer`,
`ValidIssuers` carries a null, `Validators.ValidateIssuer` throws, and the hook
answers 401 — the same way the bearer pipeline fails, and the reason no
defensive branch is added (ADR-0036: no drive-by error handling).

### D2 — `ValidateIssuerSigningKey` stays `true`, and the comment says what it does

Parity by relaxation is the forbidden shape. Setting it `false` to match the
framework default would be exactly that, and would also throw away a real check.
It stays `true` and gains a comment stating (a) that it is deliberately stricter
than the bearer pipeline, (b) that against Keycloak it date-checks the realm's
signing certificate because the JWKS carries `x5c`, and (c) that this is the
second copy of the asymmetry #2095 is about — WHEP would 401 on a lapsed realm
certificate while the nine APIs would not.

The correct long-term resolution is the *nine APIs* becoming stricter, not this
hook becoming looser. That is a different surface and a different blast radius.
**Out of scope; follow-up issue recommended.**

### D3 — `NameClaimType` stays `preferred_username`, recorded as inert

`ValidateAsync` reads `sub` (`:68`) and `scope` (`:74`) through `FindFirst` and
never touches `Identity.Name`. Confirmed by reading the method: there is no
other claim access in it. The setting has no effect on any decision this
validator makes. It gains a comment saying so, so the next parity reviewer does
not spend the round re-deriving it. It is not changed — changing it would be a
behaviour edit made for tidiness, with no test able to observe it either way.

### D4 — the handler divergence is recorded, and its migration is a separate slice

A view, as the issue asks for. **`JsonWebTokenHandler` should eventually back
both**, for three reasons: it is what the bearer pipeline uses; Microsoft
positions `JwtSecurityTokenHandler` as the legacy path; and the WHEP validator's
`catch (ArgumentException)` block (`:83-89`) exists *only* to absorb
`JwtSecurityTokenHandler`'s habit of surfacing malformed-token paths as
`ArgumentException` — a wart that disappears with the handler.

It is not done here. `JsonWebTokenHandler.ValidateTokenAsync` returns a
`TokenValidationResult` instead of throwing, so the migration rewrites the whole
control flow of `ValidateAsync`, and proving that the `ArgumentException` catch
is genuinely unnecessary afterwards needs its own adversarial pass over
malformed inputs. Bundling it here would put two behaviour changes in one slice
and make the issuer red harder to read. **Out of scope; follow-up issue
recommended.** The comment records the view and points at the reasoning.

### D5 — the authority derivation stays duplicated

`BindWhepAuthOptions` and `AddBearerAuthentication` still derive the Keycloak
base URL the same way, in two places. Spec 071 D5 scoped that out and it stays
scoped out — and after D1 it matters less than it did, because the duplicated
string no longer decides anything a token is checked against. It addresses
discovery on both sides, and a discovery URL that is wrong fails loudly and
identically for both.

---

## User Scenarios & Testing

### US-1 (P1) — a wall behind an ingress opens its streams

**As** an operator on a kiosk in a fab whose Keycloak sits behind an ingress,
**I want** WHEP stream opens to be authorized by the same issuer the management
APIs accept, **so that** the wall does not go dark while every management page
still loads.

Independently shippable: this story alone closes the wall-darkening defect and
can merge without US-2.

```gherkin
Feature: the WHEP hook validates the issuer the realm actually mints

  Background:
    Given the WHEP validator is configured with an authority URL it dials Keycloak on
    And the realm's discovery document is reachable at that URL

  # Happy path — the deployment shape we run today
  Scenario: the dialled URL and the realm's issuer agree
    Given the discovery document's "issuer" equals the dialled authority URL
    And a token signed by the realm, minted for "smart-sentinel-eye-api",
        carrying that issuer
    When MediaMTX forwards the token to POST /streams/authorize
    Then the hook authorizes the open
    And the subject's "sub" and "scope" claims are read from the token

  # The divergence — RED today, and the reason this spec exists
  Scenario: Keycloak is behind an ingress, so its issuer is not the dialled URL
    Given the WHEP validator dials "http://keycloak:8080/realms/smart-sentinel-eye"
    And the discovery document's "issuer" is
        "https://keycloak.fab.example/realms/smart-sentinel-eye"
    And a token signed by the realm carrying that ingress issuer
    When MediaMTX forwards the token to POST /streams/authorize
    Then the hook authorizes the open
    # Today it refuses, and every WHEP open in the fab 401s while the nine
    # REST APIs keep working, because they took their issuer from discovery.

  # The anti-relaxation guard — the fix must not be bought by widening
  Scenario: a token minted by something other than the realm is still refused
    Given the WHEP validator dials "http://keycloak:8080/realms/smart-sentinel-eye"
    And the discovery document's "issuer" is
        "https://keycloak.fab.example/realms/smart-sentinel-eye"
    And a token signed by the realm's key but carrying
        "http://keycloak:8080/realms/smart-sentinel-eye" as its issuer
    When MediaMTX forwards the token to POST /streams/authorize
    Then the hook refuses the open
    # The dialled URL is not an accepted issuer. It addresses discovery and
    # nothing else. This scenario fails if the fix is implemented by keeping
    # the configured URL as an additional ValidIssuer, and fails if it is
    # implemented by turning ValidateIssuer off.

  # Bad request
  Scenario: a malformed token is refused
    When MediaMTX forwards "this-is-not-a-jwt" to POST /streams/authorize
    Then the hook answers 401

  # Auth
  Scenario: no token at all is refused
    When MediaMTX forwards a null token to POST /streams/authorize
    Then the hook answers 401
```

### US-2 (P2) — the next parity reviewer starts where this one finished

**As** the next engineer comparing the WHEP hook to the bearer pipeline,
**I want** the three surviving divergences to say at the site why they survive,
**so that** I do not spend a review round re-deriving what has now been derived
twice.

Independently shippable: comments only, in one file, no behaviour change.

```gherkin
Feature: the surviving divergences explain themselves

  Scenario: the stricter signing-key check names what it checks
    When a reader opens WhepAuthValidator.CreateParameters
    Then ValidateIssuerSigningKey = true carries a comment saying it is
         deliberately stricter than the bearer pipeline
    And the comment says it date-checks the realm's signing certificate,
        because Keycloak's JWKS carries x5c
    And the comment names the asymmetry: WHEP 401s on a lapsed realm
        certificate where the nine REST APIs would not

  Scenario: the name-claim setting is marked inert
    When a reader opens WhepAuthValidator.CreateParameters
    Then NameClaimType carries a comment saying ValidateAsync reads "sub" and
         "scope" directly and never touches Identity.Name

  Scenario: the handler choice records a view rather than an accident
    When a reader opens WhepAuthValidator
    Then the JwtSecurityTokenHandler field carries a comment saying
         JsonWebTokenHandler should eventually back both sides
    And the comment names the ArgumentException catch as the wart that
        migration removes
```

---

## Functional requirements

- **FR-001** — `WhepAuthValidator` MUST validate the issuer against the value
  of the `issuer` member of the realm's OIDC discovery document, read from the
  `ConfigurationManager` already held by the instance.
- **FR-002** — `WhepAuthValidator` MUST NOT accept the configured authority URL
  as an issuer. That URL addresses discovery and nothing else.
- **FR-003** — `ValidateIssuer` MUST remain `true`. Parity MUST NOT be reached
  by relaxing any check on either side.
- **FR-004** — The change MUST add no network round-trip: the discovery
  configuration is already awaited on every `ValidateAsync` call.
- **FR-005** — A test MUST drive the real `ValidateAsync` (not
  `CreateParameters`) for both the acceptance and the refusal in FR-001/FR-002,
  because a factory-level assertion cannot see the clone the handler receives
  (#2093).
- **FR-006** — A factory-level parity test MUST compare WHEP's
  `ValidIssuer`/`ValidIssuers` against the bearer pipeline's, both sides read
  rather than asserted as constants, so a future edit that re-hardcodes the
  authority fails the build. Its doc comment MUST say it binds the factory and
  point at the runtime tests, per the pairing #2093 established.
- **FR-007** — `ValidateIssuerSigningKey`, `NameClaimType` and the handler
  choice MUST each carry a comment at the site stating why the divergence
  survives; the signing-key comment MUST NOT describe the check as harmless or
  inert.
- **FR-008** — No existing assertion in `WhepAudienceTests`,
  `WhepValidatorAudienceTests`, `BearerAudienceTests` or
  `WhepAuthIntegrationTests` may be modified. Call-site updates forced by
  FR-001's signature change are permitted; assertion edits are not.

---

## Independent end-to-end test procedure

Runnable by someone who did not write the change, without Docker for steps 1–3.

1. **Observe the red.** On `origin/develop`, add the new issuer tests and run
   ```
   dotnet test tests/StreamDistribution.Infrastructure.Tests --filter "FullyQualifiedName~WhepValidatorIssuer"
   ```
   The ingress-shaped acceptance case fails: `ValidateAsync` returns `None`
   because `ValidIssuer` is the dialled URL. Quote that failure in the PR.
2. **Apply the change and re-run the same command.** All cases pass, including
   the anti-relaxation refusal.
3. **Prove the guard by counterfactual.** Three weakenings, each reverted
   immediately. **Corrected at phase 4b against the observed runs — the first of
   the two originally written here predicted the wrong failure.**
   - Re-apply `ValidIssuer = authority` in `CreateParameters` while **keeping**
     the discovery fill: the *refusal* case fails, and so does the factory
     parity case — not the acceptance case. `Validators.ValidateIssuer` accepts
     the union of `ValidIssuer` and `ValidIssuers`, so the realm's issuer is
     still accepted and what is caught is exactly the forbidden "in addition to"
     shape.
   - Remove the discovery fill as well, i.e. the full pre-fix shape: the
     *acceptance* case fails, as the phase-4a red already showed.
   - Set `ValidateIssuer = false`: the *refusal* case and the unrelated-issuer
     control both fail.

   Three weakenings, three distinct failure sets, is the evidence the guards are
   not decorative — and the first two also show that "put the URL back" and
   "never took it from discovery" are separately caught.
4. **Non-regression, with Docker.**
   ```
   dotnet test tests/Integration.Tests --filter "FullyQualifiedName~WhepAuthIntegrationTests"
   ```
   All five existing cases stay green. In the Aspire fixture the discovery
   issuer and the dialled authority are the same string (tokens are minted from
   the proxied endpoint — the recorded trap), so the change is a no-op there,
   which is the point: it is correct in the shape we run and *also* correct in
   the shape we do not.
5. **Read the comments.** `git show -- src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs`
   and check each of the three against §US-2's scenarios.

**Why the ingress shape is not tested against the Aspire fixture.** It cannot be,
without a second hostname. The fixture's Keycloak is reached through the Aspire
proxy and its `iss` is that same proxied endpoint by construction; provoking a
mismatch means changing `KC_HOSTNAME` in `AppHost`, which every other test in
`AspireCollection` shares and which would break token minting for all of them.
The unit harness expresses the divergence exactly and costs nothing (see
plan.md §"How the red is expressed").

---

## Latency budget

**N/A to the event→overlay budget** (constitution §IV). `POST /streams/authorize`
is on the WHEP open path — click-to-first-frame, spec 077 — not on any of the six
legs from event arrival to overlay render.

Impact on click-to-first-frame: **zero added I/O.**
`oidc.GetConfigurationAsync` is already awaited on every call at
`WhepAuthValidator.cs:62`; the change reads one additional property off the
object that await already returned. No new fetch, no new cache, no new lock.

---

## Out of scope

- **Migrating WHEP to `JsonWebTokenHandler`** (D4). A control-flow rewrite plus
  an adversarial pass over malformed inputs. Follow-up issue recommended.
- **Making the nine REST APIs set `ValidateIssuerSigningKey = true`** (D2). The
  correct resolution of that asymmetry, on nine surfaces instead of one.
  Follow-up issue recommended.
- **`WhepAuthValidator` never calls `ConfigurationManager.RequestRefresh()`.**
  `JwtBearerHandler` sets `RefreshOnIssuerKeyNotFound` (default on) and calls
  `RequestRefresh()` on a signature-key-not-found failure, so the bearer
  pipeline re-reads discovery within its 5-minute refresh floor
  (measured: `AutomaticRefreshInterval = 12:00:00`,
  `RefreshInterval = 00:05:00`); WHEP waits out the full 12-hour automatic
  interval instead. Pre-existing for signing keys; this spec's discovery-fill
  extends the same staleness to the issuer, so a realm signing-key rotation
  now also leaves WHEP 401ing for up to 12 hours after the nine REST APIs have
  recovered. Filed as **#2161**.
- **Unifying the authority derivation** between `BindWhepAuthOptions` and
  `AddBearerAuthentication` (spec 071 D5, restated as D5).
- **The Mosquitto plugin's suffix-match issuer check** and its missing `aud`
  (#2085). Different surface, different plugin, ADR-0100.
- **Any change to `ValidateIssuer`, `ValidateAudience` or `RequireSignedTokens`
  on either side.** Named explicitly because relaxation is the failure mode this
  spec is most exposed to.
- **A source-scanning guard against a fourth hand-rolled
  `TokenValidationParameters`.** Speculative (ADR-0036), and spec 071 already
  declined it.

---

## Phase 4a — colour per part

Declared here so it is not resolved by taste at phase 4 (ADR-0144: ambiguity
resolves to red).

| Part | Colour | How it is discharged |
|---|---|---|
| **US-1 / FR-001–FR-006** — the issuer | **RED** | New behaviour. The ingress-shaped acceptance case must be observed failing on `develop` and the failure quoted in the PR. See plan.md §"How the red is expressed". |
| **US-2 / FR-007** — the three comments | **CHARACTERISATION, observed GREEN** | Behaviour-preserving in the strictest sense: no compiled behaviour changes at all. The covering tests already exist — `WhepValidatorAudienceTests` (2 cases, real `ValidateAsync`, real signing key, so the `ValidateIssuerSigningKey` path is genuinely exercised) and `WhepAudienceTests` (4 cases). They are captured passing **before** the comment edits and must pass **unmodified** afterwards. **No test is written for a comment**, and 4a is not skipped: the green capture *is* the discharge, and it is the honest one, because the claim being made is precisely "nothing changed". |

**FR-008 is the guard on both colours.** If an existing assertion has to move to
make the change pass, that is evidence the behaviour moved further than intended
— block, do not adjust.

---

## Success criteria

- **SC-001** — A token whose `iss` is the discovery document's `issuer` and
  differs from the dialled authority URL is authorized by the WHEP hook.
  Observed failing first.
- **SC-002** — A token whose `iss` is the dialled authority URL, when discovery
  says otherwise, is refused.
- **SC-003** — `ValidateIssuer` is `true` and `ValidateAudience` is `true` on
  both validators after the change, read from the objects rather than asserted
  as literals.
- **SC-004** — `WhepAuthIntegrationTests`' five existing cases pass unchanged
  against the Aspire fixture.
- **SC-005** — Each of the three surviving divergences carries a comment
  matching §US-2, and the signing-key comment does not call the check harmless.
- **SC-006** — No assertion in the four named test files is modified.

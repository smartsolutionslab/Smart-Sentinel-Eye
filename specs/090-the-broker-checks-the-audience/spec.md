# Spec 090 — The broker checks the audience

**Issue:** #2085
**ADRs:** ADR-0100 (the plugin and its check set), ADR-0007 / ADR-0008
(Keycloak, per-fab realm), ADR-0103 (Aspire fixture, no Testcontainers),
ADR-0144 (the lane, and phase 4a's two colours).
**Branch:** `fix/2085-the-broker-checks-the-audience`

---

## The issue as filed, and what survives contact with the repository

Six claims were checked against the tree at `45be837d`. **Four need
correcting, and one of the corrections changes what the work is.**

### 1. It is not the go-auth plugin — it is ours

The issue, ADR-0100's title, `mosquitto.conf:6` and `AppHost.cs:205` all
say "go-auth". `iegomez/mosquitto-go-auth` is **not what runs**. ADR-0100's
2026-05-31 addendum records that plugin being *rejected* — it verifies
signatures with the configured secret as raw HMAC bytes, has no asymmetric
path and no JWKS option, and Keycloak realm tokens are RS256 — and records
what shipped instead: a custom ~180-line Mosquitto v5 plugin at
`src/AppHost/mosquitto/plugin/jwt_auth.go`, module
`smartsentineleye.local/mosquitto-jwt-auth`, built into the image by
`src/AppHost/mosquitto/Dockerfile` stage 2.

**This is the correction that decides the issue.** "Configure the plugin to
check `aud`, *if it supports it*" is a vendor-capability question about a
vendor we do not use. The real question is whether
`github.com/golang-jwt/jwt/v5 v5.3.1` — pinned in
`plugin/go.mod`/`go.sum` — offers an audience option. It does, and §*The
capability, established* below is the evidence.

### 2. "That is undocumented" — false

The check set is documented in three places:

- **ADR-0100**, 2026-05-31 addendum: *"It enforces `azp == username` (the
  device's Keycloak client_id) and that the issuer is our realm, plus
  `exp`."*
- **`docs/design/scenario-simulator-m2.md:68`**, including the suffix
  semantics: *"plugin requires **`azp == MQTT username`** and `iss` ends in
  `/realms/smart-sentinel-eye`"*.
- **`jwt_auth.go:1-21`**, the file header.

What is genuinely unwritten is only the *absence of an `aud` check as a
deliberate choice*. That is a far narrower claim than the one filed, and it
is worth being exact about, because "undocumented" is the premise under
which options 2 and 3 (write it down / leave it) look like work at all.

### 3. "#91 turns on `ValidateAudience`" — already past tense

**#91 closed 2026-09-05.** It is on `develop`:
`AuthenticationDefaults.cs:66` sets
`options.TokenValidationParameters.ValidAudiences = [ApiAudience]`, and
`ApiAudience` is `"smart-sentinel-eye-api"` (`:134`). MQTT is not *about to
become* the exception; **it already is one**, and has been for two days.

This removes the sequencing question the issue poses and replaces it with a
narrower one about *population* — §*The outage surface* below.

### 4. "the one authenticated surface" — there are three sites, not two

The issue counts *the nine HTTP APIs* and *MQTT*. It misses the WHEP
external-auth hook, which is a **separate validator with its own
`TokenValidationParameters`**: `WhepAuthValidator.CreateParameters`
(`src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs:44-56`)
already sets `ValidAudiences = [AuthenticationDefaults.ApiAudience]` and
`ValidateIssuer = true, ValidIssuer = authority`.

So: **three JWT validation sites.** Two check `aud`. This spec makes it
three.

### 5. The `event-ingestion` "inert" classification — invalidated, and so is one word of spec 069

Spec 069's inventory (`specs/069-a-token-names-the-api-it-is-for/spec.md:56`)
records `event-ingestion` as *"no — Mosquitto only, and the plugin does not
read `aud`"*, audience `smart-sentinel-eye-api` **(inert)**. That reasoning
is exactly what this spec removes. After this change the `event-ingestion`
token's `aud` is load-bearing at CONNECT.

The other two inert rows (`identity-admin`, `migration-runner`) reach only
the Keycloak Admin REST API and **stay inert**. Correcting one row, not
three, is FR-006.

### 6. The issuer suffix match is real, and weaker in form than in effect

`jwt_auth.go:172` reads, verified on this branch:

```go
if issuer, _ := claims["iss"].(string); !strings.HasSuffix(issuer, realmPath) {
```

with `realmPath` defaulting to `/realms/smart-sentinel-eye` and overridable
by `plugin_opt_jwt_realm_path` (set to that same value in
`mosquitto.conf:19`). Any host serving a path ending in that string passes.

**But the practical severity is low, and the framing "materially looser on
two fields" overstates it.** The signature is verified against *our realm's*
JWKS, fetched from `SSE_JWT_JWKS_URI`. A token from
`https://evil.example.com/realms/smart-sentinel-eye` must still be signed by
our realm's private key. The suffix match is loose in *form*; the RS256
check makes it close to inert in *effect*. It is defence-in-depth worth
having, not a live hole.

**It is not in this slice.** §*What this leaves* gives the reason, which is
about the fix's shape rather than about tidiness.

---

## The capability, established

`plugin/go.mod` pins `github.com/golang-jwt/jwt/v5 v5.3.1`.
`parser_option.go` at tag `v5.3.1` exports:

```go
// WithAudience configures the validator to require any of the specified
// audiences in the `aud` claim.
func WithAudience(aud ...string) ParserOption

// WithIssuer configures the validator to require the specified issuer in
// the `iss` claim.
func WithIssuer(iss string) ParserOption
```

And `validator.go` at the same tag: when `len(v.expectedAud) > 0` the
audience claim becomes **required** — a token with no `aud` fails with
`ErrTokenRequiredClaimMissing` rather than passing — and matching is
**any-of** unless `WithAllAudiences` is used.

Any-of is the semantics we need: Keycloak client-credentials tokens
routinely carry `"account"` alongside the mapper-supplied audience, so
requiring *all* would reject every real token.

The existing call is `jwt_auth.go:151-156`:

```go
token, err := jwt.Parse(
    password,
    set.Keyfunc,
    jwt.WithValidMethods([]string{"RS256"}),
    jwt.WithExpirationRequired(),
)
```

**The change is one option added to that list.** No plugin configuration,
no new dependency, no version bump.

---

## Where the audience comes from

`src/AppHost/Realms/smart-sentinel-eye-realm.json:103-122` defines the
`sse-audience` client scope with an `oidc-audience-mapper`:

```json
"included.custom.audience": "smart-sentinel-eye-api",
"id.token.claim": "false",
"access.token.claim": "true",
"introspection.token.claim": "true"
```

`access.token.claim: true` is what puts it on the token MQTT sees.
`include.in.token.scope: false` keeps it out of the `scope` claim, so it
carries no permission — the scope exists only to name a destination.

**All nine realm clients carry it as a default scope** (read from the realm
file; `tests/Architecture.Tests/RealmAudienceTests.cs:91` holds it):
`smart-sentinel-eye-web`, `management-web`, `kiosk-web`, `kiosk-wall`,
`identity-admin`, `migration-runner`, `stream-distribution-attribution`,
`scenario-simulator`, `event-ingestion`.

**All three runtime-created personas attach it at creation**
(`EnrollKioskCommandHandler.cs:38`, `RegisterDeviceCommandHandler.cs:55`,
`RotateWebhookClientCommandHandler.cs:109`, each spelling
`KeycloakScopeBundles.AudienceScope`;
`tests/Identity.Application.Tests/KeycloakAdmin/RuntimeClientAudienceTests.cs`
holds all three).

**Who actually presents a JWT to Mosquitto today:** `event-ingestion` (the
subscriber, `MqttTokenProvider`), `scenario-simulator` (the publisher — both
observed connecting, per ADR-0100's 2026-08-06 addendum), and every device
client created by `POST /devices/register`. All three populations are
covered above.

---

## The outage surface

The population that breaks is **not** "clients that have not been given the
scope yet" — #91 gave it to all of them. It is **clients Keycloak already
stored without it**.

`RegisterDeviceCommandHandler` **creates** a Keycloak client and never
updates one. `HttpKeycloakAdminClient` issues exactly two PUTs, and neither
touches default scopes: the one `PutAsync` call is a group join (`:188`), and
`DisableClientAsync` PUTs `{ enabled: false }` on a client through an
`HttpRequestMessage` (`:147`). *(Corrected in phase 4b: this paragraph said
"the only `PutAsync` … is a group join", which is true of the method and
reads as "no PUT touches a client", which is false. `plan.md`'s R2 states it
correctly. The conclusion is unaffected — nothing updates
`defaultClientScopes`.)* Keycloak stores `defaultClientScopes` on the client
at creation. So a device client registered **before #91 merged on 2026-09-05**
still mints tokens without `aud`, and this change refuses it at CONNECT.

**Where such a client can survive:** a developer's persistent Keycloak data
volume. `AppHost.cs:122-128` gives Keycloak
`ContainerLifetime.Persistent` + `WithDataVolume()` in run mode when not
running e2e. Devices registered into that realm before 2026-09-05 are still
there.

**Where it cannot:** CI and e2e boot Keycloak without the persistent
lifetime, so the realm is imported fresh every run. There is no production
deployment (constitution §VII / ADR-0118 — the production sink is deferred
because there is nothing to deploy to), so no production device population
exists.

**Mitigation, stated as an instruction rather than a note (FR-007):**
delete the Keycloak data volume before the first run after this lands.
Restarting the container is not enough — it keeps the old realm and the
stack looks perfectly healthy.

**The mitigation has its own second-order consequence.** Dropping the
Keycloak data volume does not merely retire the pre-#91 clients this
section is about — it destroys **every** runtime-registered device, kiosk
and webhook client, because Keycloak's realm data is where they live.
Identity's own record of them does not go with it:
`RegisteredClientRepository` persists `RegisteredClient` rows
(`src/Identity/Domain/RegisteredClient/RegisteredClient.cs`) to Postgres,
which the volume drop never touches. After the drop, every such row names
a `ClientId` Keycloak no longer has — a healthy-looking Identity database
pointing at clients that do not exist, discoverable only when something
tries to use one. The instruction in the PR body must say both halves:
drop the volume, and reconcile or clear the now-orphaned
`RegisteredClient` rows.

---

## User stories

### US-1 (P1) — The broker refuses a token minted for something else

As the operator of a 24/7 fab, I want the MQTT broker to require the same
audience every other authenticated surface requires, so that a realm-signed
token issued for a different destination cannot open a broker session merely
because it names a matching client in `azp`.

`azp` says which client *asked for* the token. `aud` says which service the
realm *minted it for*. A broker checking only `azp` is trusting the client's
own claim about itself.

**Independently shippable:** one Go file, one integration test class, and
the documentation lines that state the check set. Observable end-to-end
through a booted stack in one step (§*Independent end-to-end test
procedure*).

**This is the whole spec.** There is no US-2. The issuer change is #TBD
(§*What this leaves*).

---

## Functional requirements

- **FR-001** — `jwt_auth.go` MUST require the access token's `aud` claim to
  contain `smart-sentinel-eye-api`, using
  `jwt.WithAudience` on the existing `jwt.Parse` call. A token whose `aud`
  omits it, or which carries no `aud` at all, MUST be refused with
  `MOSQ_ERR_AUTH`.
- **FR-002** — The expected audience MUST be a named constant in the plugin,
  spelt once, adjacent to `defaultRealmPath`. It is a **fourth** spelling of
  a literal that `AuthenticationDefaults.ApiAudience`, `RealmAudienceTests`
  and `BearerAudienceTests` already spell — Go cannot import a C# constant,
  and ADR-0051's reasoning for the third spelling applies unchanged. FR-005
  is what stops the four drifting.
- **FR-003** — A non-JWT password MUST still return `MOSQ_ERR_PLUGIN_DEFER`.
  The audience check MUST NOT move the JWT/non-JWT fork.
- **FR-004** — The existing `azp`, issuer, `exp`, RS256 and JWKS behaviour
  MUST be unchanged. In particular the issuer check MUST NOT be tightened,
  loosened, or moved in this slice.
- **FR-005** — A test MUST fail if the plugin's audience literal and
  `AuthenticationDefaults.ApiAudience` disagree, by reading the Go source.
  Without it the fourth spelling is the one nobody notices.

  *(Corrected in phase 4b: this was built as
  `tests/Architecture.Tests/PluginAudienceLiteralTests.cs` and kept because
  the case for it — that the runtime pair only pins the literal if the two
  tokens' `aud` sets differ by exactly `smart-sentinel-eye-api` — had not
  been measured. It has now been measured, against this branch's realm
  file, reproducing both creation paths:*

  | Token | `aud` as Keycloak issued it |
  |---|---|
  | throwaway, no `sse-audience` | claim absent entirely |
  | device via `RegisterDeviceCommandHandler` | `"smart-sentinel-eye-api"` |
  | `event-ingestion` realm client | `"smart-sentinel-eye-api"` |

  *`B.aud \ A.aud = {smart-sentinel-eye-api}` exactly — `account` never
  appears, because neither client carries the built-in `roles` scope
  (both are created with an explicit `defaultClientScopes` list). And it
  was checked by counterfactual, not inferred: a third image with the
  const drifted to `"account"` — the exact candidate this note used to
  flag — makes the positive control go red:*

  ```
  ===== plugin const drifted to "account" =====
    [B plc-…]  Connection Refused: not authorised / CONNACK (5)
  ```

  *So the runtime pair (`MqttAudienceIntegrationTests`) does pin the
  literal on its own. `PluginAudienceLiteralTests` was deleted — its
  remaining value was diagnostic only: reading source and asserting a
  string is present, the shape issue #2141's census exists to catalogue.
  FR-005 is satisfied by the runtime pair, not by a dedicated test.)*
- **FR-006** — Spec 069's inventory row for `event-ingestion` MUST be
  corrected: its audience stops being inert. The `identity-admin` and
  `migration-runner` rows stay inert and MUST NOT be touched.
- **FR-007** — The Keycloak-volume instruction MUST appear in the PR body
  and in the verification note, not only in this spec.
- **FR-008** — The check set MUST be re-stated where it is already stated
  and would otherwise go stale: `jwt_auth.go`'s header, `mosquitto.conf`'s
  auth comment, and `docs/design/scenario-simulator-m2.md:68`.
  **ADR-0100 is deliberately excluded** — see §*The ADR question*.

---

## The ADR question

**No new ADR, and none amended by this slice.**

Making one surface agree with the posture the other two already have is
*implementation* of ADR-0007/ADR-0008 and of spec 069's already-taken
decision. Nothing new is being decided.

The opposite conclusion would have been: **deliberately keeping MQTT
looser is a decision, and would require an ADR.** That is the blocked
outcome — and it is not the conclusion here, because the argument for it
fails on the facts. Option 2/3's premise is that `aud` "adds nothing" at the
broker. It adds exactly what it adds everywhere else: after this change, a
client created without `AudienceScope` — which is every runtime client
created before 2026-09-05, and any future one whose creator forgets the
constant — is refused at the broker instead of silently admitted. The broker
would otherwise be the one place the system's own invariant is not asserted.

**One flag for the human, not for the lane.** ADR-0100's 2026-05-31 addendum
enumerates the check set, and that enumeration goes stale the moment FR-001
lands. Correcting it is the same *kind* of factual correction the ADR
already carries twice ("the password_file path is fiction"), not a new
decision — but it is still an edit to an ADR, and the lane may not make
one. **T012 records the one-line correction as text and stops.** A human
applies it, or explicitly declines. The slice is complete and correct
either way; what is at stake is whether ADR-0100 keeps saying something
true.

---

## Acceptance scenarios

### Happy — a properly minted device token still connects

```gherkin
Given a device registered through POST /devices/register on the running stack
  And its Keycloak client carries the sse-audience default scope
 When it mints a client_credentials token
  And it opens an MQTT CONNECT to Mosquitto with client_id as username and the token as password
 Then the broker answers CONNACK Success
```

### Conflict — a realm-signed token minted for something else is refused

```gherkin
Given a Keycloak client in the smart-sentinel-eye realm whose default scopes omit sse-audience
  And service accounts enabled, so it can mint client_credentials tokens
 When it mints a token, correctly signed by the realm and carrying azp equal to its own client_id
  And it opens an MQTT CONNECT with that client_id as username and that token as password
 Then the broker refuses the connection
  And the refusal is an authentication failure, not a timeout or a transport error
```

This is the scenario that is **green today and must be red before the
change** — it is the whole point of the slice.

### Bad request — a password that is not a JWT still defers

```gherkin
Given the password_file path
 When a client connects with a password that is not a three-segment JWT
 Then the plugin returns MOSQ_ERR_PLUGIN_DEFER
  And the outcome is decided by password_file exactly as before
```

`passwords.txt` contains no hashes (ADR-0100, 2026-08-06 addendum), so the
observable outcome is refusal — **by the password file, not by the plugin**.
The scenario asserts the fork is unmoved, not that anyone gets in.

### Auth — azp is still required, and is still not sufficient

```gherkin
Given a token carrying aud smart-sentinel-eye-api and a valid realm signature
 When it is presented with an MQTT username that does not equal its azp claim
 Then the broker refuses the connection
```

The audience check is added *alongside* `azp`, not in place of it. A change
that made a correct `aud` excuse a mismatched `azp` would satisfy FR-001 and
break FR-004.

### No soft edge — the literal cannot drift from the one the APIs use

```gherkin
Given AuthenticationDefaults.ApiAudience
 When the audience literal in jwt_auth.go is changed to any other string
 Then the test required by FR-005 fails
```

*(Corrected in phase 4b: "the test required by FR-005" is
`MqttAudienceIntegrationTests.A_token_minted_with_the_api_audience_still_connects`,
not a dedicated source-reading test — see FR-005's note. A drifted literal
makes that positive control's CONNACK fail instead, which is what the
counterfactual there measured.)*

---

## Independent end-to-end test procedure

Reproducible by a human with no knowledge of the implementation. **Requires
the Aspire stack.**

1. **Delete the Keycloak data volume first** (`docker volume rm` the
   Keycloak volume for this AppHost). Restarting the container is not
   enough — it keeps the old realm and the stack looks healthy while the
   realm is stale.
2. Boot the AppHost in run mode. Wait for `keycloak` and `mosquitto`
   Running.
3. Mint an admin token; `POST /devices/register?fabId=munich` with a fresh
   `deviceIdentifier`. Keep the returned `clientId` / `clientSecret`.
4. Mint a `client_credentials` token for that device from Keycloak's token
   endpoint, **through Aspire's proxied endpoint** — not the container's
   mapped port, or the issuer will not be the one the stack uses.
5. Decode the token's payload (base64url, second segment). Confirm `aud`
   contains `smart-sentinel-eye-api`. **If it does not, stop — step 1 was
   not done, and every later step will mislead.**
6. Connect to `mosquitto` over MQTT with username = `clientId`, password =
   the token. **Expect CONNACK Success.**
7. Through Keycloak's Admin REST API, create a second confidential client in
   the same realm with service accounts enabled and default scopes
   `["sse.events.publish"]` — deliberately **without** `sse-audience`. Mint
   its `client_credentials` token. Decode it: `aud` must **not** contain
   `smart-sentinel-eye-api`.
8. Connect with username = that second client's id and password = its token.
   **Expect refusal.** Before this change it connects; that difference is
   the whole observable behaviour of the spec.
9. Delete the throwaway client.

---

## Latency budget

**N/A — not on the event-to-overlay path.** Broker CONNECT authentication is
NFR-002 (≤ 5 ms p99 auth overhead), which is a separate budget from
constitution §IV's six legs. This change touches none of the six.

**NFR-002 itself is not eroded.** `jwt.WithAudience` adds a string comparison
against an already-parsed claim inside the same `jwt.Parse` call — no
allocation beyond what claim parsing already does, no I/O, no Keycloak
round-trip. The auth overhead is recorded at roughly 70 µs
(`NFR002_MqttConnectAuthTests` class remarks); this is not measurable against
that. `NFR002_MqttConnectAuthTests` continues to run and continues to report,
and is the regression guard.

---

## Non-functional

- **NFR-002 (≤ 5 ms p99 connect auth)** — must continue to hold.
  `NFR002_MqttConnectAuthTests` must pass **unmodified**. If its assertions
  have to be edited, the change moved something it should not have.
- **No new dependency.** `go.mod` and `go.sum` must be byte-identical
  afterwards. `WithAudience` is in the pinned `v5.3.1`.
- **The plugin has no Go test suite and no Go toolchain on this machine**
  (`go` is not on PATH; the plugin is only ever compiled inside
  `Dockerfile` stage 2). Phase 4a is discharged through the Aspire fixture
  — §*Phase 4a*.

---

## Phase 4a — the colour, and how it is obtained

**RED.** This is behaviour-changing: the broker begins refusing CONNECTs it
previously accepted. A test that arrives green is a phase-4 failure.

**The red is genuinely obtainable, on Go code, from xUnit.** The Aspire
fixture boots the real broker from `mosquitto/Dockerfile`, which compiles
`jwt_auth.go` into the image. A test in `tests/Integration.Tests/Identity/`
exercises the compiled plugin. That directory's tests carry **no `Category`
trait** (grepped), and `ci.yml:179` filters only
`Category!=Measurement&Category!=Disruptive&Category!=Maintenance` — so a new
test there runs in CI's Docker integration job. No Go test framework is
needed and none is being introduced.

**The red test** is the *Conflict* scenario: a realm client whose default
scopes omit `sse-audience`, minting a correctly-signed token with matching
`azp`, connects successfully today.

**Getting an audience-less client is the real cost of this slice, and it is
new plumbing.** No test in the repository touches the Keycloak Admin REST
API (grepped `/admin/realms/` and `admin-cli` across `tests/`: zero hits).
The test must create a throwaway client and delete it in teardown.
`defaultDefaultClientScopes` is empty in the realm file and `sse-audience`
is not one of Keycloak's built-ins, so a client created without listing it
genuinely lacks it — verified from the realm JSON, not assumed.

**Spec 069 declined to build this negative, and that precedent has to be
answered rather than stepped around.**
`TokenAudienceIntegrationTests`'s class doc says so in as many words:

> **No negative here, deliberately.** Minting an audience-less token needs a
> client that contradicts FR-003, and a test that rewrites the realm under a
> running stack is worse than the documented drill.

The objection is to **rewriting the realm** — mutating the shared client
definitions the rest of the suite authenticates through. It is not an
objection to *creating a client*: the third `[Fact]` in that very class
creates a Keycloak client on every run, through `POST /devices/register`.
A throwaway client, uniquely named, created and deleted by one test, adds a
row and removes it; it changes no existing client and no realm-level
setting. That is materially different from what spec 069 refused.

The other half of spec 069's reasoning was that its negative *had a home* —
`BearerAudienceTests` holds the exact validation function the nine APIs
build, so the refusal is provable without a token. **The plugin has no such
home.** There is no in-process object to assert against; the check exists
only inside a Go binary compiled into a container image. Either the red is
obtained against the running broker or it is not obtained at all — and
ADR-0144 does not offer 4a an exemption. If the Admin REST approach proves
impractical in phase 4, the slice **blocks**; it does not fall back to a
source-scanning guard.

**Three ways of getting the red that are forbidden**, named because each is
cheaper than the right one:

- Adding an audience-less client to `smart-sentinel-eye-realm.json`.
  `RealmAudienceTests` asserts **every** realm client carries the scope;
  editing the realm to dodge a guard is the forbidden shape.
- Loosening `RealmAudienceTests` to permit an exception.
- Hand-crafting a JWT. We do not hold the realm's private key, and a token
  we sign ourselves fails at the signature — proving nothing about `aud`.

**The characterisation half.** `NFR002_MqttConnectAuthTests` already
registers a real device, mints its token and opens 110 authenticated
CONNECTs. It is captured **green before** the change and must pass
**unmodified after**. That is the positive control: it proves FR-001 did not
simply break every connection.

---

## Assumptions, marked

- **A1 — not an assumption; already proven.** That a runtime-registered
  device's `client_credentials` token carries `aud: smart-sentinel-eye-api`
  is asserted today by
  `TokenAudienceIntegrationTests.A_client_enrolled_at_runtime_mints_a_token_that_names_it`,
  which registers a real device through `POST /devices/register` against the
  booted stack and reads the claim off the minted token. The sibling facts
  for the browser client and for a `client_credentials` service account are
  the other two `[Fact]`s in that class. **Nothing here rests on a reading of
  the realm file alone.**
- **A2** — No device client that matters was created before 2026-09-05
  anywhere except a developer's persistent volume. Follows from there being
  no production deployment. If someone has a long-lived stack they care
  about, FR-007's volume deletion is the whole remedy.
- **A3** — `scenario-simulator` and `event-ingestion` are the only realm
  clients presenting JWTs to the broker, per ADR-0100's 2026-08-06 addendum
  ("both observed connecting"). Both carry `sse-audience`. Not re-observed
  for this spec.

---

## What this leaves

### The issuer suffix match — its own issue, and the reason is real

`jwt_auth.go:172` stays exactly as it is. **The reason is the fix's shape,
not tidiness.**

The audience has a known expected value: the string is already in the realm
file and in `AuthenticationDefaults.ApiAudience`, and it is the same for
every token in the system. **The issuer's expected value is not known to the
plugin.** The plugin holds only `SSE_JWT_JWKS_URI`, which `AppHost.cs:224`
builds from `keycloak.GetEndpoint("http")` resolved for a **container**
consumer — Mosquitto is the one container in this path. Every token in the
stack is minted by host processes and by the test host through **Aspire's
proxied** Keycloak endpoint. Those are different hosts.

`jwt.WithIssuer` is an exact string match. Deriving the issuer by trimming
`/protocol/openid-connect/certs` off the JWKS URI would therefore very
likely reject **every** token — the "everything 401s" shape this stack
already produces when the issuer and the minting endpoint disagree.

So tightening the issuer needs: a new configured input (an expected issuer
injected by AppHost from the same expression the services resolve), and a
booted-stack observation of what `iss` actually is. That is a different
AppHost change, a different design question and a different outage risk.

**Bundling them would mean the cheap, safe, high-value half is held hostage
by the risky half — and if the broker stopped accepting everything, nobody
could tell which of the two checks did it.** That is the reason, and it is
sufficient.

Severity for the follow-up issue to carry: **low**. The suffix match is
loose in form, but the RS256/JWKS signature check binds the token to our
realm's keys, so a foreign host cannot exploit it without our private key.
It is defence-in-depth, and it should say so rather than be filed as a hole.

### ADR-0100's stale title

The ADR is called `0100-mosquitto-go-auth.md` and its own body records that
`mosquitto-go-auth` was rejected. `mosquitto.conf:6` and `AppHost.cs:205`
inherit the name. **Not renamed here** — an ADR filename is a stable
reference and renaming it is a decision. Worth a separate issue; it is how
#2085 came to ask a vendor-capability question about a vendor we do not use.

---

## Gate (Phase 1)

Spec reviewed; no `[NEEDS CLARIFICATION]` remains. Four corrections to the
issue's premises are recorded above, and one of them — that the plugin is
ours, not `iegomez`'s — changes the work from an investigation into a
one-option edit. Proceed to `plan.md`.

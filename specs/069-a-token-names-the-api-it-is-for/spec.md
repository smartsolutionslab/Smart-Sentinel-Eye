# Feature Specification: A token names the API it is for

**Branch**: `fix/91-a-token-names-the-api-it-is-for` · **Issue**: #91
**Created**: 2026-09-05 · **Status**: Phase 1 complete, awaiting review
**ADRs**: 0007, 0008, 0106, 0051, 0103, 0109, 0139, 0144 — **no new ADR**
**Specs it continues**: 041 (the client split), 042 (`sse-identity`, and the
rule that one fact lives in one scope), 052 (`kiosk-wall`)

---

## Summary

`src/ServiceDefaults/AuthenticationDefaults.cs:62` says
`options.TokenValidationParameters.ValidateAudience = false`, so every one of
the nine APIs accepts **any** token this realm signs. Turning that off is four
lines of change and one large question, and the question is the spec.

**The change is not "add a mapper".** Enabling `ValidateAudience` rejects every
token whose `aud` does not contain the new value. There is no degradation
mode: a client that does not carry the audience stops authenticating
**entirely, everywhere, at once** — REST through the gateway, the SignalR hub,
and the webhook JWT path alike. So the deliverable is an **inventory** of every
principal that mints a token an API validates, and a mechanism that cannot be
attached to eight of nine places.

That mechanism is a **client scope**, not a per-client mapper. The issue asks
for a mapper on "the web client"; there are now several web clients, and a
per-client mapper would additionally fail `RealmIdentityTests
.No_client_carries_its_own_mapper`, which spec 042 added for exactly this
failure shape. See *What the issue got right, and what has moved* below.

---

## The inventory

**Enumerated from the realm file and the minting code, not from memory.** Every
row was read on this branch at `70f9223`.

`aud` is a property of the **client**, never of the user, so the four `wall-*`
accounts and the eight operator accounts do not appear here — they mint through
`kiosk-wall` and `management-web` / `smart-sentinel-eye-web` and inherit those
rows.

### Declared in `src/AppHost/Realms/smart-sentinel-eye-realm.json`

| clientId | Grant | Minted by | Reaches an API that validates? | `aud` after this change |
|---|---|---|---|---|
| `smart-sentinel-eye-web` | authz code, password | `apps/management-web/src/app/auth.ts`; `AspireFixture.ClientId` (`AspireFixture.Auth.cs:12`) | **yes** — all nine, plus the hub | `smart-sentinel-eye-api` |
| `management-web` | password | `WebhookBearerValidationIntegrationTests.cs:40` (stands in for a rotated webhook client) | **yes** — EventIngestion `POST /webhooks/...` | `smart-sentinel-eye-api` |
| `kiosk-web` | authz code (no direct grants) | `apps/kiosk-web/src/app/auth.ts`, `VITE_KIOSK_MODE` unset | **yes** — reads + hub | `smart-sentinel-eye-api` |
| `kiosk-wall` | authz code + refresh (no direct grants) | `apps/kiosk-web/src/app/auth.ts` in wall mode; `e2e/wall-withdrawal.spec.ts` refresh exchange | **yes** — reads + hub | `smart-sentinel-eye-api` |
| `stream-distribution-attribution` | client credentials | `src/StreamDistribution/Infrastructure/Attribution/CameraCatalogTokenProvider.cs` | **yes** — CameraCatalog | `smart-sentinel-eye-api` |
| `scenario-simulator` | client credentials | `src/ScenarioSimulator/Keycloak/KeycloakTokenProvider.cs`; `PlantFloor.cs`; `FabGroupClaimIntegrationTests.cs` | **yes** — cameras, overlays, rules, layouts | `smart-sentinel-eye-api` |
| `identity-admin` | client credentials | `src/Identity/Infrastructure/KeycloakAdmin/KeycloakAdminTokenProvider.cs` | no — Keycloak Admin REST only | `smart-sentinel-eye-api` (inert) |
| `migration-runner` | client credentials | MigrationRunner worker | no — Keycloak Admin REST only | `smart-sentinel-eye-api` (inert) |
| `event-ingestion` | client credentials | `src/EventIngestion/Infrastructure/Ingress/MqttTokenProvider.cs` | **yes** — Mosquitto's JWT plugin requires the audience at CONNECT (spec 090) | `smart-sentinel-eye-api` |

**Corrected by spec 090 (2026-09-07).** This row read *"no — Mosquitto only,
and the plugin does not read `aud`"*, with the audience marked **(inert)**, and
that was true when it was written. Spec 090 added `jwt.WithAudience` to
`src/AppHost/mosquitto/plugin/jwt_auth.go`, so `event-ingestion`'s audience is
now load-bearing: without it the subscriber is refused at CONNECT.
**`identity-admin` and `migration-runner` stay inert** — both reach only the
Keycloak Admin REST API, which validates no audience — so this is one row, not
a sweep of three.

**The remaining inert rows still get the scope.** The rule that survives contact
with the next feature is *every client in this realm*, not *the seven we
reasoned about*. An extra audience on a token nobody audience-checks costs
nothing; a client-by-client exemption list is the thing that goes stale — and
`event-ingestion` is the row that proves it, having gone from inert to
load-bearing without anyone editing the realm.

**`management-web` is minted only by a test.** The app named after it signs in
as `smart-sentinel-eye-web` (`apps/management-web/src/app/auth.ts`). That is a
pre-existing oddity of the same shape spec 041 guarded against with
`The_retired_kiosk_client_stays_retired`; **this spec does not fix it** and
does not depend on it. It is recorded because a reader checking the inventory
will otherwise conclude a row is wrong.

### Created at runtime, through the Keycloak Admin REST API

These are **not in the realm file** and are the outage risk. All three build a
`KeycloakClientRepresentation` — which has **no `protocolMappers` field at
all** — and rely entirely on `DefaultClientScopes`.

| Created by | Bundle it is given | Reaches an API that validates? | `aud` after this change |
|---|---|---|---|
| `RotateWebhookClientCommandHandler` | `KeycloakScopeBundles.WebhookIntegration` (`sse.events.write`) | **yes** — EventIngestion, the JWT webhook mode | `smart-sentinel-eye-api` **only once the handler attaches the scope** |
| `EnrollKioskCommandHandler` | `KeycloakScopeBundles.Kiosk` (5 reads + `sse.events.write`) | yes if used; constitution §VIII records that the kiosk app does **not** use these today | same |
| `RegisterDeviceCommandHandler` | `KeycloakScopeBundles.Device` (`sse.cameras.read`, `sse.events.publish`) | CameraCatalog for the read; MQTT for the publish | same |

**None of the three is covered by the existing suite in a way that would catch
this.** `WebhookBearerValidationIntegrationTests` exercises the JWT webhook
path with `management-web` standing in for a rotated client, so a runtime
client with no audience would pass every test in the repository and fail in
production. FR-006 and FR-010 exist for that.

### Out of realm

`e2e/wall-withdrawal.spec.ts` mints from `admin-cli` in the **`master`** realm
for cleanup. It never presents that token to one of our APIs, and it would fail
issuer validation before audience validation if it did. **Unaffected.**

---

## What breaks visibly if a client is missed

**A 401 with no body.** The JWT bearer handler's challenge is not an exception,
so none of the `AddExceptionHandler` registrations in
`AuthenticationDefaults.cs:70-82` and none of `AddProblemDetails` applies. What
carries the diagnosis is the response header:

```
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="invalid_token", error_description="...IDX10214: Audience validation failed..."
```

Nothing in this repository configures `JwtBearerEvents.OnChallenge`,
`OnAuthenticationFailed` or `IncludeErrorDetails` (grepped: zero hits), so this
is the framework default. The exact `IDX10214` wording is a Microsoft.IdentityModel
string and is **confirmed at phase 5, not asserted from here**.

Two surfaces, two symptoms:

- **REST.** `apps/shared/src/api/gateway.ts:51` gives a 401 exactly one silent
  renewal and one retry. A renewal against a *fixed* realm returns a token that
  works, so a browser self-heals on the first failure. A renewal against a
  **stale** realm returns another audience-less token, the retry 401s too, and
  the user sees a login that appears broken rather than a misconfiguration.
- **The hub.** `LayoutLifecycleHub` takes its token from `?access_token=`
  (`src/LayoutComposition/Api/Program.cs:18-35`) through the same validation
  pipeline. A kiosk with a bad audience shows the permanently-stuck *"live
  updates degraded"* badge from spec 011 — video keeps playing, labels stop
  moving. **That is the quiet failure**, and the one worth watching for.

---

## A token minted before the change

The realm sets `accessTokenLifespan: 3600` — one hour.

- **Integration tests and e2e: no such population exists.** `AppHost.cs:123-128`
  gives Keycloak `ContainerLifetime.Persistent` and a data volume only when
  `isRunMode && !isE2ETests`. Every test run gets a fresh container, a fresh
  import and fresh tokens.
- **Developer `dotnet run`: this is where it bites, and it does not expire.**
  Keycloak keeps the imported realm in its volume; a changed realm file is
  **silently ignored** on restart. The services demand an audience the realm
  never emits, every request 401s, the browser's renew-and-retry produces a
  second audience-less token, and the stack reports itself perfectly healthy.
  The fix is to delete the **volume**, not the container. FR-011 and the phase-5
  procedure both say so, because a verifier who restarts instead will conclude
  the mapper works when it was never imported.
- **Production: not reachable today** — the Aspire k8s publisher has never been
  run (ADR-0130). If it were, the rule is **realm first, then services**: an
  extra `aud` on a token nobody validates is inert, so importing first costs
  nothing, while rolling services first opens a ≤1 h window in which every
  unexpired token 401s. Browsers self-heal on their first 401 via the renew
  path; service accounts self-heal on their next mint.

---

## Decisions

### D1 — one audience, `smart-sentinel-eye-api`, not one per context

**Because the gateway forwards.** ADR-0106 keeps JWT validation per service and
does no auth offload (`src/ApiGateway/Program.cs:25-27`); YARP passes the
`Authorization` header through unchanged to all nine clusters. A browser holds
**one** token and calls nine contexts with it. Per-context audiences would
therefore require every token to carry all nine values — which is not narrower
than one value, it is one value spelled nine times.

ADR-0008 makes it worse: one realm **per fab**, so each audience is duplicated
per plant. Nine audiences × N fabs of realm entries buys nothing over one.

Recorded as a consequence of decisions already made, not as a new one.

### D2 — the mapper lives on a client scope, `sse-audience`

Spec 042 (FR-005) established that a shared fact has one definition and no
client keeps a private copy; `RealmIdentityTests.No_client_carries_its_own_mapper`
fails the build if one does. A per-client `oidc-audience-mapper` — the issue's
step 2 — would break that test on nine clients.

`sse-audience` mirrors `sse-identity` exactly, including the naming convention
this realm already relies on: **hyphen means a claim carrier, dot means a
permission**. That convention is load-bearing in two existing test files
(`RealmIdentityTests.No_permission_scope_carries_an_identity_mapper` and
`KioskScopeParityTests.IsPermission`), and it is why adding `sse-audience` to
`kiosk-web` does **not** break the kiosk scope-parity assertions.

The scope sets `include.in.token.scope: false`, so it grants nothing and does
not appear in the `scope` claim — the `sse.management` and `sse.*` policies are
untouched.

### D3 — `included.custom.audience`, and no bearer-only client entity

The issue's step 1 asks for a bearer-only `smart-sentinel-eye-api` client.
**Recommended against**, for three reasons and with the alternative named:

1. `RealmIdentityTests.Every_client_holds_the_identity_scope` asserts, per
   client, that it can put a subject in a token. A client that enables no flow
   and mints nothing cannot satisfy that meaningfully — it would either need a
   scope it will never use, or the guard would need an exemption clause. Adding
   an escape hatch to an existing guard to accommodate a new entity is the
   wrong trade.
2. Keycloak 26.5 (the version AppHost comments cite) has deprecated the
   bearer-only client type. `"bearerOnly": true` in a realm representation is
   accepted but no longer a first-class type; depending on it is a bet.
3. ADR-0008 again: an entity per audience is an entity per audience **per fab**.

`oidc-audience-mapper` supports `included.custom.audience` — a free string —
natively, and needs no client. What the client would have bought (documentation
of the resource server) is bought instead by FR-009: an architecture test that
pins `AuthenticationDefaults.ApiAudience` to the string in the realm file, so
the two cannot drift.

**If a reviewer wants the realm entity anyway**, the cost is item 1 above,
resolved by giving the client `"defaultClientScopes": ["sse-identity"]`. Say so
at the phase-1 gate; it is a one-line change to the plan, not a redesign.

### D4 — every declared client carries it, uniformly

See the inventory. Three of nine cannot use it. All nine get it, because the
enforceable rule is *all of them* and the alternative is an exemption list.

### D5 — the three runtime handlers attach it

`KeycloakScopeBundles.{Kiosk,Device,WebhookIntegration}` must **not** gain
`sse-audience`: `KioskScopeParityTests` compares the `Kiosk` bundle against the
realm client's `sse.`-prefixed scopes in both directions, and a non-permission
entry in the bundle would fail the second direction. The scope is appended at
the three call sites from one shared constant instead.

### D6 — `Audience` is set once

All nine APIs call `builder.AddBearerAuthentication()` with no arguments. One
assignment in `AuthenticationDefaults` covers all nine. **No per-context
override is needed**, and ADR-0051's per-context `Add<Context>Api` methods do
not touch authentication.

---

## Out of scope

- **The MQTT broker does not validate `aud`.** `src/AppHost/mosquitto/plugin/jwt_auth.go`
  verifies the RS256 signature, requires expiry, checks `azp == username` and
  checks the issuer suffix — there is no reference to the audience claim
  anywhere in the file, and ADR-0100 does not mention one. Unlike the REST side,
  this gap is undocumented. **It is a separate issue**, not a silent extension
  of this one: the plugin is Go, it is compiled in the AppHost image, and it
  shares no code with `AuthenticationDefaults`.
- **The gateway.** It validates nothing and rewrites nothing; it needs no
  audience. Confirmed at `src/ApiGateway/Program.cs:25-27, 54-56` and ADR-0106.
- **`management-web` being declared but signed into by nothing.** Pre-existing.
- **The `scenario-simulator` credential duplicated in three places.**
  Pre-existing (`PlantFloor.cs`, `FabGroupClaimIntegrationTests.cs`, and
  `SimulatorOptions`); this spec adds no fourth copy.

---

## User Scenarios & Testing

### User Story 1 — a token that does not name this API is refused (P1)

**The whole feature, and the only story.** It is one vertical slice: one realm
file, one `ServiceDefaults` constant, three handler call sites, and the tests
that hold them together. There is nothing to split that would still be
observable end to end.

**Independent test**: mint a token from `smart-sentinel-eye-web` against a
freshly-imported realm, decode it, see `smart-sentinel-eye-api` in `aud`, and
watch a protected `GET` succeed. Then remove the scope from that one client,
re-import, and watch the same `GET` return 401 with the audience description in
`WWW-Authenticate`. Both halves are reachable without any code change.

#### Acceptance scenarios

**Happy — an operator's token names the API**

```gherkin
Given the realm has been imported with the sse-audience client scope
When a token is minted for smart-sentinel-eye-web by the password grant
Then its aud claim contains "smart-sentinel-eye-api"
And GET /cameras with that token returns 200
```

**Happy — a runtime-created client's token names the API**

```gherkin
Given a webhook integration client has just been rotated through POST /webhook-integrations
When a token is minted for that client by the client-credentials grant
Then its aud claim contains "smart-sentinel-eye-api"
And POST /webhooks/{name} with that token returns 201
```

**Auth — a token the realm signed for something else is refused**

```gherkin
Given a client in this realm that does not carry the sse-audience scope
When a token minted for it is presented to GET /cameras
Then the response is 401
And the response body is empty
And WWW-Authenticate reports an invalid_token whose description names audience validation
```

**Auth — the quiet surface fails the same way**

```gherkin
Given a kiosk token whose aud does not contain smart-sentinel-eye-api
When the kiosk opens /hubs/layouts?access_token=<that token>
Then the handshake is refused
And the kiosk shows the "live updates degraded" badge rather than stale labels
```

**Bad request — an unparseable token is still a 401, not a 500**

```gherkin
Given the audience check is enabled
When "Bearer not-a-jwt" is presented to GET /cameras
Then the response is 401
And no exception handler runs, because a challenge is not an exception
```

**Conflict — the declared audience and the validated audience disagree**

```gherkin
Given AuthenticationDefaults.ApiAudience is edited without editing the realm
When the architecture tests run
Then RealmAudienceTests fails naming both values
And no stack is required to observe it
```

---

## Requirements

- **FR-001** The realm declares a client scope `sse-audience`, protocol
  `openid-connect`, `include.in.token.scope: "false"`,
  `display.on.consent.screen: "false"`.
- **FR-002** That scope carries exactly one `oidc-audience-mapper` with
  `included.custom.audience: "smart-sentinel-eye-api"`,
  `access.token.claim: "true"`, `introspection.token.claim: "true"`,
  `id.token.claim: "false"`.
- **FR-003** Every client in `smart-sentinel-eye-realm.json` lists
  `sse-audience` in `defaultClientScopes`. Default, never optional — an
  optional scope must be requested, and four of the minting paths do not
  control the `scope` parameter.
- **FR-004** No client carries a private `protocolMappers` array. (Restates
  spec 042 FR-005; listed because the issue asks for the opposite.)
- **FR-005** `AuthenticationDefaults` exposes
  `public const string ApiAudience = "smart-sentinel-eye-api"` and sets
  `options.Audience = ApiAudience`.
- **FR-006** `options.TokenValidationParameters.ValidateAudience = false` is
  removed. The default is `true`; the line is deleted rather than negated.
- **FR-007** `EnrollKioskCommandHandler`, `RegisterDeviceCommandHandler` and
  `RotateWebhookClientCommandHandler` each include `sse-audience` in the
  `DefaultClientScopes` they hand to `IKeycloakAdminClient.CreateClientAsync`,
  from one shared constant.
- **FR-008** `KeycloakScopeBundles.Kiosk`, `.Device` and `.WebhookIntegration`
  are **unchanged** (D5).
- **FR-009** An architecture test asserts `AuthenticationDefaults.ApiAudience`
  equals the `included.custom.audience` in the realm file.
- **FR-010** An integration test enrolls a client at runtime and asserts its
  minted token carries the audience — the case no existing test covers.
- **FR-011** The phase-5 procedure deletes the Keycloak **data volume**, not
  the container.
- **FR-012** No client description added or edited exceeds 255 characters.
  (A longer one kills the realm import and hangs the whole Aspire fixture.)

## Success criteria

- **SC-001** `ValidateAudience` is `true` on the options every API builds,
  provable without Docker.
- **SC-002** A token minted through `AspireFixture` carries
  `smart-sentinel-eye-api` in `aud`.
- **SC-003** A token minted from a client enrolled at runtime carries it too.
- **SC-004** Every client in the realm file carries `sse-audience`, asserted
  per client rather than by sampling.
- **SC-005** The full integration suite passes unchanged — no test's assertions
  are edited to accommodate the audience.
- **SC-006** A token without the audience is observed being refused, with the
  401 and the `WWW-Authenticate` description quoted in the phase-5 note.

---

## Independent end-to-end test procedure

Run from the worktree root. **Steps 1 and 2 are not optional**; skipping them
is the documented way to verify a realm that was never imported.

1. `docker rm -f` the Keycloak container **and** `docker volume rm` its data
   volume. Enumerate with `docker volume ls | grep -i keycloak`.
2. `dotnet run --project src/AppHost` and wait for Keycloak to report healthy.
3. Mint: `POST {keycloak}/realms/smart-sentinel-eye/protocol/openid-connect/token`
   with `grant_type=password&client_id=smart-sentinel-eye-web&username=admin&password=Admin1234&scope=openid sse.management`.
   Use the **Aspire proxied endpoint**, not the container's mapped port, or the
   issuer will not match and everything 401s for the wrong reason.
4. Decode the access token's payload. `aud` **must** contain
   `smart-sentinel-eye-api`. Record the decoded `aud` verbatim in the note.
5. `GET {gateway}/camera-catalog/cameras` with that token → **200**.
6. Open the kiosk at `http://localhost:5174`, sign in, open a wall. The *live
   updates* badge must **not** be degraded — that is the hub handshake passing
   audience validation.
7. **The negative.** Remove `"sse-audience"` from `smart-sentinel-eye-web`'s
   `defaultClientScopes` in the realm file, repeat steps 1-3, and repeat step 5.
   Record the **status code, the empty body, and the full `WWW-Authenticate`
   header**. Restore the file and confirm step 5 returns to 200.

Step 7 is the only place in this feature where the refusal is observed against
a real Keycloak. It is a phase-5 step rather than a test because producing an
audience-less token requires a realm that contradicts FR-003, and a test that
edits the realm file under itself would be worse than a documented drill.

---

## Locked tech choices

Keycloak per fab (ADR-0007, ADR-0008); JWT validated per service, never at the
gateway (ADR-0106); one shared `AddBearerAuthentication` (ADR-0051); Aspire is
the composition root and the realm is imported by `WithRealmImport`
(constitution §VI); integration tests against the Aspire fixture, no
Testcontainers (ADR-0103); xUnit + Shouldly (ADR-0052); sentence-style test
names (ADR-0053).

## Latency budget impact

**N/A to all six legs of constitution §IV.** Audience validation is an
in-memory string comparison against an already-parsed token; it adds no network
call, no key fetch and no allocation of consequence. The *event → overlay state*
leg is unaffected: the hub connection is authenticated once at handshake, not
per frame.

---

## What the issue got right, and what has moved

The issue was written when the realm had one web client. Three of its four
steps have aged.

| Issue says | Status |
|---|---|
| 1. Add a bearer-only `smart-sentinel-eye-api` client | **Superseded** — D3. The audience is real; the client entity is not needed and collides with a spec-042 guard. |
| 2. Add an `oidc-audience-mapper` **on `smart-sentinel-eye-web`** | **Stale twice.** There are now four browser clients plus five service accounts plus three runtime-created kinds; and a per-client mapper fails `RealmIdentityTests.No_client_carries_its_own_mapper`. → D2. |
| 3. Set `Audience` and remove the override | **Stands.** FR-005, FR-006. |
| 4. Verify all integration tests still pass | **Stands, and is not sufficient.** No existing test mints from a runtime-created client, so the suite would stay green while webhook integrations broke. → FR-010. |

The issue's own framing also stands: this blocks no current attack vector. It
is defence in depth, and its value is that the next thing which *can* mint a
realm token — an SSO federation, a second product sharing the realm, a
misconfigured service account — no longer gets nine APIs for free.

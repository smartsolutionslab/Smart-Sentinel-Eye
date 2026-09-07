# Plan — Spec 090, the broker checks the audience

**Spec:** `specs/090-the-broker-checks-the-audience/spec.md`
**Issue:** #2085

---

## Shape of the change

One parser option, one constant, one integration test class, and four
documentation lines. Everything else on the list below exists to stop the
constant drifting or to stop the change taking the stack down.

```
jwt.Parse(password, set.Keyfunc,
    jwt.WithValidMethods([]string{"RS256"}),
    jwt.WithExpirationRequired(),
+   jwt.WithAudience(apiAudience),          // FR-001
)
```

`WithAudience` makes the claim **required** when an expected audience is
configured (golang-jwt v5 `validator.go`), so no separate presence check is
needed and none should be added — a hand-written `claims["aud"]` guard
alongside it would be a second, divergent implementation of the same rule.

---

## Not a bounded context

This is infrastructure. `src/AppHost/mosquitto/` is an AppHost container
asset, not a context: no Domain, no Application, no aggregate, no value
object, no domain event, no integration event, no migration, no EF model.
The DDD sections of the plan template are **N/A by construction**, and
saying so is cheaper than inventing a layer for a Go file.

**Boundary rules are untouched.** No project reference is added or moved, so
NetArchTest's cross-context rule is not in play. The one C# project this
touches is `tests/Integration.Tests`, which already references what it
needs.

---

## Where the audience literal lives, and the fourth spelling

`smart-sentinel-eye-api` is currently spelt three times on purpose:

| Spelling | File | Why it is separate |
|---|---|---|
| `AuthenticationDefaults.ApiAudience` | `src/ServiceDefaults/AuthenticationDefaults.cs:134` | what the nine APIs and the WHEP hook validate against |
| `RealmAudienceTests.ApiAudience` | `tests/Architecture.Tests/RealmAudienceTests.cs:45` | reads the realm file; an independent spelling so realm and services cannot drift in silence |
| `BearerAudienceTests` | `tests/Architecture.Tests/` | reads the options the services build |

This adds a **fourth**, in Go. It is not avoidable: Go cannot import a C#
constant, and this is the same reasoning `KeycloakScopeBundles` already
records for its own re-spelling under ADR-0051 — a duplicated string is
cheaper than the reference that would remove it.

**A fourth spelling nobody checks is how §II and §IV drifted.** FR-005 is
therefore not optional garnish: a test reads `jwt_auth.go` as text and fails
if its audience literal is not `AuthenticationDefaults.ApiAudience`. It
belongs in `tests/Architecture.Tests`, beside `RealmAudienceTests`, which is
already the place where realm-side and service-side spellings are held
together.

**Two mechanics that source-scanning tests in this repo have got wrong
before**, both already solved in `AgentBriefClaimTests`:

- Locate the file by walking up from `AppContext.BaseDirectory`
  (`AgentBriefClaimTests.RepositoryRoot()`), never by a relative path from
  the test binary.
- Normalise separators with
  `Path.GetRelativePath(...).Replace(Path.DirectorySeparatorChar, '/')`
  before comparing or reporting. A backslash literal is green on Windows and
  red on Linux CI.

---

## The test design

### The red: `MqttAudienceIntegrationTests`

New class in `tests/Integration.Tests/Identity/`, `[Collection(AspireCollection.Name)]`,
no `Category` trait — so it runs in CI's Docker integration job
(`ci.yml:176-179` filters only `Measurement`, `Disruptive`, `Maintenance`).

```
A_token_minted_without_the_api_audience_is_refused_by_the_broker   <- the red
A_token_minted_with_the_api_audience_still_connects                <- positive control
```

**The audience-less client.** Created through Keycloak's Admin REST API and
deleted in teardown. No test in this repository touches that API today
(grepped `admin/realms/` and `admin-cli` across `tests/`: zero hits), so this
is genuinely new plumbing and is the bulk of the slice's cost.

Mechanics, each checked against the tree rather than assumed:

- **Credentials.** `identity-admin`, secret `dev-only-identity-admin-secret`,
  in the realm file — the same client `HttpKeycloakAdminClient` authenticates
  as. Mint `client_credentials` and call
  `POST admin/realms/smart-sentinel-eye/clients`, mirroring
  `HttpKeycloakAdminClient.cs:58`.
- **Reaching Keycloak.** `aspire.CreateKeycloakClient()`. It targets Aspire's
  **proxied** endpoint and accepts the dev certificate. Do not reach the
  container's mapped port — the issuer differs and the tokens are then not
  the ones the stack uses.
- **The client body.** `serviceAccountsEnabled: true`, `publicClient: false`,
  `standardFlowEnabled: false`, and `defaultClientScopes:
  ["sse.events.publish"]` — deliberately without `sse-audience`.
  `defaultDefaultClientScopes` is **empty** in the realm file and
  `sse-audience` is not one of Keycloak's built-ins, so a client created
  without naming it genuinely lacks it. Verified from the realm JSON.
- **Guard the premise before asserting the conclusion.** Decode the minted
  token and assert its `aud` does **not** contain `smart-sentinel-eye-api`,
  *then* attempt CONNECT. Without that line, a Keycloak that quietly grants
  the scope turns the test into one that passes for the wrong reason —
  which is precisely the failure mode a negative test invites.
- **Teardown.** `DELETE admin/realms/{realm}/clients/{uuid}` in
  `IAsyncLifetime.DisposeAsync`. Unique `clientId` per run
  (`Guid.CreateVersion7()`), so a failed teardown leaves an inert,
  uniquely-named row rather than colliding with the next run.
- **Reading the refusal.** *(Corrected in phase 4a — this bullet previously
  said MQTTnet surfaces a refusal "as a non-`Success` `ResultCode` or as a
  thrown `MqttClientConnectingFailedException` depending on version and code
  path". Both halves were wrong, and the error was load-bearing.)* No such
  type exists; the real one is `MQTTnet.Adapter.MqttConnectingFailedException`,
  and in 5.2.0.1603 it carries **no** `ResultCode` property. Nor is the
  choice a disjunction — probed against a loopback server speaking raw CONNACK
  bytes:

  ```
  connack-rc5-not-authorized: RETURNED ResultCode=NotAuthorized
  connack-rc4-bad-user-pass:  RETURNED ResultCode=BadUserNameOrPassword
  no-connack-socket-closed:   THREW MQTTnet.Adapter.MqttConnectingFailedException
  ```

  **A CONNACK refusal is always *returned*; the exception means no CONNACK
  arrived at all.** That distinction is what T002 needs: "non-`Success` **or**
  the exception" would let *a broker that is down* satisfy the test, which is
  exactly what T002 forbids. So the test asserts a `ResultCode` was returned
  (a transport failure fails it) and only then that the code is not
  `Success` — refusal, not a specific code. Mosquitto's `MOSQ_ERR_AUTH` maps
  to `NotAuthorized` / `BadUserNameOrPassword` depending on protocol version;
  the test pins MQTT v3.1.1 as `NFR002_MqttConnectAuthTests` does.

### The characterisation, observed green before the change

`NFR002_MqttConnectAuthTests` registers a real device, mints its token and
opens 110 authenticated CONNECTs against the real broker. It is the positive
control for the whole slice and must pass **unmodified** afterwards. An
assertion that has to be edited is evidence the behaviour moved further than
intended — block, do not adjust.

`TokenAudienceIntegrationTests` (3 facts) must also stay green unmodified; it
is what proves the tokens this change now depends on actually carry the
claim.

---

## Ordering, and the one thing that must not be reordered

```
T001 ──> T002 ──> T003 (RED, verbatim)  ──> T004 ──> T005 (GREEN)
 │                                              │
 └──> T006 [P] ─────────────────────────────────┤
                                                ├──> T009 ──> T010 ──> T011
      T007 [P] ─────────────────────────────────┤
      T008 [P] ─────────────────────────────────┘
```

**T003 before T004 is the gate, not a preference.** The red test must be
observed failing against the *unmodified* plugin. Once `WithAudience` is in
the image, the red is unobtainable without reverting — and a Docker image
rebuild is the slowest revert in this repository.

**The documentation tasks (T006-T008) are `[P]`** — three disjoint files,
none of them Go, none of them read by the tests. They can run beside the
implementation. **The Go change and the test are not `[P]` with each other**:
they are two halves of one gate, in a fixed order.

There is no cross-context fan-out here. One context, one container asset —
the parallelism available is small and honest, and pretending otherwise would
just produce merge conflicts in `spec.md`.

---

## Files phase 4 will touch

| File | Change | Task |
|---|---|---|
| `src/AppHost/mosquitto/plugin/jwt_auth.go` | `apiAudience` const + `jwt.WithAudience` + header comment | T004, T006 |
| `tests/Integration.Tests/Identity/MqttAudienceIntegrationTests.cs` | **new** | T002 |
| `tests/Architecture.Tests/PluginAudienceLiteralTests.cs` | **new** — FR-005 | T009 |
| `src/AppHost/mosquitto/mosquitto.conf` | auth comment states the check set | T007 |
| `docs/design/scenario-simulator-m2.md` | line 68 states the check set | T008 |
| `specs/069-a-token-names-the-api-it-is-for/spec.md` | one row: `event-ingestion` stops being inert | T010 |

**Not touched, deliberately:** `plugin/go.mod`, `plugin/go.sum`
(`WithAudience` is in the pinned v5.3.1 — a diff here means someone ran
`go get` and the slice must stop); `Dockerfile`; `AppHost.cs`;
`smart-sentinel-eye-realm.json`; `RealmAudienceTests`;
`NFR002_MqttConnectAuthTests`; **`docs/adr/0100-mosquitto-go-auth.md`**.

---

## Risks

**R1 — the change refuses everything, and the symptom is silent.** A
mismatched literal, or the audience mapper not firing, refuses every CONNECT.
`event-ingestion` stops subscribing and MQTT ingestion goes dark — the exact
failure ADR-0100's 2026-08-06 addendum records happening once already, "for
long enough that MQTT ingestion was silently dark". *Mitigation:* the
positive control runs in the same class as the red, and
`NFR002_MqttConnectAuthTests` opens 110 real CONNECTs in the same CI job.
Both must be green before the PR is opened.

**R2 — a stale developer stack breaks after the merge.** Keycloak stores
`defaultClientScopes` at client creation and nothing in this codebase ever
updates them — the only `PUT` on a client in `HttpKeycloakAdminClient`
(`:147`) sets `enabled: false`. So a device registered before #91 merged on
2026-09-05 keeps minting `aud`-less tokens and will be refused. It survives
only in a persistent Keycloak data volume (`AppHost.cs:122-128`, run mode,
non-e2e). *Mitigation:* FR-007 — the volume-deletion instruction goes in the
PR body and the verification note, not only in the spec. CI and e2e import a
fresh realm every run and are unaffected. There is no production deployment,
so no production device population exists.

**R3 — the throwaway client outlives the test.** A failed teardown leaves an
audience-less service-account client in the dev realm. *Mitigation:* unique
`clientId` per run; the client has one scope (`sse.events.publish`) and, once
this change lands, **cannot connect to the broker** — it is inert by
construction. It is also not in the realm file, so `RealmAudienceTests` is
not affected. A `docker volume rm` clears it, which R2 already requires.

**R4 — the red is obtained for the wrong reason.** A misconfigured throwaway
client can fail CONNECT because `azp` mismatched, because the secret was
wrong, or because the broker was not up. *Mitigation:* T003 does not accept
"it failed"; it requires the decoded token to show a valid signature path and
a **matching `azp`** and a **missing audience**, so the only remaining
difference from the passing case is the one under test.

**R5 — nobody applies ADR-0100's correction.** The lane may not edit an ADR,
so T012 writes the correction as text and stops. If it is never applied,
ADR-0100 keeps enumerating a check set that is missing one entry. *Not
mitigated by this slice* — it is surfaced for the human, and named here so it
is not discovered later as drift.

---

## Gate (Phase 2)

The plan adds no architecture. It implements the audience posture ADR-0007 /
ADR-0008 and spec 069 already established, at the one surface that was left
out. No ADR is written or amended (§*The ADR question* in `spec.md`); the one
ADR edit the change implies is handed to a human as T012. No constitution
section is touched: this is off the §IV latency path and NFR-002 is
unaffected.

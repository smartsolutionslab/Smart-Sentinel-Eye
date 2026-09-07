# Tasks — Spec 090, the broker checks the audience

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2085
**Engineer:** `infra-engineer` (Go source, a Mosquitto config, an Aspire-fixture
test). Phase 4a's red test is written by `test-writer` first, per ADR-0144 —
the engineer receives its verbatim output as its brief and **may not edit the
test to pass**.

**Phase 4a colour: RED.** Behaviour-changing. A test that arrives green is a
phase-4 failure.

**Requires the Aspire stack** for T003 and T005. One stack at a time.

---

## Foundational — blocks everything

### T001 — Admin REST plumbing for the test project

`tests/Integration.Tests/` has **no** Keycloak Admin REST helper (grepped
`admin/realms/`, `admin-cli`: zero hits). Add the minimum:

- Mint `client_credentials` for `identity-admin` /
  `dev-only-identity-admin-secret` through `aspire.CreateKeycloakClient()`.
- `POST admin/realms/smart-sentinel-eye/clients` — mirror the body shape at
  `src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:58`.
- `GET admin/realms/{realm}/clients?clientId=…` for the UUID (mirror `:158`),
  `GET …/clients/{uuid}/client-secret` for the secret (mirror `:194`).
- `DELETE admin/realms/{realm}/clients/{uuid}`.

Keep it in the test class or a private helper. **Do not** add it to
`AspireFixture` — one test needs it, and a fixture method is a standing
invitation to mutate the shared realm, which is what spec 069 refused.

*Done when:* it compiles and the four calls are exercised by T002's test
body. No behaviour asserted yet.

**Blocks:** T002.

---

### T002 — Write the red test (test-writer)

New `tests/Integration.Tests/Identity/MqttAudienceIntegrationTests.cs`.
`[Collection(AspireCollection.Name)]`, **no `Category` trait** — that is what
puts it in CI's Docker integration job (`ci.yml:179`).

```
A_token_minted_without_the_api_audience_is_refused_by_the_broker
A_token_minted_with_the_api_audience_still_connects
```

The audience-less client: `serviceAccountsEnabled: true`,
`publicClient: false`, `standardFlowEnabled: false`,
`defaultClientScopes: ["sse.events.publish"]`, `clientId` unique per run.
Deleted in `DisposeAsync`.

**Assert the premise before the conclusion** — three lines, in this order:

1. the minted token's `aud` does **not** contain `smart-sentinel-eye-api`;
2. its `azp` **equals** the MQTT username it is about to present;
3. only then, CONNECT.

Without (1) and (2) a refusal proves nothing: `azp` mismatch and a broker
that is down both produce the same red.

Pin MQTT v3.1.1 and assert **refusal**, not a specific `ResultCode` —
MQTTnet surfaces `MOSQ_ERR_AUTH` as `NotAuthorized` or
`BadUserNameOrPassword` depending on path. A transport error must **fail**
the test, not satisfy it.

The positive control registers a device through
`POST /devices/register?fabId=munich` and expects `CONNACK Success` — the
same route `TokenAudienceIntegrationTests` already uses.

*Done when:* both tests compile and run. **Not** when they pass.

**Blocks:** T003. **Depends on:** T001.

---

## The red run — the gate between the halves

### T003 — Observe the red, and check it is the *right* red

Boot the Aspire stack. Run `MqttAudienceIntegrationTests`. **Capture the
verbatim output.**

Expected before the change:

- `A_token_minted_without_the_api_audience_is_refused_by_the_broker` —
  **FAILS**, because the broker accepts the connection.
- `A_token_minted_with_the_api_audience_still_connects` — **PASSES**.

**One passing and one failing is the signal.** Two failures means the harness
is broken, not the plugin — most likely the throwaway client did not get
created, or `azp` did not match. Diagnose before proceeding; a red obtained
for the wrong reason is worse than no red.

Also capture `NFR002_MqttConnectAuthTests` **green** in the same run. That is
the characterisation baseline, and it is only meaningful taken *before* the
change.

*Done when:* the verbatim failure of the first test and the verbatim pass of
the other two are recorded for the PR body (ADR-0139).

**Blocks:** T004.

---

## The change

### T004 — `jwt.WithAudience` on the parse

`src/AppHost/mosquitto/plugin/jwt_auth.go`.

Add beside `defaultRealmPath`:

```go
// The API every token in this realm is minted for. A fourth spelling of a
// literal ServiceDefaults, RealmAudienceTests and BearerAudienceTests
// already carry — Go cannot import a C# constant.
// PluginAudienceLiteralTests holds the four together.
const apiAudience = "smart-sentinel-eye-api"
```

and one option on the existing `jwt.Parse` at `:151`:

```go
jwt.WithAudience(apiAudience),
```

**Do not** add a hand-written `claims["aud"]` check: `WithAudience` already
makes the claim required, and a second implementation of the same rule is how
the two diverge.

**Do not touch** `go.mod` or `go.sum` — `WithAudience` is in the pinned
v5.3.1. A diff there means someone ran `go get`; stop and report.

**Do not touch** line 172's issuer check. That is #TBD, and FR-004 says so.

*Done when:* the file diff is exactly one const, one option, and T006's
comment.

**Depends on:** T003. **Blocks:** T005.

---

### T005 — Green, and what the green is worth

Rebuild the image (the plugin is compiled in `Dockerfile` stage 2 — a
restart is not enough), boot, and re-run.

- `A_token_minted_without_the_api_audience_is_refused_by_the_broker` — passes.
- `A_token_minted_with_the_api_audience_still_connects` — still passes.
- `NFR002_MqttConnectAuthTests` — passes **unmodified**. If an assertion has
  to be edited, the behaviour moved further than intended: **block, do not
  adjust.**
- `TokenAudienceIntegrationTests` (3 facts) — passes unmodified.

*Done when:* all of the above, with output captured.

**Depends on:** T004. **Blocks:** T009, T011.

---

## Documentation — three disjoint files, parallel with the change

### T006 [P] — `jwt_auth.go` header states the check set

The header comment (`:1-21`) enumerates what is validated. Add the audience.
Same file as T004 — do them in one commit.

### T007 [P] — `mosquitto.conf` auth comment

`src/AppHost/mosquitto/mosquitto.conf:6-10` describes what the plugin
validates. Add the audience.

**Also correct "go-auth"** in that comment to name the custom plugin. The
name is inherited from ADR-0100's title and it is what led #2085 to ask a
vendor-capability question about a vendor this project does not use.
Comment only — **do not rename the ADR file**, and do not touch
`AppHost.cs:205` (a different file, no test depends on it, and it is not
worth widening the diff).

### T008 [P] — `docs/design/scenario-simulator-m2.md:68`

The MQTT-auth row states `azp == MQTT username` and the issuer suffix. Add
the audience. Leave the issuer wording exactly as it is — it is accurate, and
changing it would imply #TBD landed.

---

## The guard, and the record

### T009 — FR-005: the fourth spelling cannot drift

New `tests/Architecture.Tests/PluginAudienceLiteralTests.cs`.

Read `src/AppHost/mosquitto/plugin/jwt_auth.go` as text; extract the
`apiAudience` const; assert it equals
`AuthenticationDefaults.ApiAudience`. Fail with a message that names both
files and both values.

Two mechanics, both already solved in `AgentBriefClaimTests` and both
previously got wrong in this repo:

- find the repo root by walking up from `AppContext.BaseDirectory`
  (`AgentBriefClaimTests.RepositoryRoot()`), never a relative path from the
  test binary;
- normalise separators with
  `Path.GetRelativePath(...).Replace(Path.DirectorySeparatorChar, '/')`
  before comparing or reporting — a backslash literal is green on Windows and
  red on Linux CI.

*Done when:* it passes, **and** T011 has shown it can fail.

**Depends on:** T005.

---

### T010 [P] — Correct spec 069's inventory row

`specs/069-a-token-names-the-api-it-is-for/spec.md:56`. The `event-ingestion`
row says *"no — Mosquitto only, and the plugin does not read `aud`"* and
marks the audience **(inert)**. Both halves are now false.

Change **that row only**. `identity-admin` and `migration-runner` reach only
the Keycloak Admin REST API and stay inert — a sweep that marks all three
would be wrong.

Add one line naming spec 090 as what changed it, so the correction is
traceable rather than looking like a typo fix.

---

### T011 — Prove the guard by counterfactual

T009 passing proves nothing on its own — a guard that cannot fail is the
defect this repo has found repeatedly.

Change the Go const to `"smart-sentinel-eye-apx"`. Confirm
`PluginAudienceLiteralTests` **fails**, and that the message names both files
and both values. Revert.

**Restoring a file keeps its old timestamp** and MSBuild will skip the
rebuild, so the test can still show the failure after the revert. `touch` the
file, or rebuild explicitly, before believing the green.

Quote the counterfactual failure in the PR body.

**Depends on:** T009.

---

### T012 — ADR-0100's correction, written down and handed over

**The lane may not edit an ADR.** ADR-0100's 2026-05-31 addendum enumerates
the check set:

> It enforces `azp == username` (the device's Keycloak client_id) and that
> the issuer is our realm, plus `exp`.

That sentence goes stale with T004. Write the proposed one-line correction
**in the PR body** — naming the ADR, the line, and the replacement text —
and **stop**. Do not edit `docs/adr/0100-mosquitto-go-auth.md`.

A human applies it or declines it. The slice is complete either way; what is
at stake is whether ADR-0100 keeps saying something true.

---

### T013 — Verification note (phase 5)

Run the independent end-to-end procedure in `spec.md` from a **fresh
Keycloak volume**. Record:

- the decoded `aud` of a registered device's token (the premise);
- `CONNACK Success` for it (the positive control);
- the refusal for the audience-less client (the behaviour);
- `NFR002_MqttConnectAuthTests`'s reported p50/p99 — **reported, not gated**
  off-CI (#1905), and cited only as evidence NFR-002 was not eroded.

**FR-007 — the note and the PR body must both carry this instruction:**

> After merging, delete the Keycloak data volume before the next run-mode
> boot. A device client registered before 2026-09-05 was created without
> `sse-audience`, Keycloak stores default scopes at creation time, and
> nothing in this codebase updates them — so it will be refused at CONNECT.
> Restarting the Keycloak container is **not** enough; it keeps the old realm
> and the stack looks perfectly healthy.

**Latency:** N/A — not on the §IV event-to-overlay path. NFR-002 is a
separate budget and is unaffected (a string comparison on an already-parsed
claim; no I/O, no allocation beyond claim parsing).

---

## Commits

Each must build on its own (ADR-0087). Conventional Commits, **no
`Co-Authored-By` footer and no session trailer** (ADR-0086).

1. `test(integration): the broker is asked for a token minted elsewhere` — T001, T002
2. `fix(mosquitto): the plugin requires the audience the APIs require` — T004, T006
3. `docs(mosquitto): the check set says what it checks` — T007, T008
4. `test(architecture): the plugin's audience cannot drift from the APIs'` — T009
5. `docs(specs): 069 — event-ingestion's audience stops being inert` — T010

Commit 1 lands the red test **passing-as-red is not a thing** — it lands with
the negative failing only in the working tree, never on a branch tip. Land
commits 1 and 2 together if CI would otherwise see a red suite.

---

## Not in scope

- **The issuer suffix match** (`jwt_auth.go:172`). Its own issue — the
  expected issuer is not known to the plugin, `jwt.WithIssuer` is an exact
  match, and the JWKS URI the plugin holds is the **container-reachable**
  Keycloak host while every token is minted through Aspire's **proxied**
  endpoint. Tightening it needs a new configured input and a booted-stack
  observation of the real `iss`. Bundling it would risk the broker refusing
  everything with no way to tell which check did it.
- **Renaming ADR-0100** away from "go-auth". A filename is a stable
  reference; renaming it is a decision.
- **The publish ACL** (scope/groups/topic binding), still unenforced per
  ADR-0100's scope note. Unrelated.
- **Back-filling `sse-audience` onto pre-2026-09-05 clients.** The population
  is developer volumes; FR-007's deletion is the whole remedy, and a
  migration for it would be more code than the thing it fixes.

---

## Gate (Phase 3)

Tasks are atomic and ordered; T003 is a hard gate between the halves. Two
`[P]` groups (T006-T008 documentation; T010 alongside) — the parallelism is
genuinely small, because this is one file in one container asset and
pretending otherwise would only produce conflicts.

**Board:** #2085 is already on Project #13 (In Progress) and carries
`agent:ready`. No per-task issues — that stopped after spec 028. One new
issue must be **filed** for the issuer suffix match before this closes, and
`#TBD` throughout these artifacts replaced with its number.

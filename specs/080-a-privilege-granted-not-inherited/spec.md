# Spec 080 — A privilege granted, not inherited

**Issue:** #1995 · **Branch:** `fix/1995-offline-access-is-granted-not-inherited`
**Phase:** 1 (Specify) · **Date:** 2026-09-06
**ADRs:** ADR-0007 / ADR-0008 (Keycloak per fab), **ADR-0134** (the decision
this reverses — it filed #1995 as declined), ADR-0132 (superseded, and one of
its recorded measurements is corrected below), ADR-0131 (a kiosk keeps its own
grant), ADR-0067 (`MigrationRunner` as the one-shot pre-API worker), ADR-0024
(Aspire is the composition root), ADR-0103 (integration against the Aspire
fixture, no Testcontainers), ADR-0052 (xUnit + Shouldly), ADR-0036 (smallest
change), ADR-0037 (phased workflow), ADR-0109 (parallel markers), ADR-0139
(new behaviour starts red), ADR-0144 (autonomous lane — **and the reason this
spec cannot be implemented without a human first, see §Declarations**).

**Latency budget (constitution §IV): N/A.** Nothing here touches the
event→overlay path. Realm role composition is read once at sign-in, and wall
sign-in is not one of the six legs.

---

## 1. The decision, as given

The repository owner decided on 2026-09-06:

- **Remove `offline_access` from the `default-roles-smart-sentinel-eye`
  composite**, so no account inherits it by existing.
- **Grant it explicitly, by name, to the four `wall-*` accounts** that need it.

Rationale on record: the privilege is invisible at the point of account
creation, which is how a hand-created account acquired it unnoticed. Four named
grants make the exception reviewable, and make a fifth an edit somebody has to
justify.

**Not re-opened here.** Removing the privilege from the wall accounts too was
considered and rejected — unattended kiosk reboot is why it exists, and #1976
records that the kiosk app does not yet use its enrolled device credentials.
This spec does not enter #1976's territory.

---

## 2. The premise, re-established

Established twice: against **the file**, and against a **fresh import** of that
file into Keycloak 26.6 (`quay.io/keycloak/keycloak:26.6`, `start-dev
--import-realm`, throwaway container, no reused volume — a long-lived dev
Keycloak reuses its volume and would prove nothing about a file edit).

### 2.1 What the file declares

`src/AppHost/Realms/smart-sentinel-eye-realm.json`:

| Line | Declaration |
|---:|---|
| 14–19 | `"roles": { "realm": [ {"name":"user"}, {"name":"admin"} ] }` — **two** realm roles |
| 20 | `"defaultRoles": ["user"]` — the legacy field |
| 359, 381, 403, 425 | `"realmRoles": ["user", "offline_access"]` on `wall-munich`, `wall-dresden`, `wall-berlin`, `wall-hamburg` |
| — | **`default-roles-smart-sentinel-eye` does not appear in this file at all.** |

### 2.2 What a fresh import actually produces

```
realm roles defined:  admin, default-roles-smart-sentinel-eye, offline_access, uma_authorization, user
default-roles-smart-sentinel-eye -> manage-account, offline_access, uma_authorization, user, view-profile
realm.defaultRole                -> default-roles-smart-sentinel-eye
legacy "defaultRoles" field after import: absent
```

Keycloak **synthesises** the composite, merges the legacy `defaultRoles: ["user"]`
into it rather than replacing it, and puts `offline_access` inside. All 12
declared users, effective realm roles:

```
admin                     [admin,user]        op-hamburg@…    [user]
admin@munich.test         [admin,user]        op-multi@…      [user]
op-3@munich.test          [user]              operator        [user]
op-berlin@…               [user]              wall-berlin     [offline_access,user]
op-dresden@…              [user]              wall-dresden    [offline_access,user]
op-hamburg@…              [user]              wall-hamburg    [offline_access,user]
                                              wall-munich     [offline_access,user]
```

**A declared user receives exactly the roles it names and inherits nothing** —
note none of them holds `uma_authorization` either. So the four wall grants are
already explicit; **half the decision is already true in the file.**

An account created **after** import is the population at issue:

```
NEW USER            effective: default-roles-…, offline_access, uma_authorization, user
NEW SERVICE ACCOUNT effective: default-roles-…, offline_access, uma_authorization, user
```

**The premise holds.** The earlier phase-3 pass's finding is confirmed, now
against a fresh import rather than a long-lived dev realm.

### 2.3 The correction — ADR-0132's recorded measurement is no longer true

ADR-0132:173–181 records, and `tests/Architecture.Tests/WallDisplayAccountTests.cs:34–38`
repeats, that a declared `default-roles-*` composite is **"discarded on import,
which was tried and verified"** — "the stored role came back with an empty
description and the provider's own five composites".

**That is not what Keycloak 26.6 does.** Measured, four variants, each a fresh
container:

| Variant | Result |
|---|---|
| Composite declared, referencing `uma_authorization` (not declared as a role) | **Keycloak refuses to boot.** `ERROR: Unable to find composite realm role: uma_authorization`, exit 1. Import aborts; nothing starts. |
| Same, plus the legacy `defaultRoles` field kept | identical hard failure |
| Composite declared **with every referenced role also declared** | boots, `Import finished successfully` |
| Minimal composite naming only `user` | boots, `Import finished successfully` |

For the two that boot, the declaration **is honoured** — and then orphaned:

```
[declared+complete]  stored description: "Default privileges. offline_access deliberately absent."
                     default-roles-smart-sentinel-eye    -> manage-account, uma_authorization, user, view-profile
                     default-roles-smart-sentinel-eye-1  -> manage-account, offline_access, view-profile
                     realm.defaultRole                   -> default-roles-smart-sentinel-eye-1   <<<
                     NEW ACCOUNT inherits offline_access: TRUE
```

Keycloak creates a **second** role, `default-roles-smart-sentinel-eye-1`, and
binds the realm's `defaultRole` to *that* one — which carries `offline_access`.
The declared role is stored, correct, readable by name, and **governs nothing**.

**This is the single most dangerous finding in the spec.** The naive fix
produces a realm in which `GET /roles/default-roles-smart-sentinel-eye/composites`
returns exactly what you wanted to see, while every new account still inherits
the privilege. A guard that reads the composite *by its expected name* goes
green on a broken realm.

**So the conclusion of ADR-0132 survives even though its measurement did not:
the realm file cannot close this.** It fails two ways — a hard boot failure, or
a silent decoy.

### 2.4 What does work, and what authority it needs

Post-import, over the Admin API:

```
DELETE /admin/realms/smart-sentinel-eye/roles/default-roles-smart-sentinel-eye/composites
body: [ <the offline_access role representation> ]
```

Measured against the freshly imported realm:

| Principal | `GET` composites | `DELETE` composite |
|---|---|---|
| `identity-admin` **as declared today** | **403** | **403** |
| `identity-admin` + `realm-management/view-realm` | 200 | **403** |
| `identity-admin` + `realm-management/manage-realm` | 200 | **204** |
| master-realm bootstrap admin | 200 | 204 |

After a successful removal:

```
composite now:              manage-account, uma_authorization, user, view-profile
NEW ACCOUNT effective:      default-roles-smart-sentinel-eye, uma_authorization, user
  >>> inherits offline_access: FALSE
wall-munich  effective:     offline_access, user        <-- unchanged
```

`DELETE` is **idempotent**: it answers 204 whether or not the member is present,
so the step is safe to run on every boot, including against a persistent dev
volume where a previous run already applied it.

**`manage-realm` is the minimum**, and ADR-0134 was right that it is broad —
it is authority over roles, session lifetimes and authentication flows alike.
`view-realm` is enough to *read* and therefore enough to *verify*, which matters
for §5.

---

## 3. User stories

### US1 (P1) — An account created after import does not inherit the privilege

**As** the person who reviews who can mint a credential that never expires,
**I want** a newly created account to hold no long-lived-credential privilege,
**so that** an account made by hand in the provider's console is not silently
privileged.

This is the whole feature and it ships alone.

### US2 (P1, same slice) — A wall display still holds it, by name

**As** a fab operator, **I want** each fab's wall display to keep its
never-expiring grant, **so that** a screen still comes back from an unattended
reboot.

**Not separable from US1.** US1 without US2 is an outage; US2 is what stops US1
being satisfied by a realm in which nobody holds the privilege at all. They are
one vertical slice with two directions of assertion.

### Out of scope

- **#1976** — the kiosk app using its enrolled device credentials.
- **Withdrawing the privilege from wall displays.** Rejected; see §1.
- **`KioskPrivilegeSweep` is unregistered dead code.** `src/Identity/Application/KeycloakAdmin/KioskPrivilegeSweep.cs`
  exists, is unit-tested, and has **no `AddHostedService` registration anywhere**,
  though `specs/052-wall-past-its-ceiling/tasks.md:37` marks T005 done. The
  spec-052 backfill over already-enrolled kiosks therefore never ran. **Real
  defect, found here, not fixed here** — file it separately.
- **Production.** There is no production realm.

---

## 4. Acceptance scenarios

### AS-1 — Happy path: the inherited privilege is gone

```gherkin
Given the realm has been imported fresh from smart-sentinel-eye-realm.json
  And the post-import narrowing step has completed
 When an account is created through the Admin API after import
 Then its effective realm roles do not include "offline_access"
  And they do include "user" and "uma_authorization"
```

### AS-2 — The safety-critical direction: a wall display keeps it

```gherkin
Given the realm has been imported fresh and narrowed
 When the effective realm roles of "wall-munich" are read
 Then they include "offline_access"
  And the same holds for wall-dresden, wall-berlin and wall-hamburg
```

### AS-3 — Observable end behaviour: a wall can still mint an offline token

```gherkin
Given the narrowing step has run
 When wall-munich signs in at kiosk-wall requesting scope "openid offline_access"
 Then the sign-in succeeds
  And the stored refresh token carries typ "Offline" with no "exp" claim
```

### AS-4 — The decoy is refused (the §2.3 trap)

```gherkin
Given the realm has been imported fresh
 When the realm's defaultRole is read
 Then its name is exactly "default-roles-smart-sentinel-eye"
  And no realm role named "default-roles-smart-sentinel-eye-1" exists
```

This is the assertion that separates "the composite we edited" from "the
composite the realm actually uses". Without it, AS-1 can be satisfied by reading
an orphan.

### AS-5 — Conflict / re-run: the step is idempotent

```gherkin
Given the narrowing step has already removed the privilege from the composite
 When the step runs a second time against the same realm
 Then it completes successfully and changes nothing
  And an account created afterwards still does not inherit "offline_access"
```

### AS-6 — Bad request / boot integrity: the realm file still imports

```gherkin
Given smart-sentinel-eye-realm.json after this change
 When Keycloak imports it into an empty container
 Then the import finishes successfully and the server starts
  And no client description exceeds 255 characters
```

The realm file is a contention file: a 733-character client description has
previously failed the whole import (ADR-0134), and §2.3 shows a malformed
composite declaration does the same. This scenario is the guard on the edit
itself.

### AS-7 — Auth: the narrowing step is refused without authority

```gherkin
Given a principal holding realm-management view-realm but not manage-realm
 When it attempts to remove offline_access from the default composite
 Then the provider answers 403 and the composite is unchanged
```

Measured (§2.4). Worth asserting because the failure mode otherwise is a step
that logs a 403 and lets every account keep the privilege.

### AS-8 — Regression: no operator gains anything

```gherkin
Given the realm has been imported fresh and narrowed
 When the effective realm roles of "operator" are read
 Then they are exactly ["user"]
```

---

## 5. What a test can honestly assert — the crux

Three tiers, and only one of them makes the claim this feature is about.

### Tier 1 — reading the realm file. **Cannot make the claim at all.**

`tests/Architecture.Tests/WallDisplayAccountTests.cs` reads the file and asserts
the four `wall-*` grants exist and reach nobody else. That is real and worth
keeping — **the four grants are genuinely a file fact** (§2.1), so for US2 a
file test is not a proxy, it *is* the artifact.

For US1 it is worse than weak: **there is nothing in the file to read.** The
composite is not declared, and §2.3 shows that declaring it either kills the
boot or creates a decoy. A file test for US1 would be the "guards that read the
design artefact" failure in its purest form — and the repo has already shipped
exactly that once, which `KioskInheritedPrivilegeIntegrationTests`'s own class
comment records ("its architecture guard read the realm file, stayed green for
the entire feature, and the claim it stood for was false throughout").

**So: no new architecture test asserts anything about the composite.** Existing
file-reading tests stay as they are, with two prose corrections (§7).

### Tier 2 — asking the running provider. **This is the claim.** Buildable today.

`tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs`
already has every piece: an admin client against the fixture's Keycloak, helpers
that create and delete a throwaway principal, and
`role-mappings/realm/composite` — the endpoint that resolves composites, which
is what actually decides whether a grant is issued.

The fixture is the honest surface: `AspireFixture` boots with `E2ETests=true`
(`tests/Integration.Tests/Fixtures/AspireFixture.cs:124`), which makes Keycloak
**ephemeral with no data volume** (`src/AppHost/AppHost.cs:119-128` applies
`WithDataVolume()` only when `isRunMode && !isE2ETests`). So the integration
suite always gets a fresh import. A realm-file edit reaches it immediately.

What Tier 2 proves: **that a real account, created by the real provider, from
the real realm, does not hold the privilege.** That is the requirement verbatim.

What Tier 2 does **not** prove: anything about a realm whose volume predates the
change (a developer's persistent dev Keycloak keeps the old realm and the old
composite — this is a documented trap, and the narrowing step being idempotent
and boot-time is what covers it in practice); anything about production, which
does not exist; and that a token is actually minted — that is Tier 3.

**AS-4 is the assertion that makes Tier 2 trustworthy.** Read
`realm.defaultRole.name` and assert no `-1` sibling exists. Without it a future
edit that declares the composite in the file would leave every other assertion
green.

### Tier 3 — end-to-end. Already exists; must stay green untouched.

`e2e/wall-outlives-its-session.spec.ts:62-65` signs in as `wall-munich` at
`kiosk-wall` and asserts the stored refresh token decodes to `typ: 'Offline'`
with `exp` undefined. That is AS-3, already written, already covering US2's
observable behaviour. It must pass **unmodified** after this change. If it goes
red, the wall accounts lost the privilege — which is exactly the failure §6
asks about.

### Summary

| Claim | Tier 1 (file) | Tier 2 (running) | Tier 3 (e2e) |
|---|---|---|---|
| Four wall accounts are granted it by name | **proves it** | corroborates | — |
| A new account does not inherit it | **impossible** | **proves it** | — |
| The composite in force is the one we narrowed | impossible | **proves it (AS-4)** | — |
| A wall can still mint an offline token | no | corroborates | **proves it** |

---

## 6. What breaks if the wall accounts lose it by accident

The four grants are the safety-critical half, and the failure is loud rather
than silent — which is the good news.

**Observable behaviour.** `kiosk-wall` sign-in requests `openid offline_access`
unconditionally (`apps/kiosk-web/src/app/auth.ts:50-52`, gated by
`VITE_KIOSK_MODE=wall`). A requested scope the account cannot satisfy fails the
**entire sign-in**, not just the offline part: ADR-0132 records the exact
provider answer, `not_allowed: Offline tokens not allowed for the user or
client`, measured when the scope was briefly made a *default* on the client. So
a wall display that lost the grant does not degrade to short sessions — **it
drops to a sign-in prompt and never gets past it.** A dark wall, immediately.

**Can a test assert it?** Yes, at two tiers, and both already exist or are one
assertion away:

- Tier 2, AS-2: `A_wall_display_account_does_hold_it` already asserts
  `wall-munich` holds it (`KioskInheritedPrivilegeIntegrationTests.cs:121-129`).
  **Widen it to all four** — one fab covered and three dark is a real outcome
  that the single-account version reads as ruled out, and
  `WallDisplayAccountTests.Every_fab_has_a_wall_display_account_that_holds_it`
  already makes exactly that argument about the file.
- Tier 3, AS-3: `wall-outlives-its-session.spec.ts` fails at sign-in.

**Why the risk is low here specifically.** The grants are direct
(`realmRoles: ["user","offline_access"]`) and a declared user bypasses the
composite entirely — measured in §2.4: after the removal, `wall-munich` still
reads `offline_access, user`. The narrowing step touches the composite only and
cannot reach a directly-assigned role. The realistic way to break this is
editing the four user blocks by hand, which Tier 1 catches at build time.

---

## 7. Blast radius

### 7.1 The one hard test failure

`tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs:69-101`,
`An_account_the_provider_creates_holds_it_until_something_removes_it`, asserts
against the **running** provider that a freshly created client's service account
**does** hold `offline_access`. It goes red the moment the composite stops
carrying it.

**It must be inverted, and that is not weakening a gate.** Its purpose is to
stop its sibling (`A_kiosk_enrolled_at_runtime_does_not_hold_…`) passing
vacuously — "the assertion above would prove nothing if a new account never held
it". After this change the sibling *does* pass vacuously, because nothing is
left to strip. So:

- The old control asserted **the world this feature changes**. Inverting it is
  the feature, expressed as a test.
- **The anti-vacuity duty is preserved by a different control**: `A_wall_display_account_does_hold_it`
  (AS-2) still guarantees the privilege exists and is held by somebody, so
  "nobody holds it at all" cannot make AS-1 green.

Phase 4 must state this explicitly in the PR body, because "a deleted or
inverted test" is otherwise indistinguishable from the thing ADR-0144 forbids.

### 7.2 Nothing else depends on inheritance

Searched `tests/`, `e2e/`, `scripts/`, `apps/`, `src/`, `.github/`, `build/`,
`deploy/`, `docs/` for `offline_access`, `offline-access`, `default-roles`,
`defaultRoles`, `uma_authorization`, `refresh_token`.

- **Every offline/refresh-token path already uses a `wall-*` principal**, all
  four `wall-munich`: `e2e/wall-outlives-its-session.spec.ts`,
  `e2e/wall-withdrawal.spec.ts`, `e2e/wall-authority.spec.ts`. **No test
  anywhere mints an offline token as a non-`wall-*` account.**
- Non-wall e2e (`e2e/support/sign-in.ts`, `e2e/support/kiosk-session.ts`,
  `e2e/kiosk-reconciliation.spec.ts`) all sign in as `operator` requesting
  `openid` only — no lockout risk.
- `wall-withdrawal.spec.ts:145-148` mints a **master-realm** `admin-cli` token;
  outside this realm, untouched.
- `uma_authorization` appears in prose only (ADR-0132, spec 052 quickstart).
  No code, no test.
- `RealmIdentityTests.cs:77` `ProvidedByKeycloak = ["offline_access"]` concerns
  the **client scope**, not the realm role. Keycloak creates that scope per
  realm regardless of composite membership, and `kiosk-wall` names it as an
  *optional* scope (realm file L249). **Unaffected — do not touch it**, and say
  so in the PR, because it is a near-miss a reviewer will flag.

### 7.3 `StripInheritedRealmRolesAsync` becomes belt-and-braces — keep it

`src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:85`
(implementation `:319-351`) removes **every directly-assigned realm role** from
a newly created service account, which in practice is the single
`default-roles-smart-sentinel-eye` mapping. It fires for all three callers —
`EnrollKioskCommandHandler.cs:49`, `RegisterDeviceCommandHandler.cs:68`,
`RotateWebhookClientCommandHandler.cs:117`.

After this change it still runs, still succeeds, and removes nothing
privileged. **Keep it**: it costs two calls, it guards drift, and it closes the
window in §8.

### 7.4 Prose that now describes a world that will not exist

Compiles fine; asserts a false premise in comments. **Correct only what this
change makes wrong, and no more** (ADR-0036):

| File | What it claims |
|---|---|
| `tests/Architecture.Tests/WallDisplayAccountTests.cs:22-39` | the composite "**includes the offline privilege**" and "**the realm file cannot close it** … so it needs a step after import, and nothing here can guard it" — the second half stays true, the first becomes false, and §2.3 corrects *why* the file cannot close it |
| `tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs:11-29` | "The realm hands every account created after import a default privilege that includes `offline_access`" |
| `src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:80-85` | "Take back what the realm gave it for free" |
| `src/Identity/Application/KeycloakAdmin/IKeycloakAdminClient.cs:109-134` | same premise |
| `apps/kiosk-web/src/app/auth.ts:83-89` | same premise |

Left alone deliberately: `docs/adr/0131`, `docs/adr/0132`, `specs/052/*`,
`specs/050/*`. **An ADR and a merged spec are historical records** — they say
what was true when written. The correction belongs in the new ADR (§9), not in
edits to old ones.

### 7.5 The contention file

`src/AppHost/Realms/smart-sentinel-eye-realm.json` gets **one** edit: adding
`manage-realm` to `service-account-migration-runner`'s `realm-management`
client roles (currently `["query-groups","view-users"]`, L546-555). No client
description is touched; AS-6 guards the import.

---

## 8. Independent end-to-end test procedure

Runnable by a person, no test framework, on a machine with Docker:

```sh
# 1. Fresh Keycloak, fresh import of the realm file under test — no reused volume.
docker run -d --name sse-verify -p 18080:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  -v "<abs path to src/AppHost/Realms>:/opt/keycloak/data/import:ro" \
  quay.io/keycloak/keycloak:26.6 start-dev --import-realm

# 2. It must actually boot. AS-6.
docker logs sse-verify 2>&1 | grep -E "Import finished successfully|ERROR"
```

Then, with a master admin token:

3. `GET /admin/realms/smart-sentinel-eye` → `defaultRole.name` is exactly
   `default-roles-smart-sentinel-eye`, and `GET .../roles` contains **no**
   `default-roles-smart-sentinel-eye-1`. **(AS-4)**
4. Run the narrowing step (boot the stack, or issue the `DELETE` by hand).
5. `GET /roles/default-roles-smart-sentinel-eye/composites` → `manage-account,
   uma_authorization, user, view-profile`. No `offline_access`. **(AS-1)**
6. `POST /users` a throwaway account; `GET /users/{id}/role-mappings/realm/composite`
   → no `offline_access`. Delete it (expect 204). **(AS-1)**
7. For each of `wall-munich`, `wall-dresden`, `wall-berlin`, `wall-hamburg`:
   same endpoint → **contains** `offline_access`. **(AS-2)**
8. `operator` → exactly `["user"]`. **(AS-8)**
9. Re-run step 4. Expect success and no change. **(AS-5)**
10. Boot the wall app and sign in as `wall-munich`; decode the stored
    `refresh_token` → `typ: "Offline"`, no `exp`. **(AS-3)**
11. `docker rm -f sse-verify`.

Steps 1–9 need only the Keycloak container and cost about four minutes. Step 10
needs the stack.

---

## 9. Declarations

### 9.1 Which engineer

**`infra-engineer`.** The work is the realm JSON, a `MigrationRunner` migrator,
and Aspire composition — all three named in that brief. The new
`IKeycloakAdminClient` method is a thin HTTP call in
`Identity/Infrastructure`, the same shape as the existing
`StripInheritedRealmRolesAsync`; it does not warrant splitting the slice to
`backend-engineer`.

### 9.2 Behaviour-changing or -preserving

**Behaviour-changing → phase 4a is RED** (constitution §Testing, ADR-0139).

Concretely, what is red before the realm edit and against what:

| Test (new) | Red because, measured today |
|---|---|
| `An_account_created_after_import_does_not_inherit_the_long_lived_privilege` | a fresh account's effective realm roles are `default-roles-smart-sentinel-eye, offline_access, uma_authorization, user` — the assertion `ShouldNotContain("offline_access")` fails on the last-but-two |
| `The_realm_default_role_is_the_one_the_file_governs` (AS-4) | passes today and must keep passing — **it is a characterisation guard**, not a red one; it exists to fail if phase 4 reaches for the realm-file declaration §2.3 rules out |
| `Every_wall_display_account_holds_it` (AS-2, widened to four) | passes today; must pass unmodified after |

`An_account_the_provider_creates_holds_it_until_something_removes_it` is
**green today and must be inverted** — §7.1 gives the argument, and the PR body
must carry it verbatim, because an inverted control otherwise reads as the gate
weakening ADR-0144 forbids.

Ambiguity does not arise: the feature changes what a new account holds.

### 9.3 Is the honest answer a new ADR? **Yes — and it blocks phase 4.**

Reasoned rather than reached for:

- **ADR-0007 / ADR-0008** decided Keycloak per fab. **ADR-0132** decided one
  wall account per fab. If this were only "grant the four accounts by name",
  it would be implementation of those and need no ADR — and indeed **that half
  is already in the file** and always was (§2.1). Nothing new is decided there.
- **But ADR-0134 explicitly considered this exact change and declined it.** Its
  *Alternatives Considered* reads: "**Narrow the provider's default privilege
  set.** Total, and needs authority broader than the privilege it contains.
  Filed." Its cost table records "Accounts created by hand | — | **still
  inherit it** (issue 1995)" as an accepted negative. Its body: "Closing that
  means narrowing the default set itself, which requires realm-management
  authority … judged not worth taking."
- **This spec reverses that judgement and takes the widening ADR-0134 refused.**
  §2.4 measures the price precisely: `manage-realm` on a service account —
  authority over roles, session lifetimes and authentication flows alike, which
  is broader than the `offline_access` it removes. ADR-0134's objection was
  correct and is being overridden, not dissolved.
- Independently, **ADR-0132's measurement is now false** (§2.3), and a decision
  record that says "we tried it and the import discarded it" when the real
  behaviour is "it boots and silently governs nothing, or refuses to boot at
  all" will mislead the next person to reach for it.

**Therefore: a new ADR is required, superseding or amending ADR-0134.** It must
record the reversal, the `manage-realm` price, why `migration-runner` is a
narrower host for that authority than `identity-admin`, and the §2.3 correction.

**Per ADR-0144, the autonomous lane may not write an ADR or amend the
constitution.** The owner's decision (§1) is a decision; it is not yet a
decision *record*. **Phase 4 is blocked until a human writes that ADR**, and
this spec's ADR list must then cite it. This is a blocked outcome, not a
judgement call — flagged here so the gate is met deliberately rather than by
surprise.

---

## 10. Success criteria

- **SC-001** An account created after a fresh import holds no `offline_access`,
  asserted against the running provider. (US1, AS-1)
- **SC-002** All four `wall-*` accounts hold it, asserted against the running
  provider and against the file. (US2, AS-2)
- **SC-003** The realm's `defaultRole` is the composite the system narrowed, and
  no `-1` sibling exists. (AS-4)
- **SC-004** `e2e/wall-outlives-its-session.spec.ts` passes **unmodified**.
  (US2, AS-3)
- **SC-005** The realm file imports into an empty Keycloak and the server
  starts. (AS-6)
- **SC-006** The narrowing step is idempotent across boots. (AS-5)
- **SC-007** `operator` gains nothing: effective roles exactly `["user"]`.
  (AS-8)

## 11. Assumptions, marked

- **A-1** Keycloak stays at 26.6. Every measurement here is version-specific,
  and §2.3 is a direct demonstration that this behaviour changed between the
  version ADR-0132 measured and this one. A Keycloak upgrade must re-run §8.
- **A-2** There is no production realm, so the change has one deployment target:
  the dev/CI Aspire stack. Production carries the same obligation whenever it
  exists; nothing here discharges it.
- **A-3** Widening `migration-runner` with `manage-realm` is acceptable because
  it is a one-shot worker with no HTTP surface that exits before any API starts.
  If the ADR (§9.3) rejects that, the fallback is the master bootstrap admin
  credential the AppHost already owns — **broader** (authority over every realm),
  and therefore the second choice.

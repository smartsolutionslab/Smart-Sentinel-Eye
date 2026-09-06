# Plan 080 — A privilege granted, not inherited

**Spec:** `specs/080-a-privilege-granted-not-inherited/spec.md` · **Issue:** #1995
**Phase:** 2 (Plan) · **Date:** 2026-09-06
**Gate note:** phase 4 is **blocked** on the ADR required by spec §9.3.

---

## 1. Shape of the change: no bounded context gains anything

This is honest rather than modest. There is **no aggregate, no entity, no value
object and no domain event** in this feature, and inventing one would be
speculative generality (ADR-0036).

What the feature actually is: *the identity provider's realm must be configured
one way rather than another, and something must configure it after import
because the realm file demonstrably cannot* (spec §2.3).

| Layer | Touched? | Why |
|---|---|---|
| `<Context>/Domain` | **no** | nothing here is a domain concept; realm role composition is provider configuration |
| `Identity/Application` | **contract only** | one method added to `IKeycloakAdminClient`, the port that already owns Keycloak |
| `Identity/Infrastructure` | **yes** | one HTTP call in `HttpKeycloakAdminClient`, mirroring `StripInheritedRealmRolesAsync` |
| `Identity/Api` | **no** | no endpoint; this is not caller-driven |
| `MigrationRunner` | **yes** | the new `IMigrator` that runs the step once at boot |
| `src/AppHost/Realms/*.json` | **yes, one edit** | `manage-realm` for `service-account-migration-runner` |

**Bounded context: Identity**, because Identity owns Keycloak — established by
`AddKeycloakAdminClient()` and `IKeycloakAdminClient`. Nothing crosses a context
boundary, so `Shared.Contracts` is untouched.

## 2. Where the step lives, and why `MigrationRunner`

Spec §2.4 establishes the step must run **after import** and needs
`realm-management/manage-realm`. Three candidate homes were considered:

| Home | Verdict |
|---|---|
| **`MigrationRunner`** | **Chosen.** ADR-0067 already makes it the one-shot pre-API worker; `Program.cs:60-61` already calls `AddKeycloakAdminClient()` for spec 019's `KeycloakProvisionedFabSource`; every service `WaitForCompletion`s on it (`AppHost.cs:276-279`), so the step is guaranteed complete before any account can be created through an API. It has **no HTTP surface** and exits. |
| `Identity.Api` hosted service | Rejected. Would put `manage-realm` on `identity-admin`, the credential a live, internet-adjacent API uses for kiosk enrolment, device registration and webhook rotation. Strictly wider blast surface for the same result. |
| AppHost lifecycle hook using the master bootstrap admin | Rejected as first choice. It avoids widening any realm service account, but master-realm admin is authority over **every** realm — broader still. Kept as the documented fallback (spec A-3) if the ADR refuses `manage-realm`. |

**The precedent is exact.** `EventPartitionRolloverMigrator`
(`src/EventIngestion/Infrastructure/Persistence/EventPartitionRolloverMigrator.cs`)
is an `IMigrator` that reads the realm through the Keycloak admin client and
provisions per-fab state. This migrator is the same shape with a write instead
of a read. `IMigrator` is `src/ServiceDefaults/IMigrator.cs`:

```csharp
public interface IMigrator
{
    string ContextName { get; }
    Task RunAsync(CancellationToken cancellationToken);
}
```

**Registration order matters and is the opposite of the existing one.** The
comment at `Program.cs:63-64` registers `EventPartitionRolloverMigrator` *last*
because it needs the EF migrations to have run. This migrator depends on
**nothing in Postgres** — only on the realm having been imported, which Aspire
guarantees before `MigrationRunner` starts. Register it **first**, so the
narrowing lands as early as possible and the window in §5 is at its smallest.

## 3. The port method

Added to `IKeycloakAdminClient` (`src/Identity/Application/KeycloakAdmin/`),
implemented in `HttpKeycloakAdminClient` (`src/Identity/Infrastructure/KeycloakAdmin/`):

```
RemoveRealmDefaultPrivilegeAsync(string privilege, CancellationToken cancellationToken)
```

Guard with `Ensure.That(privilege).IsNotNull().IsNotNullOrWhiteSpace()`
(ADR-0105), `CancellationToken` last (ADR-0049), no `ConfigureAwait`.

**Two HTTP calls**, both measured in spec §2.4:

1. `GET admin/realms/{realm}/roles/{privilege}` — the role representation.
2. `DELETE admin/realms/{realm}/roles/default-roles-{realm}/composites`, body
   `[ <that representation> ]` → **204**.

**One measured detail worth writing down, because it contradicts the
neighbouring method.** `StripInheritedRealmRolesAsync`'s own doc comment warns
that the user-role-mapping delete "only recognises role objects the realm
reports as *directly* mapped to this account. A role fetched from the realm's
own role list looks identical and produces a `404`." **The composites endpoint
does not behave that way** — the representation from `/roles/{name}` is
accepted and answers 204. Measured, not assumed. Say so at the call site, or
the next reader will copy the read-back dance for no reason.

**Idempotent by the provider, not by us** — `DELETE` answers 204 whether or not
the member is present (spec §2.4). So, unlike `StripInheritedRealmRolesAsync`,
this needs **no** "already stripped, return early" branch. Do not add one; it
would be an unreachable guard.

**Error handling at the trust boundary only** (ADR-0036). A non-success status
must fail the migrator and therefore the process — every service
`WaitForCompletion`s on `MigrationRunner`, so a swallowed 403 would leave the
whole system booted and every account still privileged. That is the exact silent
failure spec AS-7 exists to name. `EnsureSuccessStatusCode()` is correct here.

## 4. Invariants

No domain invariants — there is no domain. The **system** invariants this
feature must hold, each mapped to a spec assertion:

| Invariant | Enforced by | Asserted by |
|---|---|---|
| `default-roles-{realm}` never contains `offline_access` after boot | the migrator | AS-1 |
| The realm's `defaultRole` is `default-roles-smart-sentinel-eye` with no `-1` sibling | **the realm file NOT declaring the composite** | AS-4 |
| Exactly the four `wall-*` accounts hold `offline_access` | the four `realmRoles` entries in the realm file | AS-2 (running) + existing `WallDisplayAccountTests` (file) |
| No operator holds it | declared users inherit nothing | AS-8 |

**The second invariant is negative and therefore fragile**: it is held by the
*absence* of a declaration. AS-4 is the only thing that will notice if a future
change adds one. This is the whole reason AS-4 exists.

## 5. Ordering, and the window

Keycloak's import is create-if-absent, and the realm file's four wall grants
resolve against the realm role `offline_access`, which **Keycloak creates
unconditionally** — not against the composite. Measured (spec §2.4): after the
composite is narrowed, `wall-munich` still reads `offline_access, user`. So:

- **The four grants do not depend on the composite existing.** No import-order
  hazard, and no silently-dropped mapping. This was the risk worth checking and
  it is not present.
- **The composite, conversely, cannot be expressed in the file at all** — spec
  §2.3. Declaring it either aborts the boot (`Unable to find composite realm
  role: uma_authorization`, exit 1) or creates the `-1` decoy.

**The window.** Between realm import and the migrator's `DELETE`, an account
created would inherit the privilege. It is bounded and, in this composition,
empty: `MigrationRunner` runs to completion before any API starts, so no
enrolment, registration or console-driven creation can occur inside it.
`StripInheritedRealmRolesAsync` (spec §7.3) covers runtime-created service
accounts regardless. **Keep it** — that is what makes the window a non-issue
rather than an argument.

**The persistent dev volume.** `WithDataVolume()` applies only when
`isRunMode && !isE2ETests` (`AppHost.cs:119-128`). A developer's long-lived dev
Keycloak keeps the old realm across restarts, so the realm-file half of any
change does not reach it — a documented trap. The migrator half **does**, on
every boot, because it is idempotent and unconditional. That asymmetry is a
reason to put the change in the migrator, not merely a consequence of it.

## 6. Messaging

**None.** No domain event, no integration event, no `Shared.Contracts` change.
Nothing outside `MigrationRunner` observes this, and manufacturing an event for
a boot-time configuration step would be speculative generality.

## 7. Boundary rules (ADR-0109, NetArchTest)

- **No cross-context project reference is added.** `MigrationRunner` is the
  composition root and is already allowed to reference every context — the
  comment at `Program.cs:56-59` states exactly that rule for the spec-019
  precedent ("Identity owns Keycloak and EventIngestion may not reference it, so
  the two meet here — in the composition root, which is allowed to know both").
  This feature stays inside Identity, so it does not even use that allowance.
- `Identity/Application` gains a method on an existing port; no new dependency.
- `Identity/Infrastructure` gains an implementation; no new package.
- The domain layers are untouched, so `PrimitiveBoundaryTests` and
  `HandlerDeconstructionTests` are unaffected — there is no handler and no
  domain model here.

## 8. Testing strategy

Follows spec §5's three tiers.

- **Unit** (`tests/Identity.Application.Tests/` or the infrastructure test
  project, mirroring where `StripInheritedRealmRolesAsync`'s tests live): the
  migrator calls the port once with `"offline_access"`; a port failure fails the
  migrator rather than being swallowed. Hand-written fake or Moq (ADR-0052),
  fluent builders (ADR-0054), sentence-style names (ADR-0053).
- **Integration** (`tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs`,
  ADR-0103, Aspire fixture — `E2ETests=true` gives a fresh Keycloak with a fresh
  import every run): AS-1, AS-2, AS-4, AS-8. **This is the tier that makes the
  claim.**
- **Architecture**: **no new test.** Spec §5 Tier 1 explains why a file-reading
  guard cannot make this claim and would reproduce a failure this repo has
  already shipped once. Existing file guards keep their scope; two prose
  comments are corrected (spec §7.4).
- **E2E**: `e2e/wall-outlives-its-session.spec.ts` must pass **unmodified**
  (SC-004). It is the characterisation half of this change.

**Coverage gates** (ADR-0065): the new code is Infrastructure and
`MigrationRunner`, neither of which carries a gate. The one Application-layer
addition is an interface method. No gate movement expected; if Application
coverage dips, it is because an interface grew, and the migrator unit tests
cover the behaviour.

## 9. Risks

| Risk | Mitigation |
|---|---|
| The realm-file edit breaks the import and hangs the whole Aspire fixture (recorded incident: a 733-char client description) | The edit is **one array entry**. AS-6 boots an empty Keycloak against the file before anything else runs. Do not touch client descriptions. |
| Phase 4 reaches for the realm-file declaration because it looks simpler | AS-4 fails on the `-1` decoy; spec §2.3 records the measurement and the boot failure. |
| The inverted control test reads as a weakened gate (ADR-0144) | Spec §7.1 gives the argument; the PR body must carry it, naming the replacement anti-vacuity control. |
| `manage-realm` is broader than the privilege removed | Real, and ADR-0134 declined the change on exactly this ground. **Not mitigated here — it is the trade the ADR must record** (spec §9.3). Narrowed as far as measurement allows by hosting it in the one-shot worker rather than the live API. |
| A Keycloak upgrade changes the import semantics again | Spec A-1; §8's procedure is re-runnable in four minutes. |

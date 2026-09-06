# Tasks 080 — A privilege granted, not inherited

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #1995
**Phase:** 3 (Tasks) · **Date:** 2026-09-06
**Engineer:** `infra-engineer` (spec §9.1)
**Phase 4a colour:** **RED** — behaviour-changing (spec §9.2)

---

## Gate: phase 4 is blocked

**T000 is not an implementation task and cannot be done by the autonomous lane.**
Spec §9.3 establishes that this feature reverses ADR-0134's recorded and
reasoned refusal ("Narrow the provider's default privilege set. Total, and needs
authority broader than the privilege it contains. **Filed.**") and takes on the
`manage-realm` widening it declined. ADR-0144 forbids the lane writing or
amending an ADR.

| ID | Task | Owner |
|---|---|---|
| **T000** | Write the ADR superseding or amending ADR-0134: the reversal, the `manage-realm` price, why `migration-runner` hosts it rather than `identity-admin`, and the correction to ADR-0132's "discarded on import" measurement (spec §2.3). Then add its number to spec 080's ADR list. | **human** |

**Nothing below starts until T000 lands.**

Board (per CLAUDE.md phase-3 gate): add the **feature-level** issue #1995 to
Project #13 by hand — `/speckit-tasks` adds nothing. No per-task issues; that
stopped after spec 028.

```sh
gh project item-add 13 --owner smartsolutionslab --url <issue-url>
```

---

## Parallelism

`[P]` marks disjoint file ownership (ADR-0109).

**T001 is foundational and blocks everything.** It is the port method every
other piece calls. After it, the migrator (T002/T003), the realm edit (T004) and
the test work (T005–T008) touch disjoint files and fan out.

```
T000 (human, ADR)
  └─ T001  IKeycloakAdminClient + HttpKeycloakAdminClient      [blocks all]
       ├─ T002 [P] migrator class
       ├─ T004 [P] realm JSON: manage-realm
       ├─ T005 [P] integration tests   <-- RED, written first
       └─ T006 [P] unit tests          <-- RED, written first
     T003 depends on T002 + T004 (registration needs both to be meaningful)
     T007, T008 after T003 (they observe the wired system)
```

Phase 4a (`test-writer`) runs **T005 and T006 before** T001–T004 and returns the
verbatim failure output. The engineer receives that output as its brief and may
not edit the tests to pass.

---

## US1 + US2 — the one slice (P1)

Spec §3 records why these are not separable: US1 without US2 is a dark wall, and
US2 is what stops US1 passing vacuously.

### Phase 4a — tests first, observed red

| ID | P | Story | Task |
|---|---|---|---|
| **T005** | [P] | US1+US2 | In `tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs`: add `An_account_created_after_import_does_not_inherit_the_long_lived_privilege` (AS-1) and `The_realm_default_role_has_no_duplicate_sibling` (AS-4); widen `A_wall_display_account_does_hold_it` to all four `wall-*` accounts (AS-2); keep `An_operator_does_not_hold_…` (AS-8). **Expected red:** AS-1 fails — a fresh account's effective realm roles are `default-roles-smart-sentinel-eye, offline_access, uma_authorization, user`. AS-4 and AS-2 pass today and are characterisation guards. Quote the AS-1 failure verbatim in the PR. |
| **T006** | [P] | US1 | Unit tests for the migrator, mirroring where `StripInheritedRealmRolesAsync`'s tests live: it calls the port once with `"offline_access"`; a port failure propagates and fails the migrator rather than being swallowed. Moq or a hand-written fake (ADR-0052), sentence-style names (ADR-0053). **Expected red:** the migrator does not exist. |

### Phase 4b — implementation

| ID | P | Story | Task |
|---|---|---|---|
| **T001** | — | US1 | **Foundational, blocks T002–T008.** Add `RemoveRealmDefaultPrivilegeAsync(string privilege, CancellationToken)` to `src/Identity/Application/KeycloakAdmin/IKeycloakAdminClient.cs` and implement it in `src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs`: `GET roles/{privilege}`, then `DELETE roles/default-roles-{realm}/composites` with that representation. `Ensure.That(...)` guard (ADR-0105), token last (ADR-0049), `EnsureSuccessStatusCode()` — a swallowed 403 boots the system with every account still privileged. **No "already removed" early-return branch**: the provider answers 204 either way (plan §3), so one would be unreachable. Comment the one measured surprise: unlike the neighbouring user-role-mapping delete, the composites endpoint accepts the representation from the realm's own role list. |
| **T002** | [P] | US1 | New `IMigrator` in `src/MigrationRunner/` (e.g. `DefaultPrivilegeNarrowingMigrator.cs`) calling T001's method with `"offline_access"`. Mirror `EventPartitionRolloverMigrator`'s shape. Keep under 300 LOC / 30 LOC per method / complexity ≤ 10 (ADR-0084). |
| **T004** | [P] | US1 | `src/AppHost/Realms/smart-sentinel-eye-realm.json`: add `"manage-realm"` to `service-account-migration-runner`'s `realm-management` client roles (currently `["query-groups","view-users"]`, ~L546-555). **One array entry. Touch nothing else** — this file is a contention file and a >255-char client description has previously failed the whole import and hung the fixture. |
| **T003** | — | US1 | Register the migrator in `src/MigrationRunner/Program.cs`. **Register it first**, before the persistence migrators — it depends on nothing in Postgres, only on the realm import, and registering early makes the plan §5 window as small as possible. Note this is deliberately the opposite of `EventPartitionRolloverMigrator`'s "registered last" comment; say why at the call site. Depends on T002 + T004. |

### Phase 4c — the record, and the things that must not silently change

| ID | P | Story | Task |
|---|---|---|---|
| **T007** | [P] | US1+US2 | Invert `An_account_the_provider_creates_holds_it_until_something_removes_it` (`KioskInheritedPrivilegeIntegrationTests.cs:69-101`). **The PR body must carry spec §7.1's argument verbatim**: the old control asserted the world this feature changes, and the anti-vacuity duty it served is preserved by `Every_wall_display_account_holds_it`. An inverted control is otherwise indistinguishable from the gate-weakening ADR-0144 forbids. |
| **T008** | [P] | — | Correct the prose that now states a false premise, and **only** that (ADR-0036): `tests/Architecture.Tests/WallDisplayAccountTests.cs:22-39`, `tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs:11-29`, `src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:80-85`, `src/Identity/Application/KeycloakAdmin/IKeycloakAdminClient.cs:109-134`, `apps/kiosk-web/src/app/auth.ts:83-89`. **Leave `docs/adr/0131`, `docs/adr/0132`, `specs/050/*` and `specs/052/*` alone** — an ADR and a merged spec are historical records; the correction belongs in T000's ADR. **Do not touch `RealmIdentityTests.cs:77`** (`ProvidedByKeycloak = ["offline_access"]`): that is the client *scope*, not the realm role, and it is unaffected — say so in the PR, because it is a near-miss a reviewer will flag. |

### Must not change

| ID | Task |
|---|---|
| **T009** | `e2e/wall-outlives-its-session.spec.ts` passes **unmodified** (SC-004). It is the characterisation half of this change: if it goes red, the wall accounts lost the privilege. Do not adjust its assertions — that would encode the regression. |

---

## Phase 5 (Verify) — do not skip

| ID | Task |
|---|---|
| **T010** | Run spec §8's independent procedure end to end. Steps 1–9 need only the Keycloak container (~4 min); step 10 needs the stack. **Check `C:` free before booting; do not boot below 6 GB** — a full worktree has taken C: to zero and killed the Docker engine, and only a GUI restart brought it back. Write the verification note on the PR. Latency: **N/A**, not on the event→overlay path (spec header). |

---

## Out of scope, filed separately

- **`KioskPrivilegeSweep` is unregistered dead code.** `src/Identity/Application/KeycloakAdmin/KioskPrivilegeSweep.cs`
  exists and is unit-tested but has **no `AddHostedService` registration
  anywhere**, though `specs/052-wall-past-its-ceiling/tasks.md:37` marks T005
  done — so spec 052's backfill over already-enrolled kiosks never ran. Real
  defect, found while writing this spec. **File it; do not fix it here** —
  mixing it in would make this a bug fix and a refactor at once.
- **#1976** — the kiosk app using its enrolled device credentials.
- **Withdrawing `offline_access` from wall displays.** Rejected (spec §1).

## Files phase 4 will touch

```
src/Identity/Application/KeycloakAdmin/IKeycloakAdminClient.cs          T001, T008
src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs    T001, T008
src/MigrationRunner/DefaultPrivilegeNarrowingMigrator.cs                T002  (new)
src/MigrationRunner/Program.cs                                          T003
src/AppHost/Realms/smart-sentinel-eye-realm.json                        T004  (contention)
tests/Integration.Tests/Identity/KioskInheritedPrivilegeIntegrationTests.cs  T005, T007, T008
tests/Identity.Application.Tests/…/DefaultPrivilegeNarrowingMigratorTests.cs T006  (new)
tests/Architecture.Tests/WallDisplayAccountTests.cs                     T008 (comment only)
apps/kiosk-web/src/app/auth.ts                                          T008 (comment only)
docs/adr/NNNN-….md                                                      T000  (human, new)
```

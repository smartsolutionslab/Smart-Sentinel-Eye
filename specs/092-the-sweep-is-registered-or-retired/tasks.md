# Tasks — 092 The kiosk privilege sweep is wired to the failure it was written to catch

**Spec:** [spec.md](./spec.md) · **Plan:** [plan.md](./plan.md) · **Issue:** #2132
**Phase:** 3 (Tasks) — ADR-0037

Ten tasks. One backend slice, one documentation correction.

**Phase 4a colour: RED** for US1 (new behaviour — a service that has never run).
**Skipped, declared** for US2 (documentation-only). Constitution §Testing,
ADR-0139, ADR-0144.

---

## Do not

- **Do not register in `AddKeycloakAdminClient`.** `MigrationRunner/Program.cs:60`
  calls it. The registration goes in `AddIdentityInfrastructure` and nowhere
  else. This is the single easiest thing here to get wrong.
- **Do not modify `KioskPrivilegeSweepTests`.** All five stay and stay green,
  unmodified. Two of them cannot fail on a defect in the class under test (spec
  §"Whether the existing tests can fail"); that is recorded, not repaired here,
  and it is not a licence to delete them (ADR-0144).
- **Do not inject `IKeycloakAdminClient` into the hosted service.** It is
  `AddScoped`. Use `IServiceScopeFactory`.
- **Do not let the pass stop the host.** An unreachable provider at boot must not
  keep the Identity API from serving.
- **Do not tick anything else in `specs/052/tasks.md`.** T005 only.
- **Do not request new admin authority.** ADR-0134 measured that none is needed.
- **Do not fix `TryDeleteClientAsync`.** Separate defect, separate issue — and it
  would remove this spec's own justification.
- **Do not throw for expected failures.** `Result<T, Error>`; `Ensure.That` for
  argument guards (ADR-0105).
- **Do not write bare `#NNNN` issue numbers** in committed docs.
- **Do not offer a green test suite as evidence the sweep ran at boot.** Only the
  log line in a running stack is that.

---

## Phase 1 — Red (the gate)

- [ ] **T001** [US1] Integration test in
  `tests/Integration.Tests/Identity/` — **the load-bearing red.** Plant a residue
  client through the Admin API: `serviceAccountsEnabled: true`, attributes
  `sse.kind=kiosk` and `sse.fab=munich`, created **directly** so nothing strips
  it. Resolve the wired sweep from the running Identity API's container, drive
  one pass, then **ask the provider** for that account's effective realm roles and
  assert `offline_access` is absent. Delete the probe in a `finally`.
  Mirror `KioskInheritedPrivilegeIntegrationTests` — same fixture, same
  `EffectiveRealmRolesAsync` helper, same cleanup discipline. **Assert on what
  the provider says, never on the pass completing.**

- [ ] **T002** [P] [US1] **The control, in the same file.** A client created the
  same way but **without** the `sse.kind` stamp, holding realm roles. After the
  same pass, assert it holds **exactly what it held**. Its own task because it is
  the assertion that can actually fail dangerously: the removal takes away every
  directly-assigned realm role, so a sweep that matched everything would strip an
  operator bare **and not throw while doing it** — T001 would pass on the way
  past. Spec 052 T006 made this point and its unit-level version turned out
  vacuous; this is the version that is not.

- [ ] **T003** [P] [US1] Registration assertion in `tests/Architecture.Tests/`
  that the Identity API's service collection contains the hosted-service
  registration. **Label it `declaration only` in its own docstring**, exactly as
  spec 052's T010 labelled its realm-file guard, and say in that docstring that
  it does not show the pass ran. It is the weaker of the two reds and is here
  because T001 cannot see startup wiring — not because reading a container is
  good evidence.

- [ ] **T004** [P] [US1] Unit test in
  `tests/Identity.Application.Tests/KeycloakAdmin/` — a **new file**, so
  `KioskPrivilegeSweepTests` is not touched — that a failing **enumeration** does
  not stop the host. Drive the wrapper, not `SweepAsync`; use
  `FakeKeycloakAdminClient.FailNextCall`. Red today because
  `KioskPrivilegeSweep.cs:44` sits outside the try and no wrapper exists.

**Checkpoint.** T001–T004 observed **red**, and the verbatim output captured for
the PR body (ADR-0139). A test arriving green here is a phase-4 failure, not a
shortcut. Nothing in Phase 2 lands until the red is recorded.

---

## Phase 2 — Green

- [ ] **T005** [US1] Add `KioskPrivilegeSweepHostedService` in
  `src/Identity/Infrastructure/KeycloakAdmin/`, implementing `IHostedService`.
  Take `IServiceScopeFactory` and `ILogger<T>`; `StartAsync` creates an async
  scope, resolves `KioskPrivilegeSweep`, drives one pass, and **catches
  everything except `OperationCanceledException`**, logging and returning.
  `StopAsync` returns `Task.CompletedTask`. Mirror
  `StreamDistribution/Infrastructure/Attribution/StreamFabAttributionService.cs`
  — including a comment saying the swallow is **chosen**: Identity must serve
  requests even when Keycloak is unreachable, and the next start retries.

- [ ] **T006** [US1] Register it in
  `src/Identity/Infrastructure/IdentityInfrastructureModule.cs` —
  `AddScoped<KioskPrivilegeSweep>()` alongside the other scoped handlers, and
  `AddHostedService<KioskPrivilegeSweepHostedService>()` inside
  `AddIdentityInfrastructure`. **Not in `AddKeycloakAdminClient`** (plan §II).
  Its own task, separate from T005, because this one line is the entire defect
  #2132 reports and a reviewer should be able to see it alone.

- [ ] **T007** [US1] Add the pass-failed message to
  `src/Identity/Infrastructure/Log.cs` at Warning, `[LoggerMessage]` source-gen
  (ADR-0050), placed next to `CouldNotRemoveHalfEnrolledClient` — the message on
  the other side of the same failure, so both halves are read together.

- [ ] **T008** [US1] In `src/Identity/Application/KeycloakAdmin/KioskPrivilegeSweep.cs`:
  guard the `SweptKioskPrivileges` call on `kiosks.Count > 0`, and replace the
  "why a sweep and not only the enrolment path" paragraph — its stated population
  is provably empty — with the live reason: the half-enrolled client
  `HttpKeycloakAdminClient.TryDeleteClientAsync` could not remove and delegates
  here by name. Cite that call site. **Behaviour-preserving for the five existing
  unit tests, which must pass unmodified** — an assertion that has to be edited
  is evidence the behaviour moved: block, do not adjust (constitution §Testing).

**Checkpoint.** T001–T004 green. The five pre-existing `KioskPrivilegeSweepTests`
green **and unmodified** — verify with `git diff --stat` on that file returning
nothing.

---

## Phase 3 — US2: the record stops ticking a mechanism that never ran *(P2)*

- [ ] **T009** [P] [US2] Correct `specs/052/tasks.md:37`. T005 shipped
  `KioskPrivilegeSweep` and no wiring, so the class existed and the sweep never
  ran. State that, cite this spec, and **change no other tick in that file** —
  the repair is against what shipped, not toward internal consistency.
  Note the task also names the wrong folder: the class landed in
  `Application/KeycloakAdmin/`, not `Application/Kiosks/`.

---

## Phase 4 — Verification

- [ ] **T010** [US1] `verification.md`, following the independent procedure in
  spec §"Independent end-to-end test procedure". **Plant the residue before
  restarting** — with the steady-state line now silenced (T008), an empty realm
  produces no output and would read as success. Quote the log line with a
  non-zero count, and the provider's answer before and after. **State what was
  not done**: no production deployment exists, and no environment has ever
  contained a residue client outside this procedure.

---

## Dependencies

```
T001 ─┐
T002 ─┤ (all four red first — the gate)
T003 ─┤
T004 ─┘
        │
        ▼
      T005 ─▶ T006 ─▶ T007          Phase 2, sequential:
        │                            the wrapper, then its registration,
        └─────────────▶ T008         then its log message
        │
        ▼
      T010 (verification)

T009 ──────────────────────────────  independent of everything
```

**T001–T004 are `[P]` among themselves** — T001 and T002 share one file and must
be written together by one agent; T003 owns an Architecture.Tests file and T004
owns a new unit-test file, both disjoint (ADR-0109).

**T005 → T006 → T007 are strictly sequential.** One wrapper, its registration,
its message; T006 does not compile before T005 exists.

**T008 is parallel with T005–T007** — different project, different file.

**T009 is parallel with everything.** It touches only `specs/052/tasks.md`. It is
the one task that could ship on its own, and it needs no ADR and no test.

**Each commit builds on its own** (ADR-0087). T006 must not be committed without
T005 in the same commit or an earlier one — a registration referencing a type
that does not yet exist breaks `git bisect` forever.

---

## Ways this could go wrong, and what catches each

| # | The wrong turn | Caught by |
|---|---|---|
| 1 | Registered in `AddKeycloakAdminClient`, so MigrationRunner sweeps | Reviewer; the task names the method. Nothing automated catches this. |
| 2 | `IKeycloakAdminClient` injected into the singleton | Container validation at boot — T001 fails to start the fixture |
| 3 | Enumeration failure stops the Identity API host | T004 |
| 4 | The sweep matches accounts enrolment did not create | T002, asserted on the operator's roles |
| 5 | `KioskPrivilegeSweepTests` edited to accommodate the wrapper | `git diff --stat` on that file; ADR-0144 forbids it |
| 6 | T003 offered as evidence the sweep ran | Its own `declaration only` docstring, and T010 |
| 7 | Verified against an empty realm, silence read as success | T010's residue-first ordering, and the procedure's step 3 abort |
| 8 | T008's docstring edit changes behaviour | The five existing tests, which must pass **unmodified** |

---

## What is being claimed, and by what

| Claim | Proven by | **Not** proven by |
|---|---|---|
| A residue kiosk loses the privilege | **T001**, asking the provider | the pass completing |
| An account enrolment did not create is untouched | **T002**, asserted on that account | the sweep not throwing |
| The sweep is wired into the host | **T003** (declaration) + **T010** (the boot log) | T001, which resolves it deliberately |
| An unreachable provider does not dark Identity | **T004** | the happy path passing |
| The pass ran at boot in a real stack | **T010**, the log line with a non-zero count | any green test |
| The class is correct in isolation | the 5 pre-existing tests — of which **2 cannot fail** | — |

---

## Board (ADR-0037 phase 3 gate)

Feature-level issue, not per-task — the repo has created no `[TNNN]` issues since
spec 028. #2132 is already on Project #13 with status **In Progress** and carries
`agent:ready`, so the gate is met with no `item-add` needed. Verify with
`--limit 2000`; the default 30 makes a filled board look empty.

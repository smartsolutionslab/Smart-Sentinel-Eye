# Plan — 092 The kiosk privilege sweep is wired to the failure it was written to catch

**Spec:** [spec.md](./spec.md) · **Issue:** #2132 · **Phase:** 2 (Plan) — ADR-0037

---

## I. Bounded context and layers

**One context: `Identity`.** No cross-context reference is added or needed, so
the ADR-0051 / NetArchTest boundary rules are untouched. Nothing goes into
`Shared.Contracts` — the sweep publishes no integration event and no other
context needs to know it ran.

| Layer | Change | File |
|---|---|---|
| `Identity.Application` | Guard the whole pass, not only the per-kiosk strip. Silence the steady-state log line. Correct the docstring's stated reason. | `KeycloakAdmin/KioskPrivilegeSweep.cs` |
| `Identity.Application` | One log-level/condition change; no new message. | `Log.cs` |
| `Identity.Infrastructure` | **New** — the `IHostedService` wrapper. | `KeycloakAdmin/KioskPrivilegeSweepHostedService.cs` |
| `Identity.Infrastructure` | One `AddHostedService` line and one `AddScoped`. | `IdentityInfrastructureModule.cs` |

**Why the wrapper lives in Infrastructure, not Application.** Hosting is a
framework concern and `IServiceScopeFactory` is a composition detail. Every
sibling context puts its hosted services in Infrastructure —
`StreamFabAttributionService`, `MediaMtxReconciler`,
`ReverseIndexSeederHostedService`, `RuleCacheSeederHostedService`,
`MqttSubscriberHostedService`. The one exception, `AuditRetentionHostedService`,
sits in Application and is registered from Infrastructure; it is the outlier and
is not the pattern to copy. `KioskPrivilegeSweep` itself stays in Application —
it is the use case, and it keeps its unit tests.

**Why not make `KioskPrivilegeSweep` itself the `IHostedService`.** It would drag
`Microsoft.Extensions.Hosting` into Application and force the class to take
`IServiceScopeFactory` instead of the collaborator it actually uses — which would
break all five existing unit tests. Keeping the pass a plain method and wrapping
it is what lets the existing suite stay unmodified, and is exactly the shape
`StreamFabAttributionService.AttributeOnceAsync` already uses.

---

## II. Registration — where, and where not

```
Identity/Api/Program.cs
  └── builder.AddIdentityInfrastructure()        ← the sweep registers HERE
        ├── builder.AddKeycloakAdminClient()     ← NOT here
        └── builder.Services.AddHostedService<KioskPrivilegeSweepHostedService>()

MigrationRunner/Program.cs
  └── builder.AddKeycloakAdminClient()           ← must NOT get a sweep
```

**`AddKeycloakAdminClient` is shared and must stay clean.**
`src/MigrationRunner/Program.cs:60` calls it to enumerate fab sub-groups. Putting
the registration there would give MigrationRunner a sweep it has no business
running, and MigrationRunner runs to completion and exits — a hosted service
there would race the migration it exists to perform. This is the single most
consequential placement decision in the change and the easiest to get wrong,
because `AddKeycloakAdminClient` is where every other Keycloak thing already is.

**Scoping.** `IKeycloakAdminClient` is `AddScoped`
(`IdentityInfrastructureModule.cs:150`). `KioskPrivilegeSweep` therefore
registers `AddScoped` too, and the hosted service — a singleton — resolves it
through `IServiceScopeFactory.CreateAsyncScope()`. Injecting a Scoped service
into a hosted service constructor throws at container validation; that is the
failure `StreamFabAttributionService` already routes around.

---

## III. Entities, value objects, invariants

**None are added.** Nothing here touches a domain model, so constitution §II and
`PrimitiveBoundaryTests` are not engaged.

`KioskSweepOutcome(int KioskCount, IReadOnlyList<string> Unreachable)` already
exists and stays as-is. It is an Application-layer result record, not a domain
model — §II's ban does not reach it, and introducing `KioskCount` as a value
object would be speculative generality (ADR-0036) for a number that is logged and
discarded.

**Invariants the pass must hold, all pre-existing and all preserved:**

1. **Bounded to what enrolment stamped.** The set comes from
   `GetEnrolledKioskClientIdsAsync`, which filters on `sse.kind == "kiosk"` — the
   same attribute `EnrollKioskCommandHandler` writes. A second naming convention
   would drift.
2. **Idempotent.** `HttpKeycloakAdminClient` returns early when the account has
   no direct realm mappings. This is what makes a startup pass safe.
3. **One failure does not stop the rest.** Per-kiosk `try`/`catch`, each failure
   named in `Unreachable`.
4. **New — the pass never stops the host.** The addition.

---

## IV. The three code changes, precisely

### 1. The guard moves outward (`KioskPrivilegeSweep.cs`)

Today the enumeration is outside the `try`:

```csharp
IReadOnlyList<string> kiosks = await keycloak.GetEnrolledKioskClientIdsAsync(cancellationToken);   // line 44 — unguarded
```

An unreachable or refusing provider throws out of `SweepAsync`. Registered as
`IHostedService.StartAsync`, that stops the Identity API host.

The fix belongs in the **hosted service**, not in `SweepAsync`. `SweepAsync`
returning a `KioskSweepOutcome` for a pass that never enumerated anything would
be a lie — `KioskCount = 0` already means "nothing to do". So `SweepAsync` keeps
throwing on enumeration failure, and the wrapper catches, logs, and returns —
mirroring `StreamFabAttributionService.StartAsync` exactly, comment and all. The
per-kiosk catch inside `SweepAsync` stays where it is.

**This keeps the change smallest and keeps all five existing unit tests passing
unmodified**, which matters: they are the characterisation of a class this spec
does not intend to alter behaviourally.

### 2. The steady state goes quiet (`Log.cs`, `KioskPrivilegeSweep.cs`)

`SweptKioskPrivileges` is currently logged unconditionally at Information. In
every environment that exists, that is `(0, 0)` on every start, forever.

Guard the call on `kiosks.Count > 0`. Keep the level and the message —
`StreamFabAttributionService` sets the precedent and states the reason:

> Silent on purpose: this is the steady state, reached after the first pass and
> never left. A line per restart saying nothing happened trains an operator to
> skip the one that matters.

**Consequence for phase 5, and it must not be lost:** the end-to-end procedure's
step 8 depends on a residue being present to produce a line. Verifying against an
empty realm will produce no output and prove nothing. The verification note must
plant the residue first.

### 3. The docstring says the real reason (`KioskPrivilegeSweep.cs`)

The class currently justifies itself with "every kiosk enrolled before this
existed is still holding a privilege". That population is empty and provably so
(spec §"The verdict"). The live reason is the half-enrolment residue that
`HttpKeycloakAdminClient.cs:364` delegates here by name.

Replace the "why a sweep" paragraph with the backstop reason and a pointer to
that call site. **Comments say why** (ADR-0036), and this one currently says a
why that is false — which is how the sweep came to look retirable.

---

## V. Messaging

**None.** No domain event, no integration event, no Wolverine handler. The sweep
neither reads nor writes the Identity database, so ADR-0088's outbox and
per-module queue isolation are not engaged, and there is no transaction to join.

Deliberate: publishing a `KioskPrivilegesSweptV1` would be speculative
generality (ADR-0036) for an event with no subscriber. The observability
obligation is discharged by the log line, which is what §VII asks for.

---

## VI. Boundary rules

- No cross-context project reference is added. `Identity.Infrastructure` already
  references `Identity.Application`.
- Nothing is added to `Shared.Contracts`.
- `Identity.Application` gains no framework reference — the wrapper carries the
  hosting dependency, and Infrastructure is where framework references belong.
- NetArchTest's existing rules cover all of this; no new architecture rule is
  written, because a rule asserting "this one service is registered" is the
  declaration-only guard the spec already flags as weak evidence.

---

## VII. Observability

| Signal | When | Level |
|---|---|---|
| `SweptKioskPrivileges(stripped, total)` | end of a pass that found ≥ 1 kiosk | Information |
| `CouldNotSweepKiosk(clientId, ex)` | per kiosk the provider refused | Warning |
| **new** — the pass itself failed | enumeration threw; host still starts | Warning |

The third is the only new message. It follows `AttributionPassFailed`'s shape and
goes in `Identity/Infrastructure/Log.cs`, next to
`CouldNotRemoveHalfEnrolledClient` — the message on the other side of the same
failure. A reader who sees that warning and later sees this one has the whole
story in one place.

**Constitution §VII's dashboard rule is not engaged** — it binds implemented
latency legs (ADR-0117), and this change is on no leg.

---

## VIII. Risks

| Risk | Mitigation |
|---|---|
| Registered in `AddKeycloakAdminClient`, so MigrationRunner sweeps too | §II. The task names the file and the method explicitly. |
| Scoped-into-singleton container validation failure | `IServiceScopeFactory`, mirroring `StreamFabAttributionService`. Caught at boot by any integration test. |
| The sweep matches more than kiosks and strips an operator bare | Pre-existing and unchanged — the filter is not touched. Red A's control asserts on an unstamped account. |
| A green Red A + Red B read as proof the sweep ran at boot | Spec §"Phase 4a" states it does not. Phase 5 observes the log line in a running stack. |
| Verifying against an empty realm and reading silence as success | §IV.2. The verification note plants a residue first, and step 3 of the procedure aborts if the control does not hold. |
| The existing unit suite is taken as evidence the sweep works | Two of its five tests cannot fail on a defect in the class (spec §"Whether the existing tests can fail"). Recorded, not relied on. |

---

## IX. What this plan deliberately does not do

- **Does not delete the sweep.** The `TryDeleteClientAsync` backstop is live.
- **Does not amend ADR-0134.** Registering makes §Decision 1 true; a blocked
  outcome is avoided rather than argued around.
- **Does not touch `TryDeleteClientAsync`.** Its unchecked response is a real
  defect and a separate issue — and repairing it would remove this spec's own
  justification, which is two changes in one commit (ADR-0036).
- **Does not repair the two vacuous tests.** Recorded in the spec, left green.
- **Does not add an architecture test.** A guard that reads the container is the
  weak evidence the spec already labels as such; adding a second one does not
  make it stronger.

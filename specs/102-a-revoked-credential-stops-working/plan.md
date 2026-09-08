# Plan 102 — A revoked credential stops working

**Spec:** `specs/102-a-revoked-credential-stops-working/spec.md`
**Issue:** #603
**Scope:** test-only. No production file is edited at any point except
temporarily, and reverted, for the counterfactual (T003).

---

## 1. Bounded context and layers

| | |
|---|---|
| Context | **EventIngestion** — single context, no cross-context reference. |
| Layer under observation | `Api` (`EventsEndpoints.Writes.AuthenticateWebhookAsync`) over `Infrastructure` (`WebhookIntegrationRepository` → Postgres) and `Domain` (`WebhookIntegration.IsRevoked`). |
| Layer of the new code | **none** — `tests/Integration.Tests`, which references the booted stack over HTTP, not the projects. |
| Boundary rules | Unaffected. Nothing new crosses a context; no `Shared.Contracts` change; NetArchTest's rules do not apply to a test that speaks HTTP. |

## 2. The path under observation

```
POST /events/webhook/{name}?fabId=munich       (AllowAnonymous — EventsEndpoints.cs:59)
  └─ IngestWebhook                             (Writes.cs:121)
       └─ AuthenticateWebhookAsync             (Writes.cs:185)
            ├─ bearer prefix present?          :192
            ├─ name parses?                    :202
            ├─ integrations.GetByNameAsync      :209   ← fresh, request-scoped, no RevokedAt filter
            ├─ !found || found.IsRevoked → null :211   ← THE LINE UNDER TEST
            ├─ IsIntegrationsOwnFab             :226
            └─ TokenHash.Matches / ValidateJwt  :231
       └─ integration is null → 401             :138-141
```

Everything after `:211` is unreachable once the integration is revoked, which is
why the test must reach `:211` with an *otherwise valid* request — right token,
right fab, right name. That is scenario 1's design.

## 3. Entities, value objects, invariants

Nothing new. The invariant asserted is an existing one, previously only observed
in memory:

> `WebhookIntegration.IsRevoked => RevokedAt is not null`
> (`Domain/WebhookIntegration/WebhookIntegration.cs:92`), set once and only once
> by `Revoke(IClock)` (`:94-103`), which raises
> `WebhookIntegrationRevokedDomainEvent` and is idempotent (`:97`).

This slice adds the missing half: **that the Api consults it.**

## 4. Messaging

None added. `WebhookIntegrationRevokedDomainEvent` → outbox →
`WebhookIntegrationRevokedV1` continues to flow on the revoke; the test does not
observe it and must not wait on it (that would make a delivery timeout read as a
revocation failure — the trap
`WebhookIntegrationConcurrencyIntegrationTests.cs:26-32` documents for the
rotation path).

## 5. Files

### New — one

`tests/Integration.Tests/EventIngestion/WebhookRevocationRefusesDeliveryIntegrationTests.cs`

- `[Collection(AspireCollection.Name)]`, **no `[Trait]`**.
- Two `[Fact]`s (spec §3 scenarios 1 and 2). Scenario 3 is a note, not a test.
- Private helpers, all modelled on existing ones:
  `RegisterAndCaptureTokenAsync` (from
  `WebhookBearerValidationIntegrationTests.cs:166-175`), `PostWebhookAsync`
  (`:194-203`), `FindAsync`/`Conditional` (from
  `WebhookIntegrationConcurrencyIntegrationTests.cs:169-188`), `DiagnoseAsync`
  (`:197-202`), `UniqueName` (`:190`).
- ADR-0084 budget: ≤ 300 LOC, ≤ 30 LOC/method, ≤ 4 params, complexity ≤ 10,
  depth ≤ 3. Comfortable: the largest method is ~20 lines.

### Modified — none

`ci.yml` is **not** touched: the exclusion filter at `:179` is a deny-list, so an
untraited file is selected by construction (spec SC-003). `ci.yml` is an ADR-0109
contention file, and not touching it keeps this slice conflict-free.

### Temporarily modified, then reverted — one

`src/EventIngestion/Api/EventsEndpoints.Writes.cs:211`, for T003 only. It must be
reverted before the commit; `git status` clean apart from the new test file and
this spec directory is the check.

## 6. ADR-0109 contention check — **clean**

| Contention file | Touched? |
|---|---|
| `src/Shared.Kernel/*`, `src/Shared.Contracts/*` | no |
| `src/AppHost/AppHost.cs` | no |
| `apps/shared/*` | no |
| `e2e/support/*`, any e2e spec | no |
| `.github/workflows/ci.yml` | **no** — see §5 |
| `Directory.Packages.props`, `global.json` | no |

The slice owns exactly one new file plus `specs/102-*/`. It can run concurrently
with any other slice that does not also add a file to
`tests/Integration.Tests/EventIngestion/` — and even then, two new files do not
collide.

## 7. Test structure

```
Register_and_revoke_then_deliver:            [Fact] scenario 1
  admin  = CreateAdminClientAsync("event-ingestion")
  name   = UniqueName("revoked")
  token  = RegisterAndCaptureTokenAsync(admin, name)          // 201, capture token
  before = PostWebhookAsync(name, "munich", token)            // 201  ← the control
  version= FindAsync(admin, name).version
  revoke = admin.SendAsync(Conditional(name, version))        // 200
  after  = PostWebhookAsync(name, "munich", token)            // 401  ← the assertion

The_refusal_does_not_reveal_that_the_integration_existed: [Fact] scenario 2
  … same setup through the revoke …
  revoked  = PostWebhookAsync(name,              "munich", token)
  unknown  = PostWebhookAsync(UniqueName("gone"),"munich", token)
  revoked.StatusCode.ShouldBe(unknown.StatusCode)
  body(revoked).ShouldBe(body(unknown))
```

**Design notes that are load-bearing, not preference:**

- **The `before` 201 is an assertion, not a warm-up.** Without it the final 401
  is unattributable (spec §3). Assert it with a `DiagnoseAsync` message, because
  a 503 there means munich storage was not provisioned (assumption A3) and a
  reader needs to see that rather than guess.
- **Same fab throughout.** Delivering to another fab would be refused at `:226`
  instead and would pass with `:211` deleted — the counterfactual would then
  prove nothing. Spec §3 scenario 3 states this so a later "hardening" does not
  quietly break it.
- **No polling, no `Task.Delay`.** The revoke commits before its 200 returns and
  the next POST reads a fresh row (spec §2.3). A retry loop here would mask a
  genuine ordering defect.
- **Unique names per test.** No reset helper exists for this context; every
  neighbour mints a unique name and shares the stack. Follow that.
- **Scenario 2 registers its own integration** rather than reusing scenario 1's —
  the two `[Fact]`s may run in either order and xUnit gives no ordering promise.

### Reuse before invention

Every helper this file needs already exists in a sibling. The instruction to the
engineer is to **copy the idiom, not to design one**, and to keep the copies
local (a shared base class across these files is a refactor, not this slice —
ADR-0036's smallest-possible-change).

## 8. Gates

| Gate | How it is met |
|---|---|
| Phase 4a colour | Characterisation, observed **green** (spec §6.3). Evidence is the counterfactual, T003. |
| Coverage (ADR-0065) | Unaffected — no production code added; integration tests do not count toward the Domain/Application gates. |
| Format + analyzers | `dotnet format --verify-no-changes`, Release build clean. |
| CI selection | SC-003 — `--list-tests` under the real filter names both `[Fact]`s. |
| Latency (§IV) | N/A, stated in the spec header. |

## 9. Risks

| Risk | Mitigation |
|---|---|
| The counterfactual reddens more than the new class | Predicted not to (spec §8, with the reasoning). If it does, **report it** — the new test proves less than claimed. Do not silently proceed. |
| A first run after machine churn fails and reads as a defect | Run twice (SC-004) before drawing any conclusion. |
| Copying a `Disruptive` neighbour | Explicitly forbidden in spec §4 and checked by SC-003. Three files in this very folder carry the trait. |
| The revoke returns 428/409 instead of 200 | The version comes from the listing immediately before, and no rotation runs in between. If it happens, `DiagnoseAsync` on the revoke's status makes the cause visible in the CI log. |

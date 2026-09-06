# Tasks 078 — Health answers in Production

**Phase:** 3 (Tasks) · **Date:** 2026-09-06 · **Issue:** #1015 item 4
**Spec:** `spec.md` · **Plan:** `plan.md`

## The three declarations (ADR-0144)

### 1. Which engineer

**`infra-engineer`.**

The change is in `src/ServiceDefaults`, the shared hosting and observability
layer — not a bounded context. `backend-engineer`'s brief is
Domain/Application/Infrastructure/Api inside a context; this touches none of
those, adds no aggregate, no handler and no persistence. Health-endpoint
mapping, k8s probe surface and the Aspire composition root are squarely
infrastructure.

### 2. Behaviour-changing or -preserving

**Behaviour-changing → phase 4a is RED.**

An endpoint that returned 404 in Production returns 200. That is new behaviour,
so a test arriving green is a phase-4 failure (ADR-0139, CLAUDE.md §Testing).

**What the red asserts, concretely.** In
`tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs`, a host built with

```csharp
WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production })
```

then `AddDefaultHealthChecks()` + `MapDefaultEndpoints()`, exposes **no**
endpoint whose `RoutePattern.RawText` is `/health` or `/alive`, because
`Extensions.cs:138` gates both mappings behind `IsDevelopment()`. The assertion
that one is present therefore fails.

**How it is driven without a cluster.** In-process route-table inspection —
neither `WebApplicationFactory` (zero occurrences in this repository) nor
`DistributedApplicationTestingBuilder` nor the Aspire fixture. No Docker, no
network, no Kubernetes. The pattern is lifted from
`tests/Architecture.Tests/SystemVariableReadScopeTests.cs:108`, which already
builds a `WebApplication` in-process and reads
`((IEndpointRouteBuilder)app).DataSources`. `plan.md` records why a real HTTP
round trip was rejected (it would need `Microsoft.AspNetCore.TestHost`, a
package the repo does not reference, to re-prove framework behaviour).

**Note for whoever runs 4a**: the Aspire fixture cannot produce this red at
all. Nothing in `src/AppHost` or `tests/Integration.Tests` sets
`ASPNETCORE_ENVIRONMENT` — grep returns zero hits — so every fixture-booted
service runs in Development, where the endpoints are already mapped. An
integration test would come back green and prove nothing.

### 3. Is the honest answer a new ADR?

**Per item, not overall:**

| #1015 item | New ADR needed? | Verdict |
|---|---|---|
| 1 — TLS at the edge | No. ADR-0106:39 already decides the gateway is the TLS edge. | **Blocked on infrastructure** — no Ingress, no chart, no cluster. |
| 2 — Prod gateway URL for the SPAs | **Yes.** No ADR decides how a built frontend artifact learns its environment; the three options are a real architectural choice. | **Blocked on a decision.** |
| 3 — Ingress + HA | No. ADR-0000 row 025 already locks k3s + Helm. | **Blocked on infrastructure** — the publisher has never been run; there is no manifest to confirm. |
| **4 — Health in Production** | **No** — recommended, with the counter-argument recorded in `spec.md`. | **Deliverable now.** |

Item 4's reasoning in full is in `spec.md` under "Is item 4 itself an ADR?".
Short form: ADR-0000 row 025 (k3s, Locked) and ADR-0106:74 ("≥ 2 replicas with
health checks") already presuppose reachable probes; a probe cannot bind to a
404; making an endpoint answer that two Locked decisions assume implements them
rather than deciding anything new. Anything beyond the minimal mapping —
authentication, a separate port, a detailed writer — would be a new ADR, and
FR-006 excludes all three.

## Parallelism

**None, and this is deliberate.** ADR-0109 marks `[P]` for tasks owning
disjoint files. This feature has two production-relevant files total — one
source, one test — and they are strictly ordered by the red-first rule. There
is nothing to fan out, and a `[P]` marker here would be decoration.

There are no foundational blockers either: no `Shared.Kernel` or
`Shared.Contracts` change, no AppHost change, no new Aspire resource. The
orchestrator has nothing to parallelise and should run this as a single thread.

## Tasks

Format: `[ID] [P?] [Story]`. All tasks belong to the single P1 story.

| ID | P | Story | Task | Depends on |
|---|---|---|---|---|
| **T001** | — | P1 | **Write the red tests.** Add `tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs` covering the six acceptance scenarios in `spec.md`: `/health` present in Production; `/alive` present in Production; both present in Development; both present in an arbitrary environment (`Staging`); the `/alive` predicate admits `live` and rejects `ready`; neither endpoint carries `IAuthorizeData`. Sentence-style underscored names (ADR-0053), xUnit + Shouldly (ADR-0052), hand-written setup, no AutoFixture (ADR-0054). No `.csproj` edit (NFR-001). | — |
| **T002** | — | P1 | **Run T001 and capture the failure verbatim.** `dotnet test tests/ServiceDefaults.Tests`. The Production and Staging scenarios must fail; the Development ones must already pass. Quote the exact output — it is the phase-4 evidence and goes in the PR body (ADR-0139). A green run here is a phase-4 failure, not a shortcut. | T001 |
| **T003** | — | P1 | **Make it pass.** In `src/ServiceDefaults/Extensions.cs:134-151`, remove the `if (app.Environment.IsDevelopment())` gate so both `MapHealthChecks` calls run unconditionally. Do not touch the path constants, the `live` predicate, the check registrations, or the (absent) response writer — FR-002, FR-006. Do not edit the tests (ADR-0144). | T002 |
| **T004** | — | P1 | **Replace the comment (FR-005).** Swap the Aspire boilerplate at `Extensions.cs:136-137` for one stating what the endpoints expose — aggregate status only, default writer, two checks, `/health` does one Postgres query — and that whether the public Ingress should route `/{context}/health` is #1015 item 3's call, not settled here. Say *why*, not *what* (ADR-0036). | T003 |
| **T005** | — | P1 | **Prove Development is untouched.** Boot the Aspire stack and run `dotnet test tests/Integration.Tests --filter GatewayRoutingIntegrationTests` **unmodified**. Nine contexts must still answer 200 through YARP. Check `C:` free space first — do not boot below 6 GB. This is FR-003's evidence. | T003 |
| **T006** | — | P1 | **Update `OutboxBacklogHealthCheck`'s stale rationale.** The comment at `src/ServiceDefaults/OutboxBacklogHealthCheck.cs:117-120` justifies logging by "the health endpoint is mapped in Development only". After T003 that premise is false. Correct the comment; **keep the logging** — the log is still the right signal for an unattended backlog and removing it is out of scope. Comment-only. | T003 |
| **T007** | — | P1 | **Phase 5 verification note.** Run `ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/ApiGateway`; `curl -i` both paths; record 200 against the 404 the same command produces on `develop`. The note must state plainly that **no kubelet probe was observed** and that items 1-3 of #1015 remain open — a discharge nobody earned is the defect class this spec exists to avoid. | T003, T004 |

### Dependency shape

```
T001 → T002 → T003 → ┬→ T004 → T007
                     ├→ T005
                     └→ T006
```

T004, T005 and T006 are independent of one another once T003 lands, but they
touch either the same file as T003 (T004), a shared file (T006), or require an
exclusive Docker stack (T005) — so they are sequential in practice, not `[P]`.

## Files phase 4 touches

| File | Change |
|---|---|
| `tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs` | **new** — the six scenarios |
| `src/ServiceDefaults/Extensions.cs` | remove the `IsDevelopment()` gate (line 138); replace the comment (lines 136-137) |
| `src/ServiceDefaults/OutboxBacklogHealthCheck.cs` | comment only (lines 117-120) |

Three files. No `.csproj`, no `Directory.Packages.props`, no AppHost, no
contract, no migration, and none of the ten `Program.cs` call sites.

## Board (ADR-0037 phase-3 gate)

The gate is that **the feature's issue is on Project #13** — feature-level, not
per-task, since spec 028. `/speckit-taskstoissues` is **not** to be run for this
feature; seven task issues on a board used for in-flight feature tracking would
be noise.

**Not actioned here** — this run was asked to leave the board alone. What the
orchestrator should do, and why, is in the report: #1015 is currently labelled
`agent:ready` while three of its four items are not actionable, which is the
one board change worth making.

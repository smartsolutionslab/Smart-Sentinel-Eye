# Plan 078 — Health answers in Production

**Phase:** 2 (Plan) · **Date:** 2026-09-06 · **Spec:** `spec.md`
**Issue:** #1015 item 4

## Bounded context and layers

**None — and that is the point of the slice.**

`ServiceDefaults` is not a bounded context. It is the shared hosting layer every
context's `Api` project consumes (ADR-0024: Aspire is the composition root;
ADR-0051: per-context `Add<Context>{Infrastructure,Api}` extensions build on
top of it). It contains no domain model, no aggregate, no entity, no value
object, and this change adds none.

| Layer | Touched | Why not |
|---|---|---|
| Domain | no | There is no domain concept here. "Is this process alive" is a hosting fact, not a business invariant. |
| Application | no | No use case, no command, no query, no handler. |
| Infrastructure | no | No persistence, no messaging, no adapter. |
| Api | no | No endpoint of ours; `MapHealthChecks` is framework-supplied. |
| ServiceDefaults (shared hosting) | **yes** | One method, `MapDefaultEndpoints`. |

**Consequences of that, stated so nobody looks for the missing pieces:**

- **No entities or value objects, so no invariants.** Constitution §II's
  primitive ban is not engaged: no domain model is declared or modified, so
  `PrimitiveBoundaryTests` has nothing new to inspect.
- **No messaging.** No domain event, no integration event, no
  `Shared.Contracts` addition, no `V<N>` record (ADR-0040, ADR-0073). Health is
  a pull signal that a prober scrapes; it publishes nothing.
- **No `Result<T, Error>` and no `ApiError`** (ADR-0047, ADR-0089). The
  framework's `HealthCheckResult` already models the outcome, and the endpoint's
  status-code mapping is framework default.
- **No `Ensure.That` guard** (ADR-0105). `MapDefaultEndpoints` is an extension
  method on a non-nullable `WebApplication` that the existing code does not
  guard; adding one would be drive-by scope (ADR-0036).
- **No `Option<T>`** (ADR-0141). No absence is being modelled.
- **No handler, so no deconstruction rule** (CLAUDE.md house rules).

## Boundary rules

Unaffected, and worth saying explicitly because the change is cross-cutting:

- **No cross-context project reference is created.** The change is confined to
  `ServiceDefaults`, which every `Api` already references. NetArchTest's
  boundary rules see no new edge.
- **Nothing is added to `Shared.Contracts`.** The ten hosts pick the change up
  through the shared method they already call, not through a new contract.
- The blast radius is the ten call sites in the spec's table; **none of them is
  edited.** That is the design: one method decides the probe surface, so one
  method is the correct unit of change (ADR-0036, smallest change).

## The change

`src/ServiceDefaults/Extensions.cs`, `MapDefaultEndpoints` (line 134). Remove
the `if (app.Environment.IsDevelopment())` gate at line 138 so both mappings
execute unconditionally, and replace the Aspire boilerplate comment above it
(lines 136-137) with one that records what is exposed and where the residual
public-edge decision lives (FR-005).

Both `MapHealthChecks` calls, the two path constants, the `live` predicate and
the absence of a `ResponseWriter` are all left exactly as they are (FR-002,
FR-006). The diff is a removed conditional, a re-indent, and a comment.

**Explicitly not done** (FR-004, FR-006): no `IConfiguration` flag, no
environment allowlist, no auth, no separate port, no detailed writer. Each is
either speculative generality or a decision this lane may not make.

## How phase 4a drives a Production host with no cluster

This is the part of the plan that is not obvious, so it is specified rather than
left to the engineer.

**Reuse the existing in-process pattern**, established at
`tests/Architecture.Tests/SystemVariableReadScopeTests.cs:108`:

```csharp
WebApplication app = WebApplication.CreateBuilder([]).Build();
app.MapSystemVariableEndpoints();
return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];
```

For this feature the builder additionally sets the environment, which
`WebApplicationOptions` supports directly:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(
    new WebApplicationOptions { EnvironmentName = Environments.Production });
builder.AddDefaultHealthChecks();
WebApplication app = builder.Build();
app.MapDefaultEndpoints();
// then read ((IEndpointRouteBuilder)app).DataSources and assert on RoutePattern.RawText
```

**Why route-table inspection rather than a real HTTP round trip.** The only
behaviour this change alters is *whether the mapping happens*. Whether a mapped
`MapHealthChecks` endpoint returns 200 is framework code, and it is already
exercised end-to-end by the Aspire-fixture suite in Development
(`GatewayRoutingIntegrationTests`, nine contexts). Asserting the route table
tests exactly the delta and nothing else — the smallest test for the smallest
change.

**The alternative was considered and rejected on cost.** A real `GET` would
need `Microsoft.AspNetCore.TestHost`, a NuGet package this repository does not
reference anywhere, violating NFR-001 to re-prove framework behaviour. There is
no `WebApplicationFactory` in the repository either — grep confirms zero hits —
so there is no established HTTP-level host-test pattern to follow, and
introducing one for this is disproportionate. The `dotnet run` step in the
spec's end-to-end procedure covers the round trip at phase 5, where a human
observes it.

**Test project:** `tests/ServiceDefaults.Tests` — it already references
`src/ServiceDefaults`, which carries `FrameworkReference
Microsoft.AspNetCore.App`, and a `FrameworkReference` flows transitively. So
**no `.csproj` edit is required** and NFR-001 holds without effort. New file:
`tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs`.

## Verification strategy per phase

| Phase | What is observed | Needs a cluster |
|---|---|---|
| 4a (red) | Production/Staging scenarios fail against `develop`'s gate; verbatim output quoted in the PR (ADR-0139) | no |
| 4b | Same tests green; Development scenarios green throughout | no |
| 5 | `ASPNETCORE_ENVIRONMENT=Production dotnet run` + `curl /alive`, `/health` → 200, against 404 on `develop` | no |
| 5 | A kubelet probe succeeding against a deployed pod | **yes — out of scope, and the note must say so** |

## Constitution and gate alignment

- **§IV latency budget** — N/A, no leg. Health paths are already excluded from
  tracing (`Extensions.cs:100-103`), so no span or meter changes.
- **§VII dashboards** — not engaged; the obligation binds implemented latency
  legs, and this is not one.
- **ADR-0065 coverage gate** — `ServiceDefaults` is **not** in
  `scripts/coverage-check.ps1`'s threshold map, which lists only each context's
  `Domain` (≥ 90%) and `Application` (≥ 80%) plus `Shared.Kernel` and
  `Shared.Contracts` (≥ 90%). No threshold applies here. Recorded so the gate is
  not assumed either way.
- **ADR-0084 code metrics** — the change *removes* a nesting level from
  `MapDefaultEndpoints` and adds no lines of logic. File and method LOC,
  parameter count, complexity and depth all improve or hold.
- **ADR-0103** — no Docker in the fast lane; the new tests are pure in-process.
- **ADR-0139 / §Testing** — behaviour-changing, so the tests start **red**. See
  `tasks.md` for the declaration.

## Risks

1. **A merged 078 reads as a closed #1015.** Three of four items remain. Both
   the spec and the PR body must say so, and #1015 must stay open. This is the
   main risk and it is documentary, not technical.
2. **The public-edge exposure arrives unexamined with item 3.** Mitigated by
   FR-005's comment at the mapping site and by the spec's exposure section, but
   mitigation is a pointer, not a guarantee. Called out again in the report.
3. **The exposure statement is conditional on the check set.** If a future
   change adds `AddNpgSql`/`AddRabbitMQ` checks or a detailed writer, `/health`
   starts disclosing topology and this spec's analysis silently stops being
   true. Nothing enforces that today; noted as a known limit rather than
   papered over.

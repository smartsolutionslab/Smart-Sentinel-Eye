# Spec 078 — Health answers in Production

**Issue:** #1015 (item 4 only) · **Branch:** `feat/1015-health-answers-in-production`
**Phase:** 1 (Specify) · **Date:** 2026-09-06
**ADRs:** ADR-0106 (API gateway; the one place in the decision record that
assumes health checks exist — "must run ≥ 2 replicas with health checks"),
ADR-0000 row 025 (k3s + Helm as the production target, still Locked), ADR-0024
(Aspire as composition layer), ADR-0036 (smallest change; no speculative
generality), ADR-0037 (phased workflow), ADR-0052 / ADR-0103 (xUnit + Shouldly;
no Docker in the fast lane), ADR-0084 (code metrics), ADR-0109 (parallel
markers), ADR-0139 (new behaviour starts red), ADR-0144 (autonomous lane; no ADR
is written here), ADR-0130 (a record nobody checks against the thing it
describes drifts — the reason the section below exists).

## Scope: one of the four items on #1015

#1015 carries four items. **This spec covers item 4 only, and only the half of
item 4 that can be observed without a cluster.** Items 1, 2 and 3 are not in
scope and are not discharged by this work; the section "What this spec does not
do" records why, so that a merged 078 is not mistaken for a closed #1015.

## The issue as filed, and what survives contact with the repository

> the gateway's `/health` and `/alive` return **404 in Production** —
> `ServiceDefaults.MapDefaultEndpoints` maps them only under `IsDevelopment()`
> (Aspire's security default, `src/ServiceDefaults/Extensions.cs:100`). k8s
> liveness/readiness probes need a Production-available endpoint. … (Applies to
> every service, but the gateway is the Ingress-fronted one.)

**The defect holds. Two details in the sentence do not.**

1. **The line number has drifted.** The `IsDevelopment()` gate is at
   `src/ServiceDefaults/Extensions.cs:138`, not `:100`. Line 100 today is a
   comment inside the OpenTelemetry tracing filter. The reference was correct
   when filed and is 38 lines stale now.

2. **"Applies to every service" is exactly right, and is the more important
   half of the issue.** The parenthesis reads as an aside; it is the scope.
   `MapDefaultEndpoints` is called by **ten hosts** — the gateway and all nine
   context APIs — so one method decides the probe surface of the entire system.

### The population, measured on `979a695d`

Every caller of `MapDefaultEndpoints`, all of which 404 on `/health` and
`/alive` outside Development:

| # | Host | Call site |
|---:|---|---|
| 1 | ApiGateway | `src/ApiGateway/Program.cs:60` |
| 2 | AuditObservability | `src/AuditObservability/Api/Program.cs:15` |
| 3 | Automation | `src/Automation/Api/Program.cs:15` |
| 4 | CameraCatalog | `src/CameraCatalog/Api/Program.cs:15` |
| 5 | EventIngestion | `src/EventIngestion/Api/Program.cs:15` |
| 6 | Identity | `src/Identity/Api/Program.cs:15` |
| 7 | LayoutComposition | `src/LayoutComposition/Api/Program.cs:43` |
| 8 | OverlayDesigner | `src/OverlayDesigner/Api/Program.cs:15` |
| 9 | StreamDistribution | `src/StreamDistribution/Api/Program.cs:15` |
| 10 | SystemVariables | `src/SystemVariables/Api/Program.cs:15` |

### Reproduce the counts without reading the table

Git Bash. No `jq` and no usable `python` on this machine — the `python` on
`PATH` is the Windows Store alias stub, which is why the commands below are
`grep` and `find` only.

```sh
# 10 — the hosts whose probe surface this one method decides
grep -rn "MapDefaultEndpoints" --include=*.cs src | grep -v obj/ \
  | grep -v "ServiceDefaults/Extensions.cs" | wc -l

# 138 — where the gate actually is
grep -n "IsDevelopment" src/ServiceDefaults/Extensions.cs

# 2 — the health checks that exist, and their tags
grep -rn "AddCheck\|AddTypeActivatedCheck" --include=*.cs src | grep -v obj/
```

## Why this is a real defect and not a theoretical one

Three independent places in the repository already assume these endpoints
answer in Production. Each is currently wrong.

1. **`src/ApiGateway/Program.cs:28`** — "The policy is named and attached per
   route below, so the gateway's own health endpoints and the k8s liveness and
   readiness probes are never throttled." A comment carefully protecting probes
   from the rate limiter, guarding endpoints that 404.

2. **`src/ServiceDefaults/OutboxBacklogHealthCheck.cs:117-120`** — the check
   logs its Degraded result *because* the endpoint is unreachable: "the health
   endpoint is mapped in Development only … and production is the only place an
   outbox grows unattended. A signal that exists solely on a surface nobody can
   reach in production is not a signal (FR-009)." The workaround is in the code,
   with the reason written next to it.

3. **ADR-0106:74** — "must run ≥ 2 replicas with health checks (it is
   stateless, so it scales horizontally)." A liveness probe cannot bind to a
   404, so `.WithReplicas(2)` (`src/AppHost/AppHost.cs:455`) cannot deliver the
   availability ADR-0106 claims for it.

## What `/health` and `/alive` actually expose

This matters more than usual, because the change reverses a framework default
that exists for security reasons. The exposure is small, but it is not nil, and
it is stated here so that nobody has to reconstruct it later.

**The registered checks are two, and only two:**

| Check | Registered at | Tags | Does I/O |
|---|---|---|---|
| `self` | `src/ServiceDefaults/Extensions.cs:129` | `live` | no |
| `outbox-{module}` | `src/ServiceDefaults/WolverineDefaults.cs:168` | `ready` | **yes** — one Postgres query per request |

**The response writer is the framework default.** No `ResponseWriter` is set on
either mapping, so ASP.NET Core writes the aggregate `HealthReport.Status` as
plain text and nothing else — one word: `Healthy`, `Degraded` or `Unhealthy`.
Per-check names, descriptions and the `data` dictionary (which does carry the
outbox schema name and pending counts) are **not** serialised. The "health
endpoints leak dependency topology" warning that motivates Aspire's default
describes a *detailed* writer over a rich check set; it does not describe this
payload today.

That last clause is conditional, and the condition is load-bearing: **the
exposure statement holds only while the writer stays default and the check set
stays these two.** Adding `AddNpgSql`, `AddRabbitMQ` or a JSON writer changes
what this endpoint discloses, and would be the moment to revisit it.

**So, concretely, once mapped in Production:**

- `/alive` — predicate `Tags.Contains("live")`, so only `self`. Always
  `200 Healthy` while the process is running, no I/O. Discloses nothing a
  successful TCP connect does not already disclose.
- `/health` — no predicate, so both checks. Runs one Postgres query per
  request. Returns `200` for Healthy **and Degraded** (framework default
  `ResultStatusCodes`), `503` only for Unhealthy — which is the correct
  readiness semantic: an outbox backlog must not pull a replica out of
  rotation.

**To whom.** In-cluster, the kubelet on the pod network. Publicly, only if an
Ingress routes it — and the gateway's route table means that path is real once
an Ingress exists: `/{context}/{**catch-all}`
(`src/ApiGateway/appsettings.json`) forwards `/{context}/health` to each
service's `/health`, unauthenticated, for all nine contexts. The gateway does
not validate JWTs (ADR-0106 keeps that per service), so nothing on that path
requires a token. The existing routing test depends on precisely this:
`tests/Integration.Tests/ApiGateway/GatewayRoutingIntegrationTests.cs:9` — "a
200 from each service's unauthenticated `/health` proves YARP routing".

**That public path is out of scope here and is recorded, not inherited.** No
Ingress exists (see "What this spec does not do"), so merging this spec makes
nothing newly reachable from outside a cluster. Deciding whether the public
edge should expose `/{context}/health` belongs to #1015 item 3, which builds
the Ingress. FR-005 below exists so that decision is met rather than discovered.

## User stories

### P1 — An operator's cluster can tell whether a replica is alive (this spec)

**As** whoever deploys Smart Sentinel Eye to k3s,
**I want** every service to answer `/alive` and `/health` in Production,
**so that** k8s liveness and readiness probes bind to a real signal and
`.WithReplicas(2)` delivers the availability ADR-0106 claims for it.

There is no second story. The slice is one method in one file, and it is the
smallest change that makes all ten hosts probeable (ADR-0036).

## Functional requirements

- **FR-001** `MapDefaultEndpoints` maps `/health` in every environment, not
  only Development.
- **FR-002** `MapDefaultEndpoints` maps `/alive` in every environment, with its
  `live` tag predicate unchanged.
- **FR-003** Development behaviour is unchanged — both endpoints stay mapped,
  with the same paths, predicate and default writer. The existing Aspire-fixture
  integration tests that depend on `/health` must pass **unmodified**.
- **FR-004** No configuration knob, no environment allowlist, no opt-out flag.
  Speculative generality is banned (ADR-0036); a knob here would also mean a
  cluster could be deployed with probes silently off, which is the defect this
  spec removes.
- **FR-005** The Aspire security comment above the mapping is replaced by one
  that states what is exposed and what the residual public-edge question is,
  citing #1015 item 3. The comment says *why*, which is the only kind this repo
  keeps (ADR-0036).
- **FR-006** No change to the check set, the tags, or the response writer. The
  exposure statement in this spec must remain true after the change.

## Non-functional requirements

- **NFR-001** No new NuGet package reference, in `src/` or `tests/`.
- **NFR-002** No new host, no new endpoint path, no new port.

## Acceptance scenarios

The four categories this repo asks for do not all map onto an unauthenticated
health endpoint, and inventing them would be worse than saying so. **Conflict**
and **bad-request** have no meaning for a `GET` that takes no input and mutates
nothing — there is no version to conflict on and no body to reject. They are
recorded as not-applicable rather than fabricated. **Auth** does apply, and its
scenario is that the endpoints are deliberately anonymous.

```gherkin
Scenario: A Production host answers the readiness probe
  Given a host configured with environment "Production"
  And AddDefaultHealthChecks and MapDefaultEndpoints have been called
  When the mapped endpoints are enumerated
  Then an endpoint with the route "/health" is present

Scenario: A Production host answers the liveness probe
  Given a host configured with environment "Production"
  And AddDefaultHealthChecks and MapDefaultEndpoints have been called
  When the mapped endpoints are enumerated
  Then an endpoint with the route "/alive" is present

Scenario: Development is unchanged
  Given a host configured with environment "Development"
  And AddDefaultHealthChecks and MapDefaultEndpoints have been called
  When the mapped endpoints are enumerated
  Then endpoints with the routes "/health" and "/alive" are both present

Scenario: An arbitrary environment answers too
  Given a host configured with environment "Staging"
  And AddDefaultHealthChecks and MapDefaultEndpoints have been called
  When the mapped endpoints are enumerated
  Then endpoints with the routes "/health" and "/alive" are both present

Scenario: Liveness ignores the readiness-tagged checks
  Given a host configured with environment "Production"
  When the "/alive" endpoint's health-check predicate is applied
  Then it admits the check tagged "live"
  And it rejects the check tagged "ready"

Scenario (auth): The probes are anonymous by design
  Given a host configured with environment "Production"
  When the mapped "/health" and "/alive" endpoints are inspected
  Then neither carries any IAuthorizeData metadata
```

The last scenario asserts the *deliberate* absence of authorization rather than
its presence. It is written down because "the probe endpoint is anonymous" is a
security-relevant property that should fail a test if someone later changes it
by accident — in either direction. A kubelet cannot present a bearer token.

## Independent end-to-end test procedure

Executable on this machine, no cluster, no Kubernetes, no Docker.

1. Check out the branch and build: `dotnet build`.
2. Run the ServiceDefaults unit tests:
   `dotnet test tests/ServiceDefaults.Tests`. The new tests pass; before the
   change, the Production and Staging scenarios fail (see phase 4a).
3. Confirm Development is genuinely untouched by booting the real stack and
   running the gateway routing suite, which asserts an unauthenticated `200`
   from `/{context}/health` through YARP for all nine contexts:
   `dotnet test tests/Integration.Tests --filter GatewayRoutingIntegrationTests`.
   These tests are **not** modified by this work; that they still pass is the
   evidence for FR-003.
4. Observe the change in a running process without a cluster — the closest
   thing to a probe this repo can perform today:

   ```sh
   ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/ApiGateway
   curl -i http://localhost:<port>/alive     # expect 200, body "Healthy"
   curl -i http://localhost:<port>/health    # expect 200
   ```

   On `develop` both answer `404`. That contrast is the verification note for
   phase 5.

**What this procedure cannot show**, and no procedure in this repository can
today: that a kubelet's probe succeeds against a deployed pod. That needs the
cluster item 3 is blocked on. Phase 5's note must say so rather than implying a
probe was observed.

## Locked tech choices

Nothing new is chosen. The work reuses:

- .NET 10 + ASP.NET Core + Aspire (ADR-0024); `MapHealthChecks` from the shared
  framework, already available via `FrameworkReference Microsoft.AspNetCore.App`
  in `src/ServiceDefaults/SmartSentinelEye.ServiceDefaults.csproj:15`.
- xUnit + Shouldly (ADR-0052); no Docker in the fast lane (ADR-0103).
- The in-process host-building pattern already established at
  `tests/Architecture.Tests/SystemVariableReadScopeTests.cs:108`
  (`WebApplication.CreateBuilder(...)`, then read
  `((IEndpointRouteBuilder)app).DataSources`). Reused rather than reinvented,
  and it is why NFR-001 costs nothing: `ServiceDefaults.Tests` already inherits
  the ASP.NET framework reference transitively through its project reference.

## Latency-budget impact

**N/A — no leg.** Health endpoints are not on the event-to-overlay path
(constitution §IV). They are already excluded from tracing at
`src/ServiceDefaults/Extensions.cs:100-103`, so mapping them in more
environments creates no spans and touches no meter. The `/health` Postgres query
runs only when something calls it; nothing on the budgeted path does.

Constitution §VII's dashboard obligation is not engaged: it binds implemented
*latency legs*, and this is not one.

## What this spec does not do

The other three items on #1015, and why each is left open. The load-bearing
fact for all three: **no Kubernetes cluster, chart, or publisher output exists
in this repository.** Verified on `979a695d`:

```sh
# zero hits — no k8s package is referenced anywhere
grep -rn "Aspire.Hosting.Kubernetes\|AddKubernetesEnvironment\|PublishAsKubernetes" \
  --include=*.csproj --include=*.cs --include=Directory.Packages.props . | grep -v obj/

# two files — one hand-written Mosquitto fragment, and no Chart.yaml
find deploy -type f
```

| # | Item | Status | Why |
|---|---|---|---|
| 1 | TLS termination at the edge | **Blocked on infrastructure** | ADR-0106:39 decides the gateway *is* the TLS edge, and that much is settled. Termination lives in an Ingress / Helm chart that does not exist; ADR-0000 rows 024/025 decide nothing about TLS at all. Nothing to build in this repository, nothing to verify without a cluster. |
| 2 | Prod gateway-URL injection for the SPAs | **Blocked on a decision — needs an ADR** | No ADR decides how a built frontend artifact learns its environment. The issue's three options are a genuine architectural choice, and ADR-0144 forbids this lane from making one. See the note below. |
| 3 | k3s Ingress → gateway + HA | **Blocked on infrastructure** | "Confirm `.WithReplicas(2)` lands as `replicas: 2` plus a PodDisruptionBudget" cannot be confirmed: the Aspire Kubernetes publisher has never been run and no k8s package is referenced, so there is no manifest to inspect. Generating one is itself unbuilt work. |

**On item 2, one correction the issue could not have made about itself.** Its
third option — "same-host Ingress so the app can fall back to same-origin (the
`gatewayApiUrl` helper already degrades to same-origin when the env is empty)"
— rests on behaviour that no longer exists in production builds. Spec 011
FR-010 replaced that fallback with a loud throw:

```
apps/shared/src/api/gateway.ts:19-21
  if (import.meta.env.PROD && gatewayOrigin === '') {
    throw new Error('VITE_API_GATEWAY_URL must be set in production builds …');
  }
```

The same-origin fallback survives only when `import.meta.env.PROD` is false —
unit tests and previews. The file's own header comment (lines 10-11) still
claims the general fallback, so `gateway.ts` currently contradicts itself, and
the issue quotes the stale half. Whoever picks up item 2 must either choose a
different option or deliberately reopen FR-010. That is a decision, which is
why item 2 is blocked on one.

`docs/deployment-frontend-env.md` already documents build-time bake as the
current contract for all three `VITE_*` variables. Item 2 is therefore not a
greenfield choice but a proposal to change a documented contract — which
strengthens, not weakens, the case that it wants an ADR.

## Is item 4 itself an ADR?

Recorded because ADR-0144 makes it a gating question, and because the answer is
a judgement call rather than a lookup.

**No ADR is searchable on this.** No ADR and no section of the constitution
decides whether health endpoints are exposed in production, what paths they
use, or how probes bind to them. ADR-0106:74's "≥ 2 replicas with health
checks" is the only sentence in the entire decision record that assumes they
exist.

**The recommendation is that the minimal fix is implementation, not a new
decision**, on this reasoning: ADR-0000 row 025 locks k3s + Helm as the
production target and ADR-0106:74 commits the gateway to running with health
checks. A probe cannot bind to a 404. Making an endpoint answer that two
Locked decisions already presuppose implements them rather than deciding
anything new — and the change adds no path, no port, no payload and no
configuration surface.

**The counter-argument, stated so the reviewer can overturn this.** The change
reverses a framework security default across ten hosts, and once item 3's
Ingress exists, `/{context}/health` becomes publicly reachable for nine
contexts unauthenticated. Someone should decide that is acceptable. This spec's
answer is that the decision belongs to the work that builds the Ingress, and
FR-005 plants the pointer so it is met rather than inherited silently — but a
reviewer who wants it decided *before* the endpoints are mapped is making a
reasonable call, and that would make item 4 blocked too.

**What would unambiguously be a new ADR**, and is therefore excluded from this
spec by FR-006: putting health behind authentication, moving it to a separate
port or host, or adding a detailed response writer. None is proposed here.

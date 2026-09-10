# Spec 126 — A seeder that authenticates

**Issue:** #2158
**Branch:** `fix/2158-a-seeder-that-authenticates`
**Status:** Phase 3 complete — awaiting gate
**Lane:** autonomous (ADR-0144)
**ADRs:** 0037 (phases), 0144 (lane, 4a colour), 0036 (smallest change),
0139 (red first), 0100 / 0111 / 0116 (the four existing
`client_credentials` service accounts), 0143 (retry only idempotent
methods), 0105 (`Ensure.That`), 0049 (`CancellationToken` last), 0050
(`[LoggerMessage]`), 0051 (per-context DI extension), 0103 (Aspire
fixture), 0052/0053/0054 (xUnit + Shouldly + hand-written fakes),
0084 (code metrics).
Constitution §II (no primitives on a domain model — not engaged, this is
Infrastructure), §IV (latency budget — see *Latency* below), §Testing.

## Problem

Spec 005 T061 specified a reverse-index seeder that *"authenticates with
Keycloak (service account)"*. **The seeder shipped; the authentication did
not.** The code says so in its own doc comment
(`ReverseIndexSeederHostedService.cs:15-19`): auth is deferred, v1 hits the
endpoint unauthenticated, and a 401 is accepted as "skip seeding for now".

Underneath it, `GET /overlays/` became
`.RequireAuthorization(Scope.Sse.Overlays.Read)`
(`OverlayEndpoints.cs:56-57`). So the deferred half is no longer deferred —
it is broken.

This is **not** the #2132 "registered nowhere" shape. The hosted service is
registered (`SystemVariablesInfrastructureModule.cs:102`) and it runs. It
runs and is refused.

### Observed, not inferred

The issue explicitly recorded that the 401 was *not established at runtime* —
the audit was `git grep` only. It was established before any code was
changed, against the dev Aspire stack (2026-09-10). `verification.md`
carries the transcript. In summary:

| Observation | Result |
|---|---|
| `GET http://localhost:5288/overlays?state=Published`, no bearer | **401** |
| the same, with a bearer | **200** |
| `system-variables` console at cold start | `warn: ... ReverseIndex seed: overlay-designer returned Unauthorized; starting with empty index.` |
| set a variable an overlay references, index warmed by the published event | `Pushed ResolvedOverlayTextChanged to 1 overlays after 'obs2158' changed.` |
| restart `system-variables`, set the same variable again | seed logs `Unauthorized`; **no** `Pushed ResolvedOverlayTextChanged` line at all |

The last two rows are the defect. The same variable change fans out to one
overlay before the restart and to nothing after it, because the index the
fan-out reads is empty and only republication refills it.

## What this spec does

Gives the seeder the service account spec 005 said it would have, and stops
treating a refusal as routine.

1. **A fifth `client_credentials` sibling.** `ClientCredentialsTokenProvider`
   (ServiceDefaults) is the shared mechanism; four thin wrappers already sit
   on it. This adds the fifth, modelled on `CameraCatalogTokenProvider` —
   the closest analogue, being service-to-service HTTP with a scoped read —
   plus the `AuthorizingHandler` that attaches it to the named client.

2. **A minimal Keycloak client.** `system-variables-seeder`, holding
   `sse.overlays.read` and nothing else that grants anything.

3. **A refusal is loud.** 401/403 gets its own `Error`-level event that does
   not promise self-healing. Every other non-success status keeps the
   existing warning.

## The 401 decision, made explicitly

The issue asked for this to be decided rather than drifted into. **A 401 is
no longer swallowed as expected: it becomes an `Error` with its own message.
It does not abort host start.**

Why loud:

- Post-authentication a 401 means *the credential is wrong* — a missing
  scope, a stale secret, a disabled client. It cannot self-heal. The
  existing message ("the index will populate as new
  `OverlayRevisionPublishedV1` events arrive") is **true for an
  overlay-designer outage and false for a misconfigured credential**, and a
  message that is confidently wrong is worse than none.
- ADR-0143 already covers the transient case. `GET` is retried by the
  standard resilience handler, so a Keycloak blip or a slow overlay-designer
  is absorbed before the seeder ever sees a status code. What reaches the
  seeder as a 401 has survived those retries. There is nothing left for
  swallowing to buy.

Why not fatal:

- Throwing from `IHostedService.StartAsync` aborts host start, trading a
  degraded reverse index for a total SystemVariables outage — no variable
  reads, no writes, no API.
- The sibling precedent settled this exact trade the other way and wrote
  down why: StreamDistribution's startup attribution deliberately does not
  gate host start (ADR-0116), and
  `StreamFabAttributionFailureTests.An_unreachable_camera_catalog_does_not_block_host_start`
  is the assertion that keeps it that way. Video keeps flowing; here,
  variable management keeps working.
- Widening the blast radius of a startup call is the kind of decision the
  autonomous lane does not get to make on its own (ADR-0144).

**This did not need an ADR.** It is a log level and a message for a failure
mode that becomes reachable-for-a-new-reason inside this issue's own scope,
resolved by an existing decision (ADR-0143) and an existing precedent
(ADR-0116). No principle moves.

## Scope of the credential

`GET /overlays` is `.RequireAuthorization(Scope.Sse.Overlays.Read)` and
OverlayDesigner runs **no fab guard** — the endpoint file says so, and an
overlay is a fab-neutral template (ADR-0115). So the seeder needs:

| Scope | Why |
|---|---|
| `sse.overlays.read` | the one endpoint it calls |
| `sse-identity` | emits `sub`; carries no permission |
| `sse-audience` | emits `aud: smart-sentinel-eye-api`; carries no permission |

and **not** `sse-groups` (no fab guard to satisfy), and nothing writable.

No existing client fits. `scenario-simulator` does hold `sse.overlays.read`,
but it is dev-only (ADR-0111) and carries write scopes across five contexts;
reusing it would hand a startup reader the ability to author overlays,
rules, layouts and cameras. `stream-distribution-attribution` is
`sse.cameras.read` only.

## Latency (constitution §IV)

**Not touched.** Cold-start seeding is not one of the six legs — it happens
once per host start, outside any event's path.

The distinction worth being precise about: the reverse index this seeds *is*
read on leg 4 (*Event → overlay state*, ≤ 200 ms), by
`VariableValueChangedDomainEventHandler`. But seeding does not run on that
leg and adds no work to it. An empty index makes leg 4 *faster* and wrong —
`LookupOverlays` returns nothing and the handler returns early. So this
change alters **whether the leg produces an overlay update at all**, not how
long it takes. No budget claim, no measurement obligation, no §VII
dashboard row.

## Functional requirements

- **FR-001** The seeder presents a `client_credentials` bearer token on
  `GET /overlays?state=Published`.
- **FR-002** The token is minted through `ClientCredentialsTokenProvider`,
  via a thin per-context wrapper, registered as a singleton so the cache
  means something (#2037).
- **FR-003** The `system-variables-seeder` realm client holds
  `sse.overlays.read` and no scope that grants a write or an admin
  capability.
- **FR-004** A 401 or 403 from overlay-designer is logged at `Error`, with a
  message that does not claim the index will self-heal.
- **FR-005** Any other non-success status keeps the existing `Warning`.
- **FR-006** A refusal, of any kind, still leaves the host started.
- **FR-007** The doc comment counting the token-provider siblings is correct
  after this change.

## Out of scope

- Making the seeder retry, or re-seeding periodically. The fix is that it
  succeeds; the loud error is for when it does not.
- Any change to `GET /overlays`, its scope, or its response shape.
- The reverse index itself.

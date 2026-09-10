# Spec 122 — a compensation that says when it fails

**Issue:** #2166. **Branch:** `fix/2166-a-compensation-that-says-when-it-fails`.

## Problem

`HttpKeycloakAdminClient.TryDeleteClientAsync` issues its DELETE and never
reads the answer:

```csharp
using HttpResponseMessage response = await httpClient
    .DeleteAsync($"admin/realms/{realm}/clients/{clientUuid}", cancellationToken);
```

The variable is declared, disposed, and never inspected. A DELETE that is
*refused* — 409, 500, 401, 403 — therefore travels the same path as one that
succeeded: no branch, no log, nothing. Only a thrown `HttpRequestException`
produces a line today.

### What survives a refused compensation

The delete is the compensating half of a non-atomic enrolment: when
`StripInheritedRealmRolesAsync` throws after the client is already created,
the delete removes the half-made client. When the delete is refused, what
stays in the realm is

- a client stamped `sse.kind=kiosk`, holding the inherited realm roles
  including `offline_access`;
- a service account whose secret **the caller never received**, because the
  enrolment call failed;
- and an existence probe (`CreateClientAsync:59`) that answers *already
  enrolled* for it, so the same kiosk can never be enrolled again.

The only signal is the original enrolment error, which describes the *strip*
failure. Nothing anywhere names the residue.

## The premise that has changed since filing

The issue's point 1 — *"the startup sweep has never run"* — is **no longer
true**. Spec 092 landed: `IdentityInfrastructureModule.cs:56-57` registers
`AddScoped<KioskPrivilegeSweep>()` and
`AddHostedService<KioskPrivilegeSweepHostedService>()`, and #2132 is closed.

**Point 2 is the live defect, and the sweep does not reach it.** Read at
`KioskPrivilegeSweep.SweepAsync`, the sweep's entire body per kiosk is

```csharp
await keycloak.StripInheritedRealmRolesAsync(clientId, cancellationToken);
```

It **strips privileges and removes nothing**. It does not delete the residue
client, does not disable it, does not flag it, and does not distinguish a
half-enrolled residue from a healthy enrolled kiosk — `GetEnrolledKioskClientIdsAsync`
selects on `sse.kind=kiosk` alone, which both carry.

So after the sweep runs, the residue client still exists, still carries
`sse.kind=kiosk`, and the existence probe still answers *already enrolled*.
**The re-enrolment block survives the sweep entirely**, on every start,
forever. That is the thing this spec makes visible.

The sweep is not changed here. It is spec 092's, freshly landed, and it does
the privilege half correctly.

## What Keycloak actually answers (measured, not assumed)

Probed against the running run-mode container `keycloak-18bcf406`
(quay.io/keycloak/keycloak:26.6), realm `smart-sentinel-eye`, via
`DELETE /admin/realms/{realm}/clients/{uuid}`:

| Case | Status | Body |
|---|---|---|
| Existing client | **204** | *(empty)* |
| Same uuid a second time | **404** | `{"error":"Could not find client"}` |
| Unknown uuid | **404** | `{"error":"Could not find client"}` |
| Malformed uuid | **404** | `{"error":"Could not find client"}` |
| Unknown realm | **404** | `{"error":"Realm not found."}` |
| No bearer token | **401** | `{"error":"HTTP 401 Unauthorized"}` |

Success is exactly `204` with an empty body, so `IsSuccessStatusCode` is a
sound predicate and there is no success case carrying a payload to misread.
The throwaway client used for the 204/404 pair was created and deleted inside
the probe; the realm is otherwise untouched and the volume was not deleted.

## Scope

**In.** Inspect the DELETE's status; log a refused compensation at a level
that matches "only a human clears this", naming the client in structured
fields.

**Out.** Making the delete throw (explicitly forbidden by the issue — the
original enrolment error is the one that explains why compensation was needed).
Changing `KioskPrivilegeSweep`. Retrying the delete. Changing what the API
returns to the caller — see FR-005.

## Requirements

- **FR-001** — a DELETE answering a non-success status **other than 404**
  MUST produce a log record naming the client, because the client demonstrably
  still exists.
- **FR-002** — that record MUST carry the client's `clientId` and its Keycloak
  uuid as **structured fields**, not only inside message text. `clientId` is
  what the existence probe collides on and what an operator searches by; the
  uuid is what a manual `DELETE` needs.
- **FR-003** — the record MUST state the consequence that is actually true:
  the client survives, and re-enrolment reports *already enrolled* until it is
  removed by hand. It MUST NOT claim the startup sweep resolves it — the sweep
  strips privileges and leaves the client.
- **FR-004** — a **404** MUST NOT be reported as a surviving residue. Keycloak
  is saying the client is not there, so the compensation's goal is met. It is
  still unexpected one call after the create re-probe found it, so it is logged
  — as an absence, at a lower level.
- **FR-005** — nothing about the residue is added to what the caller receives.
  The enrolment error stays exactly as it is.
- **FR-006** — the delete MUST NOT throw, and MUST NOT alter the exception the
  caller is already receiving.

## Non-functional

- ADR-0050: `[LoggerMessage]` in the per-layer `Log` catalog, `this ILogger`
  extension methods, structured fields, no interpolation.
- ADR-0084: ≤4 parameters per method — binding on both the new catalog entries
  and on `TryDeleteClientAsync` itself.
- ADR-0049: `CancellationToken` last. ADR-0105: `Ensure.That`.
- **§IV latency: not touched.** Kiosk enrolment is an administrative,
  operator-initiated call. It is not on the event-to-overlay path and appears
  in none of the six legs. This change adds one status comparison and, only on
  an already-failing path, one log call.

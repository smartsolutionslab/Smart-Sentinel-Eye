# Spec 121 — an explicit off is sent

**Issues:** #2165 and #2207 — **the same defect, filed independently**, six weeks
apart, by two different investigations, **without either referencing the other**.
#2165 came out of the phase-6 security review of #2085 (spec 090); #2207 came out
of verifying an unrelated claim during #603. Both observed it against a live
Keycloak. Neither found the other. That is itself part of the record: the defect
is invisible in review, so it is found only by reading what Keycloak *stored*,
and two people who did that independently both had to file it fresh.

**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0144 (autonomous lane), 0037 (phases), 0036 (smallest change),
0052/0053/0054 (xUnit + Shouldly, sentence-style names, hand-written fakes),
0105 (`Ensure.That`), 0049 (`CancellationToken` last), 0141 (NRT).

## Problem

`src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:36-39`
configures the one `JsonSerializerOptions` this client uses for every request it
sends:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
};
```

`WhenWritingDefault` drops **every property whose value equals the default for
its type** — which for `bool` is `false`. So a request that explicitly asks for a
flag to be *off* sends nothing at all, Keycloak sees an absent field, and
Keycloak applies **its own default**.

#2207 states the failure mode exactly: *an explicit "off" is indistinguishable
from an omitted field. Every security-relevant flag in this API is a boolean
whose safe value is `false`, so the serializer silently discards precisely the
settings worth stating.*

### Observed, not inferred

Both issues measured a live realm rather than reading source.

```
[KC] client webhook-<name>: enabled=True, serviceAccountsEnabled=True,
     publicClient=False, standardFlowEnabled=True
```

```
### flags as stored:
"standardFlowEnabled":true       <- the handler asked for false
"directAccessGrantsEnabled":false
"publicClient":false
```

### Three creation paths, one serialiser

All three pass an identical flag set, and all three are wrong on the wire in the
same way:

| Call site | flags passed |
|---|---|
| `EnrollKioskCommandHandler.cs:34-37` | `ServiceAccountsEnabled: true`, `StandardFlowEnabled: false`, `DirectAccessGrantsEnabled: false`, `PublicClient: false` |
| `RegisterDeviceCommandHandler.cs:51-54` | identical |
| `RotateWebhookClientCommandHandler.cs:105-108` | identical |

Of the four, **one** arrives. The three `false` values are dropped.

### The coincidence, and why it is not a mitigation

#2165 closed #2207's open worry by measuring it: `directAccessGrantsEnabled` is
stored `false`, so no client accepts a username/password grant today. `publicClient`
is stored `false` too. **Neither is stored `false` because we sent `false`** — they
are stored `false` because Keycloak's own default happens to agree. Nothing in the
tree depends on that agreement holding, and nothing would notice if a Keycloak
upgrade changed it.

`standardFlowEnabled` is the one where the defaults disagree, so it is the one
that shows. Exploiting it is currently blocked by the absence of `redirectUris`
— a mitigation by omission, not by design.

### A second instance the issues did not find

`DisableClientAsync` (`HttpKeycloakAdminClient.cs:147-152`) is the *revocation*
path — `DisableDeviceCommandHandler.cs:31` and `DisableKioskCommandHandler.cs:31`
both call it. It sends its whole intent in one boolean, through the same options:

```csharp
Content = JsonContent.Create(new { enabled = false }, options: JsonOptions),
```

`enabled = false` is a default `bool`, so the body serialises to **`{}`**. Keycloak's
client update applies only the fields a representation carries, so a `PUT` of `{}`
changes nothing. **Disabling a kiosk or a device marks the aggregate `Disabled`
locally and leaves the Keycloak client enabled**, with its service account still
able to mint tokens. Spec 102 ("a revoked credential stops working") has no
`verification.md`, so this was never observed either way.

This is the same one-line cause with a materially larger blast radius than the
latent flag, and it is why the fix belongs at the serialiser rather than on the
representation's four properties.

## Scope

**In:** the `DefaultIgnoreCondition` on `HttpKeycloakAdminClient.JsonOptions`, and
tests that read what goes on the wire.

**Out:** what any handler passes. All three creation handlers already state the
intent correctly; the defect is entirely between the handler and the socket. If a
handler's intent were wrong that would be a separate issue, and none is.

**Out:** adding `redirectUris`, changing scope bundles, or anything that would
make a client more permissive. Nothing here may loosen a setting; the whole change
is making settings that were already asked for actually arrive.

## Requirements

- **FR-001** — A `bool` on a Keycloak admin request body is serialised whatever its
  value. An explicit `false` appears on the wire as `false`.
- **FR-002** — `standardFlowEnabled`, `directAccessGrantsEnabled` and `publicClient`
  are all present in the `POST /admin/realms/{realm}/clients` body, all `false`, for
  every one of the three creation paths.
- **FR-003** — `DisableClientAsync` sends `{"enabled":false}`, not `{}`.
- **FR-004** — No request this client sends gains a field it did not previously
  carry other than the ones FR-001..003 name. In particular, no body starts sending
  an explicit `null` where it previously omitted the field.

## Non-functional

- **Latency (constitution §IV):** not touched. Keycloak client provisioning and
  revocation are administrative calls on the enrolment path; neither appears on any
  of the six legs of `event arrival → overlay rendered`. No leg's budget is affected,
  and no leg's state changes.
- **Security:** strictly restrictive. Every value this change causes to be
  transmitted is `false` where the alternative was Keycloak's default, and for
  `standardFlowEnabled` Keycloak's default is `true`. The change can only turn
  things off.

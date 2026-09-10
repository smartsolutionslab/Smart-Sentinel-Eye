# Plan 121 — an explicit off is sent

## What was surveyed before choosing

`JsonOptions` is `private static readonly` on `HttpKeycloakAdminClient` and is
used **twelve** times in that file and nowhere else in the tree (`grep -rn
"DefaultIgnoreCondition\|WhenWritingDefault" --include=*.cs src/ tests/` returns
exactly one line — the declaration). Three of the twelve are writes; nine are reads.

`DefaultIgnoreCondition` is a **write-side** option. The nine
`ReadFromJsonAsync(JsonOptions, …)` calls — `ClientRow[]`, `ClientDetailRow[]`,
`GroupRow`, `GroupRow[]`, `ServiceAccountUser`, `RealmRoleRow[]`,
`ClientCredentialPayload` ×2 — are unaffected by any value it takes.

The three writes, in full:

| Line | Request | Payload type | Nullable members | Value-type members |
|---|---|---|---|---|
| `:60` | `POST admin/realms/{realm}/clients` | `KeycloakClientRepresentation` | none | 4 × `bool` |
| `:149` | `PUT admin/realms/{realm}/clients/{uuid}` | anonymous `{ enabled = false }` | none | 1 × `bool` |
| `:347` | `DELETE admin/realms/{realm}/users/{id}/role-mappings/realm` | `RealmRoleRow[]` (`string Id, string Name`) | none | none |

**No type this client serialises has a nullable member.** Nothing it sends today
can therefore be affected by a change in how `null` is treated. The two empty
collections in the representation (`OptionalClientScopes: Array.Empty<string>()`)
are already on the wire as `[]` — an empty array is not the default for a
reference type, so `WhenWritingDefault` never dropped them.

That survey is the whole basis for the decision below, and it is what #2207's
step 2 asked for: *"`WhenWritingDefault` is presumably there to keep `null`
optional fields out of PATCH bodies … not to remove it wholesale, which may be
load-bearing for the partial-update calls."* **It is not load-bearing.** There
are no nullable optional fields, and the one partial-update call in the file —
`DisableClientAsync` — is the request this condition *breaks* most completely.
The guess in the issue is reasonable and wrong, and it is worth recording as
wrong rather than quietly not acting on it.

## The decision

Narrow the condition rather than remove it:

```csharp
DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
```

**Delta, exactly:** value-type defaults are now written. Null handling is
unchanged — a `null` was dropped before and is dropped after. Given the survey,
that delta is precisely the four `false` booleans of FR-001..003 and nothing else,
which satisfies FR-004 by construction rather than by inspection.

### The two alternatives, and why not

- **Remove `DefaultIgnoreCondition` entirely.** Behaviourally identical *today*,
  because nothing written has a nullable member. It differs the moment one is
  added: an optional field would then be sent as an explicit `null` rather than
  omitted, and Keycloak's admin API does distinguish those on some representations.
  `WhenWritingNull` keeps the property the original author was plainly reaching for
  and gives up only the part that was wrong.
- **Make the four booleans always serialise** (`[JsonIgnore(Condition =
  JsonIgnoreCondition.Never)]` per property, or a converter). It fixes `:60` and
  leaves `:149` — the revocation path — still sending `{}`. It also puts an
  Infrastructure serialisation concern onto an Application-layer record, and it
  would have to be repeated on every boolean anyone adds later. It fixes the
  symptom at three of the four sites and re-arms the trap.

## Phase 4a — RED, behaviour-changing

Three reds, each on a different wire effect, all at the serialisation boundary
and none needing Keycloak:

A `StubKeycloakHandler` (`HttpMessageHandler`, hand-written per ADR-0054) answers
the request sequence `CreateClientAsync` and `DisableClientAsync` actually make,
and records each request's method, path and **body**. The subject is the real
`HttpKeycloakAdminClient` over that handler — the defect lives in its private
`JsonOptions`, so nothing may stand in for it.

1. `An_explicit_off_is_sent_rather_than_omitted` — the `POST /clients` body has
   `standardFlowEnabled` present and `false`. Fails today: the property is absent.
2. `The_flags_Keycloak_only_agrees_with_by_coincidence_are_sent_too` —
   `directAccessGrantsEnabled` and `publicClient` present and `false`. Fails today.
   These are the two #2165 measured as landing correctly; asserting they are *sent*
   is what stops a future Keycloak default change from flipping them silently.
3. `Disabling_a_client_sends_the_flag_that_disables_it` — the `PUT /clients/{uuid}`
   body is `{"enabled":false}`. Fails today: the body is `{}`.

Test 1 also asserts `serviceAccountsEnabled` is `true` on the wire. That half is
green today and is a control, not a claim: it shows the assertion can see a
property that *is* being sent, so the three reds are reading an absent field
rather than a broken stub.

## Phase 5 — verification

Serialisation is the gate; a live Keycloak is the corroboration. If the Aspire
stack is available, create a client through the real admin API and read the stored
flags back, and record the before/after in `verification.md`. **One machine, one
stack** — check nothing else is booted first, and the realm volume is not deleted.

## Out of scope, stated

Nothing about what any handler passes, nothing about `redirectUris`, no change to
scope bundles. No setting becomes more permissive.

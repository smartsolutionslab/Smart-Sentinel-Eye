# Spec 102 — A revoked credential stops working

**Issue:** #603 (`[T069] IngestWebhookEventCommandHandlerTests`).
**Origin:** spec 006 `tasks.md` T069, never implemented — and, as written,
**stale**: see §2.0.
**ADRs:** ADR-0103 (Aspire fixture, no Testcontainers), ADR-0109 (contention
files), ADR-0144 (autonomous lane; a decision is not this lane's to make),
ADR-0052/0053/0054 (xUnit + Shouldly, sentence-style naming, hand-written data),
ADR-0084 (code metrics), ADR-0113 (`If-Match` expected version — the revoke
needs one).
**Latency budget:** N/A — test-only. No production code changes, so no leg of
constitution §IV is touched.

---

## 1. Why this slice exists

Revocation is the only lever an operator has when a webhook credential leaks.
The domain flips correctly (`WebhookIntegration.cs:92-103`, unit-tested at
`tests/EventIngestion.Domain.Tests/WebhookIntegration/WebhookIntegrationTests.cs:65`)
and the endpoint that consumes the flag is one clause on one line
(`EventsEndpoints.Writes.cs:211`). **Nothing anywhere revokes an integration and
then attempts a delivery through it.** A regression that made `IsRevoked` inert
at that line would leave every existing test green — verified in §2.1, and
predicted concretely in §8.

## 2. Premise verification

### 2.0 The issue's framing is stale, and what remains splits in two

- **`IngestWebhookEventCommand` / `…Handler` do not exist and never did.**
  `grep -rn "IngestWebhookEventCommand" --include=*.cs .` → zero hits. Webhook
  ingest runs through the generic handler:
  `EventsEndpoints.Writes.cs:360` calls
  `handler.HandleAsync(new IngestEventCommand(envelope), cancellationToken)`,
  with `EventIdentifier.New()` minted per call at `:156`. So the issue's
  "mints a new `eventId` each time, no dedup contract" half is **already true in
  effect** and needs no dedicated handler or test.
- **There is no `WebhookIntegrationRevoked` *error*.** The name exists only as a
  domain event (`WebhookIntegration.cs:103`,
  `Domain/WebhookIntegration/Events/WebhookIntegrationRevokedDomainEvent.cs`) and
  as a `[LoggerMessage]` method (`Application/Log.cs:42`). A revoked integration
  receives no named error at all — §2.2.

What is left is one testable behaviour (this spec) and one **decision** that
ADR-0144 forbids this lane from taking (§7).

### 2.1 Is the end-to-end refusal genuinely untested? — **Yes.**

Searched by mechanism across the whole tree, not by filename:
`grep -rni "revok"` over `tests/`, `e2e/`, `apps/`;
`grep -rn "events/webhook\|/webhook/"` over `tests/`.

Only **two** test files ever POST to `/events/webhook/{integrationName}`:

| File | Revokes? |
|---|---|
| `tests/Integration.Tests/EventIngestion/WebhookBearerValidationIntegrationTests.cs:197` (8 `[Fact]`s) | no |
| `tests/Integration.Tests/EventIngestion/FabStorageRefusalIntegrationTests.cs:201` | no |

And the two files that **do** revoke never re-attempt a delivery — they assert
registry state only:

- `WebhookIntegrationConcurrencyIntegrationTests.cs:67-70, 88-89, 113, 128-129`
  — `version` and `revokedAt` off `GET /webhook-integrations?includeRevoked=…`.
- `WebhookRegistryFabScopingIntegrationTests.cs:111-122` — a cross-fab revoke is
  a 404 and `revokedAt` stays `Null`.

`e2e/` has **no webhook coverage of any kind** (21 specs, zero `revok`/`webhook`
hits). The refusal is asserted **nowhere above the domain unit test**.

**Drifted citation found.** `AnonymousIngestIsRefusedTests.cs:14` claims specs
013/018/021 hardened, among other things, *"whether a revoked webhook
integration still works"*. They did not — §2.1 is that claim's disproof. The
sentence is a doc comment, so nothing red guards it. Reported, not fixed here
(out of scope: §7.2).

### 2.2 What does a revoked integration receive today? — **A bare 401, empty body.**

Read from source, `EventsEndpoints.Writes.cs`:

```
:211   if (!found.HasValue || found.Value.IsRevoked) { return null; }   // in AuthenticateWebhookAsync
:136   WebhookIntegration? integration = await AuthenticateWebhookAsync(…);
:138   if (integration is null) { return Results.Unauthorized(); }
```

- **Status:** `401 Unauthorized`.
- **Body:** empty. `Results.Unauthorized()` writes a status and nothing else.
  `AddProblemDetails()` is registered (`ServiceDefaults/AuthenticationDefaults.cs:88`)
  but neither `UseStatusCodePages` nor any status-code middleware is wired, and
  `UseExceptionHandler` (`EventIngestion/Api/Program.cs:17`) only sees
  exceptions — a returned 401 is not one. So no `ProblemDetails` is produced.
- **Headers:** no `WWW-Authenticate` challenge is invoked — the route is
  `.AllowAnonymous()` (`EventsEndpoints.cs:59`), so the authentication middleware
  never challenges and `Results.Unauthorized()` does not add one. *This is
  inferred from source, not observed at runtime*, which is why the test asserts
  it **indirectly**, by byte-equality against the unknown-integration 401
  (§3, scenario 2) rather than by naming a header.
- **Indistinguishable from an unknown integration.** `:211` collapses "no such
  integration" and "revoked integration" into the same `null`, and the summary at
  `:181-183` says so deliberately: *"Every failure path collapses to `null` so the
  401 response never leaks which integrations exist."*

**The spec asserts this, not what the issue wished for.** Whether the collapse is
right is §7.1's open question, and is not settled here.

### 2.3 Is revoke-then-ingest reachable in an integration test? — **Yes, with one constraint.**

The plaintext bearer exists **exactly once**, in the `POST /webhook-integrations`
201 body (`WebhookIntegrationsEndpoints.cs:113` →
`RegisteredWebhookResponse(…, registration.PlainToken)`); the row stores only a
hash (`TokenHash.Matches`), which is why CLAUDE.md records this as the one create
that cannot carry an idempotency key. So the token **must be captured at
registration and held in a local** — it cannot be recovered afterwards.

`WebhookBearerValidationIntegrationTests.cs:166-175` already does exactly this
(`RegisterStaticHashIntegrationAsync` returns `body.GetProperty("token")`), and
this spec reuses that idiom verbatim rather than inventing one.

Revocation is reachable too: `DELETE /webhook-integrations/{name}` with
`If-Match: "{version}"`, the version read from
`GET /webhook-integrations` — the idiom in
`WebhookIntegrationConcurrencyIntegrationTests.cs:118-130` (`Conditional` at `:182`,
`FindAsync`), which observes a 200.

And the revocation is **visible to the next delivery**:
`WebhookIntegrationRepository.GetByNameAsync` queries
`dbContext.WebhookIntegrations` by name with no `RevokedAt` filter and no cache,
and the repository is request-scoped, so each POST gets a fresh read of the
committed row. (Consequently `:211`'s `IsRevoked` clause is the *only* thing
stopping the delivery — nothing else filters revoked rows on this path.)

### 2.4 Which principals does the test need? — **Two, both already available.**

| Step | Principal | Source |
|---|---|---|
| Register, list, revoke | `admin`, scope `sse.webhooks.write`, group `/fabs/munich` | `aspire.CreateAdminClientAsync("event-ingestion")` |
| Deliver the webhook event | the integration's own bearer, **no OIDC** | the token captured in step 1, sent on `aspire.EventIngestion` (the unauthenticated client) |

`/webhook-integrations` requires `Scope.Sse.Webhooks.Write`
(`WebhookIntegrationsEndpoints.cs:31`); `/events/webhook/{integrationName}` is
`.AllowAnonymous()` (`EventsEndpoints.cs:59`). `admin` holds `/fabs/munich` only,
so the integration is registered without `?fabId=` (resolves to munich) and
delivered with `?fabId=munich` — matching `WebhookBearerValidationIntegrationTests`'s
`Fab` constant. **Established idioms, not invented ones:** register/revoke follow
`WebhookIntegrationConcurrencyIntegrationTests`, delivery follows
`WebhookBearerValidationIntegrationTests`.

### 2.5 Does revocation actually stop ingestion? — **Yes, by reading.**

`:211` is reached before the storage-readiness check (`:147`), before the envelope
is built (`:155-162`), and before `StoreOrRefuseAsync` (`:171`). There is no second
path into `IngestWebhook`. **No security defect found**, so this stays a test-only
slice as scoped.

---

## 3. User story

**US1 (P1)** — As a security-conscious operator, when I revoke a webhook
integration's credential, I need deliveries presenting that credential to stop
being accepted, so that revocation is an actual containment action rather than a
registry annotation.

Independently shippable: one test class, no production change, observable end to
end against the booted stack.

### Acceptance scenarios

```gherkin
Scenario: A credential that worked stops working once revoked
  Given a StaticHash webhook integration registered in munich
    And its plaintext bearer token captured from the 201 registration response
    And a delivery presenting that token has been accepted with 201 Created
   When the integration is revoked with DELETE /webhook-integrations/{name}
        carrying If-Match with the listed version
   Then the revoke answers 200 OK
    And a second, otherwise identical delivery presenting the same token
        answers 401 Unauthorized
```

The accepted 201 in the `Given` is load-bearing, not decoration: without it a 401
at the end could equally mean the token was captured wrongly, the fab was wrong,
or the name was wrong. It makes the revocation the **only** variable.

```gherkin
Scenario: The refusal is silent about which integrations exist
  Given a revoked webhook integration in munich
   When a delivery presents its token
    And a delivery presents the same token against a name never registered
   Then both answer with the same status code
    And both answer with the same response body
```

This pins **today's** behaviour (§2.2) — the collapse at `:211`. It is asserted
because a change to it should be a decision someone takes deliberately (§7.1),
not one that drifts in. It is the assertion to strike if the open question is
settled the other way; §7.1 says so explicitly.

```gherkin
Scenario: Revocation refuses even when the delivery names the right plant
  Given a revoked webhook integration in munich
   When a delivery presents its token with ?fabId=munich
   Then it answers 401 Unauthorized
```

Already covered by scenario 1; stated so nobody "strengthens" the test by
delivering to another fab, which would be refused a step later by the fab
comparison (`IsIntegrationsOwnFab`, `:226`) and would prove nothing about
revocation. **Not a separate `[Fact]`.**

**Bad-request and auth scenarios:** already covered on this endpoint and
deliberately not re-asserted — wrong bearer
(`WebhookBearerValidationIntegrationTests.StaticHash_mode_rejects_a_wrong_bearer`),
wrong fab (`…rejects_a_delivery_naming_another_plants_fab`), malformed body
(`:164-169` → `EVENT_INVALID_INPUT` 400). Repeating them is duplicate coverage a
later reader has to disambiguate.

### Independent end-to-end test procedure

Runnable by a human without reading the test code, against a booted AppHost:

1. `POST /webhook-integrations {"name":"rev-demo","defaultKind":"WebhookAlarm"}`
   with an admin bearer → **201**; copy `token` from the body.
2. `POST /events/webhook/rev-demo?fabId=munich` with
   `Authorization: Bearer <token>` and `{"payload":{"severity":"high"}}` → **201**.
3. `GET /webhook-integrations` with the admin bearer → note `version` for
   `rev-demo`.
4. `DELETE /webhook-integrations/rev-demo` with the admin bearer and
   `If-Match: "<version>"` → **200**.
5. Repeat step 2 verbatim → **401**, empty body.
6. `POST /events/webhook/never-registered-xyz?fabId=munich` with the same token →
   **401**, byte-identical response to step 5.

---

## 4. Locked choices

| Concern | Choice | Why |
|---|---|---|
| Test kind | Integration, `AspireFixture` (ADR-0103) | The behaviour is HTTP + EF + Keycloak; no in-memory double reaches `:211`. |
| Placement | `tests/Integration.Tests/EventIngestion/WebhookRevocationRefusesDeliveryIntegrationTests.cs` | Next to the two files whose idioms it reuses. |
| **Trait** | **none** | `Category=Measurement`, `Disruptive` and `Maintenance` are all excluded by `ci.yml:179` (verified, and pinned by `IntegrationTestSelectionTests.cs:44-45`, also verified). Three files in this folder carry `Disruptive`; copying one delivers a test CI never runs. An untraited file is selected. |
| Collection | `[Collection(AspireCollection.Name)]` | Shares the one booted stack. |
| Uniqueness | per-test `UniqueName(prefix)` | This context has no reset helper; every neighbour mints a unique name instead. |
| Assertions | Shouldly, sentence-style names (ADR-0052/0053) | House style. |
| Diagnostics | a `DiagnoseAsync`-style message on the non-obvious statuses | `WebhookIntegrationConcurrencyIntegrationTests.cs:196-203` — CI has no other route to the service's stack trace. |

## 5. Out of scope

- Any production change — explicitly including a "fix" of the 401 collapse (§7.1).
- The dedup half of #603: already true in effect (§2.0). Nothing to assert that
  `EventIdentifier.New()` at `:156` does not already state.
- The false claim at `AnonymousIngestIsRefusedTests.cs:14` (§2.1) — a doc-comment
  correction in a file this slice does not otherwise touch. File separately.
- Revocation propagation to Keycloak for rotated (JWT-mode) integrations. A
  revoked JWT-mode integration is refused at the same `:211` before the JWT
  branch runs, so this slice covers the refusal by construction; whether Keycloak
  also stops minting is a different question in a different context.

---

## 6. Declarations (ADR-0144)

1. **Engineer:** `backend-engineer`. C#/xUnit against the Aspire fixture; no
   frontend, no infrastructure, no AppHost or CI change.
2. **ADR needed?** **Not for this slice.** US1 asserts behaviour already decided
   and already implemented, and cites ADR-0103, ADR-0113, ADR-0109. **Half 2
   (§7.1) may well need one** — whether a revoked credential deserves a
   distinguishable refusal is an architectural decision with a real security
   argument on each side, and ADR-0144 forbids this lane from making it. **US1
   stands independently of that ADR**: it asserts the behaviour that exists
   today, and if the decision later goes the other way exactly one `[Fact]`
   (scenario 2) changes — scenario 1 is unaffected either way.
3. **Colour: behaviour-preserving → characterisation, observed green.** No
   production code changes, so there is no red to observe; a compile error is not
   a red test (spec 061, `24e6fc4c`). The evidence is the **counterfactual** in
   §8, which is mandatory here, not optional.

---

## 7. The open question — for the human to file, not for this lane

### 7.1 Should a revoked webhook integration be distinguishable from an unknown one?

`EventsEndpoints.Writes.cs:211` — `if (!found.HasValue || found.Value.IsRevoked)
{ return null; }` — collapses both into one 401 with an empty body.

**For the collapse (keep it).** A distinguishable answer is an **oracle**: it
tells a caller holding a stolen token that the token was once valid and that the
integration is real. That is precisely the disclosure the endpoint's own summary
(`:181-183`) and the existing test
`A_delivery_to_another_plants_integration_looks_like_one_that_does_not_exist`
were written to prevent — and #1545 closed a cross-fab probe on the same
reasoning. The population that benefits from a clearer answer (a legitimate
integrator) can be told out of band; the population that benefits from the oracle
cannot.

**Against the collapse (distinguish it).** On a 24/7 industrial system, an
integrator whose credential was deliberately revoked receives the same answer as
one who mistyped the name, never registered, or has a misconfigured fab. The
operator who revoked it knows why; the person debugging at 03:00 does not, and
the endpoint offers nothing to tell the cases apart. A deliberate containment
action becomes an outage of unknown cause.

**Middle grounds** (the shape of the space, not a recommendation): distinguish
only for a caller whose token actually *matches* the revoked integration's hash —
the oracle then reveals nothing a valid-token holder does not already know; or
keep the 401 opaque and emit a distinguishable **audit/log** line, which the
operator can read and the attacker cannot.

**Dependency to note when filing:** scenario 2 of §3 asserts the collapse.
Settling this the other way means amending that one `[Fact]` — which is a
feature, because it makes the change deliberate rather than silent.

### 7.2 The false claim in `AnonymousIngestIsRefusedTests.cs:14`

A doc comment asserting coverage that does not exist (§2.1). Trivial, but it is
this repo's named failure mode — a record nobody checked against what was
actually happening.

---

## 8. The counterfactual — written prediction, recorded before the run

**The change:** in `src/EventIngestion/Api/EventsEndpoints.Writes.cs:211`, drop
the second clause:

```csharp
if (!found.HasValue)          // was: if (!found.HasValue || found.Value.IsRevoked)
{
    return null;
}
```

It compiles: `found.Value` is used unconditionally below and `!found.HasValue`
still guards it.

**Prediction, recorded now:**

- **The new test class is the only failure.** Both of its `[Fact]`s fail:
  scenario 1's second delivery answers **201 Created** instead of 401, and
  scenario 2's revoked-vs-unknown comparison fails on the status code (201 vs
  401) before it reaches the body.
- **No existing test reddens.** The reasoning, not hope:
  - `IsRevoked` has exactly **three** production readers repo-wide
    (`grep -rn "IsRevoked" --include=*.cs`): `:211`, the domain property and its
    own idempotency guard (`WebhookIntegration.cs:92,97`), and
    `RevokeWebhookIntegrationCommandHandler.cs:50`. Only `:211` is touched.
  - The **only** two existing files that POST to `/events/webhook/` never revoke
    (§2.1), so their integrations are live and the clause is inert for them.
  - `WebhookIntegrationConcurrencyIntegrationTests` and
    `WebhookRegistryFabScopingIntegrationTests` revoke but never POST to the
    ingest endpoint — they read `revokedAt` and `version` off the registry
    listing, which `:211` does not feed.
  - The unit tests naming `IsRevoked` (`WebhookIntegrationTests.cs:27,65`;
    `WebhookIntegrationCommandHandlerTests.cs:84,110`;
    `StaleVersionRejectionTests.cs:46,74,149`) exercise the domain and the revoke
    handler, not the endpoint.
  - No source-scanning architecture guard reads this line.
    `EndpointScopeDeclarationTests.cs:866` pins the endpoint's *summary* against
    `BearerValidationMode`'s zero value, not the revocation clause;
    `PrimitiveBoundaryTests.cs:216,221` mentions `IsRevoked` only in a comment.

**If the prediction is wrong** — if an existing test also reddens — the new test
proves less than it appears to, and that must be reported in the PR body before
it ships, not discovered later. Record the actual failing set either way.

---

## 9. Success criteria

- **SC-001** — With the counterfactual applied the new test class fails; with it
  reverted it passes. Both runs' output quoted verbatim in the PR.
- **SC-002** — The counterfactual run's failing set is **exactly** the new class.
  If it is larger, §8's prediction is corrected in the PR body and the extra
  files named.
- **SC-003** — The new file carries **no** `[Trait("Category", …)]`, so
  `ci.yml:179`'s exclusion filter selects it. Confirmed by
  `dotnet test --filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance" --list-tests`
  naming both `[Fact]`s.
- **SC-004** — Green on a clean `develop`, and green again on a second run (a
  first-run-after-churn failure is not a verdict).

## 10. Assumptions (unavoidable guesses, marked)

- **A1** — The 401 body is empty and carries no `WWW-Authenticate`. Derived from
  source (§2.2), **not observed at runtime**. The test does not assert either
  directly; scenario 2's byte-equality holds whatever the body turns out to be.
- **A2** — `admin`'s token carries `sse.webhooks.write`. Inferred from
  `WebhookIntegrationConcurrencyIntegrationTests` registering and revoking
  successfully with `CreateAdminClientAsync("event-ingestion")`. If it does not,
  the register step 401s immediately and the cause is unambiguous.
- **A3** — munich's event storage is provisioned in the fixture, so the `Given`'s
  first delivery is 201 rather than 503. Inferred from
  `WebhookBearerValidationIntegrationTests.StaticHash_mode_accepts_the_matching_legacy_bearer`
  asserting `Created` against munich today.

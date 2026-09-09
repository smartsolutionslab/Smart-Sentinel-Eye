# Spec 112 — a metadata source that can be supplied

**Issue:** #2099 (`tech-debt`, `agent:ready`) — two follow-ups from #2093.
**Branch:** `test/2099-a-refusal-shaped-like-mediamtx-sends-it`
**ADRs:** ADR-0037 (phased workflow), ADR-0144 (autonomous lane), ADR-0011
(`0000-initial-decisions.md` row 011 — MediaMTX as the SFU with an external
auth hook), ADR-0105 (`Ensure.That` guards), ADR-0141 (`Option<T>` scope, and
its Infrastructure exemption), ADR-0051 (per-context DI extension methods),
ADR-0052 (xUnit + Shouldly), ADR-0139/§Testing (the two colours).

---

## The issue has two parts. Only one of them is still true.

### Part 1 — falsified against the tree

> *"It is the only test that exercises the WHEP hook through MediaMTX's actual
> request shape … **Every case in it asserts a 200.**"*

It does not. `tests/Integration.Tests/StreamDistribution/WhepAuthIntegrationTests.cs`
holds six cases, **four of which assert a refusal**:

| Case | line | asserts |
|---|---|---|
| `Authorize_without_a_token_returns_401` | :24 | **401** |
| `Authorize_with_an_invalid_path_returns_403` | :34 | **403** |
| `Authorize_with_a_valid_admin_token_returns_200` | :47 | 200 |
| `Authorize_with_a_malformed_token_returns_401` | :63 | **401** |
| `Authorize_with_a_Bearer_prefix_strips_it_and_validates` | :73 | 200 |
| `Authorize_a_publish_with_a_valid_admin_token_returns_403` | :92 | **403** |

The premise was **already false when the issue was filed** (2026-09-05 12:25 UTC).
At that moment the file held five cases — 401, 403, 200, 401, 200 — and
`Authorize_a_publish_…_returns_403` was added two hours later by 35b27c28.
Every one of them goes through `AssertStatusAsync` (:108), which asserts the
**exact** status and prints the body on mismatch. So each of the three failures
the issue lists as invisible is in fact visible today:

- **a refusal that 500s instead of 401s** — `AssertStatusAsync` compares the
  exact code; a 500 fails four tests, and prints the developer-exception body.
- **a refusal that answers 403 where 401 is correct, or vice versa** — the two
  branches are pinned against each other by the four cases above, and again at
  handler level by `AuthorizeWhepCommandHandlerTests` (13 cases, including
  `Authorize_with_a_token_granting_neither_the_read_scope_nor_the_bundle_returns_Forbidden`
  at :129).
- **200 with an empty body** — a 200 with an empty body *is the allow contract*.
  `AuthorizeWhep` answers `Results.Ok()` (`StreamEndpoints.cs:378`), which is
  exactly that.

### There is no body for a deny to assert

The issue's "done" asks for *"the body MediaMTX expects for a deny"*. **MediaMTX
expects no body at all.** The repo already records this, in
`specs/074-the-hook-answers-the-action-it-was-asked/spec.md:223` (decision D3),
which quotes upstream and then states:

> **A `401` is a credential challenge that invites a retry.** MediaMTX treats any
> non-`2xx` as a failure, so both codes deny — but `401` specifically tells the
> client to come back with credentials.

The whole contract is the **status code**. `src/AppHost/Resources/mediamtx.yml:40-45`
says the same from the other side ("returns 200/401/403"), and
`AuthorizeWhepErrors.cs:8` says it a third time ("MediaMTX uses the HTTP status
to allow or reject"). A test asserting the ProblemDetails body would pin
ASP.NET's `Results.Problem` shape, which MediaMTX never reads — a test of our own
serializer, sold as a test of an integration contract.

**Disposition: Part 1 is closed as already satisfied.** No new integration test,
and therefore **no Docker**. One residue is recorded as a finding below rather
than smuggled into a test-shaped issue.

### The residue — filed, not fixed here

`WhepAuthValidator.ValidateAsync` (:83-113) catches `SecurityTokenException` and
`ArgumentException` only. `oidc.GetConfigurationAsync` (:85) throws
`InvalidOperationException` (IDX20803) when the realm's discovery document
cannot be fetched, and nothing converts it — no `IExceptionHandler` in
`src/ServiceDefaults/Authorization/` covers it. **A Keycloak outage therefore
answers the hook with 500, not 401.** That is genuinely the issue's first bullet,
it is genuinely uncovered, and turning it into a deny is a **behaviour change**
this issue did not ask for and did not decide. It gets its own issue.

---

## Part 2 — the metadata source, and this is the whole feature

`WhepAuthValidator` builds its `ConfigurationManager<OpenIdConnectConfiguration>`
inline in its constructor (`WhepAuthValidator.cs:45-48`) with an
`HttpDocumentRetriever`. Nothing can substitute it.

**Two** test files reach past that with reflection, not one — the issue names
only #2093's:

- `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorAudienceTests.cs:81,145`
- `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorIssuerTests.cs:115,206`

Both resolve `typeof(WhepAuthValidator).GetField("oidc", …)` and `SetValue` an
in-memory manager, which is how they run without Docker, a socket or a realm.

### User Story 1 — the tests supply the metadata source instead of reflecting (P1)

Independently shippable: one production file, two test files. No realm change,
no frontend change, no migration, no Aspire resource, no contract change.

#### Acceptance scenarios

```gherkin
Scenario: an in-memory metadata source can be supplied without reflection
  Given a WhepAuthValidator constructed with a stubbed IConfigurationManager<OpenIdConnectConfiguration>
  When a token signed by that stub's JWKS, for this API's audience, is validated
  Then the validator returns Some, exactly as it does through the reflected field today
  And no test file references System.Reflection to build it
```

```gherkin
Scenario: the refusal the seam exists to protect is unchanged
  Given the same stubbed metadata source
  When a token minted for another API is validated
  Then the validator returns None
  And the assertion message is the one WhepValidatorAudienceTests already carries
```

```gherkin
Scenario: production wiring is untouched
  Given the StreamDistribution service container
  When IWhepAuthValidator is resolved
  Then a WhepAuthValidator is constructed against the configured authority's
       discovery document, through the public IOptions<WhepAuthOptions> constructor
```

```gherkin
Scenario: the seam is not public API
  Given an assembly outside SmartSentinelEye.StreamDistribution.Infrastructure
    and outside its InternalsVisibleTo test assembly
  When it tries to construct a WhepAuthValidator with its own metadata source
  Then it does not compile — the only public constructor takes IOptions<WhepAuthOptions>
```

**No bad-request scenario.** No HTTP surface changes. **No conflict scenario** —
`If-Match`/409 (ADR-0113) do not apply; `/streams/authorize` is explicitly
exempt (`docs/adr/0113-optimistic-concurrency-two-layer.md:172`). **No auth
scenario beyond the fourth above**, which is the auth scenario: the seam must not
widen what the hook accepts.

---

## Requirements

- **FR-001** `WhepAuthValidator`'s metadata source is supplied through an
  **`internal`** constructor taking `IConfigurationManager<OpenIdConnectConfiguration>`.
- **FR-002** The existing `public WhepAuthValidator(IOptions<WhepAuthOptions>)`
  remains the **only public** constructor, and keeps building today's
  `ConfigurationManager` + `HttpDocumentRetriever { RequireHttps = false }`
  with today's comment (:37-44) intact.
- **FR-003** The `oidc` field's declared type widens to
  `IConfigurationManager<OpenIdConnectConfiguration>`. `ValidateAsync` calls
  only `GetConfigurationAsync(CancellationToken)` (:85), which the interface
  declares, so the body does not change.
- **FR-004** The internal constructor guards its arguments with
  `Ensure.That(…).IsNotNull()` (ADR-0105).
- **FR-005** `WhepValidatorAudienceTests` and `WhepValidatorIssuerTests` build
  their validator through the seam. The `OidcField`/`GetField`/`SetValue` code
  and the `System.Reflection` using are deleted from both.
- **FR-006** Every assertion and every `customMessage` in those two files
  survives verbatim. Only the construction changes.
- **FR-007** Nothing widens what the hook accepts: no new public surface, no
  change to `CreateParameters`, no change to `ValidateAsync`'s control flow, no
  change to the endpoint or the handler.

## Independent end-to-end test procedure

Docker-free and stack-free, ~1 s:

```sh
dotnet test tests/StreamDistribution.Infrastructure.Tests \
  --filter "FullyQualifiedName~WhepValidator"
```

Then, for FR-003's "the production path still resolves", the existing
Docker-backed suite is the observation — but it is **already run by CI** and is
not re-run locally for this change:

```
tests/Integration.Tests/StreamDistribution/WhepAuthIntegrationTests.cs — 6 cases, unchanged
```

Counterfactual for the seam being real: `grep -rn "System.Reflection" tests/StreamDistribution.Infrastructure.Tests/Auth/` returns nothing.

## Locked tech choices

.NET 10, xUnit + Shouldly (ADR-0052), `Ensure.That` guards (ADR-0105),
`Microsoft.IdentityModel.Protocols.IConfigurationManager<T>` (already on the
dependency graph — `ConfigurationManager<T>` implements it), the
`InternalsVisibleTo` already declared at
`src/StreamDistribution/Infrastructure/SmartSentinelEye.StreamDistribution.Infrastructure.csproj:8`.

## Latency-budget impact

**N/A — no leg.** The WHEP external-auth hook runs **once at session setup**,
before any media flows. Constitution §IV's six legs are camera → SFU,
SFU → kiosk decode, presentation buffer, event → overlay state, composite +
render, and headroom; session setup precedes the first of them and is not one of
them. This change touches a constructor only and adds no work to any request
path. §VII's dashboard obligation (ADR-0117) does not attach.

## Assumptions marked

- **A1.** Microsoft.Extensions.DependencyInjection considers only **public**
  constructors, so an `internal` second constructor introduces no ambiguity at
  `StreamDistributionInfrastructureModule.cs:57`. Pinned by a task, not trusted.
- **A2.** MediaMTX's auth response contract is status-only. Cited from spec 074
  D3, `mediamtx.yml:40-45` and `AuthorizeWhepErrors.cs:8` — three independent
  places in this repo. Not re-verified against upstream in this phase.

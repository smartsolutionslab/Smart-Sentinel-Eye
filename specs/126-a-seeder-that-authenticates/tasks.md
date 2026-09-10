# Tasks — Spec 126, A seeder that authenticates

**Phase 4a colour: RED, behaviour-changing** (declared at phase 3, per
ADR-0144). Ambiguity does not arise: the seeder gains a credential it did
not have and a refusal changes level.

## Phase 4a — tests, observed red

- **T001** `tests/Integration.Tests/SystemVariables/ReverseIndexSeedCredentialTests.cs`
  — against the real Keycloak and the real overlay-designer booted by the
  AspireFixture (ADR-0103):
  - `The_seeder_service_account_can_list_published_overlays` — mint
    `client_credentials` for `system-variables-seeder`, call
    `GET /overlays?state=Published`, expect **200** and a `published` key.
    **Red today: the client does not exist, so the grant is refused.**
  - `The_seeder_service_account_cannot_author_an_overlay` — the same token
    against `POST /overlays`, expect **403**, not 201. Pins FR-003; a token
    that could write would pass the first test and fail this one.
  - `The_seeder_service_account_cannot_write_a_system_variable` — the same
    token against `POST /system-variables`, expect **403**. Second scope
    boundary, chosen because `sse.variables.write` is the scope a
    copy-paste from a sibling would most plausibly attract.

- **T002** `tests/SystemVariables.Infrastructure.Tests/Resolution/ReverseIndexSeederHostedServiceTests.cs`
  — the seeder driven through a hand-written `HttpMessageHandler` stub
  (ADR-0054, no mocking framework for the transport):
  - `A_refused_seed_is_logged_as_an_error` — 401 in, one `Error` entry out.
    **Red today: the only entry is a `Warning`.**
  - `A_refused_seed_does_not_promise_a_self_heal` — the refusal message must
    not claim the index will populate from events. **Red today: it does.**
  - `A_forbidden_seed_is_logged_as_an_error` — 403 takes the same branch.
  - `A_server_error_stays_a_warning` — 503 keeps FR-005's existing level.
    Discriminates: an implementation that simply raised every non-success to
    `Error` fails here.
  - `A_refused_seed_leaves_the_host_started` — `StartAsync` returns.
    FR-006.
  - `A_successful_seed_populates_the_index` — green companion; the parse is
    not being changed and this is what keeps it that way.
  - `The_request_carries_the_bearer_the_named_client_attaches` — the stub
    records the `Authorization` header the pipeline produced.

- **T003** Add `tests/SystemVariables.Infrastructure.Tests/Fakes/CapturingLogger.cs`,
  mirroring `StreamDistribution.Infrastructure.Tests/Fakes/CapturingLogger.cs`.
  Copied, not shared: test projects do not reference one another.

- **T004** Run T001–T002 and capture the verbatim failing output. That
  output is the phase-4b brief and goes in the PR body (ADR-0139).

## Phase 4b — implementation

- **T010** `system-variables-seeder` client in
  `src/AppHost/Realms/smart-sentinel-eye-realm.json`: confidential, service
  accounts on, standard flow off, direct access off, default scopes
  `sse-identity`, `sse-audience`, `sse.overlays.read`. Description states
  what it reads and why the scope stops there.
- **T011** `ReverseIndexSeederOptions` — `KeycloakUrl`, `Realm`,
  `ClientIdentifier`, `ClientSecret`; section name `ReverseIndexSeeder`.
- **T012** `OverlayDesignerTokenProvider` — the fifth sibling, shaped on
  `CameraCatalogTokenProvider`, `HttpClientName` const, `IDisposable`.
- **T013** `OverlayDesignerAuthorizationHandler : AuthorizingHandler`.
- **T014** Wire it in `SystemVariablesInfrastructureModule`: bind options
  from the Keycloak connection string the way
  `BindStreamFabAttribution` does, register the provider **singleton**
  (#2037), the handler transient, `RetryEveryMethod()` on the token client
  (ADR-0143), and `AddHttpMessageHandler<...>()` on the existing
  `overlay-designer` client. Keep it under ADR-0084's method limit by
  extracting a private `BindReverseIndexSeeder`, mirroring
  `BindStreamFabAttribution`.
- **T015** `Log.SeedRefused(HttpStatusCode)` at `Error`; the seeder branches
  401/403 to it and keeps `SeedNonSuccessStatus` for the rest.
- **T016** Replace the "auth is deferred" paragraph in the seeder's doc
  comment with what it now does.
- **T017** `AppHost.cs` — parameter + environment variable, with the same
  comment shape the four existing secrets carry.
- **T018** `MqttTokenProvider` doc comment: three siblings -> four (FR-007).
  `ClientCredentialsTokenProvider`'s own "Four contexts had written this"
  is a statement about history and stays as written.

## Phase 5 — verification

- **T020** Dev stack: add the client through the Keycloak Admin API (the
  volume is **not** deleted), restart `system-variables`, and observe the
  seed line say `seeded with N published overlays`. Then repeat the
  before-transcript: publish an overlay, restart, change the variable, and
  see `Pushed ResolvedOverlayTextChanged` where nothing appeared before.
- **T021** Write `verification.md` with both transcripts, before and after.

## Phase 6 — review

- **T030** `backend-reviewer` + `security-reviewer` (a credential and a
  scope), then `/code-review`.

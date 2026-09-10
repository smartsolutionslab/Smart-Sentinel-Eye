# Plan — Spec 126, A seeder that authenticates

**Phase 4a colour: RED, behaviour-changing.** The seeder acquires a
credential it did not have and a refusal changes level. Both are new
behaviour.

## Shape

Nothing new is invented. `ClientCredentialsTokenProvider` in ServiceDefaults
already mints, caches and refreshes; four contexts wrap it. This adds the
fifth wrapper and the delegating handler that puts its token on the wire,
then narrows one log branch.

```
src/ServiceDefaults/Authentication/ClientCredentialsTokenProvider.cs   (unchanged)
    ^-- MqttTokenProvider              (EventIngestion)   doc comment: 3 -> 4 siblings
    ^-- KeycloakAdminTokenProvider     (Identity)
    ^-- KeycloakTokenProvider          (ScenarioSimulator)
    ^-- CameraCatalogTokenProvider     (StreamDistribution)  <- modelled on this one
    ^-- OverlayDesignerTokenProvider   (SystemVariables)     <- new
            ^-- OverlayDesignerAuthorizationHandler : AuthorizingHandler   <- new
                    attached to the existing named client "overlay-designer"
```

`ReverseIndexSeederHostedService` keeps its constructor and its call. The
token arrives through the handler on the named client, which is why the
seeder's own code changes only in its doc comment and its refusal branch.

## Files

| File | Change |
|---|---|
| `src/AppHost/Realms/smart-sentinel-eye-realm.json` | new `system-variables-seeder` confidential client, service accounts on, `sse-identity` + `sse-audience` + `sse.overlays.read` |
| `src/AppHost/AppHost.cs` | `SystemVariablesSeederClientSecret` parameter; `ReverseIndexSeeder__ClientSecret` env on `system-variables` |
| `src/SystemVariables/Infrastructure/Resolution/ReverseIndexSeederOptions.cs` | new — Keycloak URL, realm, client id, secret |
| `src/SystemVariables/Infrastructure/Resolution/OverlayDesignerTokenProvider.cs` | new — the fifth sibling |
| `src/SystemVariables/Infrastructure/Resolution/OverlayDesignerAuthorizationHandler.cs` | new — `AuthorizingHandler` over it |
| `src/SystemVariables/Infrastructure/SystemVariablesInfrastructureModule.cs` | bind options, register the singleton provider + transient handler, attach the handler to the `overlay-designer` client, `RetryEveryMethod()` on the token client |
| `src/SystemVariables/Infrastructure/Resolution/ReverseIndexSeederHostedService.cs` | refusal branch; doc comment no longer says auth is deferred |
| `src/SystemVariables/Infrastructure/Log.cs` | `SeedRefused` at `Error` |
| `src/EventIngestion/Infrastructure/Ingress/MqttTokenProvider.cs` | "three sibling" -> "four sibling" (FR-007) |

`src/AppHost/AppHost.cs` is a contention file (ADR-0109). This slice owns it
for this batch: the seeder cannot receive a secret any other way, and the
two lines added sit beside four identical ones.

## Decisions taken here

**The token client opts back into `RetryEveryMethod()`, and says why.**
ADR-0143 gives `POST` one attempt by default. The token mint is a `POST` and
is idempotent in fact — a second token supersedes the first — which is the
first row of ADR-0143's own opt-in table, listing "the four
`client_credentials` token clients". This is the fifth, and it belongs in
that row for the same reason. Without it, a Keycloak blip at host start
would leave the index empty for the life of the process.

**The seeder's own `GET` is not opted into anything.** `GET` is already
retried by the standard handler, which is precisely what makes swallowing a
401 unnecessary (spec §"The 401 decision").

**Options carry the credential, not a bound `IOptions<T>` shared with
anything else.** Each sibling names its own options type because the four
contexts spell the same four values four different ways — the
`ClientCredentialsTokenProvider` doc comment says so. A fifth spelling is
the pattern, not a divergence.

**`Ensure.That` in the handler and the provider; the seeder keeps its
existing guards.** ADR-0105.

## Risks

- **A realm edit needs the Keycloak volume removed to take effect on a
  running dev stack.** The integration suite is unaffected: the fixture
  passes `E2ETests=true` and `AppHost.cs` gates `.WithDataVolume()` on
  `isRunMode && !isE2ETests`, so its Keycloak imports the realm fresh. For
  the phase-5 observation on the dev stack the client is added through the
  Keycloak Admin API instead — additive, and it does not destroy the volume.
- **A green integration run does not prove the dev stack works**, for the
  same reason. Phase 5 observes the dev stack directly.

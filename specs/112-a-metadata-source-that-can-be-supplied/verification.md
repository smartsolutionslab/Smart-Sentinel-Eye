# Verification note — spec 112

**Issue:** #2099 · **Branch:** `test/2099-a-refusal-shaped-like-mediamtx-sends-it`
**Covers:** US1 (the two validator suites construct through a seam instead of reflection)
**Colour:** behaviour-preserving → **characterisation** (§Testing, ADR-0139)
**Date:** 2026-09-09

**Machine:** one developer laptop, Windows 11 Pro 10.0.26100, .NET SDK 10.0.401, Docker
Desktop 28.3.3, Docker VM 11.59 GiB. **C: at 96 %, ~11 GB free** — the constraint T007
cited as its reason not to run Docker. No worktrees. One Aspire stack at a time: before
either run there were **8 orphaned run-mode containers and no AppHost process**, and the
fixture's own containers are ephemeral with DCP-random loopback ports, so nothing
collided. The one fixed host port in the tree (`8189`, the ICE mux) is behind
`isRunMode && !isE2ETests` at `src/AppHost/AppHost.cs:184`, so the fixture never publishes
it.

---

## 1. The gap this phase had to close

Phase 4 discharged the characterisation obligation: after the src-only commit `a1277046`,
the two reflecting test files passed **untouched**. But every one of those tests **stubs
the metadata source**. The thing the change actually altered — the *public* constructor,
which now delegates through `MetadataSourceFor(options)` at
`src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs:32-35` — is the one path
no unit test executes. A refactor that broke production construction while every stubbed
test stayed green is exactly what this phase exists to catch, and T007 planned to leave it
to CI.

It was not left to CI.

## 2. What was observed

**The WHEP hook validates real Keycloak-minted tokens end to end, through the public
constructor and a live discovery document, six cases out of six, twice.**

This is decisive rather than reassuring because of what the 200 requires.
`ValidateAsync` (:100-107) reads `configuration.Issuer` and `configuration.SigningKeys`
off the object `oidc` returns and hands them to `handler.ValidateToken`; there is no
static issuer and no static key. So a `200` on an admin token is only reachable if the
public constructor's `ConfigurationManager` actually fetched
`{authority}/.well-known/openid-configuration` from the realm and parsed a JWKS out of it.
The authority is an Aspire-discovered **http** URL
(`StreamDistributionInfrastructureModule.cs:181-195`), which makes the
`HttpDocumentRetriever { RequireHttps = false }` inside `MetadataSourceFor` load-bearing on
that path — had the helper lost it, IDX20108 would surface as a **500** and
`AssertStatusAsync` would print the developer-exception body. The `401` on a malformed
token proves the same fetch, one line earlier.

### How

```sh
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~WhepAuthIntegrationTests"
```

One invocation = one fixture boot (8 → 16 containers, back to 8 on teardown). The suite is
**unchanged by this work** — `git diff origin/develop...HEAD --stat` does not list it.

### Run 1 — verbatim

```
Test run for D:\Github\smart-sentinel-eye\tests\Integration.Tests\bin\Release\net10.0\SmartSentinelEye.Integration.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 4 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

### Run 2 — verbatim, same command plus `-l "console;verbosity=detailed"`

**Run twice deliberately**: the first run after machine churn on this box looks exactly
like a regression, and this repository has been fooled by that before.

```
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_with_a_Bearer_prefix_strips_it_and_validates [1 s]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_with_a_valid_admin_token_returns_200 [70 ms]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_with_a_malformed_token_returns_401 [115 ms]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_with_an_invalid_path_returns_403 [17 ms]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_a_publish_with_a_valid_admin_token_returns_403 [69 ms]
  Passed SmartSentinelEye.Integration.Tests.StreamDistribution.WhepAuthIntegrationTests.Authorize_without_a_token_returns_401 [61 ms]

Test Run Successful.
Total tests: 6
     Passed: 6
 Total time: 2,6643 Minutes
```

Both runs green, both cold-booted, no retries and no reruns.

### The unit suites, for completeness

```
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 675 ms - SmartSentinelEye.StreamDistribution.Infrastructure.Tests.dll (net10.0)
```

(`WhepValidatorAudienceTests` + `WhepValidatorIssuerTests` + the new
`WhepAuthValidatorResolutionTests`, `-c Release --no-build`.) And T006's counterfactual:

```
$ grep -rn "System.Reflection" tests/StreamDistribution.Infrastructure.Tests/Auth/
[grep exit 1]
```

Nothing. The reflection is gone.

## 3. The two phase-4b counterfactual claims, checked rather than trusted

**Claim 1 — MS.DI ignores the `internal` constructor**, proved at 4b by making the seam
`public` and getting `The following constructors are ambiguous`.

- **The src file was genuinely restored.** `git diff a1277046..HEAD --
  src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs` is **empty**, and so is
  `git diff HEAD` for it. The two later commits touch nothing under `src/`: `be30e951` is
  three files under `tests/`, `e07d2552` is `tasks.md`. There is no `public
  WhepAuthValidator(IConfigurationManager<…>)` in the tree — `:43` reads `internal`.
- **And the claim itself now has in-process evidence that does not require mutating the
  file.** `WhepAuthValidatorResolutionTests` registers **both** dependencies and resolves
  `IWhepAuthValidator`; if MS.DI considered non-public constructors it would see two
  satisfiable single-parameter constructors, neither a superset of the other, and throw on
  the ambiguity. It resolved. Passed, above.

**Claim 2 — CA1859 forced the private helper's return type to the concrete type.**

- **Release still builds at 0 warnings**, whole solution, before anything was run:

  ```
  Build succeeded.
      0 Warning(s)
      0 Error(s)

  Time Elapsed 00:01:04.24
  ```

- **The seam is intact**, i.e. the concrete type is confined to the helper's *return*:
  - field (`:21`) — `private readonly IConfigurationManager<OpenIdConnectConfiguration> oidc;`
  - internal ctor parameter (`:43`) — `IConfigurationManager<OpenIdConnectConfiguration> metadata`
  - helper (`:53`) — `private static ConfigurationManager<OpenIdConnectConfiguration> MetadataSourceFor(…)`

  A test constructing through the seam still passes any `IConfigurationManager<…>`. CA1859
  bought a concrete return type on a private static, which nothing outside the class sees.

## 4. Characterisation, at the diff

Every assertion and every `customMessage` in the two rewritten files survived the commit.
Filtering `git show be30e951` for `ShouldBe|Assert|customMessage|Throw` returns exactly two
changed lines, both the `?? throw new InvalidOperationException(` of the deleted reflection
helper. No assertion was edited, so nothing had to be adjusted to make the tests pass —
which is what the colour required.

## 5. Latency budget

**N/A — the WHEP hook runs once at session setup, before media flows; constitution §IV's
six legs start at camera → SFU.** The orchestrator's proposed line is **confirmed as
written**, and it matches the spec's own §Latency-budget impact. Nothing on the
event-to-overlay path was touched: the change is one field type, one added constructor and
one extracted private static, all inside `StreamDistribution.Infrastructure.Auth`.

## 6. What was NOT observed

Bluntly.

1. **MediaMTX never called the hook.** All six cases `POST /streams/authorize` directly, in
   MediaMTX's request shape. No WHEP session was opened, no browser played a stream, and
   nothing here observes MediaMTX's own reaction to a 401 or a 403. That gap predates this
   branch and is unchanged by it.
2. **The Keycloak-outage path is still uncovered and still wrong.** A discovery failure
   answers **500, not 401** (`IDX20803` out of `GetConfigurationAsync`, caught by nothing).
   Filed as **#2160**; deliberately not fixed here, because turning it into a deny is a
   behaviour change and this branch is behaviour-preserving. No run above provoked it —
   Keycloak was healthy throughout both boots.
3. **Nothing exercised `ConfigurationManager`'s refresh.** Both runs fetch the document
   once and finish inside a minute; the automatic refresh interval and `RequestRefresh`
   were never reached, in either construction path.
4. **No production metadata source has ever gone through the internal constructor**, and by
   design never will. What §2 observes is that the *public* one still reaches the same
   configuration; the internal one is only ever handed stubs.
5. **CI has not run this branch.** Every figure here is one laptop, two boots. The blocking
   Docker job is phase 7's business.
6. **Nothing was measured.** No timing claim is made or implied; the millisecond figures in
   run 2 are xUnit's per-test wall clock on a warm fixture, not an instrument.

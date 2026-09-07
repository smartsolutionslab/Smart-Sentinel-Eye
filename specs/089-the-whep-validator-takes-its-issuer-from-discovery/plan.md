# Plan 089 — The WHEP validator takes its issuer from discovery

**Spec:** `specs/089-the-whep-validator-takes-its-issuer-from-discovery/spec.md`
**Issue:** #2095 · **Branch:** `fix/2095-the-whep-validator-takes-its-issuer-from-discovery`
**Phase:** 2 (Plan) — ADR-0037

---

## Bounded context and layers

**Context:** StreamDistribution. Single context, single layer.

| Layer | Touched | Why |
|---|---|---|
| `StreamDistribution/Domain` | **No** | This is a trust-boundary translation concern. No aggregate, no invariant, no value object is involved. |
| `StreamDistribution/Application` | **No** | `IWhepAuthValidator` and `WhepAuthSubject` are unchanged. The port's contract — "a forwarded bearer token yields a subject, or nothing" — is exactly what it was. |
| `StreamDistribution/Infrastructure` | **Yes** | `Auth/WhepAuthValidator.cs` only. |
| `StreamDistribution/Api` | **No** | `POST /streams/authorize` and its handler are untouched. |
| `ServiceDefaults` | **No** | `AuthenticationDefaults` is read by the tests, never edited. |

**Why Infrastructure is the right home and stays it.** The validator adapts an
external protocol (OIDC discovery, JWKS, JWT) into an Application-layer port.
CLAUDE.md's `Option<T>` rule already carves Infrastructure out for exactly this
reason — it is where framework and wire vocabulary is allowed to be native. The
change is a correction *inside* that adapter and moves nothing across a layer.

**No entities, no value objects, no invariants.** Stating it rather than
omitting it: nothing in this spec introduces domain state, so §II's
primitive-boundary rule has no surface here. `WhepAuthSubject(string subject,
string[] scopes)` is an Application DTO built from wire claims and is not a
domain model; it is unchanged in any case.

**No messaging.** No domain event, no integration event, no `Shared.Contracts`
addition. The path is a synchronous HTTP callback from MediaMTX.

**Boundary rules.** No cross-context project reference is added or needed.
`StreamDistribution.Infrastructure` already references `ServiceDefaults`
(a shared platform project, not a bounded context) for
`AuthenticationDefaults.ApiAudience`, which spec 071 introduced deliberately so
the two validators name the same audience constant. NetArchTest's existing
rules are unaffected — this plan adds no reference of any kind.

---

## The change

### `src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs`

**One file. Two functional edits and three comments.**

**1. `CreateParameters` stops asserting the configured URL, and loses its
parameter.**

Today (`:45-56`) it is `internal static TokenValidationParameters
CreateParameters(string authority)` and the only use of `authority` is
`ValidIssuer = authority`. With that line gone the parameter is dead, so the
method becomes parameterless. Leaving an unused `string authority` would trip
SonarAnalyzer S1172 (ADR-0084) and, more to the point, would leave the obvious
slot for the next person to put the URL back into.

After the change the factory leaves `ValidIssuer` and `ValidIssuers` both
`null` — **the same shape the bearer options carry**, for the same reason: the
request path fills them.

**2. `ValidateAsync` fills the issuer from the configuration it already awaited.**

The clone block at `:62-64` gains one line, beside the one that already reads
the same object:

```csharp
OpenIdConnectConfiguration configuration = await oidc.GetConfigurationAsync(cancellationToken);
TokenValidationParameters validationParameters = parameters.Clone();
validationParameters.ValidIssuers = [configuration.Issuer];
validationParameters.IssuerSigningKeys = configuration.SigningKeys;
```

`ValidIssuers` is `IEnumerable<string>`, so the collection expression has a
target type and satisfies `dotnet_style_prefer_collection_expression` (CLAUDE.md
house rule, `warning` in Release).

**`ValidIssuers`, not `ValidIssuer`, and the plural is deliberate**: it is the
property `JwtBearerHandler` writes, and writing the same one keeps the two
transcriptions comparable to anyone reading them side by side. Assigning
(not concatenating) is also deliberate — the handler concatenates only because
it must tolerate a caller-configured value, and after edit 1 there is none to
tolerate. Concatenating an empty `null` would be the same result written more
obscurely.

**No guard is added on `configuration.Issuer`.** If a discovery document lacks
`issuer`, `ValidIssuers` carries a null entry, `Validators.ValidateIssuer`
throws `SecurityTokenInvalidIssuerException`, the existing `catch
(SecurityTokenException)` at `:79` returns `None`, and MediaMTX gets a clean
401. That is the same failure the bearer pipeline produces from the same input.
ADR-0036: no drive-by error handling; validate at trust boundaries only, and
this *is* the trust boundary, already validating.

**`Ensure.That`** (ADR-0105) is already on the constructor's `options` and is
unchanged. The signature carrying `CancellationToken` last (ADR-0049) is
unchanged.

**3. Three comments** — D2, D3, D4 of the spec. Placed at the site of each
setting, not in the class doc comment, so a reader inspecting one field finds
the reason without reading the file.

The class doc comment at `:13-18` already claims *"Issuer + signing keys are
fetched from the realm's OIDC discovery document"* — **which is false today for
the issuer half and becomes true with this change.** Worth noticing: the
comment described the intended design correctly and the code did not implement
it, which is the third time in this file's history that a comment has been the
only thing binding two behaviours together (spec 071's audience comment, #2090's
`// mirrors JwtBearerOptions`). It needs no edit; it needs the code to catch up
to it.

### Code metrics (ADR-0084)

The file is 91 lines and stays under 300. `ValidateAsync` spans `:58-90` — 33
physical lines including signature, braces and comments — and gains one, so it
may cross the **30-LOC method limit** depending on how the analyzer counts.
Worth flagging at plan time rather
than discovering at build time: the method body is a `try` wrapping a linear
sequence, and the honest reduction is to extract the claim-reading tail
(`:68-77`) into a private `static Option<WhepAuthSubject> SubjectFrom(
ClaimsPrincipal principal)`. That extraction is behaviour-preserving, is
covered by both existing `WhepValidatorAudienceTests` cases, and is the smallest
thing that fits. **If the analyzer does not in fact fire** (SonarAnalyzer counts
executable lines, not physical ones, and blank/comment lines do not count), do
not extract — an unnecessary refactor mixed into a security fix is exactly the
"don't mix them" the Karpathy rules forbid. **Check the Release build; do not
pre-emptively refactor.**

---

## How the red is expressed

This is the part of the plan the issue asks to be costed, so it is worked out
concretely rather than gestured at.

### It cannot be the Aspire fixture, and here is why

The fixture reaches Keycloak through the Aspire proxy endpoint, and the realm's
`iss` is that same proxied endpoint — that is the whole content of the recorded
"Keycloak issuer must match the proxied port" trap: mint from the proxied
endpoint or everything 401s. So in the fixture the dialled authority and the
discovery issuer are **equal by construction**, and there is no token in the
stack whose `iss` differs from the URL the services dial.

Provoking a difference means giving Keycloak a second hostname — setting
`KC_HOSTNAME` in `AppHost` — which is shared by every test in
`AspireCollection`, would change `iss` for all of them, and would break token
minting across the whole integration suite. That is a large, shared,
high-blast-radius edit to express one refusal. **Declined.**

### It is a unit test, on a harness that already exists

`tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorAudienceTests.cs`
(delivered for #2093, on `develop` now) already does all the hard parts:

- constructs a real `WhepAuthValidator` from `Options.Create(new WhepAuthOptions
  { Authority = … })`;
- replaces the private `oidc` field with a `ConfigurationManager` over a
  `StubbedMetadata : IDocumentRetriever` that serves a discovery document and a
  JWKS **from memory, by address** — no socket, no host resolution;
- generates an RSA key and mints real signed tokens with
  `JwtSecurityTokenHandler`.

The divergence is then two string constants apart. A **new sibling file**,
`WhepValidatorIssuerTests.cs`, reuses the same shape with the two hostnames
split:

| Constant | Value | Role |
|---|---|---|
| `DialledAuthority` | `http://keycloak:8080/realms/smart-sentinel-eye` | what `WhepAuthOptions.Authority` is set to — the in-cluster URL |
| `RealmIssuer` | `https://keycloak.fab.example/realms/smart-sentinel-eye` | the discovery document's `"issuer"` — the ingress hostname |

Neither host is ever resolved: `StubbedMetadata` answers by address string, and
the `ConfigurationManager` is constructed over `DialledAuthority`'s
`.well-known` path. `jwks_uri` stays on the dialled address so the retriever's
two-document dispatch keeps working.

Three cases:

1. **`A_token_carrying_the_realms_issuer_is_authorized_when_it_differs_from_the_dialled_url`**
   — token `iss = RealmIssuer`, `aud = AuthenticationDefaults.ApiAudience`,
   signed by the harness key. **This is the red.** On `develop` `ValidIssuer`
   is `DialledAuthority`, `Validators.ValidateIssuer` throws, and `ValidateAsync`
   returns `None`, so `HasValue.ShouldBeTrue` fails. Its failure message names
   the wall: *the whole wall goes dark behind an ingress while every management
   page still loads*.
2. **`A_token_carrying_the_dialled_url_as_its_issuer_is_refused`** — identical
   harness, only `iss = DialledAuthority`. **The anti-relaxation guard.**
   **RED on `develop`** — corrected at phase 4b, having been predicted green
   here. The reason it is red is the reason it was predicted green: that string
   *is* the configured `ValidIssuer` today, so a token carrying it is accepted
   and the refusal fails. Green after the fix for the right reason (discovery
   says otherwise). The correction runs in the safe direction — the guard goes
   red→green with the fix rather than green→green, which is stronger evidence
   than advertised. It fails if the fix keeps the configured URL as an *additional*
   accepted issuer, and it fails if the fix turns `ValidateIssuer` off. It is
   the test that makes the forbidden shapes unreachable rather than merely
   forbidden in prose.
3. **`A_token_carrying_neither_issuer_is_refused`** — `iss =
   https://keycloak.attacker.example/realms/smart-sentinel-eye`, same key.
   The control that stops case 1 from being satisfied by validating nothing.

**Case 3 alone is green on `develop`; cases 1 and 2 are both red** — corrected
at phase 4b against the observed run. The prediction here was that only case 1
would fail, on the reasoning that an anti-relaxation guard constrains the fix
rather than participating in the failure. That reasoning is sound in general and
wrong for case 2 in particular, because the shape it guards against — the
dialled URL as an accepted issuer — is not a hypothetical future edit here, it
is what `develop` already does. Case 3's issuer is neither string, which is why
it is the one green, and green for the wrong reason: it fails against whichever
of the two the hook is comparing. The over-correction pattern
`WhepAudienceTests.A_whep_token_minted_for_this_api_is_accepted` established
still describes case 3; it did not describe case 2, and this paragraph claimed
it did.

### And a factory-level parity test, paired and labelled

`WhepAudienceTests` gains one case (FR-006), in the file that already holds the
factory↔pipeline pairing:

```
The_whep_hook_leaves_the_issuer_to_discovery_exactly_as_the_bearer_pipeline_does
```

It reads `ValidIssuer` and `ValidIssuers` off **both** sides — `CreateParameters()`
and `BearerOptions().TokenValidationParameters` — and asserts they match, rather
than asserting `null` as a literal. Comparing beats asserting here for the
reason spec 069 already found: a constant asserted against itself passes
whatever it is changed to.

Its doc comment must say it binds the **factory** and point at
`WhepValidatorIssuerTests` for the runtime, per the pairing #2093 established
and the existing "What they do not cover, and where that lives" paragraph in
that file — which is extended, not replaced. **This is the one edit to
`WhepAudienceTests` beyond the four mechanical `CreateParameters(Authority)` →
`CreateParameters()` call-site updates. No existing assertion moves** (FR-008).

### Characterisation capture for US-2

Before any comment is written, run and capture:

```
dotnet test tests/StreamDistribution.Infrastructure.Tests --filter "FullyQualifiedName~Auth"
```

Six tests (4 `WhepAudienceTests` + 2 `WhepValidatorAudienceTests`) green. After
the comments, the same command, the same six, **unmodified**. The capture is the
phase-4a discharge for US-2 and is quoted in the PR alongside the US-1 red.

`WhepValidatorAudienceTests.The_validator_accepts_a_token_minted_for_this_api`
is the case that genuinely covers D2's `ValidateIssuerSigningKey = true`: its
harness serves a real JWKS and a real RSA key, so the signing-key validation
path runs for real. (It runs against an `RsaSecurityKey` there rather than the
`X509SecurityKey` a real Keycloak yields — which is precisely why the comment,
not the test, is what records the certificate behaviour.)

---

## Files touched

| File | Change | Owner |
|---|---|---|
| `src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs` | `CreateParameters` parameterless, no `ValidIssuer`; `ValidateAsync` sets `ValidIssuers` from discovery; three comments | US-1 + US-2 |
| `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorIssuerTests.cs` | **new** — three runtime cases | US-1 |
| `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepAudienceTests.cs` | four call-site updates; one new parity case; doc-comment extension | US-1 |

**Three files, one bounded context, one test project.** No AppHost change, no
Aspire resource, no migration, no realm edit, no `Shared.Contracts` change, no
frontend change. Nothing here is foundational and nothing blocks other work.

---

## Risks

| Risk | Mitigation |
|---|---|
| The fix is implemented by widening rather than redirecting (configured URL kept as a second `ValidIssuer`) | `WhepValidatorIssuerTests` case 2 fails. It is written before the change. |
| The fix is implemented by relaxing (`ValidateIssuer = false`) | Same test fails. Plus the counterfactual in the spec's step 3, run and reported. |
| An existing assertion is edited to make something pass | FR-008, and the characterisation capture is quoted in the PR — an edited assertion is visible in the diff of a file whose tests were captured green. |
| `ValidateAsync` crosses the 30-LOC metric limit | Named above with the exact extraction to reach for, and an explicit instruction not to refactor unless the analyzer actually fires. |
| The Aspire fixture regresses | The change is a no-op in the fixture (discovery issuer == dialled authority there). `WhepAuthIntegrationTests`' five cases are the check, run unmodified. |
| The new file drifts from `WhepValidatorAudienceTests` (both reflect on the private `oidc` field) | Both carry the same `FieldInfo` lookup with a message naming the rename. The new file reuses that pattern verbatim rather than inventing a second one. |

---

## Constitution and ADR alignment

- **§II value objects / primitive boundary** — no domain surface; not engaged.
- **§IV latency** — N/A to the six legs. Zero added I/O on click-to-first-frame;
  the discovery fetch is already awaited. Stated in spec.md and to be restated
  in the PR.
- **§Testing** — both obligations engaged, split per user story, declared in
  spec.md §"Phase 4a".
- **§VII dashboard rule (ADR-0117)** — binds implemented legs of the
  event→overlay budget. This change is on neither, so no obligation attaches.
- **ADR-0007/0008** — implemented, not amended.
- **ADR-0036** — one defect, one fix, no speculative generality; the handler
  migration and the nine-API signing-key question are both deferred to their own
  issues rather than absorbed.
- **ADR-0084** — the one metric at risk is named with its remedy.
- **ADR-0103** — the red is a unit test because the fixture cannot express the
  divergence; the fixture's role is non-regression only.
- **ADR-0105 / ADR-0049** — existing guard and cancellation-token placement
  unchanged.
- **ADR-0086** — Conventional Commits, no `Co-Authored-By`, no session trailer.
- **ADR-0087** — each commit builds on its own; the ordering in tasks.md is
  chosen so that is true.

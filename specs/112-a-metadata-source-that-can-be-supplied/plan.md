# Plan — spec 112

## Bounded context and layers

**StreamDistribution**, Infrastructure only.

| Layer | Touched | Why |
|---|---|---|
| Domain | no | No aggregate, no value object, no invariant moves. §II does not apply — `WhepAuthValidator` is not a domain model. |
| Application | no | `IWhepAuthValidator` (`Application/Auth/IWhepAuthValidator.cs`) is unchanged; `AuthorizeWhepCommandHandler` is unchanged. |
| **Infrastructure** | **yes** | `Auth/WhepAuthValidator.cs` — one field type, one added `internal` constructor. |
| Api | no | `StreamEndpoints.AuthorizeWhep` and its OpenAPI summary are untouched. |

No cross-context reference is added or removed; nothing enters
`Shared.Contracts`. NetArchTest boundary rules are unaffected.

## Entities, value objects, invariants

None. This is an Infrastructure adapter's construction seam. The one invariant
it must not disturb is the validator's acceptance rule, and that lives entirely
in `CreateParameters()` (`WhepAuthValidator.cs:55-79`) and `ValidateAsync`
(:81-114) — both **unmodified**.

## Messaging

None. `POST /streams/authorize` is a synchronous allow/deny (recorded in
`specs/071-.../plan.md:112`); it publishes no domain event and no integration
event. Nothing is added.

## The change, concretely

```
src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs
```

1. `private readonly ConfigurationManager<OpenIdConnectConfiguration> oidc;`
   → `private readonly IConfigurationManager<OpenIdConnectConfiguration> oidc;`
   (`Microsoft.IdentityModel.Protocols`, already imported at :4.)
   `ValidateAsync` calls only `GetConfigurationAsync(cancellationToken)`, which
   the interface declares — no other member is used, so the body is untouched.

2. The existing public constructor keeps its whole body, including the
   `RequireHttps = false` comment at :37-44, which is the load-bearing part of
   it. It delegates the assignment to the new constructor.

3. A new **`internal`** constructor takes
   `IConfigurationManager<OpenIdConnectConfiguration>` (plus whatever the public
   one needs to forward), guards with `Ensure.That(…).IsNotNull()` (ADR-0105),
   and assigns `oidc`, `parameters` and `handler.MapInboundClaims`.

### Why `internal`, not a defaulted public parameter

The issue offers *"an `IConfigurationManager<OpenIdConnectConfiguration>`
parameter defaulting to today's construction, **or a small seam**"*. The seam is
chosen for three reasons, none of them new architecture:

- **Phase 6 runs `security-reviewer`.** A public constructor accepting an
  arbitrary metadata source is a public way to point a token validator at a
  different issuer and a different JWKS. `internal` keeps the injection point
  inside the assembly.
- **The mechanism already exists in this very file.** `internal static
  TokenValidationParameters CreateParameters()` (:55) is already reached by
  `WhepAudienceTests` through the `InternalsVisibleTo` declared at
  `SmartSentinelEye.StreamDistribution.Infrastructure.csproj:8`. This reuses it
  rather than introducing a second convention (house rule: read before write).
- **A defaulted parameter cannot default to a constructed object.** C# default
  values must be compile-time constants, so that shape is really
  `IConfigurationManager<…>? metadata = null` plus a null-coalesce — a nullable
  parameter, which ADR-0141 tolerates in Infrastructure but does not prefer, and
  which MS.DI would then have to be trusted to leave alone.

**This is an implementation choice inside a prescribed envelope, not a
decision.** The issue names the type, the direction, and the constraint ("a
small seam"); the visibility follows from a rule already in force. **No ADR is
needed.**

## Boundary rules

- No cross-context project reference (ADR none-violated; NetArchTest unchanged).
- Application stays ASP.NET-free (ADR-0051) — nothing moves into Application.
- `Option<T>` (ADR-0141): no nullable parameter is introduced, so the advisory
  baseline of 70/7 is unmoved in either direction.
- `CancellationToken` last (ADR-0049): no async signature is added or changed.
- File stays under 300 LOC (ADR-0084): `WhepAuthValidator.cs` is 115 lines and
  grows by roughly a dozen.

## Phase 4a colour

**BEHAVIOUR-PRESERVING → characterisation, observed green**, and the plan is
shaped so that obligation is literally satisfiable rather than argued around.

The covering tests already exist and already pass: `WhepValidatorAudienceTests`
(2) and `WhepValidatorIssuerTests`. The trap is that the same PR wants to *edit*
them, and §Testing requires the characterisation set to pass **unmodified**.
So the work splits into two commits, each of which builds and passes on its own
(ADR-0087 requires that anyway, since rebase-merge lands them individually):

| Commit | Diff | Evidence |
|---|---|---|
| **1 — the seam** | `src/` only | The two reflecting test files pass **unmodified**. They still find a private field named `oidc`; widening its declared type and adding a constructor does not remove it. This is the characterisation run. |
| **2 — drop the reflection** | `tests/` only | The same assertions and the same `customMessage` strings pass through the seam instead of through `SetValue`. An assertion that has to be edited is evidence the behaviour moved — block, do not adjust. |

If commit 1's run is anything but green, that is a finding, not a colour to
force.

## Risks

- **R1 — DI constructor selection.** MS.DI considers public constructors only,
  so `AddSingleton<IWhepAuthValidator, WhepAuthValidator>()`
  (`StreamDistributionInfrastructureModule.cs:57`) still binds the
  `IOptions` constructor. Assumption A1; pinned by T004 rather than trusted.
- **R2 — the field disappears mid-stack.** Commit 1 must not rename or remove
  `oidc`, or commit 1's own characterisation run fails on the reflection guard's
  deliberately loud message. Named in T001.
- **R3 — scope creep into the 500-on-outage residue.** Out of scope by spec
  decision. It is a behaviour change and gets its own issue (T006).

# Tasks — spec 112

Issue #2099. One user story (US1). Phase 4a colour:
**behaviour-preserving → characterisation, observed green** (plan §Phase 4a).

Almost nothing here is parallel: T001 and T002 are the same file in sequence,
and T003 depends on T001 existing. `[P]` markers (ADR-0109) are marked where
files are genuinely disjoint.

---

## Phase 4a — characterisation, captured green **before** any src edit

- [x] **T000 [US1]** Run the covering set and capture the **verbatim** output:
  ```sh
  dotnet test tests/StreamDistribution.Infrastructure.Tests --filter "FullyQualifiedName~WhepValidator"
  ```
  Expected: green, ~1 s, no Docker, no socket. This output is the before-half of
  the characterisation and is quoted in the PR body.
  **Stop the AppHost before building** (a running stack holds the service
  binaries; MSB3027 reads as a broken build).
  *Blocks everything.*

---

## Phase 4b — the seam (commit 1, `src/` only)

- [x] **T001 [US1]** In `src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs`:
  widen the `oidc` field (:21) to
  `IConfigurationManager<OpenIdConnectConfiguration>` and add an **`internal`**
  constructor taking that interface. The public
  `WhepAuthValidator(IOptions<WhepAuthOptions>)` keeps its body — including the
  `RequireHttps = false` comment at :37-44 — and delegates.
  Guards: `Ensure.That(…).IsNotNull()` (ADR-0105).
  **Do not rename or remove the private field `oidc`**: T002's characterisation
  run still reflects on it (plan R2).
  FR-001, FR-002, FR-003, FR-004. *Depends: T000.*

- [x] **T002 [US1]** Re-run T000's command **without touching a test file**.
  The two reflecting files must pass **unmodified**. This is the characterisation
  discharge and the ADR-0139 evidence for a behaviour-preserving change.
  An assertion that has to be edited to pass means the behaviour moved — block,
  file a finding, do not adjust. *Depends: T001.*

- [x] **T003 [P] [US1]** Confirm the file still satisfies ADR-0084 (≤ 300 LOC,
  ≤ 30 LOC/method, ≤ 4 params) and that `dotnet build -c Release` is clean —
  the collection-expression and analyzer rules fail the Release build, not the
  Debug one. *Depends: T001. Disjoint from T004.*

- [x] **T004 [P] [US1]** Pin assumption A1 rather than trusting it: a test in
  `tests/StreamDistribution.Infrastructure.Tests/` asserting that the service
  container resolves `IWhepAuthValidator` to a `WhepAuthValidator` after
  `AddStreamDistributionInfrastructure` — i.e. that adding a second constructor
  did not change MS.DI's selection at
  `StreamDistributionInfrastructureModule.cs:57`. New file, disjoint from T003.
  If such a resolution test already exists for this module, extend it instead of
  adding one. *Depends: T001.*

---

## Phase 4c — drop the reflection (commit 2, `tests/` only)

- [x] **T005 [US1]** Rewrite `ValidatorWithStubbedMetadata()` in **both**
  `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorAudienceTests.cs`
  (:81, :145) and `.../WhepValidatorIssuerTests.cs` (:115, :206) to construct
  through T001's seam. Delete `OidcField`, the `GetField`/`SetValue` calls and
  the `using System.Reflection;` from both.
  **Every assertion and every `customMessage` survives verbatim** (FR-006) —
  only the construction changes. Update the two files' class-level `<summary>`
  paragraphs that currently explain the reflection ("No Docker, no network, no
  realm" in the audience file), since they now describe something that is gone.
  FR-005, FR-006.
  *Not `[P]`: one logical edit, and both files must land together or the
  intermediate commit half-describes itself.* *Depends: T002.*

- [x] **T006 [US1]** Re-run T000's command. Same assertions, same messages,
  green. Then the counterfactual that proves the seam is real:
  ```sh
  grep -rn "System.Reflection" tests/StreamDistribution.Infrastructure.Tests/Auth/
  ```
  must return nothing. *Depends: T005.*

---

## Phase 5-7 — verification, review, PR

- [ ] **T007 [US1]** Verification note: the two runs from T002 and T006, quoted,
  plus the empty grep. **Latency: N/A, no §IV leg** — the WHEP hook runs once at
  session setup, before any of the six legs (spec §Latency-budget impact).
  The Docker-backed `WhepAuthIntegrationTests` is unchanged and is observed via
  CI, not re-run locally (**C: at 96%, ~11 GB free**).

- [ ] **T008 [US1]** Phase 6 runs **both** `backend-reviewer` and
  `security-reviewer` — this touches auth. The security question to answer
  explicitly: *does anything now widen what the hook accepts?* Expected answer:
  no — the only public constructor is unchanged, the seam is `internal`, and
  `CreateParameters` and `ValidateAsync` are byte-identical.

- [ ] **T009 [US1]** PR to `develop` (`--base develop`, ADR-0028). Body carries
  T002's and T006's verbatim output, records **Part 1 of #2099 as closed by
  evidence rather than by code** (spec §Part 1), and states the colour
  (behaviour-preserving / characterisation).

---

## Follow-up filed, not fixed

- [x] **T010** *Already satisfied — filed as **#2160** (open, `agent:ready`) before
  this phase ran. No duplicate opened.* File a new issue: **the WHEP hook answers 500, not 401, when the
  realm's discovery document cannot be fetched.**
  `WhepAuthValidator.ValidateAsync:83-113` catches `SecurityTokenException` and
  `ArgumentException`; `oidc.GetConfigurationAsync` (:85) throws
  `InvalidOperationException` (IDX20803) on a discovery failure and no
  `IExceptionHandler` in `src/ServiceDefaults/Authorization/` converts it.
  This is the one true residue of #2099's Part 1, it is a **behaviour change**,
  and #2099 neither asked for nor decided it. Note that T001's seam makes it
  cheaply testable without Docker (a throwing `IConfigurationManager`).
  Add to Project #13:
  ```sh
  gh project item-add 13 --owner smartsolutionslab --url <issue-url>
  ```

---

## Not in this spec

- **No new integration test, and no Docker.** Spec §Part 1: the suite already
  holds four negative cases asserting exact status codes, and MediaMTX's deny
  contract is the status code alone (spec 074 D3).
- **No realm change.** A "valid audience, no scope" token cannot be minted from
  the fixture — only `smart-sentinel-eye-web` and `management-web` enable the
  password grant, and both carry `sse.*` **default** client scopes
  (`src/AppHost/Realms/smart-sentinel-eye-realm.json:141-147, 166-190`), which
  Keycloak emits regardless of the requested `scope`. That branch is covered at
  handler level instead (`AuthorizeWhepCommandHandlerTests.cs:129`).
- **No ADR.** Plan §"Why `internal`".

# Tasks 089 — The WHEP validator takes its issuer from discovery

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2095
**Branch:** `fix/2095-the-whep-validator-takes-its-issuer-from-discovery`
**Phase:** 3 (Tasks) — ADR-0037

**Engineer:** `backend-engineer`. One C# file in
`src/StreamDistribution/Infrastructure/Auth/` and two xUnit files. No AppHost
wiring, no CI workflow, no Docker, no Helm, no Keycloak realm edit — nothing in
the infra reviewer's territory. **Phase 4a is `test-writer` first, per
ADR-0144**; the engineer receives the verbatim output as its brief and may not
edit the tests to pass.

**Board gate (ADR-0037 phase 3).** Already satisfied: **#2095 is on Project #13,
status In Progress**, verified via `gh issue view 2095` (`projects: Smart
Sentinel Eye (In Progress)`). No per-task issues — feature-level tracking since
spec 028; `tasks.md` is the artifact the work is tracked against. Nothing to add.

**Commit convention:** Conventional Commits (ADR-0030), **no `Co-Authored-By`
footer and no session trailer** (ADR-0086). Each commit builds on its own
(ADR-0087) — the ordering below is chosen so that holds; a test commit that is
red still *compiles*, which is what the rebase-merge constraint requires.

---

## Task list

### Phase 4a — US-1, RED (`test-writer`)

| ID | P | Story | Task |
|---|---|---|---|
| **T-001** | | US-1 | **Capture the baseline green.** Run `dotnet test tests/StreamDistribution.Infrastructure.Tests --filter "FullyQualifiedName~Auth"` on the unmodified branch and save the verbatim output. Six tests, all passing (4 `WhepAudienceTests` + 2 `WhepValidatorAudienceTests`). This is the characterisation capture US-2 is discharged against **and** the proof that the red in T-002 is new, not pre-existing. |
| **T-002** | **[P]** | US-1 | **New file `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepValidatorIssuerTests.cs`.** Three cases against the real `ValidateAsync`, on the `WhepValidatorAudienceTests` harness (stubbed `IDocumentRetriever`, reflected `oidc` field with the same rename-naming failure message, generated RSA key). Constants: `DialledAuthority = "http://keycloak:8080/realms/smart-sentinel-eye"` (the `WhepAuthOptions.Authority`, and the `ConfigurationManager`'s `.well-known` address), `RealmIssuer = "https://keycloak.fab.example/realms/smart-sentinel-eye"` (the stubbed discovery document's `"issuer"`). `jwks_uri` stays on the dialled address. Cases: (1) `iss = RealmIssuer` → `HasValue` true — **the red**, failure message naming the darkened wall; (2) `iss = DialledAuthority` → `HasValue` false — anti-relaxation guard, green on `develop`; (3) `iss = "https://keycloak.attacker.example/realms/smart-sentinel-eye"` → `HasValue` false — control. All three carry `aud = AuthenticationDefaults.ApiAudience` and the harness signature, so only `iss` varies. Calls `ValidateAsync` only — compiles unchanged against `develop`. |
| **T-003** | **[P]** | US-1 | **Extend `tests/StreamDistribution.Infrastructure.Tests/Auth/WhepAudienceTests.cs`** with one case, `The_whep_hook_leaves_the_issuer_to_discovery_exactly_as_the_bearer_pipeline_does`: read `ValidIssuer` **and** `ValidIssuers` off both `CreateParameters(Authority)` and `BearerOptions().TokenValidationParameters` and assert each pair matches — compared, never asserted as a `null` literal (spec 069's reason: a constant asserted against itself passes whatever it becomes). Extend the file's existing *"What they do not cover, and where that lives"* paragraph to name `WhepValidatorIssuerTests` alongside `WhepValidatorAudienceTests`, per the #2093 pairing. **Written against the current `CreateParameters(string)` signature so this commit compiles; the call site is updated in T-005.** No existing assertion is touched (FR-008). |
| **T-004** | | US-1 | **Observe and quote the red.** Re-run the T-001 command. Expect **three** new failures — `WhepValidatorIssuerTests` cases 1 **and 2** and the T-003 parity case — and the other seven green. **Corrected at phase 4b: this row said "exactly two", and the anti-relaxation refusal (case 2) is red as well.** The reason it is red is the reason plan.md predicted it green — `ValidIssuer` *is* the dialled URL today, so a token carrying it is accepted and the refusal fails. The correction runs in the safe direction: the guard goes red→green with the fix rather than green→green. Capture the verbatim output; it is the phase-4 gate evidence and goes in the PR body (ADR-0139). If a **fourth** test is red, stop: the harness is wrong, not the code. **Commit here** (`test(089): …`). |

**T-002 ‖ T-003 are `[P]`** — disjoint files, one new and one existing, no shared
symbol beyond `AuthenticationDefaults.ApiAudience` which neither modifies
(ADR-0109). T-001 blocks both. T-004 depends on both.

### Phase 4b — US-1, the fix (`backend-engineer`)

| ID | P | Story | Task |
|---|---|---|---|
| **T-005** | | US-1 | **`WhepAuthValidator.CreateParameters` stops asserting the configured URL.** Delete `ValidIssuer = authority`; the parameter is then dead, so make the method parameterless (`CreateParameters()`). Update the four call sites in `WhepAudienceTests` and the one added in T-003 — **call sites only, no assertion edits** (FR-008). Leaving an unused parameter is not an option: S1172 (ADR-0084), and it is the slot someone puts the URL back into. |
| **T-006** | | US-1 | **`WhepAuthValidator.ValidateAsync` fills the issuer from discovery.** Add `validationParameters.ValidIssuers = [configuration.Issuer];` immediately before the existing `IssuerSigningKeys` assignment, both reading the `configuration` already awaited on the line above. Plural `ValidIssuers` and a collection expression (house rule, `warning` in Release). **Assign, do not concatenate** — the handler concatenates only to tolerate a configured value, and T-005 removed the one there was. **No null guard on `configuration.Issuer`**: it fails closed through the existing `catch (SecurityTokenException)`, identically to the bearer pipeline (ADR-0036, no drive-by error handling). |
| **T-007** | | US-1 | **Green, and the counterfactual.** Re-run the T-001 command: **ten** green (not nine — T-003 added a tenth). Then prove each guard by constructing what it claims to catch (each reverted immediately). **Observed at phase 4b, and one of the two predictions here was wrong:** re-adding the dialled URL as `ValidIssuer` *alongside* the discovery fill fails **case 2 and the T-003 parity case**, not case 1 — `Validators.ValidateIssuer` accepts the union of `ValidIssuer` and `ValidIssuers`, so the realm's issuer is still accepted and only the "in addition to" shape is caught. Case 1 fails when the discovery fill is removed as well, i.e. against the full pre-fix shape. `ValidateIssuer = false` fails **cases 2 and 3**, as predicted for case 2. Three weakenings, three distinct failure sets. Record them in the PR. Also check the Release build for SonarAnalyzer S1541/S138 on `ValidateAsync` (now 33 physical lines) — **only if it actually fires**, extract the claim-reading tail at `:68-77` into `private static Option<WhepAuthSubject> SubjectFrom(ClaimsPrincipal principal)`. Do not pre-emptively refactor. **Commit here** (`fix(089): …`). |

T-005 → T-006 → T-007, strictly sequential: same file, and T-005 alone leaves
`ValidateIssuer = true` with nothing to validate against, which is a compiling
but 401-on-everything state that must not be a commit boundary.

### Phase 4a/4b — US-2, CHARACTERISATION observed GREEN

| ID | P | Story | Task |
|---|---|---|---|
| **T-008** | | US-2 | **Three comments in `WhepAuthValidator.cs`**, at the site of each setting, not in the class doc comment. (a) `ValidateIssuerSigningKey = true` — deliberately stricter than the bearer pipeline; against Keycloak it **date-checks the realm's signing certificate**, because Keycloak's JWKS carries `x5c` and `JsonWebKeySet.GetSigningKeys` therefore yields an `X509SecurityKey`; and it is the second copy of #2095's asymmetry — WHEP 401s on a lapsed realm certificate where the nine REST APIs do not. **Must not say "harmless" or "inert"** (spec §"What the issue gets wrong"). (b) `NameClaimType = "preferred_username"` — inert: `ValidateAsync` reads `sub` and `scope` through `FindFirst` and never touches `Identity.Name`. (c) the `JwtSecurityTokenHandler` field — `JsonWebTokenHandler` should eventually back both sides; the `catch (ArgumentException)` block below exists only to absorb this handler's malformed-token behaviour and goes away with it; deferred because `ValidateTokenAsync` returns a result rather than throwing, so the migration rewrites the control flow. |
| **T-009** | | US-2 | **Re-run the T-001 command and diff against T-001's capture.** The six original tests must pass **unmodified**. An assertion that had to move is evidence the behaviour moved — block, do not adjust (ADR-0144). This green pair, before and after, **is** the phase-4a discharge for US-2: no test is written for a comment, and 4a is not skipped. **Commit here** (`docs(089): …`, or `chore(089): …` — comments only). |

T-008 depends on T-007 (same file). T-009 depends on T-008.

### Phase 5–7

| ID | P | Story | Task |
|---|---|---|---|
| **T-010** | | — | **Phase 5, verify.** `dotnet test tests/Integration.Tests --filter "FullyQualifiedName~WhepAuthIntegrationTests"` against the Aspire fixture — five existing cases green, unmodified (SC-004). The change is a **no-op in the fixture** (discovery issuer == dialled authority there), which is the point of running it: correct in the shape we run, and correct in the shape we do not. Verification note on the PR states that, plus the §IV line: **N/A to the event→overlay budget; zero added I/O on click-to-first-frame.** |
| **T-011** | | — | **Phase 6, QA.** `/code-review`, and `/security-review` — this is an authentication trust boundary, so the security pass is not optional. The reviewer's specific brief: confirm nothing was reached by relaxation, and confirm no assertion in `WhepAudienceTests`, `WhepValidatorAudienceTests`, `BearerAudienceTests` or `WhepAuthIntegrationTests` was modified. |
| **T-012** | **[P]** | — | **File the two deferred follow-ups**, after the PR opens: (1) migrate `WhepAuthValidator` to `JsonWebTokenHandler` and drop the `ArgumentException` catch (D4); (2) decide whether the nine REST APIs should set `ValidateIssuerSigningKey = true`, resolving D2's asymmetry in the strict direction rather than leaving it one-sided. Both reference spec 089. Add each to Project #13. |
| **T-013** | | — | **Phase 7, PR** to `develop` (`gh pr create --base develop`). Body carries: the T-004 red output verbatim, the T-007 counterfactual results, the T-001/T-009 characterisation pair for US-2, the §IV N/A line, and the three corrections to the issue (§"What the issue gets wrong"). |

**T-012 is `[P]`** — GitHub-only, touches no file, runs alongside T-011.

---

## Dependency graph

```
T-001 ──┬── T-002 [P] ──┐
        └── T-003 [P] ──┴── T-004 (RED, commit)
                              │
                            T-005 ── T-006 ── T-007 (GREEN, commit)
                                                │
                                              T-008 ── T-009 (characterisation, commit)
                                                          │
                                                        T-010 ── T-011 ──┬── T-013
                                                                  T-012 [P] ┘
```

**Nothing here is foundational.** No `Shared.Kernel`, no `Shared.Contracts`, no
AppHost resource, no migration — so nothing in this spec blocks other work, and
no other in-flight spec needs to wait on it. The only parallelism available is
T-002 ‖ T-003 and T-012 ‖ T-011; the rest is one file, edited in order.

---

## Definition of done

Each maps to a success criterion in `spec.md`.

- SC-001 — the ingress-shaped acceptance case passes, **and its failure on
  `develop` is quoted verbatim in the PR** (T-004).
- SC-002 — the dialled-URL token is refused, and that refusal survives both
  counterfactuals (T-007).
- SC-003 — `ValidateIssuer` and `ValidateAudience` are `true` on both
  validators, read off the objects (T-003).
- SC-004 — the five `WhepAuthIntegrationTests` cases pass unchanged (T-010).
- SC-005 — three comments present and matching §US-2; the signing-key comment
  does not call the check harmless (T-008, checked in T-011).
- SC-006 — no assertion modified in the four named test files (T-011).

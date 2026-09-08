# Spec 100 — Two placeholders in one label, both resolving, over HTTP

**Issue:** #494 (`[T068]`) · **Branch:** `test/494-two-placeholders-in-one-label`
**Worktree:** `D:/Github/wt-494` · **Base:** `8ecba0b8`
**Phase:** 1 (Specify) · **Date:** 2026-09-08
**Feature bucket:** spec `specs/005-system-variables/`, story **US3** — "Overlay
label resolves at fetch time". T068 is the last unchecked task in that phase
(`specs/005-system-variables/tasks.md:153`), and US3's own *Independent Test*
line (`:147`) names exactly this scenario.
**ADRs:** ADR-0037 (the phased workflow and its gates), ADR-0103 (integration
against the real Aspire stack, no Testcontainers), ADR-0109 (disjoint files for
parallel slices), ADR-0113 (`If-Match` on mutating writes — the
`VariableRequests` helper supplies it), ADR-0115 (a placeholder resolves in the
*viewer's* fab), ADR-0144 (the lane may not weaken a gate and may not skip
phase 4a).
**Constitution:** §Testing (two obligations, not one rule with an exception);
§IV (latency budget — see *Latency impact* below, which is N/A and says why).

---

## The issue as filed, and what survives contact with the repository

### Confirmed

- **`VariableResolutionIntegrationTests` has never existed as code.**
  `git log --all -S "VariableResolutionIntegrationTests" -- '*.cs'` returns zero
  commits at `8ecba0b8`.
- **Nothing anywhere resolves two placeholders in one label through the HTTP
  API.** Grepping every test string literal in `tests/` and `e2e/` for two
  well-formed `{{name}}` tokens returns ten hits, **all ten in
  `tests/SystemVariables.Application.Tests/`**. `tests/Integration.Tests/`
  contributes none.
- **The single-variable integration equivalents do exist**, and both bind one
  placeholder only:
  - `tests/Integration.Tests/SystemVariables/NFR_VariableResolutionLatencyTests.cs:185`
    publishes the label `$"Line 1: {{{{{variableName}}}}}"`.
  - `tests/Integration.Tests/SystemVariables/ResolvedTextReachesItsFabTests.cs:198`
    asserts `frame.ResolvedText.ShouldBe("Line 1: 82.5")`.
- **The two-variable case is reachable through the API this test would drive.**
  Nothing caps the placeholder count. `Label.From`
  (`src/OverlayDesigner/Domain/Overlay/Label.cs:48-64`) validates non-blank and
  `MaximumTextLength = 256` and nothing else; the class remark at `:12-16` says
  placeholder syntax is stored verbatim. `InMemoryReverseIndex.UpsertOverlayReferences`
  (`src/SystemVariables/Infrastructure/Resolution/InMemoryReverseIndex.cs:31-35`)
  indexes **every** extracted name. `GET /system-variables/snapshot`
  (`src/SystemVariables/Api/SystemVariableEndpoints.cs:57`) needs only
  `sse.variables.read`, which the fixture's admin client holds. **No block.**

### Corrections — three, and the third changes what the test is for

**1. The `git log --all -S` claim is over-stated as written.** Unscoped, it
returns **three** commits — `9f98a15b`, `817bbe65`, `40ddc062` — all
documentation (`specs/005-system-variables/tasks.md`,
`specs/014-system-variable-fab-scoping/{plan,research}.md`). The name has always
been a *plan*, never a file. Scoped to `-- '*.cs'` the claim holds exactly.

**2. Both integration citations have drifted by roughly a dozen lines.**
`NFR_VariableResolutionLatencyTests.cs:66` is `public Task DisposeAsync()`; the
described behaviour is the `[Fact]` at `:68-115` and `MeasureOneChangeAsync` at
`:122-142`. `ResolvedTextReachesItsFabTests.cs:186` is a comment; the quoted
assertion is at `:198` (and again at `:251`). Both files have grown since the
audit. The substance is unaffected. Separately, the NFR file does not "assert
the resolved text" — it asserts `.Contains(expected)` (`:134`) and a *median
latency* (`:114`); it is a measurement, not a correctness assertion.

**3. There is no replacement loop, and the substitution mechanism is already
covered twice in one string.** Both halves of the audit's justification are
wrong, and correcting them changes what this test is worth.

- `PlaceholderParser.Substitute`
  (`src/SystemVariables/Application/Resolution/PlaceholderParser.cs:53-63`) is a
  **single `Regex.Replace` with a `MatchEvaluator`**. There is no loop, so there
  is no off-by-one and no ordering to get wrong: `Regex.Replace` visits every
  match left to right and the evaluator is a pure per-name lookup with no
  cross-match state.
- `tests/SystemVariables.Application.Tests/EventHandlers/VariableValueChangedPreCommitTests.cs:107,128`
  — `A_sibling_variable_still_resolves_from_storage` — binds
  `"{{shift}} — OEE {{oeeLine1}}%"` and asserts `"Nights — OEE 82.5%"` against
  the **real** `Resolver`. Two placeholders, both resolving, in one string. The
  audit's "not exercised twice in one string anywhere" is false.

**What is genuinely untested is a loop, just not that one.** There are two
independent `BuildSnapshotAsync` methods, one per consumer:

| Loop | File | Two-variable coverage |
|---|---|---|
| Push path | `src/SystemVariables/Application/EventHandlers/VariableValueChangedDomainEventHandler.cs:87-119` | Yes — `VariableValueChangedPreCommitTests.cs:128` |
| **Query path** | `src/SystemVariables/Application/Queries/Handlers/GetOverlaySnapshotQueryHandler.cs:31-66` | **None, at any level** |

The query loop iterates `ExtractNames`, does an `await` repository lookup per
name, and has four `continue` exits (unparseable name `:38`, absent in every
fab `:49`, archived `:55`, unset `:60`) before reaching `snapshot[name] = …` at
`:63`. Every one of its five unit tests
(`tests/SystemVariables.Application.Tests/Queries/GetOverlaySnapshotQueryHandlerTests.cs`)
binds a single placeholder, except
`Skips_archived_and_unset_variables_so_they_render_as_literal_placeholders`
(`:53-73`, the audit's citation, which is accurate) where **neither** name
reaches `:63`. So the loop has never been observed completing a second
iteration with a value in hand.

**Revised justification, and the one this spec commits to.** The test does not
protect the substitution mechanism — that is covered. It protects the *query
handler's own multi-name snapshot loop*, and it proves the whole HTTP path
carries two independent bindings end to end. The counterfactual in `plan.md` is
chosen to match that narrower claim exactly, and the tests it leaves green are
the point of it.

---

## User story (P1, and the only one)

**US1 — A kiosk tile fetching its opening label gets every placeholder
resolved, not just the first.**
An operator defines two variables in one fab, an overlay carries both in one
label, both values are set, and `GET /system-variables/snapshot` returns text
with both substituted.

### Acceptance scenarios

**Happy path**

```gherkin
Given two Number variables "vA" and "vB" are defined in the caller's fab
  And an overlay is published whose label text is "Line A: {{vA}} / Line B: {{vB}}"
  And the overlay has been picked up by the reverse index
 When "vA" is set to 82.5 and "vB" is set to 91.5
  And GET /system-variables/snapshot?overlayIdentifier={overlay} is called
 Then the response is 200
  And resolvedText is "Line A: 82.5 / Line B: 91.5"
```

The assertion is the **whole string**, not two `Contains` checks. A pair of
`Contains` calls would pass against a resolver that dropped the separator,
duplicated a substitution, or emitted the two in the wrong order.

**Partial resolution — the control that stops the happy path passing for free**

```gherkin
Given the same label shape and two variables defined in the caller's fab
  And "vA" carries a value but "vB" has never been set
 When the snapshot is fetched
 Then resolvedText is "Line A: 82.5 / Line B: {{vB}}"
```

Included because it is the discriminator: a resolver that substituted *any*
value into *every* placeholder would satisfy the happy path and fail here. It
is the only place the two-variable case can distinguish "resolved both" from
"resolved something twice". Existing coverage of this shape is unit-level only
(`ResolverTests.cs:67-73`), never over HTTP.

**Bad request / auth — declared, not tested here**

`400` and `403` on this endpoint are already covered by
`tests/Integration.Tests/SystemVariables/VariableFabResolutionIntegrationTests.cs`
and `VariableReadScopeIntegrationTests.cs`, and `404` (unknown overlay) by the
handler's own `Returns_OverlayNotInReverseIndex_when_the_overlay_has_no_published_revision`.
Re-asserting them here would add runtime to the Docker job and prove nothing new.
**This slice adds no auth or validation assertions**, and that is a deliberate
scope decision rather than an omission.

### Independent end-to-end test procedure

Runnable by a person with the stack up, no test code involved:

1. Boot the AppHost (`dotnet run --project src/AppHost`).
2. Mint an admin token and `POST /system-variables` twice — `vA` and `vB`,
   type `Number`, `initialValue "0"`.
3. `POST /overlays` with
   `label.text = "Line A: {{vA}} / Line B: {{vB}}"`, then
   `POST /overlays/{id}/revisions/1/publish`.
4. `PUT /system-variables/vA/value` → `82.5`, and `vB` → `91.5`, each with the
   `If-Match` version read from `GET /system-variables/{name}`.
5. `GET /system-variables/snapshot?overlayIdentifier={id}` and read
   `resolvedText`.

**Pass:** `"Line A: 82.5 / Line B: 91.5"`. **Fail (and the defect this exists
for):** `"Line A: 82.5 / Line B: {{vB}}"` with `vB` demonstrably set.

---

## Locked tech choices

- xUnit + Shouldly (ADR-0052), sentence-style test names (ADR-0053).
- `[Collection(AspireCollection.Name)]` against the real stack (ADR-0103). **No
  `[Trait]`** — the CI `integration` job filters
  `Category!=Measurement&Category!=Disruptive&Category!=Maintenance`
  (`.github/workflows/ci.yml:179`) and every existing file in
  `tests/Integration.Tests/SystemVariables/` carries no trait at all, so an
  untraited class is what runs. Adding one would exclude the test.
- `aspire.CreateAdminClientAsync(...)`, `VariableRequests.SetValueAsync` (which
  supplies `If-Match`, ADR-0113), and the fixture's `ResetSystemVariablesAsync`
  — all reused, none new.

## Latency impact

**N/A.** No production code changes, so no leg moves. The test reads the same
endpoint whose read half `NFR_VariableResolutionLatencyTests` measures for the
`event → overlay state` leg (§IV, 200 ms), but it asserts **text, not time**,
and adds no timing assertion. §VII's dashboard rule is not engaged: this spec
builds no leg.

## Out of scope

- Any production change. If the run shows two placeholders resolving wrongly,
  that is a **finding and a separate issue** — a bug fix and a missing test are
  two issues (ADR-0144), and characterisation would otherwise encode the bug.
- Three or more placeholders, repeated placeholders in one label, cross-fab
  labels, and the SignalR push path — each already has an owner or is not the
  gap.

# Spec 087 — Plan

**Phase:** 2 (Plan) — ADR-0037
**Spec:** `spec.md` · **Evidence:** `census.md`

---

## What is being built

One architecture test, and four attributes.

`tests/Architecture.Tests/IntegrationTestSelectionTests.cs` — a source-scanning
derivation test asserting FR-001: **a test class under `tests/Integration.Tests`
that does not carry `[Collection(AspireCollection.Name)]` must carry a
`[Trait("Category", …)]`.**

Then `[Trait("Category", "FixtureLogic")]` on the four classes the guard reddens.

Nothing in `src/`, nothing in `apps/`, no runtime code, no message, no schema.

---

## Bounded context and layers

**None.** This change lives entirely in `tests/`. No bounded context is touched,
so the ADR-0016/0027 boundary rules (no cross-context project references;
communication only via `Shared.Contracts`) are not engaged — there is nothing to
engage them.

The one boundary that *is* engaged is the test-project graph, and it is the
reason the guard is written the way it is:

**`Architecture.Tests` takes no project reference on `Integration.Tests`, and
must not acquire one.** A reference would drag the Aspire hosting and DCP
dependency graph into a project that today runs in seconds with no Docker —
defeating the purpose of the guard, which is to move a verdict *out* of the
Docker job. Verified: `SmartSentinelEye.Architecture.Tests.csproj` contains no
`Integration` reference today.

Therefore the guard **reads source from disk**. This is an established pattern
here, not a new one:

- `LogTailCoverageTests` — scans `tests/Integration.Tests`, with the same
  rationale spelled out at `:28-34`: *"Reads source from disk rather than
  referencing `Integration.Tests` … a project reference would drag the Aspire
  hosting and DCP dependency graph into a project that today runs in seconds
  with no Docker. `GuardBanWiringTests` reads repository files for the same
  reason."*
- `GuardBanWiringTests` — same mechanism.

Both already provide `RepositoryRoot()` (walks up from `AppContext.BaseDirectory`)
and the `/`-normalising relative-path helper. **Reuse them; do not reintroduce
either.** If sharing means extracting a helper, extract it — but prefer copying
the four-line `RepositoryRoot()` over inventing an abstraction (ADR-0036, no
speculative generality).

---

## The derivation rule, and why it is this one

The obvious rule — *"no `[Collection]` ⇒ it needs no Docker ⇒ demand
`FixtureLogic`"* — **is wrong, and the census caught it.**

Two classes lack the collection *and* need a live run-mode stack:
`RunModeVariableResidueSweep` and `RunModeIngestAttributionTests`. They connect
to a stack they did not start (`RunModeStackAddress.FromEnvironment()`, then an
authenticated `HttpClient`). Demanding `FixtureLogic` of them would be demanding
the wrong declaration, and would either be worked around or would move a
Docker-needing test into the Docker-free job, where it fails for a reason that
is not a defect — the precise failure mode `ci.yml:159-174` exists to avoid.

**The rule the guard implements is therefore: a class outside the collection
must *declare a category*. The guard checks that a declaration exists; it does
not adjudicate which.**

| Class shape | Correct declaration |
|---|---|
| needs no stack | `FixtureLogic` — selected by `ci.yml:72` |
| needs a stack CI does not boot | `Measurement` / `Disruptive` / `Maintenance` — excluded by `ci.yml:179` |
| needs the fixture's stack | `[Collection(AspireCollection.Name)]`, no trait needed |

FR-005 requires the failure message to name the first two, because the correct
fix differs between them and choosing wrong is silent.

**The residual hole is stated in `spec.md` §"What the guard cannot do" and is
not closed here:** a Docker-free class mis-declared `Measurement` satisfies the
guard and runs nowhere. Closing it needs a way to decide Docker-dependence from
source, which is not reliably derivable. Recording the hole is the honest move;
inventing a heuristic for it would be the speculative-generality one.

---

## Implementation shape

```
tests/Architecture.Tests/IntegrationTestSelectionTests.cs
```

One `[Fact]`. Steps:

1. `RepositoryRoot()` → enumerate `tests/Integration.Tests/**/*.cs`.
2. For each file: **strip comments first** (FR-003) — `//` to end of line, plus
   XML doc-comment lines. Then:
   - skip if no `[Fact]` / `[Theory]` (helper types are not in the population —
     FR-003's companion, and the "no soft edge" scenario);
   - skip if `[Collection(AspireCollection.Name)]` present;
   - skip if `[Trait("Category", …)]` present;
   - otherwise it is an offender.
3. Assert the offender set is empty, reporting each by repository-relative path
   with `/` separators (FR-004).

### The two traps the implementation must not fall into

Both are documented in `census.md` §D5 and both produce the *same* wrong count,
23 instead of 34 — which is why neither is left to reviewer memory:

1. **A doc-comment naming the attribute is not the attribute.**
   `RunModeDriverTests` contains the literal
   `<c>[Collection(AspireCollection.Name)]</c>` in its doc-comment, and the
   census's unanchored `grep` credited it.

   **This shape is refused twice over, independently.** With `StripComments`
   neutered, the guard still reports 4 classes / 34 tests — proved by
   counterfactual during phase 4a — because that `[` sits behind `/// <c>` and
   never begins its line: the anchor alone refuses it. But the anchor is not
   what does the work in the guard as built either: `StripComments` deletes the
   whole `///` line, anchor or no, before either match runs. Removing just the
   anchor still reddens correctly for the same reason. Neither mechanism is
   individually *necessary* here — the counterfactual establishes that
   stripping is not *sufficient credit* for this case, not that the anchor is
   the sole cause. An earlier draft of this section, and of `tasks.md` T003,
   said the guard would be *green* on `RunModeDriverTests` without stripping.
   That holds for the census's shell `grep` and not for the guard as built,
   which refuses this shape by either mechanism on its own.

   **Stripping stays load-bearing, for a different shape:** an attribute
   commented out inside a `/* … */` block *does* begin its own line, so the
   anchor sees it exactly as it sees a live one and only the stripping tells
   the two apart. That is the case FR-003 exists for, and it is asserted
   separately (`A_commented_out_declaration_is_not_a_declaration`) so the
   requirement is not carried by a test that passes without it.
2. **Do not key on "mentions `AspireFixture`".** The same class names the
   fixture in reflection code precisely because its job is to assert it does
   *not* acquire it.

### One assertion over the tree, five discriminators beside it

`tasks.md` T003 specifies "one `[Fact]`". Six were written, and the five extra
are not drift: the tree scan is the only one that can go green by matching
nothing or by matching the wrong thing, and a source-scanning guard whose
matching is asserted only through the tree it scans stops discriminating the
moment the tree changes shape. Each of the five pins one decision on a
synthetic source string, so it keeps saying the same thing when the repository
does not:

| Fact | The decision it pins |
|---|---|
| `Every_integration_test_class_declares_where_it_runs` | the guard itself, over the real tree |
| `A_doc_comment_naming_the_collection_attribute_is_not_a_declaration` | trap 1 — prose is not an attribute |
| `A_commented_out_declaration_is_not_a_declaration` | the shape FR-003's stripping is actually for |
| `Naming_the_fixture_in_code_is_not_a_declaration` | trap 2 — a mention is not a declaration |
| `A_file_with_no_test_methods_is_not_asked_to_declare` | the population's edge: no verdict, nothing to declare |
| `Either_declaration_satisfies_the_guard` | the guard requires *a* declaration, not a specific one |

### Slash normalisation is a requirement, not a polish

`Path.GetRelativePath` returns the platform separator. A backslash literal in an
expected path is **green on Windows and red on Linux CI** — a standing trap in
this repo, and `LogTailCoverageTests:156-162` already solves it:

```csharp
Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/')
```

Reuse that form.

### Regex vs. plain matching

Prefer plain `Contains` over regex where possible; `[Trait("Category"` admits
whitespace variants (`[Trait( "Category"`), so a small anchored `Regex` is
justified for that one — mirroring `LogTailCoverageTests`' `LiteralRequest`
pattern. Compile it `RegexOptions.Compiled` as that file does.

---

## The four fixes

`[Trait("Category", "FixtureLogic")]` at class level on:

| File | Facts |
|---|---|
| `tests/Integration.Tests/AuditObservability/AttributionVerdictTests.cs` | 7 |
| `tests/Integration.Tests/AuditObservability/IngestAttributionTests.cs` | 13 |
| `tests/Integration.Tests/AuditObservability/RunModeDriverTests.cs` | 11 |
| `tests/Integration.Tests/EventIngestion/ListEventsTranslationTests.cs` | 3 |

**Why `FixtureLogic` and not a new category** (assumption A1, restated because
it is the one judgement call here): the CI step it selects is *named* "Docker-free
fixture logic tests" (`ci.yml:67`), so in this repository's usage the category
already means "needs no Docker". The name is a stretch for
`AttributionVerdictTests`, which is pure arithmetic and touches no fixture.

**Renaming the category is explicitly not done.** It would touch `ci.yml:72` and
three existing classes for a cosmetic gain — against ADR-0036's smallest-change
rule, and it would put a CI-config edit in a PR whose entire point is that it
changes no gate. If the name is judged wrong, that is a separate, trivial PR.

### Consequence to expect, so it is not read as a surprise

These four classes carry no *excluded* category, so they already run in the
30-minute `integration` job and will continue to. After the change they **also**
run in the cheap `backend` job. That is the intended effect — the verdict is
read earlier — and it duplicates 50 sub-second cases (34 `[Fact]`/`[Theory]`
sites, several carrying `[InlineData]`), which is the same shape as the three
classes already carrying `FixtureLogic`.

---

## Messaging: domain → integration event

**N/A.** No domain event, no integration event, no `Shared.Contracts` change.

## Persistence

**N/A.** No entity, no value object, no migration.

## Latency budget

**N/A.** No leg of constitution §IV is touched. `spec.md` states this and the
reasoning; the census's §IV mapping (`census.md` §2g) is *about* other tests, not
about this change.

---

## What CI does after this

| Job | Before | After |
|---|---|---|
| `backend` → "Docker-free fixture logic tests" (`ci.yml:72`) | 3 classes, 30 cases | **7 classes**, +50 cases (34 sites, 80 total) |
| `backend` → unit + architecture (`coverage-check.ps1`) | — | +1 architecture test |
| `integration` (`ci.yml:179`) | unchanged | unchanged |
| `e2e` | unchanged | unchanged |

**No gate is weakened.** Nothing is deleted, no threshold moves, no analyzer is
narrowed, no suppression is added. The only change to what CI blocks on is
*additive*: one new guard, and 50 cases now also read in a cheaper job.

---

## Alignment check

| Constraint | How this plan meets it |
|---|---|
| ADR-0139 — rules fail the build, not the review | the whole point: four classes documented their own Docker-freedom and nothing read it |
| ADR-0103 — Aspire fixture, no Testcontainers | untouched; the guard reduces reliance on the Docker job rather than adding to it |
| ADR-0036 — smallest change | one file + four attributes; the category rename is deliberately declined |
| ADR-0084 — ≤ 300 LOC/file, ≤ 30 LOC/method | one `[Fact]` plus two helpers; comfortably inside |
| ADR-0065 — coverage gates | unaffected; both projects are outside the covered set |
| ADR-0052 — xUnit + Shouldly | as the neighbouring guards |
| ADR-0144 — no ADR, no weakened gate | none written; nothing weakened |
| Constitution §Testing — new behaviour starts red | the guard is written first and observed failing on 4 classes |

---

## Gate (Phase 2)

Plan aligns with the constitution and ADRs. One judgement call is flagged rather
than buried: **assumption A1**, reusing `FixtureLogic` rather than renaming the
category. If review prefers the rename, it is a one-line change to `ci.yml:72`
plus three existing classes and should be a separate PR.

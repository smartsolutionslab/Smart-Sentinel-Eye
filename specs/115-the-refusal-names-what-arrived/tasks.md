# Tasks — spec 115

**Phase 4a colour: RED** (plan.md Declaration 1).

## Commit shape

**Two commits**, each building on its own — rebase-merge lands them individually
on `develop` (ADR-0087), so a commit that compiles only with its successor breaks
`git bisect` forever.

1. `test(streams): a refusal cannot yet name what arrived` — T002 + T003.
   The red, and the evidence quoted in the PR body (ADR-0139).

   T002 sits in this commit and not the next one, deliberately: the assertions
   cannot be *written* without the fourth command member, and a file that does
   not compile is a broken build, not a red test. T002 introduces the
   **carriage without the diagnosis** — the handler still logs the old single
   hedged message — so the tests compile, run, and fail on the assertion. Same
   resolution spec 074's T002 used for the same problem.

2. `fix(streams): the WHEP refusal names the action that arrived` — T004 + T005.

Conventional Commits (ADR-0030). **No `Co-Authored-By` footer** (ADR-0086),
regardless of any session-level attribution instruction.

---

### T001 — the phase-3 gate — orchestrator

Issue #2105 on Project #13 (feature-level; `/speckit-taskstoissues` is not run).

---

### T002 — the carriage, without the diagnosis (phase 4a, part 1)

**Adds no diagnostic behaviour.** A reviewer who finds two log methods after
this task has found the tasks executed out of order.

**New** `src/StreamDistribution/Domain/Stream/ReportedMediaMtxAction.cs` —
`sealed record : StringValueObject`, private constructor, mirroring
`MediaMtxPath`:

- `MaximumLength = 64` (plan D3).
- `From(string)` — `Ensure.That(value).IsNotNull()` (ADR-0105), then control
  characters → `U+FFFD`, then cap at 64 with a trailing `…` when it bit.
  **Does not reject an empty string** — FR-005.
- `TryFrom(string?)` → `Option<ReportedMediaMtxAction>`; `None` **only** for
  `null`. Unlike `MediaMtxAction.TryFrom`, empty and whitespace are values that
  arrived.

**Edit** `AuthorizeWhepCommand.cs` — fourth positional member
`Option<ReportedMediaMtxAction> ReportedAction`, **no default value**, for the
reason the third member has none.

**Edit** `StreamEndpoints.cs` — `ReportedMediaMtxAction.TryFrom(body.Action)`
beside the existing parse, passed to the command. **Add no branch** — the
endpoint translates, it does not decide.

**Edit** `AuthorizeWhepCommandHandlerTests.cs` — **mechanical only**: every
existing `new AuthorizeWhepCommand(...)` gains a fourth argument. No `ShouldBe`,
no expected status, no test name changes. All existing tests must still pass.

**Done when**: the solution builds and the existing suites are unchanged in
outcome.

### T003 — the refusal cannot yet name what arrived (phase 4a, part 2)

**New** `tests/StreamDistribution.Domain.Tests/Stream/ReportedMediaMtxActionTests.cs`
— beside `MediaMtxActionTests.cs`. Six assertions: missing → `None`; a value that
arrived → `Some` with that text; `""` → `Some` (FR-005); a 200-character value
capped to 64 + `…`; `\r\n` neutralised; `ToString()` is the bounded value.
**Green on arrival — declared here so nobody reads it as evidence.**

**Add to** `AuthorizeWhepCommandHandlerTests.cs`, using the file's existing
`AKioskPersona` and `SomeCamera()` and the `CapturingLogger<T>` fake:

| Test | Asserts | Today |
|---|---|---|
| `Authorize_with_an_action_this_build_does_not_recognise_names_the_value_that_arrived` | still `ActionUnknown`; warning contains `stream` | **RED, evidence** |
| `An_absent_action_and_an_unrecognised_one_are_refused_with_different_messages` | the two messages differ; only the absent one says `absent` | **RED, evidence** |
| `A_value_longer_than_the_cap_is_truncated_in_the_refusal` | warning carries 64 chars + `…`, not the whole 200 | **RED, evidence** |

Each names the fail-closed outcome **before** the message assertion, so the red
is unambiguously the diagnosis and not the refusal.

**Done when**: the three are observed failing with their assertion text captured
verbatim, and no pre-existing test's outcome has moved.

---

### T004 — two messages replace one (phase 4b)

**Brief**: T003's verbatim failure output. **You may not edit those tests.**

**Edit** `Log.cs` — `RefusedUnknownWhepAction` is replaced by
`RefusedUnrecognisedWhepAction(this ILogger, ReportedMediaMtxAction, MediaMtxPath)`
and `RefusedAbsentWhepAction(this ILogger, MediaMtxPath)`. Structured fields
only; parameter order matches placeholder order. Drop the "floating latest tag"
sentence — #2103 pinned the image.

**Edit** `AuthorizeWhepCommandHandler.cs` — deconstruct the fourth member; the
`!action.HasValue` branch picks the message via a private helper, so
`HandleAsync` does not grow (S138, ADR-0084). **The returned error is unchanged**
— `AuthorizeWhepFailures.ActionUnknown()` in both cases.

### T005 — verify

`dotnet build -c Release` clean; the three reds green; every pre-existing
`StreamDistribution` assertion unmoved; `Architecture.Tests` green
(`PrimitiveBoundaryTests`, `HandlerDeconstructionTests`,
`EndpointScopeDeclarationTests`). Note in `verification.md`.

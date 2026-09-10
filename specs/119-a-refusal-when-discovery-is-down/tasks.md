# Tasks — spec 119

**Phase 4a colour: RED** (plan.md Declaration 1).

## Commit shape

**Two commits**, each building on its own — rebase-merge lands them individually
(ADR-0087). Conventional Commits (ADR-0030), **no `Co-Authored-By`** (ADR-0086).

1. `test(streams): a WHEP open when the realm is unreachable` — T002 + T003.
   The red, quoted verbatim in the PR body (ADR-0139).
2. `fix(streams): an unreachable realm refuses instead of throwing` — T004–T007.

T002 carries the seam the assertions need (`WhepAuthFailure` and the widened
`IWhepAuthValidator`), because a test file that does not compile is a broken
build, not a red test — the resolution specs 074 and 115 used for the same
problem. It adds **no** new behaviour: the validator maps every existing refusal
to `TokenRejected`, the handler maps every failure to `Unauthorized`, and
IDX20803 still escapes.

---

### T001 — the phase-3 gate — orchestrator

Issue #2160 on Project #13 (feature-level; `/speckit-taskstoissues` is not run).

### T002 — the seam, without the behaviour (phase 4a, part 1)

`WhepAuthFailure` enum; `IWhepAuthValidator` returns
`Result<WhepAuthSubject, WhepAuthFailure>`; `WhepAuthValidator`, the handler and
`FakeWhepAuthValidator` follow the new signature with the old outcomes.

### T003 — the red (phase 4a, part 2)

- **Handler**: a validator reporting `IdentityProviderUnavailable` yields a
  failure whose `Status` is 401 and whose `Code` is **not** `WHEP_UNAUTHORIZED`.
- **Validator**: a metadata source that throws IDX20803 yields a failure of
  `IdentityProviderUnavailable` (today: the exception escapes `ValidateAsync`).
- **Validator, cancellation**: a metadata source that observes a cancelled token
  makes `ValidateAsync` throw `OperationCanceledException` — a separate test, and
  the one that would catch a fix written as a bare `catch (Exception)`.
- **Control**: the existing accept/refuse pair in `WhepValidatorAudienceTests`
  keeps passing, so the refusal is attributable to the outage and nothing else.

Run them. Capture the output verbatim.

### T004 — `AuthorizeWhepError.IdentityProviderUnavailable` + factory (D2)

### T005 — the validator's narrow catch, cancellation first (D4)

### T006 — the `[LoggerMessage]` (D5, FR-005)

### T007 — the handler maps the two failures (D3)

### T008 — verify

`dotnet build -c Release` clean; the four StreamDistribution suites plus
`ServiceDefaults.Tests` green; the red of T003 quoted going green.

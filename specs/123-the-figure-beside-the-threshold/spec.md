# Spec 123 — The figure beside the threshold

**Issue:** #2149
**Branch:** `docs/2149-the-figure-beside-the-threshold`
**Status:** Phase 3 complete — awaiting gate
**Lane:** autonomous (ADR-0144)
**ADRs:** 0037 (phases), 0144 (lane, 4a colour), 0036 (smallest change),
0139 (red first / refactors stay green), 0103 (Aspire fixture), 0084
(code metrics), 0031 (PR template — the latency-budget section is where
these figures went instead of into the tree), 0052/0053 (xUnit +
Shouldly, test naming).
Constitution §IV (latency budget), §VII (dashboard obligation), §Testing.

## Problem

Sixteen latency and resource budgets are asserted in this suite. **Nine of
them are enforced against a number that appears nowhere in the tree.** The
threshold is written down; the observation it was chosen from is not.

That is not a cosmetic gap. **A threshold with no observation beside it has
an unknowable margin**, and an unknowable margin cannot be told apart from
a guard:

- `WhepHandshakeLatencyTests` sat at roughly a 60× margin for months and
  read exactly like a 3 s SLO.
- `NFR_VariableResolutionLatencyTests` sits at 133×.

Both look like guards until you divide. Nobody divided, because there was
no numerator.

Three of the nine are sharper than the rest, because **each was explicitly
promised by its own spec and the workflow gate passed anyway**:

| Test | Promised at |
|---|---|
| `CommandLatencyTests` (200 ms p95) | `specs/001-register-camera/tasks.md:169` |
| `SignalRRevocationIntegrationTests` (1 000 ms) | `specs/003-layout-composition/plan.md:75` |
| `OverlayPushIntegrationTests` (1 000 ms) | `specs/004-overlay-designer/plan.md:73` |

The figures were produced. They went into **PR bodies** — ADR-0031 makes a
latency-budget section mandatory, and that is exactly where they landed.
A PR body is not in the tree. The record survives only as long as GitHub
does and **is not readable from a clone**, which is the same defect class
as §IV's leg table recording a built leg as unbuilt: a number nobody can
check against the thing it describes.

## What this spec does

**Measures and records. It does not adjust.**

For each budget in scope: run it, record the observed figure beside the
threshold in the source, and state the arithmetic — threshold, observation,
ratio, and *why the margin is the size it is*.

**The template is `ResolvedTextReachesItsFabTests.cs:114-131`**, which
already does this and is the only place in the suite that does it well:

> "Phase 5 measured this exact server-side leg at **555 ms and 758 ms on a
> cold stack**, so 5 s sits roughly 6.6× above the worst figure anyone has
> observed and four times below `FrameWindow`. Both margins are deliberate:
> a bound at `FrameWindow` would assert nothing at all […] and a bound near
> the observed figures […] would flake on a cold stack and be deleted by the
> next person."

Threshold, observation, arithmetic, and the reason — together, next to the
constant.

## Non-goals — stated because they are the way this goes wrong

- **NFR-001 No threshold changes, in either direction.** Not raised to fit
  an observation, not tightened because a margin looks silly. A vacuous
  margin is a **finding to file**, not a constant to edit. Adjusting a
  threshold is a blocked outcome under ADR-0144, and #2141 forbids the
  bulk-fix specifically: tightening a suite at once is how a red CI becomes
  something people route around.
- **NFR-002 No assertion changes.** Nothing this spec touches may alter what
  a test asserts, what it waits for, or how long it waits.
- **NFR-003 A test already red is a finding, not a repair.** Report it; do
  not fix it here.

## Scope

Seven of the nine budgets, in one coherent pass:

| # | Budget | Threshold | Needs the stack |
|---|---|---|---|
| 1 | `WhepHandshakeLatencyTests` | 3 000 ms p95 | yes |
| 2 | `CommandLatencyTests` | 200 ms p95 | yes |
| 3 | `SignalRRevocationIntegrationTests` | 1 000 ms | yes |
| 4 | `OverlayPushIntegrationTests` | 1 000 ms | yes |
| 6 | `NFR002_AuditSearchLatencyTests` | 200 ms p99 / 100 k rows | yes |
| 7 | `NFR002_MqttConnectAuthTests` | 15 ms p50 / 50 ms p99 | yes |
| 8 | `AelInterpreterBenchmarkTests` | 500 ms median / 1 000 ms ceiling | no |
| 9 | `PostgresConnectionBudgetIntegrationTests` | `ServiceCeiling` = 378 | yes |

**Out of scope: #5, `ReconnectReconcileIntegrationTests` (5 s).** The issue
already flags it as *also self-bounded* and files that separately — the
window it measures is the window it waits in, so a recorded figure would
describe the wait, not the reconcile. Recording an observation beside a
constant that cannot be exceeded would dress a non-guard as a guard, which
is the defect this spec exists to expose. It needs the separate issue's fix
first.

## Functional requirements

- **FR-001** Each in-scope threshold constant carries, in the source beside
  it, the observed figure, the date/context of observation, and the ratio.
- **FR-002** Each carries a one-line statement of **what the number is**:
  one of constitution §IV's six event-to-overlay legs, or a **local SLO**.
  Spec 116 established this distinction for
  `LayoutLifecycleIntegrationTests` and its wording is the model — §IV
  budgets an *asynchronous* event path; a synchronous HTTP command budget
  is a local SLO and is not a §IV leg.
- **FR-003** Where a test reports its figure only inside a Shouldly failure
  message, the figure is **also surfaced on success**, so the recorded
  observation can be re-observed rather than merely believed.
  `CommandLatencyTests:49` already does this — *"Surface the measurement in
  test output regardless of pass/fail"* — and it is the reason that test's
  figure was recoverable at all.
- **FR-004** Every figure is observed **at least twice**. The first run
  after machine churn on this box looks exactly like a regression; a spec
  about numbers nobody can check may not itself rest on a single sample.
- **FR-005** A figure taken from a merged PR body rather than observed here
  is labelled **recovered**, with the PR cited.
- **FR-006** Any margin that turns out vacuous is written down as such — in
  `verification.md`, with the arithmetic — and left alone.

## Success criteria

- **SC-001** `dotnet build -c Release` clean (0 warnings, CI treats them as
  errors).
- **SC-002** Every in-scope test passes **with its assertions unmodified**,
  before and after.
- **SC-003** A reader who has only a clone can, for each in-scope budget,
  read the threshold, the observation, and the ratio without leaving the
  file.

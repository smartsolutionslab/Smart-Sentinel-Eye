# Tasks 123 — the figure beside the threshold

Phase 4a colour: **GREEN / characterisation** (plan.md).

- **T001** Capture the seven in-scope tests green **before any edit**, in one
  filtered run against one `AspireFixture` boot, plus the unit-level
  `AelInterpreterBenchmarkTests`. Verbatim output is the phase-4a artifact.
  Any test already red is recorded as a finding and **not fixed** (NFR-003).
- **T002** Surface the measured figure on the success path in each test that
  reports it only inside a Shouldly `customMessage` (FR-003). Output only —
  no assertion, wait or threshold touched.
- **T003** Run the suite again; capture figures (observation 1).
- **T004** Run it a third time; capture figures (observation 2). FR-004.
- **T005** Write the observation, the ratio and the reason beside each
  threshold constant, in the shape of
  `ResolvedTextReachesItsFabTests.cs:114-131` (FR-001).
- **T006** State per budget whether it is one of constitution §IV's six legs
  or a local SLO, using spec 116's formulation (FR-002).
- **T007** Where a figure is recovered from a merged PR body rather than
  observed here, label it recovered and cite the PR (FR-005).
- **T008** `dotnet build -c Release` — 0 warnings (SC-001).
- **T009** `verification.md`: both runs, the arithmetic per budget, and every
  vacuous margin named as a finding to file (FR-006). No threshold edited.

# Tasks 127 — the pair that was never run

Phase 4a colour: **RED** (plan.md). The red belongs to the configuration
half; the measurement half has no natural red and says so.

- **T001** Write `DatabaseCommandLogLevelTests` — bind every
  `src/**/appsettings*.json` that configures `Logging:LogLevel:Default` at
  `Information` or lower, build a `LoggerFactory` from it, and assert
  `Microsoft.EntityFrameworkCore.Database.Command` is **not enabled at
  `Information`** (FR-005, FR-006, FR-007). Run it. **Capture the failure
  verbatim** — it must name the thirteen non-Development files and pass the
  eleven Development ones. That output is the phase-4a artifact.
- **T002** Prove the guard discriminates by counterfactual: set one
  Development file's override to `Information`, observe that file fail
  alone, revert. Recorded in `verification.md`.
- **T003** Write `IngestThroughputTests` — the unpaced fifty-writer drive,
  repeated three times in one boot, reporting each drive's achieved ev/s and
  its position (FR-001, FR-002), both halves of the logging configuration
  with their provenance (FR-004), and the arm read back off the
  `system-variables` log tail (FR-003). `[Trait("Category", "Measurement")]`
  — excluded from CI like its neighbours. Asserts no rate verdict (NFR-005).
- **T004** `dotnet build -c Release` — 0 warnings (SC-001). AppHost stopped
  first (MSB3027).
- **T005** Arm 1 — EF pinned alone: `Default=Debug`,
  `Database.Command=Warning`. One boot, three drives. Record every figure.
- **T006** Arm 2 — **the shipped pair**: `Default=Information`,
  `Database.Command=Warning`. One boot, three drives.
- **T007** Arm 3 — quiet: `Default=Warning`, `Database.Command=Warning`.
  One boot, three drives. This is the calibration against ADR-0135's
  169.8–244.4.
- **T008** Apply the EF override to the thirteen non-Development
  `appsettings.json` (#2135). Nothing else in those files moves.
- **T009** Re-run `DatabaseCommandLogLevelTests` — green, all twenty-four
  files (SC-002).
- **T010** `dotnet build -c Release` again, plus the unit and architecture
  suites (SC-001).
- **T011** `verification.md`: every arm, every drive, unaveraged; where the
  shipped pair falls in the ~103–244 range; whether it clears 100 ev/s and
  by how much; whether the spread swamps the difference (SC-003, SC-004);
  the counterfactual from T002; the file count from T008 (SC-005); and
  **what ADR-0135 should say**, written as a proposal for a human to file
  and not applied (NFR-002).

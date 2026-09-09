# Tasks: The image that defines a contract

**Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md) · **Issue**: #2103
**Phase 3 of ADR-0037.** Feature-level issue only — no per-task issues since spec 028.

**Phase 4a colour**: characterisation (green) for the pin, red-first for the guard —
plan §"Phase 4a colour" states why the issue carries both.

---

- [x] **T001** — Characterisation, observed **green before the change**.
  `tests/Integration.Tests/AppHostMediaMtxImageTests.cs`, a new class carrying
  `[Trait("Category", "FixtureLogic")]` (required by `IntegrationTestSelectionTests`;
  true, because it builds the application model and starts nothing). Asserts, in both the
  run-mode and integration-fixture shapes, that every MediaMTX container resolves to an
  **identical** image reference, and that the expected number of them exists (3 and 2).
  **No assertion names a tag** — the test must pass unmodified after T003.
  *Verify*: run it against the unpinned tree; capture the output verbatim.

- [x] **T002** — The guard, observed **red before the change**.
  `tests/Architecture.Tests/ContainerImagePinTests.cs`. Three assertions: no floating
  literal tag in `AppHost.cs`; every `bluenviron/mediamtx` `AddContainer` names one tag,
  ≥ 3 sites found; every `bluenviron/mediamtx:<tag>` under `scripts/` names that same tag.
  Each carries a scan-found-something assertion. The guard bans floating tags as a
  category and **never asserts `1.21.0-ffmpeg`**, so a future bump does not edit it.
  *Verify*: run against the unpinned tree; the failure names the three floating sites.

- [x] **T003** — The pin. `src/AppHost/AppHost.cs:134`, `:177`, `:571` —
  `"latest-ffmpeg"` → `"1.21.0-ffmpeg"`, with the manifest digest and the reason recorded
  once, at the `mediamtx` site. Update the `fixture-video` comment, which names the tag in
  prose as part of its "no image pull at all" cost argument and would otherwise be stale
  the moment the literal above it changes.
  *Verify*: `dotnet build -c Release` clean; T001 green **unmodified**; T002 green.

- [x] **T004** — The fourth reference. `scripts/generate-sim-clips.sh:99` and the prose in
  `src/AppHost/Resources/README.md` that names the image the clips came from. Not a
  container, and not in the issue's list of three — included because FR-003 asks for one
  MediaMTX version in the repository rather than nearly one, and because T002's third
  assertion would otherwise have nothing to check.
  *Verify*: `grep -r latest-ffmpeg src/ scripts/` returns nothing outside `specs/`.

- [x] **T005** — The counterfactual. Revert one site to `latest-ffmpeg` in the working
  tree, run the guard, capture the failure naming that site, restore. Proves the guard can
  fail for the reason it claims. Five assertions in this session's earlier work turned out
  to be unable to fail; this task exists so this one is not the sixth.

- [x] **T006** — Boot the pinned stack. `docker rm` the three MediaMTX containers
  (**no volumes** — none of the three has one, and dropping `postgres-data` or
  `keycloak-data` costs a stack rebuild), boot, and observe the WHEP authorization hook —
  behaviour 1 of the four, and the one the issue is most concerned with.
  *Verify*: `verification.md`, phase 5.

- [x] **T007** — `verification.md`: the before/after outputs, the counterfactual, the
  build, and an explicit list of what was **not** verified.

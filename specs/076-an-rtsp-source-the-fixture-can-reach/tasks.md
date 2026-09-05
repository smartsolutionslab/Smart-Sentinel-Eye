# Tasks — Spec 076, an RTSP source the fixture can reach

**Phase:** 3 (Tasks) · **Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #198

**Engineer: `infra-engineer`.** The load-bearing change is one gate expression
and one comment in `src/AppHost/AppHost.cs`, plus a resource-readiness wait in
the Aspire fixture. That is composition-root and container-network work — the
failure mode to reason about is "can two DCP containers resolve each other under
`E2ETests=true`", not a C# design question. No domain, application, contract,
migration, endpoint, or frontend code is touched, so `backend-engineer` would be
reviewing an Aspire model it does not own. `test-writer` owns T001–T005 per
ADR-0144's phase-4 split; the engineer receives the verbatim red output as its
brief and **may not edit the tests to pass**.

**Phase 4a colour: red — and the red is real, which is worth stating because a
test helper is the case where it often is not.**

A helper cannot be observed failing before it exists. But this spec's helper is
one constant and one wait; the *behaviour* under test is a camera reaching
`Healthy`, and that is assertable against **unmodified `src/`** today. Written
first, T004 fails with `Degraded` where `Healthy` was expected — because
`fixture-video` is absent under `E2ETests=true` and `fixture-video` does not
resolve, so the SFU's dial fails exactly as `10.0.6.1` does. **That failure
message is the evidence, and it is the whole point of the ordering below: the
test must be seen distinguishing a reachable source from an unreachable one
before the source is made reachable.** T006's guard is a second genuine red —
`ShouldContain("fixture-video")` against today's gate fails on a model built in
milliseconds, no Docker required.

One test in the new file is **green from the start and must be**: AS-2's
`Degraded` case is characterisation of existing behaviour, and it is what proves
T004's later green came from the source rather than from the watcher reporting
`Healthy` for everything. A green there is not a phase-4 failure; a red there
is, and means the baseline moved.

**Docker is required** for T004, T005, T008 and T009 (`tests/Integration.Tests`,
`AspireCollection`). T006 and T007 are build-model only —
`[Trait("Category","FixtureLogic")]`, no containers. Check `C:` free space
before booting; below 6 GB, stop and report.

**Parallelism.** T001–T005 are strictly serial (T002–T005 all edit the same new
file, so ADR-0109's disjoint-file condition fails). T006 is `[P]` against them —
a different file, no Docker, no shared state. T008 and T009 are a real fan-out
after T007.

**No ADR, no constitution amendment, no `CLAUDE.md` edit, no board mutation.**
ADR-0103, ADR-0024 and ADR-0111 already decide everything here (ADR-0144); the
plan's *Why this is not an ADR* carries the reasoning. Projects v2 is
rate-limited and the human handles the board.

---

## Foundational — blocks everything

- **[T001] [US-1]** Add a public constant to
  `tests/Integration.Tests/Fixtures/AspireFixture.cs`:
  `RtspTestSourceUrl = "rtsp://fixture-video:8554/loop"`, in that partial beside
  the other fixture-level members — **not** in `AspireFixture.Auth.cs`, which
  holds `AdminUsername`/`AdminPassword` and is not touched by this spec. The URL
  names a resource, not a credential. Doc-comment it with: which resource
  serves it, that the host is a **container-network DNS name** unreachable from
  the test process itself, and that the analogous form is
  `SimulatorOptions.RtspHost`. **Do not add the readiness wait yet** — under
  today's gate `fixture-video` is absent and a `WaitForResourceAsync` on it
  would hang the whole fixture, converting T004's honest red into a boot
  timeout.
  *Depends on: nothing. Blocks: T002–T005.*

  **Done when:** `tests/Integration.Tests` compiles and the constant is the only
  spelling of that URL in the repository (`grep -rn "fixture-video:8554" tests/`
  returns one line).

---

## US-1 (P1) — a test can point a camera at a source that answers

- **[T002] [US-1]** Create
  `tests/Integration.Tests/StreamDistribution/RtspTestSourceHealthTests.cs`:
  `[Collection(AspireCollection.Name)]`, `IAsyncLifetime` with the three
  existing resets (`ResetMediaMtxAsync`, `ResetStreamDistributionAsync`,
  `ResetCameraCatalogAsync`), and a class doc naming spec 076, issue #198, and
  the *reason both cases live in one file* (the `Degraded` case is what gives
  the `Healthy` case its meaning). Mirror
  `ListStreamsByCamerasIntegrationTests`'s shape — same `RegisterAsync` helper
  form, same `CreateAdminClientAsync` calls.
  *Depends on: T001. Blocks: T003.*

  **Done when:** the file compiles with no `[Fact]` yet.

- **[T003] [US-1]** Add the shared poll helper: `WaitForStateAsync(streamClient,
  camera, expectedState, timeout)` polling
  `GET /streams?cameraIdentifiers={camera}` every 500 ms, returning the observed
  state on match and throwing a `TimeoutException` naming **the last state
  actually seen** on expiry. The last-state-seen detail is not decoration — a
  timeout that says only "did not reach Healthy" cannot distinguish "stayed
  Provisioning" (the path was never created) from "went Degraded" (the source
  was unreachable), and those have different causes.
  *Depends on: T002. Blocks: T004, T005.*

  **Done when:** the helper compiles; the timeout message includes the last
  observed state.

- **[T004] [US-1]** Add **AS-1**:
  `A_camera_pointed_at_the_fixture_source_reaches_Healthy`. Register one camera
  at `AspireFixture.RtspTestSourceUrl`; assert `WaitForStateAsync(...,
  "Healthy", 30s)`. Budget 30 s per spec A3, matching
  `ListStreamsByCamerasIntegrationTests.SettleTimeout`.
  *Depends on: T003. Blocks: T007.*

  **Done when — RED, and the failure is quoted verbatim in the PR body:** run
  against unmodified `src/`; the test fails with a `TimeoutException` whose
  message names `Degraded` as the last observed state. **A red that names
  `Provisioning` instead means the stream was never provisioned** — a different
  defect, and the engineer must report it rather than proceed. A green here
  before T007 means the URL resolved somehow and assumption A1 needs re-reading,
  not celebrating.

- **[T005] [US-1]** Add **AS-2**:
  `A_camera_at_an_unreachable_address_reaches_Degraded`. Register at
  `rtsp://10.0.6.1/h264`; assert `Degraded` within 30 s.
  *Depends on: T003. Blocks: nothing.*

  **Done when — GREEN, before any `src/` change, and stated as such in the PR.**
  This is the characterisation half. It must still pass **unmodified** after
  T007; an assertion that has to be edited is evidence the watcher's behaviour
  moved, which is a block, not an adjustment.

- **[T006] [P] [US-1]** In `tests/Integration.Tests/AppHostE2ESwitchTests.cs`,
  add `names.ShouldContain("fixture-video")` to
  `E2ETests_argument_excludes_the_dev_only_resources`, with a message naming
  spec 076 and #198. Correct the class-level doc: the clause "`E2ETests` means
  'this is the integration fixture' and also removes the Vite apps and
  `fixture-video`" becomes the Vite apps alone, plus one sentence on why
  `fixture-video` now spans both lanes. Leave
  `The_simulator_argument_leaves_the_web_apps_and_the_fixture_video_in_place`
  untouched — it still holds and is still the standing proof the two gates are
  separate.
  *Depends on: nothing (disjoint file, no Docker). Blocks: T007.*

  **Done when — RED, quoted:** `dotnet test tests/Integration.Tests --filter
  "FullyQualifiedName~AppHostE2ESwitchTests"` fails on the new `ShouldContain`.
  Seconds, no containers.

---

## The change — after both reds are recorded

- **[T007] [US-1]** In `src/AppHost/AppHost.cs`:
  1. Change the `fixture-video` gate at line 163 from `isRunMode && !isE2ETests`
     to `isRunMode`. **Nothing else in that block changes** — same image, same
     tag, same two bind mounts, same endpoint.
  2. Rewrite the block comment at lines 143–162 per `plan.md` §2: keep spec
     056's rationale, keep the explicit "deliberately NOT `camera-sim`'s gate"
     paragraph and the guard-test name it cites, and replace the closing "an
     integration run has no browser … it never reads" with what is now true —
     the integration lane reads it, spec 076 / #198 is why, and the cost is one
     container start plus a `-c copy` FFmpeg loop.
  3. Add the readiness wait to `AspireFixture.InitializeAsync`, beside the
     existing `WaitForResourceAsync("mediamtx", …)`:
     `await _app.ResourceNotifications.WaitForResourceAsync("fixture-video",
     KnownResourceStates.Running, cts.Token)`. **No `WaitOnResourceUnavailable`**
     — this is a first-boot wait, not a transition wait; `plan.md` §3 carries the
     reasoning, and a one-line comment should too. **No HTTP probe**:
     `fixture-video.yml` declares no `api:` block, so there is nothing to poll.

  **Do not touch** `camera-sim`'s gate, the Vite apps' gates,
  `fixture-video.yml`, `mediamtx.yml`, or `camera-sim.yml`.
  *Depends on: T004 and T006 both observed red. Blocks: T008, T009.*

  **Done when:** `dotnet build -c Release` is clean; T004, T005 and T006 all
  pass; T005 passes **with its assertions unmodified**.

---

## US-2 (P2) — the badge test asserts a mix

- **[T008] [P] [US-2]** In
  `tests/Integration.Tests/StreamDistribution/ListStreamsByCamerasIntegrationTests.cs`,
  register the five cameras as three at `AspireFixture.RtspTestSourceUrl` and
  two at unreachable addresses; assert three `Healthy` and two `Degraded`
  (AS-3). Delete the doc sentence "Healthy-state assertions require an RTSP test
  source and are deferred to the polish phase (T086)" and say what the test now
  covers. Strengthen `WaitForBatchSettledAsync` to wait until every camera
  reports the state *expected of it*, rather than merely having left
  `Provisioning`.
  *Depends on: T007. Disjoint file from T009.*

  **Correction (phase 6).** This task originally read: *"Keep
  `WaitForBatchSettledAsync`'s non-`Provisioning` gate as is — it is
  state-agnostic and still correct."* **That instruction was wrong, the
  implementation deviated from it, and review adjudicated the deviation
  right.** `ReportStreamHealthCommandHandler.cs:37-44` calls `ReportDegraded`
  unconditionally when `!observation.IsReady`, and `Stream.cs:229-239` permits
  `Provisioning → Degraded`, so a camera pointed at a source that answers is
  legitimately `Degraded` for up to one poll interval before it reaches
  `Healthy`. The old gate therefore releases the wait while the Healthy-to-be
  rows are still `Degraded`, and the assertion fails on timing rather than on
  behaviour. The shipped condition is strictly stronger than the old one —
  every expected state here is a non-`Provisioning` state — so it cannot
  release earlier than the gate it replaces.

  **The transient was never observed.** Across three runs the reachable
  cameras reached `Healthy` in ~1 s. The justification above is code-reading,
  not measurement; the change closes a race the source permits rather than one
  anybody has watched happen.

  **Done when:** the test passes, and its doc no longer claims a deferral that
  has been discharged.

- **[T009] [P] [US-2]** Full integration run: `dotnet test
  tests/Integration.Tests`. One boot, whole suite. Confirms the second container
  did not disturb the shared fixture — in particular that
  `ResetMediaMtxAsync` still clears `cam-*` paths between classes, and that
  nothing else regressed on stack start-up time.
  *Depends on: T007. Disjoint from T008.*

  **Done when:** the suite is green, and the boot duration is recorded for
  comparison against a pre-change run.

---

## Phase 5 hand-off (not tasks — what `/verify` must observe)

Green tests are not the gate; `spec.md` §*Independent end-to-end test procedure*
is. Three figures phase 5 must actually read, not infer:

1. **`docker ps`** during the run shows `fixture-video` beside `mediamtx`.
2. **`docker stats`** on `fixture-video` — assumption **A2**. A `-c copy` loop is
   *claimed* negligible and has never been measured. If it is not, the remedy is
   a `runOnDemand` path, and that is a follow-up issue, not an in-flight edit.
3. **Wall clock from registration to `Healthy`** — assumption **A3**. The
   assertion budget is 30 s; the issue expected ~15 s. Record the real figure so
   a later flake can be told from a later regression.

A fourth, local only: `ffplay rtsp://localhost:<fixture-video-rtsp>/loop` shows
moving pictures. CI has no equivalent and does not need one.

**Latency budget: N/A** (`spec.md`). No leg of constitution §IV is touched and
no §VII dashboard claim is made.

## Files phase 4 touches

| File | Task | Docker |
|---|---|---|
| `src/AppHost/AppHost.cs` | T007 | — |
| `tests/Integration.Tests/Fixtures/AspireFixture.cs` | T001, T007 | — |
| `tests/Integration.Tests/AppHostE2ESwitchTests.cs` | T006 | no |
| `tests/Integration.Tests/StreamDistribution/RtspTestSourceHealthTests.cs` *(new)* | T002–T005 | yes |
| `tests/Integration.Tests/StreamDistribution/ListStreamsByCamerasIntegrationTests.cs` | T008 | yes |

Five files, one new. No `src/*/Domain`, `Application`, `Infrastructure`, `Api`,
`Shared.Contracts`, migration, `apps/`, `e2e/`, `deploy/`, or `.github/`.

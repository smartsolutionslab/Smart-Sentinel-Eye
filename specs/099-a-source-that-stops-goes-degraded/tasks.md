# Tasks 099 — A source that stops goes Degraded

**Phase:** 3 (Tasks) · **Date:** 2026-09-08 · **Issue:** #119
**Plan:** `plan.md` · **Spec:** `spec.md`

Format: `[ID] [P?] [Story]`. `[P]` means the task owns a disjoint file set and
can run beside another `[P]` task (ADR-0109).

**No per-task GitHub issues.** Phase 3 stopped creating them after spec 028; the
feature-level issue #119 is the tracked artifact and must be on Project #13.

**Phase 4a colour: characterisation, observed green, plus a mandatory
counterfactual (T005).** Green on arrival is expected and is *not* sufficient
evidence on its own — see `spec.md` §What red is available.

---

## Foundational

**None.** This slice adds no Shared.Kernel / Shared.Contracts type, no Aspire
resource and no AppHost wiring, so it blocks nothing and nothing blocks it. It
can be fanned out beside any other in-flight slice.

---

## US1 — a camera that goes dark is noticed, and a camera that returns is noticed

### T001 — [US1] Add the SFU repoint helper to the fixture

`tests/Integration.Tests/Fixtures/AspireFixture.Db.cs`.

One public method beside `ResetMediaMtxAsync`:

- `RepointMediaMtxPathAsync(string pathName, string rtspSourceUrl, CancellationToken)`
- `PATCH /v3/config/paths/patch/{pathName}` with `{ source = rtspSourceUrl }`,
  sent through the existing `SendMediaMtxWithRetryAsync` (`:265`) so it inherits
  the #964 CI-flake retry rather than growing a second one.
- Throws on a non-success status. It is a test *arrangement*: a silent failure
  here would turn AS-1 into a test that observes nothing.

Nothing else in the file changes. Depends on: nothing.

### T002 — [US1] Confirm or refute assumption A1 against the running SFU

Boot the fixture stack once by hand (or via a scratch test) and, for a camera
already `Healthy`:

1. `GET /v3/paths/get/cam-{guid}` — record `ready`.
2. `PATCH /v3/config/paths/patch/cam-{guid}` with the unreachable source.
3. Poll `GET /v3/paths/get/cam-{guid}` — record how long until `ready` is false,
   and the observed value.
4. Patch back; record how long until `ready` is true again.

**Record the two elapsed times in the verification note.** They are the only
evidence that 15 s is the right number rather than an inherited guess.

If `ready` never clears, switch to `plan.md` §Fallback (delete + re-add) and say
so in the test's doc-comment. Depends on: T001.

### T003 — [US1] Write `StreamHealthTransitionTests`

`tests/Integration.Tests/StreamDistribution/StreamHealthTransitionTests.cs` — new.

- `[Collection(AspireCollection.Name)]`, **no `[Trait]`** (see `plan.md` §Docker).
- `InitializeAsync` mirrors `RtspTestSourceHealthTests.cs:47-52`:
  `ResetMediaMtxAsync`, `ResetStreamDistributionAsync`, `ResetCameraCatalogAsync`.
- Two constants: `SettleTimeout = 30 s` (reaching a first state — matches
  `RtspTestSourceHealthTests.cs:38`) and `TransitionTimeout = 15 s` (the budget
  the issue's names claim).
- Two facts:
  - `Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds`
  - `Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds`
- Each registers its own camera at `AspireFixture.RtspTestSourceUrl`, waits for
  `Healthy` inside `SettleTimeout`, then repoints and asserts inside
  `TransitionTimeout`.
- Repoint-away is wrapped in `try` / `finally`; the `finally` repoints back.
- Reuse the polling shape of `RtspTestSourceHealthTests.WaitForStateAsync`
  (`:122-163`) — including the timeout message that names **the last state
  actually observed**, which is what distinguishes "stayed Healthy" (the latch
  this issue exists to catch) from "went Provisioning" (a different defect).

**Test names are the issue's, with `StreamHealthTransitionTests` as the class
rather than the issue's `StreamHealthIntegrationTests`** — that name has never
existed in this repository and naming a file after a class the audit itself
disproved would re-import the fiction. Recorded here so the divergence is
deliberate.

Depends on: T001, T002.

### T004 — [US1] Run the two tests green on an unmodified tree

`dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~StreamHealthTransition"`.

Capture the **verbatim** output. Also re-run `RtspTestSourceHealthTests` in the
same session to show the new class did not disturb it. Depends on: T003.

### T005 — [US1] The counterfactual, and its verbatim output

The evidence that replaces a red. In the working tree only, never committed:

1. Mutate `ReportStreamHealthCommandHandler.HandleAsync`
   (`src/StreamDistribution/Application/Commands/Handlers/ReportStreamHealthCommandHandler.cs`)
   to `return Success(stream.State);` immediately when
   `stream.State == StreamState.Healthy` — the "latch `Healthy` forever" watcher
   the issue describes.
2. Run the StreamDistribution integration tests.
3. **Expected:** `Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds`
   fails; both `RtspTestSourceHealthTests` cases still pass. That is the issue's
   claim, demonstrated.
4. `git checkout -- ` the handler. Confirm the tree is clean and re-run green.

Quote steps 2 and 4's output in the PR body. **If the new test stays green under
the mutation, it asserts nothing — block, do not ship.**

Optionally repeat with `Stream.ReportHealthy` made a no-op from `Degraded`, to
give the recovery test its own counterfactual.

Depends on: T004.

### T006 — [P] [US1] Verification note

`specs/099-a-source-that-stops-goes-degraded/verification.md` — the two elapsed
times from T002, the T004 output, the T005 counterfactual output, and the CI job
that ran it. `[P]`: owns a file nothing else writes.

Depends on: T005.

---

## Dependency summary

```
T001 → T002 → T003 → T004 → T005 → T006 [P]
```

Strictly serial apart from T006. The slice is small enough that its parallelism
is *against other slices*, not within itself — it touches no ADR-0109 contention
file, so it may be fanned out beside anything.

---

## Gate for phase 3

Issue #119 is on Project #13:

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/119
```

Verify with `--limit 2000`; `item-list` defaults to 30.

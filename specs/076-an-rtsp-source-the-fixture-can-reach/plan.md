# Plan — Spec 076, an RTSP source the fixture can reach

**Phase:** 2 (Plan) · **Spec:** `spec.md` · **Issue:** #198
**ADRs:** ADR-0103, ADR-0024, ADR-0111, ADR-0011, ADR-0052, ADR-0109, ADR-0144.

## Bounded context and layers

**None.** This spec touches no bounded context. There is no domain model, no
value object, no aggregate, no command, no query, no handler, no message, no
migration, and no endpoint.

That is the plan's most important sentence, so it is worth being explicit about
what it rules out. `StreamDistribution`'s `Stream` aggregate, `StreamState`,
`StreamHealthWatcher`, `MediaMtxRtspGateway` and `GET /streams` are all **read
from and none of them modified**. The `Provisioning → Healthy` edge already
exists and is already unit-tested; what does not exist is any *integration*
observation of it. This spec builds the conditions for that observation.

The work lives in exactly two places:

| Layer | File | Change |
|---|---|---|
| Composition root | `src/AppHost/AppHost.cs` | one gate, one comment |
| Test infrastructure | `tests/Integration.Tests/**` | one constant, one wait, one guard assertion, two tests |

## The change, in full

### 1. The gate

`AppHost.cs:163` reads `if (isRunMode && !isE2ETests)` for the `fixture-video`
block. It becomes `if (isRunMode)`.

`camera-sim`'s gate at `AppHost.cs:549` — `isRunMode && !isE2ETests &&
isScenarioSimulatorEnabled` — is **not touched**. Keeping the two blocks
distinct is the standing point of
`The_simulator_argument_leaves_the_web_apps_and_the_fixture_video_in_place`, and
the same reasoning applies in reverse here: `fixture-video` gains a lane,
`camera-sim` does not.

The Vite apps (`management-web`, `kiosk-web`, `kiosk-wall`) keep their
`!isE2ETests` gates. An integration run has no browser and this spec does not
give it one.

### 2. The comment

The block comment at `AppHost.cs:143–162` currently argues for the exclusion.
Three of its paragraphs stay true and one inverts. The rewrite must:

- keep the spec 056 rationale (a wall check that never sees a picture);
- keep the explicit statement that this is **not** `camera-sim`'s gate, naming
  the guard test that enforces the separation;
- replace the closing "an integration run has no browser … it never reads" with
  what is now true: the integration lane reads it, spec 076 and #198 are why,
  and the cost is one container start plus a `-c copy` FFmpeg loop;
- cite spec 076 alongside spec 056, so the next reader finds both reasons.

A comment that survives a change unedited is how §IV recorded a built leg as
unbuilt. This one gets edited.

### 3. The fixture

`tests/Integration.Tests/Fixtures/AspireFixture.cs`:

- A public constant naming the URL. `RtspTestSourceUrl =
  "rtsp://fixture-video:8554/loop"`. One spelling, one place. A test that
  hard-codes the string instead is a second spelling that drifts.
- A readiness wait in `InitializeAsync`, beside the existing
  `WaitForResourceAsync("mediamtx", …)` calls:
  `WaitForResourceAsync("fixture-video", KnownResourceStates.Running, …)`.

**On `WaitOnResourceUnavailable`:** it is *not* used here, and the reason is
worth writing down rather than discovering twice. It matters when waiting across
a resource *transition* — a restart, where the default gives up on the very
state change it should watch. This is a first-boot wait on a resource that
starts once and does not restart, identical in shape to the nine
`WaitForResourceAsync` calls already in `InitializeAsync`. Matching them is
right; diverging would be cargo cult.

**No HTTP readiness probe**, unlike `WaitForMediaMtxAsync`. `fixture-video.yml`
declares no `api:` block, so it has no control API to poll — there is nothing to
probe short of opening an RTSP session, which the tests do transitively anyway.
`Running` plus the test's own 30 s `Healthy` poll absorbs FFmpeg's start. Adding
`api: yes` to `fixture-video.yml` to gain a probe would widen a resource the
spec's non-goals keep closed.

### 4. The guard

`tests/Integration.Tests/AppHostE2ESwitchTests.cs`:

- `E2ETests_argument_excludes_the_dev_only_resources` gains
  `names.ShouldContain("fixture-video")`. The test then covers both sides of the
  switch it is named for.
- Its class-level doc says `E2ETests` "also removes the Vite apps and
  `fixture-video`". That clause is corrected to the Vite apps alone, with a
  sentence on why `fixture-video` moved.

This is a **build-model** test — it constructs the AppHost model and reads
resource names. It needs no Docker and carries `[Trait("Category",
"FixtureLogic")]`, so it stays in the fast lane.

### 5. The tests

`tests/Integration.Tests/StreamDistribution/RtspTestSourceHealthTests.cs` (new)
— AS-1 and AS-2 as one class. Both cases in one file because AS-2 is the
contrast that makes AS-1 meaningful; splitting them across files invites one to
be deleted without the other.

`ListStreamsByCamerasIntegrationTests.cs` — US-2 (AS-3). Three cameras at the
fixture URL, two unreachable; assert the mix. Its doc comment's "Healthy-state
assertions … are deferred to the polish phase (T086)" is deleted, because it
stops being true.

## Entities, value objects, invariants

**None introduced.** The invariants this spec *relies on* and must not disturb:

| Invariant | Where it lives | This spec's relation |
|---|---|---|
| `Provisioning → Healthy` requires a decoded frame | `Stream.ReportHealthy`, `StreamState` | observed, not changed |
| `Degraded → Offline` only after 5 min | `StreamHealthWatcher.OfflineAfter` | irrelevant at 30 s budgets |
| `RtspUrl` carries no userinfo | `RtspUrl.From` | the fixture URL complies |
| a retired stream never returns to `Healthy` | `Stream.EnsureNotRetired` | untouched |

## Messaging: domain event → integration event

**No new message.** The flow the tests observe already exists end to end:

```
POST /cameras
  → CameraRegisteredDomainEvent
  → CameraRegisteredV1                    (Shared.Contracts, RabbitMQ)
  → StreamDistribution provisions a Stream
  → MediaMtxRtspGateway.AddPathAsync(path, rtspSourceUrl)
  → MediaMTX dials rtsp://fixture-video:8554/loop     ← the only new edge
  → StreamHealthWatcher polls /v3/paths/get/{path}    (2 s)
  → ReportStreamHealthCommand → Stream.ReportHealthy
  → StreamHealthChangedDomainEvent → StreamHealthChangedV1
  → GET /streams reports "Healthy"
```

The one new edge is a **TCP connection between two containers**, not a message.
Nothing is added to `Shared.Contracts`.

## Boundary rules

- **No cross-context project reference is added.** No `src/*/` project file
  changes at all; `src/AppHost` is the composition root and is exempt by
  ADR-0024.
- **No new `Shared.Contracts` type**, so the NetArchTest boundary suite has
  nothing new to judge.
- **ADR-0103's boundary is the one under real pressure**, and the design is
  shaped by it: the issue asked for a container started *by a test*, which is
  Testcontainers by hand. Declaring the resource in the AppHost keeps
  integration infrastructure "exclusively" AppHost-sourced, as the ADR requires.
  This is why `StartRtspTestSourceAsync()` is **not** implemented — not an
  omission, a boundary.

## Why this is not an ADR

ADR-0103 already decided that integration infrastructure comes from the AppHost.
ADR-0024 already decided the AppHost is the composition root. ADR-0111 already
established one MediaMTX pulling RTSP from another by container DNS. Spec 056
already decided that `fixture-video` is the repository's test video source, with
its config and its rationale.

Every decision this plan needs is already made. **Changing which lanes an
existing resource appears in is implementation of ADR-0024, not a new
architectural choice** — the same category as spec 056 adding the resource and
#2013 adding `camera-sim`'s third conjunct, neither of which wrote an ADR.

**The run is therefore not blocked** under ADR-0144.

Two adjacent things *would* need one, and neither is done here: adopting
Testcontainers (ADR-0103 would have to be superseded), and pinning the MediaMTX
tags (#2103, which is a decision about supply-chain risk across three
resources).

## Risks and how each is caught

| Risk | Caught by | If it fires |
|---|---|---|
| **A1** — containers cannot resolve each other under `E2ETests=true` | T007's first red run | Fall back to Aspire's resolved endpoint via `WithReference`, or `MTX_*` env injection as `AppHost.cs:309` already does for the auth hook. **Report before improvising** |
| CI runner cannot sustain a second MediaMTX | the integration job | Assumption A2's remedy: a `runOnDemand` path |
| The 30 s budget is too tight on a cold CI runner | T007/T008 flaking | Widen the budget with the measured figure, never by deleting the assertion |
| Someone later folds `fixture-video` back under `!isE2ETests` | AS-4's new `ShouldContain` | The guard now covers both directions |
| `-c copy` loop burns CI CPU for the whole run | phase 5's `docker stats` | A2's remedy |

## Sequencing

**Foundational, blocks everything:** the AppHost gate. Until `fixture-video`
runs under `E2ETests=true` there is nothing for a test to reach, so T007 and
T008 cannot even fail honestly — they would fail for the wrong reason. The
fixture constant and wait are the same tranche.

**Fans out after:** the two integration tests are disjoint files with no shared
state, and the guard edit is a third disjoint file needing no Docker. ADR-0109's
condition holds for those three.

`tasks.md` carries the ordering and the `[P]` markers.

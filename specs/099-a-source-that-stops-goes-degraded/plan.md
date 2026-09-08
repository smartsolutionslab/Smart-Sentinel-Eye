# Plan 099 — A source that stops goes Degraded

**Phase:** 2 (Plan) · **Date:** 2026-09-08 · **Spec:** `spec.md`
**Depends on:** ADR-0103, ADR-0109, ADR-0143, ADR-0144, ADR-0011.

---

## Shape of the change

**Tests only.** No bounded context gains code; no layer changes; no entity,
value object, invariant, domain event or integration event is added or altered.
StreamDistribution's `Stream` aggregate, `StreamHealthWatcher`,
`ReportStreamHealthCommandHandler` and `MediaMtxRtspGateway` are all read, none
are written.

Boundary rules are trivially satisfied: the only new file lives in
`tests/Integration.Tests`, which already references every context.

## Files

| File | Action |
|---|---|
| `tests/Integration.Tests/StreamDistribution/StreamHealthTransitionTests.cs` | **new** — the two tests |
| `tests/Integration.Tests/Fixtures/AspireFixture.Db.cs` | **edit** — one public helper, `RepointMediaMtxPathAsync` |

Nothing else. In particular **`src/AppHost/AppHost.cs` is not touched**, and
neither is `AspireFixture.cs`.

### ADR-0109 contention

ADR-0109's contention list is `src/Shared.Kernel/*`, `src/Shared.Contracts/*`,
`src/AppHost/AppHost.cs`, `apps/shared/*`, `e2e/support/*`, `.github/workflows/ci.yml`,
`Directory.Packages.props`, `global.json`. **This slice touches none of them.**

`AspireFixture.Db.cs` is not on that list, but it is a shared fixture partial, so
it is the one file another in-flight slice could plausibly also edit. The edit is
an *additive* method at the end of the MediaMTX region — the smallest shape that
still reuses `SendMediaMtxWithRetryAsync`. If a rebase conflicts there, the
resolution is to keep both methods.

This slice is therefore **fully parallel-safe** and can be fanned out beside any
other. It has no foundational task blocking anything else.

## Mechanism: how the outage is provoked

**Chosen: `PATCH /v3/config/paths/patch/cam-{camera}` on the SFU, changing
`source` to an unreachable RTSP address.**

Verified, not assumed:

1. **The endpoint exists and is production-exercised.**
   `MediaMtxRtspGateway.RepointPathAsync`
   (`src/StreamDistribution/Infrastructure/Gateways/MediaMtxRtspGateway.cs:38-52`)
   issues exactly this call, and `RepointStreamCommandHandler.cs:69` drives it in
   production. The test uses an API the product already depends on.
2. **The SFU's control API is reachable from the test process.**
   `AspireFixture.Db.cs:216` already opens `App.CreateHttpClient("mediamtx", "api")`
   and both lists (`GET /v3/config/paths/list`) and deletes
   (`DELETE /v3/config/paths/delete/{name}`) paths on it, before every test.
   `AspireFixture.App` is public (`AspireFixture.cs:173`).
3. **The path name is derivable in-test**, with no extra call: `MediaMtxPath.For`
   is `cam-{camera.Value}` (`MediaMtxPath.cs:19`), canonical form asserted at `:38`.
4. **Nothing heals it behind our back.** `MediaMtxReconciler` runs one pass in
   `StartAsync` (`:36-40`) and reconciles path *presence*, not source drift.
5. **`fixture-video` is untouched.** Its own server keeps looping; only the SFU
   stops dialling it for one path.

### Why not the alternatives

- **Stop the `fixture-video` container through the Aspire resource command API.**
  Rejected on evidence, and this is the decisive one. `ci.yml:165-170` records
  that *"restarting a resource through Aspire fails outright on the runner
  ('Failed to stop resource')"*, which is why every such test carries
  `[Trait("Category", "Disruptive")]` —
  `MqttResubscribeAfterBrokerOutageIntegrationTests.cs:96`,
  `OutboxSurvivesAKillTests.cs:35`, `RestartLosesNothingIntegrationTests.cs:46`,
  `LogTailDeliversIntegrationTests.cs:167` — and `ci.yml:179` **excludes that
  category from CI**. Taking that route would deliver a test CI never runs, which
  ADR-0144 forbids ("a `[Trait]` that quietly excludes this from CI"). It would
  also break the video source for the whole shared collection.
- **Repoint the camera through StreamDistribution's own `PATCH /streams` API.**
  Rejected as unfaithful. It changes the system's *record* of the camera, so a
  watcher that reported health from the last-configured URL would still pass. The
  scenario is a camera going dark **while the configuration is unchanged**.
- **Patch `fixture-video`'s path.** Impossible: `fixture-video.yml` declares no
  `api:` block, stated in that file and repeated at `AspireFixture.cs:299`.

### Fallback, if assumption A1 is refuted

If T002 finds that a patched path keeps `ready: true` (MediaMTX not tearing down
the live source on config change), switch to
**`DELETE /v3/config/paths/delete/cam-{camera}`**. `GetPathHealthAsync` maps 404
to `IsReady: false, LastError: "path not registered"`
(`MediaMtxRtspGateway.cs:107-114`), so `Degraded` is guaranteed; restoration is
`POST /v3/config/paths/add/cam-{camera}` with the fixture source. This is a
change of mechanism inside the test only — no spec change, no ADR — and the
reason for the switch is recorded in the test's doc-comment.

## Restoration, and why the shared stack cannot be poisoned

Two independent guarantees, because ordering is not one of them.

1. **The blast radius is a path this test created.** The camera is registered
   with a fresh `Guid` in the test body, exactly as
   `RtspTestSourceHealthTests.cs:69` does, so `cam-{guid}` belongs to no other
   test. Repointing it away from the fixture source cannot affect
   `A_camera_pointed_at_the_fixture_source_reaches_Healthy`, which registers its
   own camera and its own path. **No shared resource is mutated at all** —
   `fixture-video` keeps serving, `mediamtx` keeps running, no Aspire resource is
   stopped.
2. **A `try`/`finally` restores the path regardless of the assertion.** The
   repoint-away happens inside a `try`; the `finally` repoints back to
   `AspireFixture.RtspTestSourceUrl` and swallows nothing it should not. For AS-2
   the restore *is* the act under test, so the `finally` is idempotent — a second
   patch to the same source is a no-op the SFU accepts.

**xUnit does not order tests within a collection, and this plan does not ask it
to.** That is precisely why guarantee 1 is stated first: correctness does not
depend on the `finally` running, on this class running after
`RtspTestSourceHealthTests`, or on either class's `InitializeAsync`. The
`finally` is hygiene, not the mechanism.

Per-test isolation additionally comes free from the existing
`InitializeAsync` triple — `ResetMediaMtxAsync`, `ResetStreamDistributionAsync`,
`ResetCameraCatalogAsync` (`RtspTestSourceHealthTests.cs:49-51`) — which this
class mirrors. Note the ordering consequence: `ResetMediaMtxAsync` wipes *all*
SFU paths, so any residue from this class is removed by the next class that
resets, not by luck.

## Docker, and which CI job runs this

**Docker is required.** The class carries `[Collection(AspireCollection.Name)]`
and **no `[Trait]`**, mirroring `RtspTestSourceHealthTests.cs:29`.

`IntegrationTestSelectionTests` enforces that every integration test class
declares where it runs, and spells out the three legitimate declarations
(`:334-340`): *"needs the fixture's stack → `[Collection(AspireCollection.Name)]`,
no trait needed"*. That selects the **`integration` job — "integration tests
(Docker)"**, `ci.yml:128`, whose filter is
`Category!=Measurement&Category!=Disruptive&Category!=Maintenance` (`:179`).

It is **not** the Docker-free `backend` job, which selects `Category=FixtureLogic`
only (`ci.yml:72`).

## Retry safety (ADR-0143)

The test's own calls to the SFU go through
`AspireFixture.SendMediaMtxWithRetryAsync` (`AspireFixture.Db.cs:265`), which
already exists to absorb the CI-only transient connection failures of #964 and
retries every method by hand. Reusing it is the house rule (prefer the existing
utility) and keeps the new helper to a handful of lines.

Production's own `MediaMtxRtspGateway` is one of ADR-0143's five clients that
opts back into `RetryEveryMethod()`, justified there by `patch/` setting a fixed
value — which is the same property that makes the test's restore idempotent.

## What would make this a finding rather than a green test

Recorded up front so phase 4 does not quietly adjust:

- The transition does not fit 15 s → **finding**, file a bug; do not raise the
  budget.
- `Degraded → Healthy` never completes → **finding**, the watcher cannot recover;
  file a bug. `Stream.ReportHealthy` (`:202-227`) says it should.
- The AS-3 counterfactual leaves the new test green → the test asserts nothing;
  **block**, do not ship it.

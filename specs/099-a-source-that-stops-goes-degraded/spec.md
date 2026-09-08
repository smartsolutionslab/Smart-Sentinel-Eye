# Spec 099 — A source that stops goes Degraded, and a source that returns goes Healthy

**Issue:** #119 (`[T024]`) · **Branch:** `test/119-a-source-that-stops-goes-degraded`
**Worktree:** `D:/Github/smart-sentinel-eye/wt-119`
**Phase:** 1 (Specify) · **Date:** 2026-09-08
**Feature bucket:** spec `specs/002-watch-camera-live/`, story US1
**ADRs:** ADR-0037 (the phased workflow), ADR-0103 (integration against the real
Aspire stack, no Testcontainers), ADR-0109 (disjoint files for parallel slices),
ADR-0143 (`POST`/`PATCH` are not retried by default; the MediaMTX gateway is one
of the five clients that opts back in), ADR-0144 (the lane may not weaken a gate
and may not skip phase 4a), ADR-0011 (MediaMTX as the SFU).
**Constitution:** §Testing (two obligations, not one rule with an exception).

---

## The issue as filed, and what survives contact with the repository

### Confirmed

- **`StreamHealthIntegrationTests` has never existed.**
  `git log --all -S "StreamHealthIntegrationTests" -- '*.cs'` returns zero
  commits at `4a0bae62`. The audit comment is right.
- **The behaviour is genuinely untested.** The nearest neighbour,
  `tests/Integration.Tests/StreamDistribution/RtspTestSourceHealthTests.cs`,
  registers two cameras at two *fixed* addresses and asserts two *fixed*
  outcomes (`:62`, `:84`). Nothing anywhere takes a stream that has reached
  `Healthy` and observes it fall.
- **The audit's sharpest claim is exactly true, and is the reason this spec
  exists.** A handler that returned early whenever `stream.State == Healthy`
  would leave every test on `develop` green: the unreachable-address case never
  enters `Healthy`, so it cannot be latched out of.
- **Every existing outage test drops the client's network, not the camera** —
  `e2e/kiosk-live-updates.spec.ts`, `kiosk-reconciliation.spec.ts`,
  `kiosk-identity-herd.spec.ts`.

### Not accurate — the #2097 blocker is stale

#2097 said an integration run has no RTSP source "because `fixture-video` is
gated off for it". **That has not been true since spec 076.**
`src/AppHost/AppHost.cs:176` composes `fixture-video` under `if (isRunMode)`
alone — the `!isE2ETests` conjunct is gone, and the comment at `:157-173` says
so explicitly, adding that
`E2ETests_argument_excludes_the_dev_only_resources` now asserts its *presence*,
so re-gating it fails a test. `AspireFixture.cs:304` waits for `fixture-video`
to reach `Running` on every integration boot, and `:108` lists it among the
twelve `GatedResources`.

**Consequence for this run: there is no architectural decision to take, and
therefore no ADR and no block.** The capability the task was waiting on is
already present and already consumed.

### Not accurate — "patch the MediaMTX path" needs a subject

The audit says to patch *the MediaMTX path*. There are two MediaMTX containers
and only one of them can be patched.

- `fixture-video` (`src/AppHost/Resources/fixture-video.yml`) declares **no
  `api:` block** — the file says so in as many words: *"No control API: there is
  nothing to provision at runtime."* `AspireFixture.cs:299` repeats it. It
  cannot be patched, repointed, or asked anything.
- `mediamtx`, the SFU, exposes its v3 control API on the named endpoint `api`
  (`AppHost.cs:137`), and the integration fixture already drives it —
  `AspireFixture.Db.cs:216` opens `App.CreateHttpClient("mediamtx", "api")` and
  lists and deletes paths on it before every test.

So the subject is the **SFU's per-camera path**, not the fixture source. That is
also the faithful one: repointing `cam-{guid}` on the SFU leaves
StreamDistribution's own record of the camera untouched, which is precisely the
"a camera went dark" case — the system is not told anything changed and has to
notice.

---

## Scope

One vertical slice, tests only. No production code changes.

**In:** two integration tests in the existing
`SmartSentinelEye.Integration.Tests.StreamDistribution` namespace that observe
the two transitions the suite has never made — `Healthy → Degraded` when the
source stops answering, and `Degraded → Healthy` when it answers again.

**Out:** the `Offline` edge (it needs the watcher's 5-minute `OfflineAfter`
window — `StreamHealthWatcher.cs:27` — and a wall clock the test cannot move);
anything touching the watcher's production behaviour; anything touching
`AppHost.cs`.

---

## User story (P1, and the only one)

**US1 — an operator is told when a camera goes dark, and told when it comes
back.** Today the system does this and nothing proves it. The promise of a 24/7
CCTV system is that a dark camera is noticed; the one health assertion the suite
does not make is the one that promise rests on.

---

## Acceptance scenarios

Written against the running Aspire stack. `cam-{guid}` is the canonical SFU path
name for a camera (`MediaMtxPath.For`,
`src/StreamDistribution/Domain/Stream/MediaMtxPath.cs:19`).

### AS-1 — happy path: a source that stops goes Degraded

```gherkin
Given a camera registered at AspireFixture.RtspTestSourceUrl
  And its stream has been observed reaching "Healthy"
When the SFU path "cam-{camera}" is repointed to an address nothing answers
Then the stream reports "Degraded" within 15 seconds
```

### AS-2 — happy path: a source that returns goes Healthy

```gherkin
Given a camera whose stream has been observed reaching "Degraded"
      after its SFU path was repointed away from the fixture source
When the SFU path "cam-{camera}" is repointed back to AspireFixture.RtspTestSourceUrl
Then the stream reports "Healthy" within 15 seconds
```

### AS-3 — the control that gives AS-1 and AS-2 their meaning

```gherkin
Given the production handler is mutated to return early when the stream is already "Healthy"
When the StreamDistribution integration tests are run
Then RtspTestSourceHealthTests still passes in both of its cases
  And AS-1 is the only test that fails
```

AS-3 is not a shipped test. It is the **counterfactual** that stands in for a
red — see *§ What red is available*, and T005 in `tasks.md`, which requires its
verbatim output.

### AS-4 — degenerate input

```gherkin
Given the SFU has no path named "cam-{camera}"
When the watcher probes it
Then GetPathHealthAsync returns IsReady=false with LastError "path not registered"
```

Already true and already relied on (`MediaMtxRtspGateway.cs:107-114`). Recorded
because it is the fallback mechanism if AS-1's repoint does not clear `ready` —
see `plan.md` §Fallback.

### AS-5 — auth

```gherkin
Given the test acts as an admin
When it reads /streams?cameraIdentifiers={camera}
Then the request is authorised exactly as RtspTestSourceHealthTests' is
```

No new surface, no new scope. The SFU's control API is on the AppHost container
network and is unauthenticated by design; the fixture already calls it
(`AspireFixture.Db.cs:216`).

---

## Independent end-to-end test procedure

Docker required.

1. `dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj --filter "FullyQualifiedName~StreamHealthTransition"` — both new tests pass against an unmodified tree.
2. Apply the AS-3 mutation to `ReportStreamHealthCommandHandler`; re-run the StreamDistribution integration tests; observe AS-1 red and `RtspTestSourceHealthTests` green. Quote both.
3. Revert the mutation; re-run; all green.

---

## Latency budget impact

**N/A.** No production code changes, so no leg of §IV moves. The watcher's
2-second poll is a *health* cadence, not part of the event → overlay path.

---

## The numbers this spec rests on, read from source

| Figure | Value | Source |
|---|---|---|
| Health-watcher poll interval | **2 s** | `StreamHealthWatcher.cs:26` |
| `Offline` declaration window | 5 min | `StreamHealthWatcher.cs:27` |
| Existing settle budget for reaching a first state | 30 s | `RtspTestSourceHealthTests.cs:38` |
| Poll cadence of the test's own read loop | 500 ms | `RtspTestSourceHealthTests.cs:154` |

**The 15 seconds in the issue's test names is meetable, and this is the
arithmetic.** A config patch on the SFU takes effect on the next path reload;
the watcher observes it within one 2-second sweep and dispatches immediately, so
the worst case is roughly one sweep already in flight (2 s) plus the SFU's dial
outcome (about 1–2 s) plus one more sweep (2 s) — call it 6 s, against a 15 s
budget.

**The 15 s budgets the transition only.** Reaching `Healthy` in the first place
is the existing 30-second concern and is set up before the clock starts, exactly
as `RtspTestSourceHealthTests` budgets it.

If phase 4 measures a transition that does not fit 15 s, the honest outcome is a
**finding**, not a longer number: the watcher would be slower than the issue and
this spec both assume, and that is a defect to file rather than an assertion to
adjust.

---

## What red is available

**None, and saying so is the point.** Both tests name only symbols that already
exist — `AspireFixture.App`, `AspireFixture.RtspTestSourceUrl`, the `/streams`
endpoint, the SFU's v3 API. They compile and pass on today's `develop` on their
first run.

Manufacturing a red by writing the test against a symbol that does not exist yet
would produce **CS0117, which spec 061's commit `24e6fc4c` ruled is not a red
test**: *"a prelude is required wherever the test names a symbol the previous
commit lacks"* — and the remedy there was to add the prelude, not to accept the
compile error as evidence.

So the evidence is a **counterfactual against production code** (AS-3): mutate
`ReportStreamHealthCommandHandler` to return early when the stream is already
`Healthy`, and show that AS-1 is the only thing in the suite that goes red. That
proves the exact claim the issue makes — *"a watcher that latched `Healthy`
forever would pass every test now on `develop`"* — and it proves the new test is
what closes it. The mutation is reverted; it never appears in a commit.

This makes the phase-4a colour **characterisation, observed green** (ADR-0144),
with the counterfactual as the mandatory second half. A green-on-arrival test
with no counterfactual is indistinguishable from one that asserts nothing, which
is the failure this whole issue is about.

---

## Assumptions, marked

- **A1.** A `PATCH /v3/config/paths/patch/{name}` with a new `source` causes
  MediaMTX to drop the current source and re-dial, so `ready` goes false while
  the new address is unreachable. Consistent with the production gateway's own
  comment (`MediaMtxRtspGateway.cs:43-46`), which notes that *delete + add*
  would leave a window the 2-second sweep would see — implying patch is the
  quieter of the two, not that it is silent. **Confirmed or refuted in T002**;
  `plan.md` §Fallback names what happens if it is refuted.
- **A2.** Nothing repairs the repointed path behind the test's back.
  `MediaMtxReconciler` is an `IHostedService` whose only pass runs in
  `StartAsync` (`MediaMtxReconciler.cs:36-40`), and it reconciles path
  *presence*, not source drift.
- **A3.** The domain permits recovery. `Stream.ReportHealthy`
  (`Stream.cs:202-227`) guards only against `Retired`; `Degraded → Healthy`
  raises the transition event normally. If phase 4 finds otherwise, that is a
  **finding and a bug issue**, not an assertion to soften.

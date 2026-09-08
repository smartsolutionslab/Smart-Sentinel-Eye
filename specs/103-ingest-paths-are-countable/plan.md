# Plan 103 — Ingest paths are countable

**Spec:** `specs/103-ingest-paths-are-countable/spec.md`
**Phase:** 2 (Plan) · **Date:** 2026-09-08
**Engineer:** **backend-engineer** (C# only; no frontend, no infra, no AppHost)

---

## 1. Bounded context and layers

One context: **EventIngestion**. No cross-context reference is added, nothing
enters `Shared.Contracts` or `Shared.Kernel`, and **`src/ServiceDefaults` is not
touched** (see §4).

| Layer | Change |
|---|---|
| Domain | **none** — `Source` already carries the four wire tokens the tag needs |
| Application | new `Ingress/IngestVolume.cs`; two existing command handlers gain one call each |
| Infrastructure | one `.AddMeter(...)` line in `EventIngestionInfrastructureModule` |
| Api | **none** |

`Ingress/` is an established fifth folder in this context's Application layer
(`IIngestChannel`, `BoundedIngestChannel`, `IngestWriteLimiter`,
`IngestRetryOptions`), so ADR-0093's per-message-kind layout is not being bent to
fit — the file joins a folder that already exists for exactly this kind of
ingress-mechanics type.

---

## 2. The instrument

`src/EventIngestion/Application/Ingress/IngestVolume.cs` — a static class, matching
`LatencyBudget`, `WallSkew` and `LabelDelay` exactly. Static rather than injected
because both call sites are handlers already at three constructor parameters, and
ADR-0084 caps them at four; the three existing instruments in this repo are all
static and all tested through `MeterListener`, so this reuses a pattern rather
than introducing one.

| Element | Value | Why |
|---|---|---|
| Meter name | `SmartSentinelEye.IngestVolume` | matches `SmartSentinelEye.Latency`, `SmartSentinelEye.WallSkew`, `SmartSentinelEye.LabelDelay` |
| Instrument | `Counter<long>` | a monotonic arrival count; not a histogram (there is no distribution) and not an `UpDownCounter` (arrivals do not decrease) |
| Metric name | `sse.ingest.events` | matches `sse.latency.segment.duration`, `sse.wall.skew`, `sse.overlay.label_delay` — `sse.` prefix, dot-separated segments, `snake_case` within a segment |
| Unit | `{event}` | OTel convention for a dimensionless count |
| Description | "Events accepted into the ingestion store, by the source that sent them." | says *accepted and stored*, so nobody reads it as arrivals-at-the-edge |
| Tag | `source` = `Source.Value` | FR-002 |

**The existing metric-naming convention is real and was checked**, not assumed:
`sse.latency.segment.duration` (`LatencyBudget.cs`), `sse.wall.skew`
(`WallSkew.cs`), `sse.overlay.label_delay` (`LabelDelay.cs`). Every one is a
`MeterName` const on the type plus a `Meter.Create…` in a `private static readonly`
field. `sse.ingest.events` is the first name in the `sse.ingest.*` space.

Public surface — two methods, because the batch path counts N at once (FR-005):

```
public const string MeterName = "SmartSentinelEye.IngestVolume";
public const string MetricName = "sse.ingest.events";
public const string SourceTag = "source";

public static void Record(Source source);
public static void Record(Source source, long count);
```

`Record(source)` delegates to `Record(source, 1)`. The two-argument form guards
with `Ensure.That(source).IsNotNull()` (ADR-0105) and **returns without recording
when `count <= 0`** (FR-007) — the same "drop the impossible rather than throw"
shape `WallSkew.Record` and `LabelDelay.Record` use, and for the same stated
reason.

`MetricName` and `SourceTag` are consts because the tests filter on them; a
literal repeated in three test files is a name in four places.

---

## 3. Call sites, and the invariant that makes two of them enough

### `IngestEventCommandHandler.HandleAsync`

One line, **after `await events.SaveAsync(cancellationToken);` and before
`return Success(@event.Id);`**:

```
IngestVolume.Record(envelope.Source);
```

The early returns above it already do the right thing for free: the
`EventAlreadyIngested` branch returns before reaching it (scenario 5), and the
`OccurredAtTooFarInFuture` catch returns before reaching it (scenario 7). **No new
branch is added** — this is why the call goes here rather than at the top.

### `IngestEventBatchCommandHandler.HandleAsync`

The handler currently tracks `refused` and `seen` but not what it *added*.

**A batch has no single `Source`** — it is whatever the persistence loop drained,
so in practice `plc`, `inference`, or both. The count is therefore grouped by the
envelope's own source and recorded once per distinct source present. Grouping
keeps FR-002 honest without inventing an `mqtt` bucket that the tag vocabulary
(`Source`) does not have.

**Concretely:** accumulate a `Dictionary<Source, long>` alongside the existing
`foreach`, incrementing where `events.Add(built.Value)` already happens; then
**after `await events.SaveAsync(cancellationToken);`** iterate it once:

```
foreach ((Source source, long stored) in storedBySource)
{
    IngestVolume.Record(source, stored);
}
```

Two entries at most in practice. Watch ADR-0084's 30-LOC method limit — if
`HandleAsync` trips it, extract this loop into a private method rather than
shortening a guard or a comment to buy the lines back.

### Why after the save, and why two sites suffice

- **After the save** (FR-006, scenario 11): the batch is all-or-nothing, so a
  throwing `SaveAsync` must contribute nothing before the loop retries the same
  envelopes singly through `IngestEventCommandHandler`
  (`PersistenceLoopHostedService.cs:342-353`). Counting during the build loop
  double-counts every retried batch — and spec 020 made retry the *ordinary* way
  an interruption ends, so this is the common case, not an edge.
- **Two sites suffice** because every envelope reaches storage through exactly one
  of them:

  | Ingress | Handler |
  |---|---|
  | `POST /events/manual`, `POST /events/webhook/{name}` | `IngestEventCommandHandler` (`EventsEndpoints.Writes.cs:354`) |
  | MQTT, batched | `IngestEventBatchCommandHandler` (`PersistenceLoopHostedService.cs:328`) |
  | MQTT, retried singly | `IngestEventCommandHandler` (`PersistenceLoopHostedService.cs:349`) |

  There is no fourth writer. `EventRepository.Add`/`SaveAsync` is called from
  nowhere else in the context.

---

## 4. Registration — and why `ServiceDefaults` is not touched

The meter must be registered or it records into nothing (FR-004). The three
existing meters are registered in `src/ServiceDefaults/Extensions.cs` because they
are *cross-cutting* — the kiosk and several services all report into them.

**This one cannot follow that line, for a mechanical reason:** `ServiceDefaults` is
referenced *by* every context and references *none* of them, so it cannot see
`IngestVolume.MeterName`. Registering there would mean a duplicated string literal
— the same name in two projects, which is how a rename silently unregisters a
meter.

So the registration goes in the context's own DI extension, which is exactly
ADR-0051's pattern — one line in
`src/EventIngestion/Infrastructure/EventIngestionInfrastructureModule.cs`, beside
the existing "Bounded channel + ingress" registrations:

```
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(IngestVolume.MeterName));
```

`OpenTelemetry.Extensions.Hosting` flows transitively via the module's existing
`ProjectReference` to `ServiceDefaults` (line 18 of its `.csproj`), so **no package
reference is added** (FR-008).

**Assumption A1 rides here** — that a second `AddOpenTelemetry()` after
`AddServiceDefaults()` is additive rather than a re-registration. Falsifier and
fallback are in `spec.md` §8.

### Contention (ADR-0109)

| File | Shared? |
|---|---|
| `src/EventIngestion/Application/Ingress/IngestVolume.cs` | new, context-owned |
| `src/EventIngestion/Application/Commands/Handlers/IngestEventCommandHandler.cs` | context-owned |
| `src/EventIngestion/Application/Commands/Handlers/IngestEventBatchCommandHandler.cs` | context-owned |
| `src/EventIngestion/Infrastructure/EventIngestionInfrastructureModule.cs` | context-owned |
| `tests/EventIngestion.Application.Tests/**` | context-owned |

**No file on ADR-0109's contention list is touched.** `src/ServiceDefaults/*` is
not on that list verbatim but is shared by every context, and §4 above is the
reason this slice stays out of it — deliberately, not by luck.
`Directory.Packages.props` is untouched (FR-008).

---

## 5. Phase 4a colour: **RED**, and exactly how red is obtained

**This is behaviour-changing production code.** A counter that did not exist now
emits measurements. Characterisation is not available and would be wrong: there
is no prior behaviour to capture. Per ADR-0144, ambiguity resolves to red and
there is no ambiguity here.

### The trap this must not fall into

Spec 061's `24e6fc4c` ruled that **a compile error is not a red test**. A test
asserting `IngestVolume` records, written against a tree where `IngestVolume` does
not exist, fails with `CS0246: The type or namespace name 'IngestVolume' could not
be found`. That is a broken build, not a failing assertion, and it does not
satisfy the gate.

### The sequence that avoids it

**T001 — prelude commit, behaviour-free.** Add `IngestVolume.cs` with the full
public surface and **no counter**:

- `MeterName`, `MetricName`, `SourceTag` consts — real values, final values.
- `Record(Source)` → `Record(source, 1)`.
- `Record(Source, long)` → body is `Ensure.That(source).IsNotNull();` and nothing
  more.

No `Meter` field, no `Counter<long>`, no `Add`. The guard keeps SonarAnalyzer
S1186 ("methods should not be empty") quiet **and survives unchanged into the
green implementation**, so the prelude is not scaffolding to be deleted.

The tree compiles. Nothing records. Nothing else in the repo changes.

**T002 — the tests, observed red.** They compile (every symbol they touch now
exists) and fail on **assertions about counts**.

**T003/T004 — the implementation**, then the same tests pass **unmodified**.

### The written prediction (phase 4a must quote its actual output against this)

- **Expected:** `dotnet test tests/EventIngestion.Application.Tests` after T002
  reports **0 errors, 0 warnings** from the build and **13 failing test cases**,
  enumerated in `tasks.md` §T002.
- **The invariant that matters more than the number:** every failure message is a
  Shouldly assertion about a recorded measurement — of the form
  *"recorded should have a single item but was empty"* / *"recorded should be 200
  but was 0"*. **Not one failure may carry a `CS`-prefixed compiler diagnostic.**
  If any does, the prelude was incomplete and phase 4a is not satisfied.
- If the count differs from 13 because the test-writer split or merged a case,
  that is fine and the actual number is reported. If the count is **zero**, the
  tests are not testing the new behaviour and the phase has failed.

### Counterfactuals — what proves the tests bite, run at phase 6

1. Delete `IngestVolume.Record(envelope.Source);` from
   `IngestEventCommandHandler` → the manual/webhook cases must fail.
2. Move the batch handler's recording **before** `await events.SaveAsync(...)` →
   *"a batch whose save throws counts nothing"* must fail. This is the one that
   matters; it is the only assertion standing between the counter and a figure
   inflated by every retry.
3. Drop the `count <= 0` guard → *"a batch that stored nothing records nothing"*
   must fail.
4. Remove the `.AddMeter` line → **no unit test fails.** That is not a defect in
   the tests; it is why `spec.md` §5 exists and why phase 5 may not be skipped.
   Record this counterfactual's *failure to fail* explicitly in the verification
   note, rather than letting a green suite imply a registered meter.

---

## 6. Messaging, persistence, boundaries

- **No domain event, no integration event, no contract change.** A counter is not
  a fact about the domain; nothing subscribes to it.
- **No migration, no schema change, no `Idempotency-Key` interaction.** ADR-0142's
  replay path returns the original answer *without* re-executing the handler, so a
  replayed create does not re-count — which is scenario 5's behaviour arriving for
  free.
- **No `Shared.Contracts` / `Shared.Kernel` change**; `NetArchTest`'s boundary
  rules are unaffected.
- **`PrimitiveBoundaryTests`** is unaffected: `IngestVolume` is not a domain model,
  and the `long` it takes is a measurement count, not domain state.
- **`HandlerDeconstructionTests`**: both handlers read a single field of their
  command (`command.Envelope`, `command.Envelopes`), so member access stays correct
  and no deconstruction is introduced.

---

## 7. Test isolation — a known flake this plan pre-empts

`Meter` is process-wide and xUnit parallelises test classes within an assembly.
`EventToOverlayLatencyTests` documents what happens otherwise:

> *"these assertions then failed roughly one run in six, on measurements that were
> never theirs."*

It solved it by filtering the listener to its own `segment` tag. **That trick does
not transfer**: `IngestVolumeTests` and the two handler test classes will all emit
`source="manual"`, so a tag filter cannot separate them.

**So the three classes share one xUnit collection** —
`[Collection("ingest-volume")]` — which serialises them against each other while
leaving the rest of the assembly parallel. Listeners still filter on
`instrument.Meter.Name == IngestVolume.MeterName` **and**
`instrument.Name == IngestVolume.MetricName`, so nothing from another instrument
leaks in either.

This is stated at plan time rather than discovered at phase 6, because a metrics
test that passes locally and fails one CI run in six is the most expensive
possible way to learn it.

---

## 8. Coverage and metrics gates

- Application ≥ 80% (ADR-0065): `IngestVolume` is small and fully exercised by
  `IngestVolumeTests`; both handlers gain covered lines.
- ADR-0084: `IngestVolume.cs` is well under 300 LOC. Watch
  `IngestEventBatchCommandHandler.HandleAsync`, which is close to the 30-LOC
  method limit — extract the grouped recording into a private method if it trips,
  and do **not** buy the lines back by deleting comments or guards.

# Tasks 103 — Ingest paths are countable

**Spec:** `spec.md` · **Plan:** `plan.md`
**Phase:** 3 (Tasks) · **Date:** 2026-09-08
**Issues:** #622 (`[T088]`), #619 (`[T085]`) — one slice, both closed by the PR
**Engineer:** backend-engineer. **Phase 4a colour: RED** (see `plan.md` §5).

Legend: `[ID] [P?] [Story]`. `[P]` = disjoint files, safe to run concurrently.

---

## Ordering constraint that is the whole point of this list

**T001 must land before T002 exists as a runnable test.** Without it the tests
fail with `CS0246` and a compile error is **not** a red test (spec 061,
`24e6fc4c`). T001 introduces the type with the guard and no counter, so T002's
failures are assertions about counts.

---

## Phase A — Prelude (blocks everything)

- [ ] **T001 [US1]** `src/EventIngestion/Application/Ingress/IngestVolume.cs` —
  **the type, with no measurement**.
  - `public const string MeterName = "SmartSentinelEye.IngestVolume";`
  - `public const string MetricName = "sse.ingest.events";`
  - `public const string SourceTag = "source";`
  - `public static void Record(Source source)` → `Record(source, 1);`
  - `public static void Record(Source source, long count)` → body is exactly
    `Ensure.That(source).IsNotNull();` (ADR-0105) and nothing else.
  - **No `Meter` field. No `Counter<long>`. No `Add`.** The guard keeps
    SonarAnalyzer S1186 quiet and survives unchanged into T003, so this is not
    scaffolding to be deleted.
  - XML doc says what the instrument *will* measure and why it is not called
    `ChannelMetrics` (`IIngestChannel.cs:16-19` — the HTTP paths left the channel
    at spec 020).
  - **Done when:** `dotnet build` is clean; nothing records.

  *Commit:* `feat(event-ingestion): an ingest-volume instrument with no measurements`

---

## Phase B — Tests first, observed RED (T001 → all of B)

Written by **test-writer**, run, and the **verbatim output** carried into the
engineer's brief (ADR-0144). The engineer may not edit these.

All three classes carry `[Collection("ingest-volume")]` — `Meter` is process-wide
and every one of them emits `source="manual"`, so a tag filter cannot separate
them the way `EventToOverlayLatencyTests` separates segments (`plan.md` §7).
Listeners filter on **both** `instrument.Meter.Name == IngestVolume.MeterName`
**and** `instrument.Name == IngestVolume.MetricName`.

- [ ] **T002a [P] [US1]**
  `tests/EventIngestion.Application.Tests/Ingress/IngestVolumeTests.cs` — the
  instrument itself (issue #619's literal ask).
  1. `A_recorded_ingest_counts_one_against_its_source`
  2. `Each_source_is_counted_under_its_own_tag` — `[Theory]` over `plc`,
     `inference`, `manual`, `webhook` (**4 cases**); asserts the tag key is
     `source` and the value is the wire token
  3. `A_batch_records_one_measurement_carrying_its_whole_count` — FR-005: one
     measurement of 200, not 200 of 1
  4. `A_count_of_zero_records_nothing` — FR-007
  5. `A_negative_count_records_nothing` — FR-007

- [ ] **T002b [P] [US1]** additions to
  `tests/EventIngestion.Application.Tests/Commands/IngestEventCommandHandlerTests.cs`
  6. `A_stored_event_is_counted_once_under_its_source` — scenarios 1, 2
  7. `A_duplicate_re_delivery_is_not_counted` — scenario 5
  8. `An_event_refused_for_future_skew_is_not_counted` — scenario 7

- [ ] **T002c [P] [US1]** additions to
  `tests/EventIngestion.Application.Tests/Commands/IngestEventBatchCommandHandlerTests.cs`
  9. `A_stored_batch_is_counted_once_per_stored_envelope` — scenario 3
  10. `Two_sources_in_one_batch_are_counted_under_their_own_tags` — scenario 4
  11. `A_duplicate_within_a_batch_is_counted_once` — scenario 6
  12. `An_envelope_refused_for_future_skew_is_not_counted` — scenario 7
  13. `A_batch_whose_save_throws_counts_nothing` — **scenario 11, FR-006**; the
      single most important assertion in this slice, and the one that stops the
      counter inflating on every retry

  Existing fakes are sufficient: `InMemoryEventRepository`, `FakeClock`,
  `NullLogger<T>`. Hand-written builders only (ADR-0054). For case 13 the
  repository fake must be able to throw from `SaveAsync` — extend it if it cannot,
  in the test project, not in `src/`.

- [ ] **T002d [US1]** **Run them and quote the output.**
  **Predicted:** build clean (0 errors, 0 warnings), **13 failing cases**, every
  message a Shouldly assertion about a recorded measurement
  (*"recorded should have a single item but was empty"*, *"should be 200 but was
  0"*). **No `CS`-prefixed diagnostic may appear.** A `CS` failure means T001 was
  incomplete and phase 4a is not satisfied. Zero failures means the tests are not
  testing the new behaviour and the phase has failed.

  *Commit:* `test(event-ingestion): ingest volume is counted per source` (red)

---

## Phase C — Implementation (T002d → C)

- [ ] **T003 [US1]** `IngestVolume.cs` — add the meter and the counter.
  - `private static readonly Meter Meter = new(MeterName);`
  - `private static readonly Counter<long> Ingested = Meter.CreateCounter<long>(
    name: MetricName, unit: "{event}", description: "Events accepted into the
    ingestion store, by the source that sent them.");`
  - `Record(source, count)`: after the guard, `if (count <= 0) { return; }` then
    `Ingested.Add(count, new TagList { { SourceTag, source.Value } });`
  - The `count <= 0` early return carries the comment for *why* (drop the
    impossible rather than throw — `WallSkew` / `LabelDelay` precedent), not the
    *what*.

- [ ] **T004 [US1]** call sites — **one file each, but both in `Commands/Handlers/`,
  so not marked `[P]` against each other for a single engineer.**
  - `IngestEventCommandHandler.HandleAsync`: `IngestVolume.Record(envelope.Source);`
    **after** `await events.SaveAsync(...)`, **before** `return Success(@event.Id);`.
    The two early returns above it already exclude duplicates and future-skew
    refusals — **add no new branch**.
  - `IngestEventBatchCommandHandler.HandleAsync`: accumulate a
    `Dictionary<Source, long>` where `events.Add(built.Value)` already happens,
    then record it once **after** `await events.SaveAsync(...)` (`plan.md` §3).
    Extract to a private method if ADR-0084's 30-LOC limit trips.

- [ ] **T005 [P] [US1]** `src/EventIngestion/Infrastructure/EventIngestionInfrastructureModule.cs`
  — register the meter beside the "Bounded channel + ingress" block:
  ```
  builder.Services.AddOpenTelemetry()
      .WithMetrics(metrics => metrics.AddMeter(IngestVolume.MeterName));
  ```
  with a comment saying *why it is not in `ServiceDefaults`*: that project cannot
  reference a context's Application layer, so the alternative is the same string
  in two projects (`plan.md` §4). **No package reference is added** — FR-008.
  `[P]` with T003/T004: disjoint file.

- [ ] **T006 [US1]** run the suite. **All 13 cases pass, unmodified.** Any test that
  had to be edited to pass is a phase-4 failure, not a fix (ADR-0144).

  *Commit:* `feat(event-ingestion): count accepted events by ingest source`

---

## Phase D — Verify (phase 5; T006 → D)

- [ ] **T007 [US1]** Execute `spec.md` §5 against a booted stack and write
  `verification.md`. Boot with the anonymous-dashboard flag; one Aspire stack per
  machine; mint the token from the **proxied** Keycloak endpoint.
  **This step is not optional and not skippable**: unit tests cannot detect an
  unregistered meter, which fails silently by construction (FR-004). If step 5 of
  §5 shows no `sse.ingest.events` instrument, assumption **A1** is false — apply
  the fallback in `spec.md` §8 and re-verify.

- [ ] **T008 [US1]** Record the four counterfactuals from `plan.md` §5 in the
  verification note, **including #4** — removing `.AddMeter` fails **no** unit
  test. Write that down rather than letting a green suite imply a registered meter.
  Note that the latency-budget table in constitution §IV is **unchanged**; this
  slice moves no leg.

---

## Dependency graph

```
T001  (prelude — blocks everything)
  ├─ T002a [P] ─┐
  ├─ T002b [P] ─┼─ T002d (run, quote the red)
  └─ T002c [P] ─┘        │
                         ├─ T003 ── T004 ─┐
                         └─ T005 [P] ─────┼─ T006 ── T007 ── T008
```

**Foundational:** T001 only. Nothing in `Shared.Kernel`, `Shared.Contracts`,
`AppHost` or `ServiceDefaults` is touched, so there is nothing here for an
orchestrator to serialise against other slices — and no ADR-0109 contention file
appears in the whole change set (`plan.md` §4).

---

## Board (ADR-0037 phase-3 gate)

Both issues already exist, both carry `agent:ready`, `task`,
`feature:006-event-ingestion`, `story:us5`. Per `CLAUDE.md` the gate is **the
feature's issue on Project #13** — not one issue per task. `/speckit-taskstoissues`
must **not** be run: this slice is 8 tasks against 2 existing issues.

The PR closes both with closing keywords, and the issue state is **checked after
the merge** (a PR mention auto-closes roughly 1 time in 3).

# Tasks: The instrument before the latency

**Spec**: [`spec.md`](./spec.md) · **Plan**: [`plan.md`](./plan.md) · **Issue**: #1956

**Phase**: 3 (Tasks) of ADR-0037. Tracking is at **feature granularity** — #1956 on
Project #13. No per-task issues (CLAUDE.md, phase-3 row; the `[TNNN]` series stopped at
#1845 / spec 028).

**Phase 4a colour: RED.** Behaviour-changing. Every new fact must be observed failing
first and the verbatim output quoted in the PR (ADR-0139, ADR-0144).

---

## Parallelism — read this before fanning out

**Almost nothing here is `[P]`, and the reason is physical, not architectural.**

> **One machine, one Aspire stack.** Two concurrent boots produce `FailedToStart` that
> reads exactly like a code defect. Every task that boots the fixture is therefore
> strictly serial with every other such task, regardless of which files it owns.

`[P]` appears only on tasks that touch **disjoint files and boot nothing**. Foundational
work — T001, and T101 within US3 — blocks what follows it and is called out so the
orchestrator does not fan out behind it.

Also: **do not create a git worktree for this feature.** C: was at 96% (11 GB free) when
this was written; a full disk stops the Docker engine and only a GUI restart recovers it.

---

## Phase 0 — foundation (blocks everything)

- [ ] **T001 [US1]** Stop any running AppHost
      (`Get-Process SmartSentinelEye.AppHost -ErrorAction SilentlyContinue | Stop-Process -Force`)
      and confirm no `testhost` process names this worktree. A running stack holds the
      service binaries and the build fails MSB3027, which reads as a broken build.
      Leave the persistent containers alone.
      **Blocks: everything.**

---

## US1 (P1) — an ordinary test run can divide the span

*Independently shippable. Turns one refusal into one answer, and unblocks US2 and US3.*

- [x] **T010 [US1]** **RED. DONE 2026-09-09, commit `97885925`.** With no environment variable exported by the runner, run
      `dotnet test tests/Integration.Tests --filter "FullyQualifiedName=SmartSentinelEye.Integration.Tests.AuditObservability.NFR001_AuditIngestLatencyTests.Where_the_ingest_span_goes"`.
      Capture the verbatim failure — expected: *"N rows arrived without the measurement
      stamps; turn the switch on before reading anything above"*
      (`NFR001_AuditIngestLatencyTests.cs:305-307`). This is the red the PR quotes.
      **Do not run this fact alone from a cold stack.** Two attempts to do so died at
      `IngestSpanMeasurement.DefineAsync` with
      `Polly.Timeout.TimeoutRejectedException … '00:00:10'` on the first POST —
      infrastructure, not the stamps refusal the run exists to observe. Another fact must
      warm `system-variables` first.
      **Depends: T001.**
- [ ] **T011 [US1]** Add one line inside the existing `if (isE2ETests)` block in
      `src/AppHost/AppHost.cs` (the block opening at `:410`, beside the
      `AuditObservability__Retention__TickInterval` precedent at `:429`):
      `auditObservability.WithEnvironment("AuditObservability__Measurement__RecordIngestBreakdown", "true");`
      Nothing else in `src/`.
      **Depends: T010.**
- [ ] **T012 [P] [US1]** Confirm `tests/AuditObservability.Application.Tests/EventHandlers/AuditMeasurementSwitchTests.cs`
      passes **unmodified** — the type default stays `false`
      (`:96`). Unit tests, no fixture boot, disjoint file: safe to run alongside T013.
      **Depends: T011.**
- [ ] **T013 [US1]** Re-run T010's command. Expect `measurement switch: ON`,
      `0 missing stamps`, and the run to pass. **Run it twice** — the first run after
      machine churn looks exactly like a regression. Record both, unaveraged.
      **Depends: T011. Serial with every other fixture task.**
- [ ] **T014 [P] [US1]** Confirm `tests/Architecture.Tests` `IntegrationTestSelectionTests`
      still passes — `ci.yml:179`'s filter string is unchanged and the
      `Category=Measurement` exclusion is untouched. Disjoint file, no boot.
      **Depends: T011.**

**US1 checkpoint** — `Where_the_ingest_span_goes` completes from a plain `dotnet test`.
Half of #2127's complaint is gone: the breakdown no longer requires a remembered shell
export. Shippable on its own.

---

## US2 (P2) — the verdict states the rate it was taken at

- [x] **T020 [US2]** **RED. DONE 2026-09-09, commit `97885925`.** Add a fact that drives through
      `IngestSpanMeasurement.RunAsync` and guards `Conditions.RateWasMet` — mirroring
      `NFR001_AuditIngestLatencyTests.cs:329-332` — but wire it to the **unpaced**
      driver first, so it fails reporting 45.1-57.5 ev/s against a target of 100 — measured
      at phase 4a; the earlier ~15-20 in this file came from ADR-0136's row for a
      different machine, and the guard refuses either way. Capture
      the verbatim failure. This red is the evidence that defect (A) is real, not
      inferred.
      **Depends: T013.**
- [ ] **T021 [US2]** Point the new fact at the paced shape (50 writers,
      `IngestRunShape.TargetRatePerSecond`). It reports the achieved rate, and
      `RequirementSpanFloorMs` / `RequirementSpanCeilingMs` / `RequirementSpanWidthMs`
      from `IngestAttribution.cs:69-84`, and states whether the 50 ms budget falls
      **inside** the interval.
      **It must not assert `p99 < 50` against the ceiling** — that is the assertion that
      produced every superseded verdict on this issue. **Nor against the floor** — that
      is the mirror-image error and turns the fact green for the wrong reason.
      Sentence-style name (ADR-0053), `[Trait("Category", "Measurement")]` retained.
      **Depends: T020.**
- [ ] **T022 [US2]** Strip the NFR-001 verdict from
      `Ingest_p99_from_publish_to_row_stays_under_50ms`. It keeps its shape — one
      sequential writer, `NoPacing` (`:209-210`) — because that shape is what makes its
      figure comparable with the ones quoted on #1956. It reports the end-to-end span
      **and the rate it achieved**, and its remarks say what it is not. Update the class
      remarks at `:180-194`, which currently conclude *"what is open in 1956 is no longer
      code"* on the strength of the ceiling.
      **Depends: T021.**
- [ ] **T023 [US2]** Run both facts. **Twice.** Record the achieved rates and all four
      span figures per run, unaveraged.
      **Depends: T022. Serial with every other fixture task.**

**US2 checkpoint** — no fact in the repository reports an NFR-001 verdict from a run it
did not pace, and the verdict that exists reports an interval rather than a point.

---

## US3 (P3) — locating the handover

- [x] **T101 [US3]** **The assumption check, and it comes first.** Verify A1: that
      `messages_unacknowledged` on the **`audit-observability.*`** queues is the
      in-flight population under `ProcessInParallelWithNativeAcks()`
      (`WolverineDefaults.cs:126-129`). Observe it **zero** when the audit service is
      quiescent and **non-zero** with handlers mid-flight, read from RabbitMQ's
      management API.
      **The queue name is `ContextName` + `.` + the event FQN** (`WolverineDefaults.cs:95-96`,
      `AuditObservabilityInfrastructureModule.cs:23,94`) — **not** `wolverine_audit`,
      which an earlier draft of this file named and which is the **Postgres outbox
      schema** (`:24`). A filter on `wolverine_audit` matches nothing and reports a mean
      of zero: the very failure T102 exists to refuse, arriving through the task that
      specifies it.
      **Sample through the drain, not only the drive.** RabbitMQ's management statistics
      refresh on `collect_statistics_interval` (~5 s), so a short fast sample reads a
      **cached zero**. The first version of this task sampled only the publish window at
      100 ms, reported peak 0, and falsely refuted A1. The version that holds drives
      3000 events and samples at 250 ms through the drive **and 20 s of drain** — peak
      64, 31 of 88 samples non-zero.
      **If it does not behave this way, US3 stops here and reports** — every number
      downstream divides by this one. ADR-0126's own correction came from a count that
      answered a different question from the one asked of it.
      **DONE 2026-09-09: A1 HOLDS. US3 proceeds.**
      **Blocks: T102, T104. Depends: T023.**

- [ ] **T102 [US3]** **Build the sampler and the fact that reports it, as one task.**
      Poll the `audit-observability.*` queues from the broker's HTTP API through a paced
      run, and report the mean unacknowledged count, the achieved drain rate, and the
      implied mean leg time. It must fail naming the address and the status it received
      if the broker refuses, rather than reporting a mean of zero samples. Evidence comes
      from the broker and from Postgres — **never** from an Aspire structured-log search,
      which ADR-0127 recorded returning zero hits for events that were demonstrably
      landing.
      **Depends: T101.**

      > **This task has no red available, and the PR must say so rather than imply a gate
      > it never passed.** T102 was previously split into a RED fact (T102) and its
      > implementation (T103). That split does not survive contact: both halves live in
      > `tests/Integration.Tests`, so they are **the same artefact in the same project**.
      > A fact written against a sampler that does not exist is a **compile error**, which
      > ADR-0139 does not accept as red; a fact that samples inline is **green on its
      > first run**. There is no third shape that produces an honest red here, and none is
      > to be manufactured. The two tasks are therefore merged, and **T104's two runs are
      > the evidence** — reported unaveraged, in place of a red.
      >
      > US1's and US2's reds are unaffected and were observed: commit `97885925`.

- [ ] **T104 [US3]** Run it **twice**, unaveraged. Read the outcome:
      - **near zero** → the handover is at handler entry, the requirement's span is the
        floor, NFR-001 holds on its own leg, and the *end-to-end* span — which has no
        budget anywhere in the repository — does not;
      - **deep** → the prefetch buffer holds the wait inside NFR-001's leg, the
        requirement is genuinely missed, and the next step is a per-row
        transport-receipt stamp, which is **not** taken in this pass.
      **Depends: T102. Serial with every other fixture task.**

**US3 checkpoint** — the requirement's span is located inside "before handler", or the
run says in one number why it could not be.

---

## Phase 5-7 — verification, review, and the finding

- [ ] **T200 [US1,US2,US3]** Write `specs/109-the-instrument-before-the-latency/verification.md`:
      every run, both samples of each, unaveraged, with achieved rates and conditions.
      Cite §1.0's two pre-planning runs (98.6 ev/s, requirement span 6.2-3107.4 ms; and
      99.8 ev/s, 2.5-17.5 ms) as the baseline the change is read against — and as the
      reason no task's criterion is a latency value.
      **Depends: T104 (or T023 if US3 stops at T101).**
- [ ] **T201 [P]** `/code-review` on the diff. Disjoint from T202.
      **Depends: T200.**
- [ ] **T202 [P]** Confirm `git diff --stat src/` is one added `WithEnvironment` line and
      nothing else, and that `specs/009-audit-observability/spec.md:294` is untouched —
      **NFR-001's 50 ms and its 100 ev/s do not move.** Disjoint from T201.
      **Depends: T200.**
- [ ] **T203** Post the **finding** to #1956: that the issue's premise (rising backlog,
      a ~5 s polling interval) is dead; that the instrument was wrong at both ends —
      wrong rate and wrong leg; that every verdict on the issue was read off the
      ceiling; and which of T104's two outcomes was observed. Reconcile
      `NFR001_AuditIngestLatencyTests.cs:180-194`'s standing conclusion with it.
      **Depends: T201, T202.**
- [ ] **T204** PR to `develop` (`--base develop`, ADR-0028), quoting T010's, T020's and
      T102's absence of one (tasks.md records why). Conventional Commit, **no `Co-Authored-By`**
      (ADR-0086).
      **Depends: T203.**

---

## Dependency summary

```
T001
 └─ T010 → T011 ─┬─ T012 [P]
                 ├─ T014 [P]
                 └─ T013 → T020 → T021 → T022 → T023 → T101 ─┬─ (stop & report)
                                                             └─ T102 → T104
                                                                          └─ T200 ─┬─ T201 [P]
                                                                                   └─ T202 [P]
                                                                                        └─ T203 → T204
```

**The spine is serial by physics.** Only T012/T014 and T201/T202 fan out; everything
else boots the one Aspire stack this machine can host.

---

## Atomicity and the stop conditions

Each task above changes one thing and has a verifiable success criterion that is **not a
latency value** — the pipeline is bistable at 100 ev/s (ADR-0136), so no task passes or
fails on how fast a run was.

Three stop conditions, each a **blocked outcome** rather than a failure:

1. **T101 refutes A1** — report and stop US3.
2. **Locating the handover needs a Wolverine listener policy or middleware** — anything
   running for every message in every context, rather than a read of broker state. That
   is shared messaging infrastructure (ADR-0088, ADR-0126) and wants an ADR, which the
   lane may not write (ADR-0144).
3. **The honest conclusion becomes "NFR-001's wording is wrong"** — for instance, that a
   leg starting at deliver-ack excludes the queue wait where all the observed time
   actually is. That is a product decision on
   `specs/009-audit-observability/spec.md:294`, forbidden by the issue's own terms
   (*"Do not raise the budget to fit an observation"*). Report it; do not take it.

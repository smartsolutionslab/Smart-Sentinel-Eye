# Tasks 079 — A reconnect that resubscribes

**Phase:** 3 (Tasks) · **Date:** 2026-09-06 · **Issue:** #1139
**Engineer:** `backend-engineer` · **Reviewer:** `backend-reviewer`
**Plan:** `specs/079-a-reconnect-that-resubscribes/plan.md`

Format: `[ID] [P?] [Story]` — `[P]` marks tasks that own **disjoint files** and
may run in parallel (ADR-0109).

**One PR.** Central package management pins MQTTnet once
(`Directory.Packages.props:70`) and `Integration.Tests` references
`EventIngestion.Infrastructure`, so the port cannot be split across PRs without
putting two MQTTnet majors in one test assembly. US1/US2/US3 are priorities
within the slice, not separate deliveries.

---

## Phase 0 — Capture the characterisation baseline (blocks everything)

**Runs on `4.3.7.1207`, before a single line changes.** Its output is the "green
before" half of the characterisation obligation and is quoted in the PR.

- **[T001] [US1]** Confirm the working tree is clean at `f25e468e`, `C:` has
  ≥ 6 GB free, and no AppHost is running. Build `-c Release`.
- **[T002] [US1]** Run the covering suite **on the unmodified tree** and capture
  the verbatim passing output:
  `PlantFloor`-backed tests, `DeadLetterFabScopingIntegrationTests`,
  `PoisonDeliveryEscapeIntegrationTests`, `RestartLosesNothingIntegrationTests`,
  `OutageRecoveryIntegrationTests`, `NFR002_MqttConnectAuthTests`,
  `IngestThroughputMeasurementTests`, `MosquittoConnectionFactoryTests`.
  Record the NFR002 CONNECT→CONNACK figure and the throughput figure — they are
  the before-values for the latency-leg comparison.
  **Depends on:** T001.

> **Gate.** T002 must be green before any source edit. A refactor with no
> captured covering test is a rewrite (CLAUDE.md §Testing).

---

## Phase 1 — Foundation: the package move (blocks all implementation)

Single-file-owner tasks, but sequential: the tree does not compile between T003
and T007.

- **[T003] [US1]** `Directory.Packages.props` — `MQTTnet` to `5.2.0.1603`;
  delete the `MQTTnet.Extensions.ManagedClient` line; rewrite the comment at
  line 69, which still says "managed-client wrapper".
  **Depends on:** T002.
- **[T004] [P] [US1]** `src/EventIngestion/Infrastructure/SmartSentinelEye.EventIngestion.Infrastructure.csproj`
  — remove the `MQTTnet.Extensions.ManagedClient` reference (line 33).
  **Depends on:** T003.
- **[T005] [P] [US2]** `src/ScenarioSimulator/SmartSentinelEye.ScenarioSimulator.csproj`
  — same (line 23).
  **Depends on:** T003.
- **[T006] [P] [US1]** Mechanical port of the seven test/fixture files —
  `PlantFloor.cs`, `DeadLetterFabScopingIntegrationTests.cs`,
  `IngestThroughputMeasurementTests.cs`, `OutageRecoveryIntegrationTests.cs`,
  `PoisonDeliveryEscapeIntegrationTests.cs`,
  `RestartLosesNothingIntegrationTests.cs`, `NFR002_MqttConnectAuthTests.cs`.
  **Permitted edits: the `using MQTTnet.Client;` line, and `new MqttFactory()` →
  `new MqttClientFactory()`. Nothing else.** No assertion, expected value,
  timeout, delay, arrangement or attribute may change. Any such edit is a block.
  **Depends on:** T003. Disjoint from T004/T005/T007.
- **[T007] [P] [US1]** `MosquittoConnectionFactoryTests.cs` — mechanical port
  plus the one permitted navigation change `connection.Options.ClientOptions.
  Credentials` → `connection.Options.Credentials`, forced by
  `ManagedMqttClientOptions` collapsing into `MqttClientOptions`.
  **`ShouldBeEmpty()` and every other expected value stay exactly as written.**
  **Depends on:** T003.
- **[T008] [US1]** Confirm `Architecture.Tests/BoundaryTests.cs` still passes
  unmodified — the allowed-dependency prefix is `"MQTTnet"` (lines 204, 296,
  322, 347) and should match the new assembly set. **If it needs an edit, stop
  and report**: that is a boundary change, not a port.
  **Depends on:** T004–T007.

---

## Phase 2 — US1 (P1): reconnect, then resubscribe

### 2a — Tests first (`test-writer`), gap 1 and gap 4

- **[T009] [US1]** `tests/EventIngestion.Infrastructure.Tests/MqttConnectionLoopTests.cs`
  — **new, must be observed red.** Drives a fake connection through
  connect → drop → connect and asserts:
  (a) it reconnects without external intervention;
  (b) a token is requested **before each attempt**, not once at start (gap 4);
  (c) the delay grows across consecutive failures and caps at 30 s;
  (d) the delay resets to zero after a success;
  (e) it never stops retrying — no attempt limit (assumption A3).
  Report the verbatim failure output.
  **Depends on:** T008.

### 2b — Implementation, gap 1 only

- **[T010] [US1]** `src/EventIngestion/Infrastructure/Ingress/MosquittoConnectionFactory.cs`
  — build a plain `IMqttClient` via `MqttClientFactory` and `MqttClientOptions`.
  `MqttConnection` carries `IMqttClient` + `MqttClientOptions` + `TokenHolder` +
  `MqttTokenProvider`; drop `RefreshTokenAsync`. **Keep the non-fatal startup
  mint (lines 37-55, #2038) exactly as it is** — T007 guards it.
  **Depends on:** T009 (red observed).
- **[T011] [US1]** `src/EventIngestion/Infrastructure/Ingress/MqttConnectionLoop.cs`
  — **new file.** mint → `ConnectAsync` → wait for disconnect → back off →
  repeat, until cancelled. Exponential 1/2/4/8/16 s capped at 30 s, jitter
  `[0.8, 1.2]`, reset on success, no attempt limit. Guards via `Ensure.That`
  (ADR-0105 — `ArgumentNullException.ThrowIfNull` is build-banned).
  `CancellationToken` last (ADR-0049). **Deliberately does NOT resubscribe yet.**
  **Depends on:** T010.
- **[T012] [US1]** `src/EventIngestion/Infrastructure/Ingress/MqttSubscriberHostedService.cs`
  — `StartAsync` builds the connection and **launches the loop as a background
  task without awaiting a connect**, then returns (US1-AC8; awaiting it
  reintroduces #2038 from the broker side). `StopAsync` cancels, awaits, disposes.
  Remove the `OnDisconnectedAsync` / `OnConnectingFailedAsync` token re-mints —
  the loop mints per attempt instead. **Leave the message path untouched**:
  `AutoAcknowledge = false`, `MqttDeliveryCompletion`, dead-lettering,
  `TryParseEnvelope`.
  **Depends on:** T011.
- **[T013] [US1]** `src/EventIngestion/Infrastructure/Log.cs` — add
  `MqttSubscriberRetryScheduled(delaySeconds, attempt)`. `[LoggerMessage]`
  source-gen (ADR-0050).
  **Depends on:** T011.
- **[T014] [US1]** Run T009 green. Run the T002 suite again — still green,
  unmodified.
  **Depends on:** T012, T013.

### 2c — Gap 2: the resubscribe, with the red that proves it

**This ordering is a requirement, not a preference** (plan §"Proving the red").
A test written before T011 goes red at gap 1 and proves nothing about gap 2.

- **[T015] [US1]** `tests/Integration.Tests/EventIngestion/MqttResubscribeAfterBrokerOutageIntegrationTests.cs`
  — **new.** Publish event A, wait until stored (the control). Stop mosquitto via
  `ResourceCommandService.ExecuteCommandAsync("mosquitto",
  KnownResourceCommands.StopCommand, …)`; start it; wait with
  `WaitForResourceHealthyAsync(…, WaitBehavior.WaitOnResourceUnavailable, …)`
  — the default gives up on exactly the transition being waited for (#2038).
  Publish event B with a different unique `kind`; assert it reaches
  `events_hamburg` within a polled 90 s deadline. `finally` restores mosquitto
  and waits healthy whatever happened (the pattern and its rationale are in
  `RestartLosesNothingIntegrationTests.RestartAsync`). The assertion message must
  name the diagnosis: reconnected but did not resubscribe — connected, healthy,
  receiving nothing.
  **Depends on:** T014.
- **[T016] [US1]** **Run T015 against the T014 build — the one that reconnects
  and does not resubscribe — and observe it fail.** Capture the verbatim output
  plus the `event-ingestion` log showing a successful reconnect and event B never
  arriving. **This output is the PR's central evidence.** It also discharges
  assumption A2: a pass here would disprove "the restarted broker retains no
  session" and must be reported rather than worked around.
  **Depends on:** T015.
- **[T017] [US1]** Add the resubscribe to `MqttConnectionLoop` — `SubscribeAsync
  (options.SubscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce)` after **every**
  successful connect, unconditionally. Do **not** branch on
  `MqttClientConnectResult.IsSessionPresent`: a SUBSCRIBE on a session that
  already holds the filter is idempotent at the broker, so unconditional is both
  simpler and strictly safer than a conditional that can be wrong. Add the
  companion assertion to `MqttConnectionLoopTests` — `SubscribeAsync` called once
  per connect, twice across a reconnect.
  **Depends on:** T016.
- **[T018] [US3]** `src/EventIngestion/Infrastructure/Log.cs` — add
  `MqttSubscriberResubscribed(topic)`, distinct from `MqttSubscriberConnected`,
  so a connect-without-resubscribe is readable from the log alone (US3-AC1).
  **Depends on:** T017. Same file as T013 — **not** `[P]` with it.
- **[T019] [US1]** Run T015 green. Re-run T009 and the T002 suite.
  **Depends on:** T017, T018.

---

## Phase 3 — US2 (P2): the simulator says what it dropped

Disjoint from every EventIngestion file, so `[P]` with phase 2 once phase 1
lands — though the same engineer will likely run them in sequence.

- **[T020] [P] [US2]** `tests/ScenarioSimulator.Tests/MqttPublisherDropAccountingTests.cs`
  — **new, must be observed red.** US2-AC1 (disconnected publish is dropped and
  counted, nothing thrown to the caller); US2-AC2 (one summary warning naming the
  count on reconnect, **not** one line per drop); US2-AC3
  (`MqttClientNotConnectedException` from the check/publish race is caught by its
  specific type and counted). Report the verbatim failure.
  **Depends on:** T008.
- **[T021] [P] [US2]** `src/ScenarioSimulator/Mqtt/MqttPublisher.cs` — plain
  `IMqttClient`; own connect loop (same backoff, no resubscribe — the deliberate
  duplication of decision 3, **with a one-line comment saying so** so a reviewer
  does not extract a shared client); `EnqueueAsync` → `IsConnected` check +
  `PublishAsync` + drop counter; remove `ConnectingFailedAsync`, mint per attempt.
  **Narrow the catch** from `Exception ex when (ex is not
  OperationCanceledException)` (line 82) to `MqttClientNotConnectedException`
  only — a strict tightening; anything else now surfaces.
  **Depends on:** T020.
- **[T022] [P] [US2]** `src/ScenarioSimulator/Log.cs` — add
  `MqttSamplesDropped(count, seconds)` at **Warning**.
  **Depends on:** T020.
- **[T023] [US2]** Run T020 green.
  **Depends on:** T021, T022.

---

## Phase 4 — Whole-tree verification

- **[T024] [US1]** `dotnet build -c Release` clean under
  `TreatWarningsAsErrors` — analyzers, `BannedSymbols.txt`,
  `PrimitiveBoundaryTests`, `HandlerDeconstructionTests`. Note: an S125 Release
  analyzer error on a file outside the diff has been flaky before; re-run the same
  SHA once before treating it as real.
  **Depends on:** T019, T023.
- **[T025] [US1]** **Full** integration suite, not just the MQTT tests — the
  fixture is shared and T015's stop/start could disturb neighbours if its
  `finally` is wrong. That is exactly what a full run detects.
  **Depends on:** T024.
- **[T026] [US1]** **FR-022 on a live stack.** `OutageRecoveryIntegrationTests`
  and `RestartLosesNothingIntegrationTests` green — both are only reachable if
  the ACK was withheld while the write could not succeed and the broker
  redelivered, which is the deferred-ACK mechanism observed end to end. Plus
  `IngestThroughputMeasurementTests` for the load half.
  **Depends on:** T025.
- **[T027] [US1]** **NFR-005 on a live stack**, as spec 006 actually writes it —
  process restart resumes from the broker. `RestartLosesNothingIntegrationTests`,
  green, **not** substituted by T015. They test different things.
  **Depends on:** T025.
- **[T028] [US1]** **Latency leg.** `IngestThroughputMeasurementTests`,
  `NFR001_AuditIngestLatencyTests` and `NFR002_MqttConnectAuthTests`, **each run
  twice**, figures recorded against the **Event → overlay state ≤ 200 ms** leg and
  compared to T002's before-values. A single run after machine churn looks exactly
  like a regression.
  **Depends on:** T025.
- **[T029] [US1]** Confirm nothing MQTT-related remains:
  `grep -rn "MQTTnet.Client\|ManagedClient\|MqttFactory\|EnqueueAsync\|
  ConnectingFailedAsync" --include=*.cs --include=*.csproj --include=*.props .
  | grep -v obj/ | grep -v bin/` returns nothing outside `specs/`.
  **Depends on:** T024.

---

## Phase 5 — Hand back

- **[T030]** Phase 5 verification note: the manual run-mode procedure from
  `spec.md` §"Independent end-to-end test procedure", including step 9 (the
  simulator, which no automated test can reach — `ci.yml:250` disables it and the
  AppHost does not build it under the fixture).
  **Depends on:** T026, T027, T028.
- **[T031]** PR body carries: T002's green-before output; **T016's red output**
  (the resubscribe failing against a reconnecting build — the central evidence);
  T009's and T020's red output; the twice-run latency figures; and the note that
  the **ADR-0096 addendum is not in this PR** and is drafted separately for human
  review (ADR-0144).
  **Depends on:** T029, T030.

---

## Dependency summary

```
T001 → T002 ─┬─ GATE (green before)
             └→ T003 ─┬→ T004 [P]
                      ├→ T005 [P]
                      ├→ T006 [P]
                      └→ T007 [P]  ──→ T008 ─┬→ T009 → T010 → T011 ─┬→ T012 → T014
                                             │                      └→ T013 ↗
                                             │        T014 → T015 → T016 → T017 → T018 → T019
                                             └→ T020 [P] ─┬→ T021 [P] ─┬→ T023
                                                          └→ T022 [P] ─┘
T019 + T023 → T024 → T025 → {T026, T027, T028} → T030 → T031
                    T024 → T029 ──────────────────────────↗
```

**Foundational and blocking:** T001–T008. The package move is solution-wide;
nothing compiles until it is complete, so no implementation task can start early.

**Genuinely parallel:** T004/T005/T006/T007 (four disjoint file sets after T003);
and the whole US2 chain T020–T023 against the US1 chain T009–T019 (ScenarioSimulator
and EventIngestion share no file).

**Deliberately serial and easy to get wrong:** T015 → T016 → T017. Reordering
these destroys the only evidence that the resubscribe is load-bearing.

---

## Phase 3 gate (ADR-0037)

Tasks atomic; **the feature's issue #1139 must be on Project #13** — added by
hand, `/speckit-tasks` adds nothing to the board. Per-task issues are not created
(the practice stopped after spec 028).

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/1139
```

**Not done by this phase — the board was explicitly out of scope for this run.**
Verify with `--limit 2000`; `item-list` defaults to 30 and a filled board looks
empty otherwise.

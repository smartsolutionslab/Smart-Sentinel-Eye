# Spec 079 — A reconnect that resubscribes

**Issue:** #1139 · **Branch:** `feat/1139-a-reconnect-that-resubscribes`
**Phase:** 1 (Specify) · **Date:** 2026-09-06 · **Base:** `origin/develop` @ `f25e468e`
**ADRs:** ADR-0096 (MQTTnet as the .NET MQTT client — **needs an addendum**, see
§"What the ADR-0096 addendum must record"), ADR-0095 (Mosquitto broker),
ADR-0100 (go-auth plugin; `azp == username`, JWT-as-password), ADR-0036
(smallest change, no speculative generality — the reason there are two reconnect
loops and not one abstraction), ADR-0037 (phased workflow), ADR-0049
(`CancellationToken` last), ADR-0050 (`[LoggerMessage]` source-gen),
ADR-0052 / ADR-0103 (xUnit + Shouldly; Aspire fixture, no Testcontainers),
ADR-0084 (code metrics), ADR-0105 (`Ensure.That`), ADR-0109 (parallel markers),
ADR-0139 (new behaviour starts red; refactors stay green), ADR-0141 (NRT on),
ADR-0144 (autonomous lane — **no ADR is written by this work**).

**The decision is already taken and is not re-opened here.** The repository owner
chose **option (a): hand-roll a reconnect/resubscribe wrapper around
`IMqttClient`, staying on the official `MQTTnet` package** (2026-09-06).
Options (b) an unofficial third-party managed client and (c) stay on
`4.3.7.1207` were both put and declined. The reasoning on record is that
ADR-0096 chose MQTTnet partly for its `dotnet`-org provenance, so replacing the
removed part with an unofficial fork would contradict that rationale rather than
preserve it. Nothing found during this phase makes that untenable.

---

## Premise check

Four consecutive issues in this lane rested on stale premises, so every claim the
issue makes was re-checked against the tree at `f25e468e` before anything was
planned. **The issue's core claim holds. Five of its supporting details do not.**

### Verified as filed

| Claim | Verified how | Result |
|---|---|---|
| `MQTTnet.Extensions.ManagedClient` stops at `4.3.7.1207` | flat-container index | **Holds** — last version `4.3.7.1207` |
| MQTTnet itself is at `5.2.0.1603` | flat-container index | **Holds** — `5.0.1.1416`, `5.1.0.1559`, `5.2.0.1603` |
| v5 exports no "Managed" anything | `MetadataLoadContext` over `~/.nuget/packages/mqttnet/5.2.0.1603/lib/net10.0/MQTTnet.dll` | **Holds** — zero exported types whose full name contains `Managed` |
| `MQTTnet.Client` namespace is gone | same dump | **Holds** — namespaces are `MQTTnet`, `.Adapter`, `.Certificates`, `.Channel`, `.Diagnostics.*`, `.Exceptions`, `.Formatter*`, `.Implementations`, `.Internal`, `.LowLevelClient`, `.PacketDispatcher`, `.Packets`, `.Protocol`. No `.Client`. |
| `MqttFactory` becomes `MqttClientFactory` | same dump | **Holds** — `MqttFactory` absent, `MqttClientFactory` present |
| `ConnectingFailedEventArgs` does not exist on the plain client | same dump | **Holds** — absent from the assembly entirely |
| `4.3.7.1207` is what is pinned | `Directory.Packages.props:70-71` | **Holds** — both packages pinned at `4.3.7.1207` |
| No advisory pressure | `dotnet restore -p:Configuration=Release` on the tree | **Holds** — zero `NU1902`/`NU1903`, zero warnings of any kind |

### Corrections — things the issue's table and prose get wrong

**1. The file table lists four files. Eleven are affected.** The table names the
three managed-client dependents and one test. In fact **seven test/fixture files**
import `MQTTnet.Client` and construct `new MqttFactory()`, not one, and an
**eighth** test file reads the managed options shape:

| # | File | What breaks | In the issue's table? |
|---:|---|---|---|
| 1 | `src/EventIngestion/Infrastructure/Ingress/MosquittoConnectionFactory.cs` | managed client + options + factory | yes |
| 2 | `src/EventIngestion/Infrastructure/Ingress/MqttSubscriberHostedService.cs` | managed client, queued subscribe, `ConnectingFailedAsync` | partly |
| 3 | `src/ScenarioSimulator/Mqtt/MqttPublisher.cs` | managed client, `EnqueueAsync`, `ConnectingFailedAsync` | yes |
| 4 | `tests/Integration.Tests/Identity/NFR002_MqttConnectAuthTests.cs` | namespace + `MqttFactory` | yes |
| 5 | `tests/Integration.Tests/Fixtures/PlantFloor.cs` | namespace + `MqttFactory` | **no** |
| 6 | `tests/Integration.Tests/EventIngestion/DeadLetterFabScopingIntegrationTests.cs` | namespace + `MqttFactory` | **no** |
| 7 | `tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs` | namespace + `MqttFactory` | **no** |
| 8 | `tests/Integration.Tests/EventIngestion/OutageRecoveryIntegrationTests.cs` | namespace + `MqttFactory` | **no** |
| 9 | `tests/Integration.Tests/EventIngestion/PoisonDeliveryEscapeIntegrationTests.cs` | namespace + `MqttFactory` | **no** |
| 10 | `tests/Integration.Tests/EventIngestion/RestartLosesNothingIntegrationTests.cs` | namespace + `MqttFactory` | **no** |
| 11 | `tests/EventIngestion.Infrastructure.Tests/MosquittoConnectionFactoryTests.cs` | reads `Options.ClientOptions` — a `ManagedMqttClientOptions` shape | **no** |

Plus two project files that must drop the package reference:
`src/EventIngestion/Infrastructure/SmartSentinelEye.EventIngestion.Infrastructure.csproj:33`
and `src/ScenarioSimulator/SmartSentinelEye.ScenarioSimulator.csproj:23`.

Reproduce:

```sh
grep -rln "MQTTnet.Client\|ManagedClient" --include=*.cs --include=*.csproj . \
  | grep -v obj/ | grep -v bin/
```

**2. `ConnectingFailedAsync` is attributed to `MqttPublisher` alone. The
subscriber uses it too, and its use there is the more load-bearing one.**
`MqttSubscriberHostedService.cs:60,79,138-160` subscribes to it and re-mints the
JWT on a *refused* CONNECT. The doc comment on that handler records why it was
added: a subscriber that never achieved a first connection re-presented the same
dead credential every five seconds forever, and only a restart recovered it.
That path became reachable **on purpose** when the startup mint stopped being
fatal (`MosquittoConnectionFactory.cs:37-55`, #2038). So this is not a
simulator-only concern — losing it silently reintroduces a known, fixed defect.

**3. "`SubscribeAsync(topic, qos)` confirmed still compatible" is true but
misleading, and it hides the sharpest break.** The overload survives, as an
extension method — `MQTTnet.MqttClientExtensions.SubscribeAsync(IMqttClient,
String, MqttQualityOfServiceLevel, CancellationToken)`. What does *not* survive
is the calling **order**. The subscriber today calls `SubscribeAsync` **before**
`StartAsync`:

```
MqttSubscriberHostedService.cs:63   await client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
MqttSubscriberHostedService.cs:64   await client.StartAsync(connection.Options);
```

That is a managed-client affordance: the subscription is *queued* and applied on
every connect. On a plain `IMqttClient` the same call before a connection throws.
So the port is not "keep the call, change the namespace" — the call has to move
to *after* each successful connect, which is the same code as gap 2. **Gap 2 is
therefore not an optional extra that could be forgotten; it is structurally
forced.** What can still be forgotten — and is the real risk — is doing it after
the *second* and subsequent connects rather than only the first.

**4. NFR-005 is not "auto-reconnect".** The issue's acceptance section says
"NFR-005 (auto-reconnect)". Spec 006 `spec.md:381-384` actually says:

> **NFR-005** Process restart: subscriber resumes from the broker (QoS 1,
> persistent session), no ingestion gap on rolling deploys. HTTP returns 503 only
> between ASP.NET `Started` and `Listening` (< 1 s on a warm container).

That is a claim about **session resumption across a restart of our own service**,
already covered by `RestartLosesNothingIntegrationTests`. Auto-reconnect after a
*broker* outage is a real requirement of this work, but it is **not NFR-005 and
has no covering test today.** Conflating them would let a green
`RestartLosesNothing` be mistaken for evidence that reconnect works. The
verification plan keeps them separate.

**5. One thing the issue does not mention that changes the verification plan.**
`ScenarioSimulator` **does not run under the integration fixture and does not run
in CI.** The AppHost guards it with `isRunMode && !isE2ETests &&
isScenarioSimulatorEnabled` (`AppHost.cs:560`), the fixture boots with
`E2ETests=true` (`AspireFixture.cs:124`), and `ci.yml:250` passes
`ScenarioSimulator=false`. So `MqttPublisher` has **no automated end-to-end
coverage at all** and cannot acquire any within this slice. Its port is provable
only by unit tests over the drop-accounting plus one manual run-mode boot.

### One more verified fact, load-bearing for the test design

Under the fixture, mosquitto is **not** persistent and has **no data volume** —
`AppHost.cs:228-233` applies `ContainerLifetime.Persistent` and
`WithVolume("mosquitto-data", …)` only when `isRunMode && !isE2ETests`, and the
fixture sets `E2ETests=true`. **A stopped-and-started mosquitto under the fixture
therefore comes back with no session state and no subscriptions.** That is what
makes a missing resubscribe detectable rather than merely suspected — see
US1-AC3.

### Things that survive the move (checked, not assumed)

Confirmed present on `MQTTnet 5.2.0.1603`, `lib/net10.0`, and **not** carrying
`[Obsolete]`: `MqttApplicationMessageReceivedEventArgs.AutoAcknowledge`,
`.AcknowledgeAsync(CancellationToken)` (**FR-022's deferred ACK survives intact**),
`MqttApplicationMessage.PayloadSegment` (still `ArraySegment<byte>`, still
implicitly convertible to the `ReadOnlyMemory<byte>` the parser takes),
`IMqttClientCredentialsProvider`, `MqttClientOptionsBuilder.{WithClientId,
WithTcpServer, WithCredentials(IMqttClientCredentialsProvider), WithCleanSession,
WithKeepAlivePeriod, WithTimeout, WithTlsOptions(Action<…>)}`,
`MqttClientTlsOptionsBuilder.UseTls(bool)`, `MqttApplicationMessageBuilder.
{WithTopic, WithPayload(String), WithQualityOfServiceLevel}`,
`MqttClientConnectResultCode`, `MqttClientDisconnectedEventArgs.{Reason,
Exception, ClientWasConnected}`, and `MqttClientConnectResult.IsSessionPresent`.

`tests/Architecture.Tests/BoundaryTests.cs` allows the dependency by the prefix
`"MQTTnet"` (lines 204, 296, 322, 347), which matches both the old and the new
assembly set. **No architecture-test change is expected**; phase 4 confirms.

---

## The gaps, restated as what an operator would see

| # | Gap | Symptom if unaddressed | Detectable by |
|---:|---|---|---|
| 1 | No auto-reconnect | Broker blips; subscriber never returns; ingestion stops until the pod is restarted | The new outage test; nothing in production today |
| 2 | **No auto-resubscribe** | Subscriber reconnects, `IsConnected == true`, logs "connected", **receives nothing, forever** | **Only** an end-to-end assertion that an event published *after* the reconnect is stored |
| 3 | No offline queue | `PublishAsync` throws `MqttClientNotConnectedException` where `EnqueueAsync` buffered | Simulator unit tests |
| 4 | No `ConnectingFailedAsync` | A refused CONNECT no longer triggers a JWT re-mint; #2038's loop-on-a-dead-credential returns | Unit test on the connect loop |

**Gap 2 is the dangerous one, and this spec refuses to treat it as a comment.**
A subscriber that reconnects without resubscribing is connected, healthy-looking,
and silent — the failure class of #2109 and #2111. There is **no MQTT health
check in EventIngestion** (`grep -rn "AddHealthChecks\|IHealthCheck"
src/EventIngestion/` returns nothing), so nothing in the running system would
report it. It surfaces as "events stopped arriving" hours later, with a green
dashboard.

---

## User stories

### US1 — P1: the subscriber keeps receiving after the broker comes back

**As** a fab operator, **when** the MQTT broker restarts, **I want** event
ingestion to resume on its own, **so that** a broker maintenance window does not
silently stop the event → overlay path until someone restarts a pod.

This is the shippable slice and the whole risk of the issue.

**Why it cannot be sliced smaller.** Central package management pins MQTTnet once
for the solution (`Directory.Packages.props:70`), and `Integration.Tests`
references `EventIngestion.Infrastructure`, so a single test assembly cannot hold
both major versions. The mechanical port of all eleven files is therefore an
indivisible foundation for US1, US2 and US3, and all three land in one PR. This
is recorded rather than worked around: a per-project version override would put
two MQTTnet majors in one process, which is worse than a slightly larger slice.

#### Acceptance scenarios

```gherkin
Scenario: US1-AC1 — the happy path is unchanged (characterisation)
  Given the Aspire fixture is running with mosquitto healthy
  When a device publishes a well-formed event on fab/hamburg/plc/dev-1
  Then the event is stored exactly once in events_hamburg
  And the observable behaviour is identical to MQTTnet 4.3.7.1207
```

```gherkin
Scenario: US1-AC2 — the subscriber reconnects after a broker outage
  Given the subscriber is connected and receiving
  When the mosquitto resource is stopped and then started again
  Then the subscriber reconnects without any process restart
  And it logs a reconnect rather than repeating "connect failed" forever
```

```gherkin
Scenario: US1-AC3 — the subscriber RESUBSCRIBES after the reconnect
  Given the subscriber has reconnected to a broker that holds no session state
    And the broker therefore holds no subscription for the event-ingestion client
  When a device publishes a NEW event on fab/hamburg/plc/dev-1
  Then that event is stored in events_hamburg within the deadline
  # This is the scenario that fails when only gap 1 is fixed. The client is
  # connected, the service is healthy, and the broker discards the publish
  # because nobody is subscribed. Nothing but this assertion distinguishes it.
```

```gherkin
Scenario: US1-AC4 — a refused CONNECT re-mints the credential (conflict / auth)
  Given the broker refuses CONNECT because the presented JWT is not acceptable
  When the connect loop retries
  Then it obtains a credential again before the next attempt
  And it does not re-present the same rejected credential unchanged
  And it keeps retrying rather than giving up
```

```gherkin
Scenario: US1-AC5 — a malformed delivery is still dead-lettered (bad request)
  Given the subscriber is connected
  When a device publishes a payload that is not valid JSON
  Then the delivery is recorded in dead_letters and acknowledged
  And ingestion continues for well-formed deliveries
```

```gherkin
Scenario: US1-AC6 — the broker still rejects an unauthorised client (auth)
  Given a client presents a JWT whose azp does not match its MQTT username
  When it attempts CONNECT
  Then the broker refuses it
  And the CONNECT → CONNACK timing still satisfies NFR002_MqttConnectAuthTests
```

```gherkin
Scenario: US1-AC7 — deferred ACK still absorbs a storage outage (FR-022)
  Given one fab's event storage is unavailable
  When 20 events are published for that fab
  Then none is acknowledged to the broker while the write cannot succeed
  And after storage returns, all 20 are stored exactly once
```

```gherkin
Scenario: US1-AC8 — the host still starts when Keycloak is away
  Given Keycloak refuses to mint a token at startup
  When the event-ingestion host starts
  Then the host reaches Running rather than FailedToStart
  And the subscriber connects once a credential becomes obtainable
  # Preserves #2038. The connect must NOT be awaited inside IHostedService.StartAsync.
```

### US2 — P2: the simulator says what it dropped

**As** a developer running the simulator, **when** the broker is away, **I want**
to be told how many samples were dropped, **so that** a gap in the wall is
explained rather than mysterious.

`ScenarioSimulator` is dev-only (`ci.yml:250` disables it; the AppHost only
builds it in run mode), so **accepting the drop is the chosen answer** — see
design decision 2 in `plan.md`. What is not acceptable is a swallowed exception.

```gherkin
Scenario: US2-AC1 — a sample published while disconnected is dropped and counted
  Given the publisher is not connected to the broker
  When a billet sample is published
  Then the sample is discarded
  And a dropped-sample counter is incremented
  And the process does not throw MqttClientNotConnectedException to its caller
```

```gherkin
Scenario: US2-AC2 — the operator is told once, not 20 times a second
  Given the broker has been away for a period during which N samples were dropped
  When the publisher reconnects
  Then exactly one warning reports N dropped samples and the outage duration
  And the per-sample path did not emit a log line per drop
```

```gherkin
Scenario: US2-AC3 — a disconnect racing the publish is counted, not swallowed
  Given the connection drops between the connected check and the publish
  When MqttClientNotConnectedException is raised
  Then it is caught by its specific type, counted as a drop, and not rethrown
  And no broader exception type is caught on that path
```

### US3 — P3: the reconnect is visible

**As** an operator reading logs, **I want** connect, disconnect, retry and
resubscribe to be distinguishable, **so that** "connected but silent" is
diagnosable from the log alone.

```gherkin
Scenario: US3-AC1 — the resubscribe is logged distinctly from the connect
  Given the subscriber reconnects after an outage
  Then the log contains a connect line and a separate resubscribe line naming the topic
  And a reader can tell a connect-without-resubscribe from a complete recovery
```

---

## Independent end-to-end test procedure

Runnable by a human with no knowledge of the implementation. Requires Docker and
≥ 6 GB free on `C:` (this machine had 16 GB at planning time).

1. From `D:\Github\wt-1139`, stop any running AppHost, then
   `dotnet run --project src/AppHost/SmartSentinelEye.AppHost.csproj -c Release`.
2. Wait for `event-ingestion` and `mosquitto` to report healthy on the dashboard.
3. In the dashboard, confirm `event-ingestion` logs `MqttSubscriberStarted` and
   `MqttSubscriberConnected`.
4. Publish a probe event and confirm it is stored — publish on
   `fab/hamburg/plc/dev-1` with a unique `kind` and query `events_hamburg`.
5. **Provoke the outage.** On the dashboard, use the mosquitto resource's **Stop**
   command (or `docker stop` the mosquitto container). Confirm `event-ingestion`
   logs a disconnect and then repeated connect attempts with a *growing* delay.
6. Leave it down for ~60 s — long enough that the backoff has reached its cap.
7. **Start** mosquitto again. Confirm `event-ingestion` logs a reconnect **and** a
   resubscribe naming `fab/+/+/+`, within one backoff period.
8. **The step that matters.** Publish a *second* probe event with a *different*
   unique `kind`, and confirm it lands in `events_hamburg`. If step 7 logged a
   reconnect but this event never arrives, gap 2 is unfixed — which is precisely
   the state a "we subscribed at startup" test would call passing.
9. Simulator (US2): boot with the simulator enabled, stop mosquitto for ~30 s,
   and confirm exactly one warning naming a dropped-sample count appears after it
   returns — not one line per sample, and no unhandled exception.

---

## Locked tech choices this spec does not revisit

MQTTnet (official `dotnet`-org package) as the client — ADR-0096. Mosquitto as
the broker — ADR-0095. Keycloak-minted JWT as the MQTT password with
`azp == username` — ADR-0100. xUnit + Shouldly, Aspire fixture, no
Testcontainers — ADR-0052, ADR-0103. `Ensure.That` for argument guards —
ADR-0105. `[LoggerMessage]` source-gen — ADR-0050. NRT on — ADR-0141.

**Target version: `MQTTnet 5.2.0.1603`** — the newest published, already in the
local NuGet cache, and the version the issue's evidence was gathered against.

---

## Latency-budget impact — **not N/A**

The MQTT subscriber sits on the **Event → overlay state ≤ 200 ms** leg
(constitution §IV). §VII's dashboard rule binds implemented legs, and this one is
implemented, so the obligation attaches here.

**The steady-state per-message path does not change shape.** The same handler
runs, reading `args.ApplicationMessage.PayloadSegment` (unchanged type), setting
`args.AutoAcknowledge = false` and calling `args.AcknowledgeAsync` (both present
and un-obsoleted on v5), writing to the same bounded channel. Nothing is added to
the per-delivery path — the reconnect loop runs only while disconnected.

**But "does not change shape" is not a measurement**, and a client-library major
version can move wire-level defaults. Phase 5 must therefore re-run the existing
measurements rather than reason about them:

- `tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs`
- `tests/Integration.Tests/AuditObservability/NFR001_AuditIngestLatencyTests.cs`
- `tests/Integration.Tests/Identity/NFR002_MqttConnectAuthTests.cs` — gates on
  CONNECT → CONNACK timing, the figure most exposed to a wire-level default change.

**Run each twice.** The first run after machine churn looks exactly like a
regression; a single run is not evidence either way.

---

## What the ADR-0096 addendum must record

**This spec does not write it** — ADR-0144 forbids the autonomous lane from
writing ADRs, and so does this phase. What follows is the fact set, so whoever
drafts it is not re-deriving the evidence. The addendum's PR is held for human
review rather than merged on green CI.

1. **What was removed, and that it was removed rather than renamed.**
   `MQTTnet.Extensions.ManagedClient`'s flat-container index stops at
   `4.3.7.1207`. `MQTTnet 5.2.0.1603`'s `lib/net10.0/MQTTnet.dll` exports no type
   whose full name contains `Managed`. It was not folded into the core package.
2. **Which of ADR-0096's stated rationales this touches, and which it does not.**
   The `dotnet`-org provenance argument is *unaffected* — the core package is
   still `dotnet/MQTTnet`, MIT, and actively released. What the removal costs is
   convenience, not provenance. The addendum should say this plainly, because the
   issue framed the removal as undercutting ADR-0096's reasoning, and on
   inspection it does not: it undercuts an assumption about the *package set*,
   not about the maintainer.
3. **The decision taken, with the alternatives that were declined.** Option (a),
   hand-rolled reconnect/resubscribe on the official package. Options (b)
   third-party managed client and (c) stay on 4.3.7.1207 were put and declined,
   (b) specifically because an unofficial fork would contradict the provenance
   rationale.
4. **That there was no advisory pressure**, so this was a planned move and not a
   forced one: `dotnet restore -p:Configuration=Release` on `4.3.7.1207` reports
   zero `NU1902`/`NU1903`.
5. **The replacement's shape and the four behaviours it must supply**, so a later
   reader knows what the hand-rolled code is for: reconnect with capped jittered
   backoff and no attempt limit; unconditional resubscribe after every successful
   connect; per-attempt credential mint replacing `ConnectingFailedAsync`; and
   the explicit decision *not* to replace the offline publish queue.
6. **One claim in ADR-0096 that is now stale and should be corrected while the
   file is open.** ADR-0096's Decision section cites `MQTTnet.AspNetCore` as
   mapping onto the per-context `Add<Context>Infrastructure()` pattern
   (ADR-0051). That package has never been referenced — `Directory.Packages.props`
   pins only `MQTTnet` and `MQTTnet.Extensions.ManagedClient`. A rationale that
   rests on an unused package is the same clerical drift the constitution's §IV
   leg table and the Phase 3 board gate both suffered.
7. **The version moved to:** `MQTTnet 5.2.0.1603`, and the header's version claim
   updated from "MQTTnet 4.3.x" so the ADR does not go on naming a major the repo
   has left.

---

## What this spec does not do

- **It does not write the ADR-0096 addendum.** See above.
- **It does not add an MQTT health check.** EventIngestion has none today. One
  would be a genuine improvement — it is the thing that would have caught gap 2
  in production — but it is new behaviour beyond this issue and belongs in its
  own issue. Recorded here so its absence is a decision, not an oversight.
- **It does not add an offline publish buffer.** See design decision 2 in `plan.md`.
- **It does not extract a shared MQTT client abstraction.** See design decision 3.
- **It does not close #1135**, from which #1139 was split.

## Assumptions, marked

- **A1.** `MQTTnet 5.2.0.1603` behaves compatibly at the wire level for MQTT
  3.1.1 with `cleanSession=false` against Mosquitto + go-auth. Verified only by
  API surface at phase 1; **phase 5's live run is what actually tests it**, and
  `NFR002` is the specific gate.
- **A2.** Stopping and starting the mosquitto Aspire resource under the fixture
  yields a broker with no retained session. Derived from `AppHost.cs:228-233`
  plus `AspireFixture.cs:124`; phase 4 confirms it by observing the resubscribe
  test fail against a reconnect-without-resubscribe build (see plan §"Proving the
  red").
- **A3.** After a mosquitto restart the go-auth plugin may refuse the first
  CONNECTs until it has re-fetched the realm JWKS. The connect loop must
  therefore retry **without a maximum attempt count**; a loop that gives up would
  turn a transient JWKS fetch into a permanent outage.

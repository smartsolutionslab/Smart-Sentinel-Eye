# Plan 079 — A reconnect that resubscribes

**Phase:** 2 (Plan) · **Date:** 2026-09-06 · **Spec:** `specs/079-a-reconnect-that-resubscribes/spec.md`
**Bounded contexts:** EventIngestion (Infrastructure only) + ScenarioSimulator
(a dev-only worker, not a bounded context) + the test assemblies.

**No Domain or Application code changes.** Nothing here touches an aggregate, a
value object, a handler, a contract or a migration. There is no domain event and
no integration event, so ADR-0040 / ADR-0073's domain → integration mapping does
not apply to this work. No cross-context reference is created; the change is
confined to one context's Infrastructure layer plus a dev worker.

---

## The four design questions, answered

### Design decision 1 — how a test catches a missing resubscribe

**The test asserts an event published *after* the reconnect is stored. Nothing
weaker works.**

The failure mode is silent by construction: the client is connected, `IsConnected`
is `true`, the log says "connected", there is no MQTT health check to go amber,
and the broker discards the publish because it holds no matching subscription. No
exception is thrown anywhere. So every indirect signal — connection state, a
health endpoint, a call count at startup, a log line — reads exactly the same in
the broken world and the working one.

**Why the fixture makes this decisive rather than probabilistic.** Under the
integration fixture mosquitto is a throwaway container: `AppHost.cs:228-233`
applies `ContainerLifetime.Persistent` and the `mosquitto-data` volume only when
`isRunMode && !isE2ETests`, and `AspireFixture.cs:124` boots with
`E2ETests=true`. A stop/start therefore yields a broker with **no session state
at all** — `IsSessionPresent` will be false on the reconnect CONNACK and the
broker holds no subscription for `event-ingestion`. There is no window in which
a stale subscription could mask the bug.

The test, `MqttResubscribeAfterBrokerOutageIntegrationTests`:

1. Publish event **A** with a unique `kind`; wait until it is in `events_hamburg`.
   *This is the control.* Without it, a later absence could be explained by
   "publishing never worked".
2. Stop mosquitto via `ResourceCommandService.ExecuteCommandAsync("mosquitto",
   KnownResourceCommands.StopCommand, …)`.
3. Start it again, then
   `aspire.App.ResourceNotifications.WaitForResourceHealthyAsync("mosquitto",
   WaitBehavior.WaitOnResourceUnavailable, …)`. **`WaitOnResourceUnavailable` is
   mandatory** — the default gives up on exactly the transition being waited for
   (#2038, and the comment in `LogTailDeliversIntegrationTests.WaitForHealthyAsync`
   records it).
4. Publish event **B** with a *different* unique `kind`.
5. Assert **B** reaches `events_hamburg` within a polled deadline (90 s, matching
   `OutageRecoveryIntegrationTests`' recovery window; the backoff cap is 30 s so
   two attempts fit).
6. `finally`: start mosquitto and wait healthy, whatever happened — copied from
   `RestartLosesNothingIntegrationTests.RestartAsync`, whose comment records that
   the first version without the `finally` left the resource down on CI and every
   later EventIngestion test failed with an unrelated error.

**The assertion message carries the diagnosis**, because this test failing means
one specific thing:

> event B never arrived after the broker returned — the subscriber reconnected
> but did not resubscribe, so it is connected, healthy, and receiving nothing

**A cheap fast-lane companion, not a substitute.** A unit test over the connect
loop drives a fake connection through connect → drop → connect and asserts
`SubscribeAsync` was called **twice**, once per connect. That catches a
regression in milliseconds without Docker. It is explicitly *not* the proof: it
tests our own loop against our own fake, and cannot see a broker that silently
drops publishes. Both are required.

**Explicitly rejected as insufficient:** asserting `SubscribeAsync` was called at
startup; asserting `IsConnected` after the outage; asserting a "reconnected" log
line; asserting the health endpoint is 200. All four pass in the broken world.

### Design decision 2 — the offline queue: **accept the drop, count it, report it once**

`ScenarioSimulator` is dev-only. It is disabled in CI (`ci.yml:250` passes
`ScenarioSimulator=false`) and is not built by the AppHost under the fixture
(`AppHost.cs:560`). It publishes a synthetic billet timeline at a steady cadence.

**Buffering would be worse than dropping, not merely more work.** A buffered
sample carries the `occurredAt` it was generated with. Replaying a minute of
backlog on reconnect would push a burst of stale readings at a system whose whole
point is a live wall — they would either render as a jump of stale overlays or be
rejected downstream. For a simulator, a gap in the timeline is the honest
representation of a broker outage.

**What the operator sees**, and this is the part that makes it a stated choice
rather than a swallow:

- `PublishAsync` checks `client.IsConnected`. If disconnected, it increments a
  drop counter and returns. **No log line per drop** — the timeline publishes
  fast enough that per-sample logging would bury the diagnosis it is meant to
  provide.
- On the next successful connect, exactly one **Warning**:
  `MqttSamplesDropped(count, outageDuration)` — "N samples dropped while the
  broker was away (M s)".
- The disconnect itself is already logged (`MqttPublisherDisconnected`), so the
  pair reads as: broker went away → broker came back, and here is what it cost.

**The exception path is narrowed, not widened.** A disconnect can race between the
`IsConnected` check and the publish. That raises
`MqttClientNotConnectedException`, which is caught **by its specific type** and
counted as a drop. The current code catches `Exception ex when (ex is not
OperationCanceledException)` (`MqttPublisher.cs:82`) and logs — a broad catch on
a hot path. Replacing it with a single named type is a strict tightening: any
other failure now surfaces instead of being logged and forgotten. This is
recorded because a caught-and-ignored `MqttClientNotConnectedException` is a
review blocker under the house rules, and the difference between that and this is
the counter, the summary warning, and the narrowed type.

**The counter is a field, not a metric.** No Prometheus counter is added — that
would be new observability surface for a dev-only tool, which is the speculative
generality ADR-0036 forbids.

### Design decision 3 — **two loops, not one wrapper**

The reconnect loop lives in **EventIngestion's Infrastructure** for the
subscriber, and **inside `MqttPublisher`** for the simulator. They are not shared.

**Three reasons, in order of weight.**

1. **There is nowhere to share it that is not a new problem.** The two callers
   live in assemblies with no common reference:
   `SmartSentinelEye.EventIngestion.Infrastructure` and
   `SmartSentinelEye.ScenarioSimulator`. A shared wrapper would have to go in
   `Shared.Kernel` — which CLAUDE.md defines as "value-object base types,
   `Result<T,E>`, no domain" — and would drag an MQTT client package into a
   project that all nine contexts reference. That is a worse outcome than
   duplication, and it is a boundary change nobody asked for.
2. **The needs genuinely differ.** The subscriber must resubscribe to
   `fab/+/+/+`, mints through `MqttTokenProvider` for the `event-ingestion`
   client, holds a `TokenHolder` read by a credentials provider, and must never
   block `IHostedService.StartAsync`. The publisher has no subscriptions, mints
   through `KeycloakTokenProvider` for `scenario-simulator`, and needs
   drop-accounting the subscriber has no use for. A shared abstraction would be
   mostly configuration hooks for a population of two.
3. **Two callers is the classic under-threshold for extraction** (ADR-0036, no
   speculative generality). The shared part is roughly twenty lines of backoff
   arithmetic.

**This duplication is deliberate and must be recorded in the code**, one line at
each site, so a later reviewer does not "fix" it by extracting a shared client.

**Within EventIngestion, the loop goes in its own file** —
`src/EventIngestion/Infrastructure/Ingress/MqttConnectionLoop.cs`.
`MqttSubscriberHostedService.cs` is already 360 lines and dense with the
message-handling path; grafting a connect loop onto it would obscure both.
(ADR-0084 documents a 300-LOC ceiling; the repo enforces S104 at SonarAnalyzer's
default rather than at 300, so this is a readability judgement, not a build
constraint — stated so the reasoning is not mistaken for a rule.)

### Design decision 4 — backoff shape, and why it cannot outlive the JWT

**Shape:** first attempt immediately, then `1s, 2s, 4s, 8s, 16s`, capped at
**30 s**, each delay multiplied by jitter in `[0.8, 1.2]`. The delay resets to
zero on every successful connect. **No maximum attempt count** — the loop retries
until its `CancellationToken` is cancelled by `StopAsync`.

- **Why exponential rather than the current fixed 5 s.** Each attempt mints or
  reads a token and opens a TCP connection to the broker. At the 250-camera
  target a fab-wide broker outage means many clients retrying in lockstep against
  one Keycloak and one Mosquitto. Backing off is what stops the recovery being
  the second outage.
- **Why jitter.** Without it, every client that dropped at the same moment
  retries at the same moment, forever. The jitter band is deliberately narrow —
  it is de-synchronisation, not a policy knob.
- **Why 30 s and not longer.** After a mosquitto restart the go-auth plugin
  re-fetches the realm JWKS and may refuse the first CONNECTs (assumption A3). A
  cap much beyond 30 s would turn a few seconds of JWKS fetch into a minutes-long
  ingestion gap for no benefit.
- **Why no attempt limit.** A subscriber that gives up is precisely the outage
  this issue exists to prevent, and A3 makes early failures expected rather than
  exceptional.

**The token cannot go stale across a backoff, and this is structural rather than
arithmetic.** The loop calls `MqttTokenProvider.GetAccessTokenAsync` **inside the
loop body, immediately before `ConnectAsync`** — after the delay, not before it.
So no delay, however long, sits between minting and presenting.

Two independent facts make this safe even if that ordering were later disturbed:

- `ClientCredentialsTokenProvider` (which `MqttTokenProvider` delegates to)
  **refreshes proactively at 80 % of `expires_in`** — its doc comment at
  `MqttTokenProvider.cs:11-16` records that this is exactly what keeps a reconnect
  from presenting an expired JWT.
- The realm's `accessTokenLifespan` is **3600 s**
  (`src/AppHost/Realms/smart-sentinel-eye-realm.json:5`), so the provider hands
  back a token with at least ~720 s of life. A 30 s cap is two orders of
  magnitude inside that.

**This also replaces `ConnectingFailedAsync` (gap 4) without needing an
equivalent event.** That handler existed only because the managed client owned
the connect and we could not intervene between attempts. Owning the loop, we mint
before every attempt unconditionally — which is strictly stronger than re-minting
after a failure, and closes #2038's "re-presented the same dead credential
forever" by construction rather than by a callback.

`TokenHolder` and `TokenCredentials` stay. `IMqttClientCredentialsProvider` is
synchronous and is read by MQTTnet during `ConnectAsync`, so the loop writes
`token.Value` and then connects. `MqttConnection.RefreshTokenAsync` and the
`OnDisconnectedAsync` / `OnConnectingFailedAsync` re-mint handlers are removed —
their job moves into the loop.

---

## Structure after the change

### EventIngestion / Infrastructure / Ingress

| File | Change |
|---|---|
| `MqttConnectionLoop.cs` | **New.** Owns: mint → connect → resubscribe → wait-for-disconnect → back off → repeat. Exposes the live `IMqttClient` and the connected state. Cancelled by the hosted service. |
| `MosquittoConnectionFactory.cs` | Builds a **plain** `IMqttClient` via `MqttClientFactory` plus `MqttClientOptions` (no longer `ManagedMqttClientOptions`). `MqttConnection` carries `IMqttClient` + `MqttClientOptions` + `TokenHolder` + `MqttTokenProvider`. `RefreshTokenAsync` removed. The non-fatal startup mint (#2038) stays exactly as it is. |
| `MqttSubscriberHostedService.cs` | `StartAsync` builds the connection and **launches the loop without awaiting a connect**, then returns. Message handling, dead-lettering, `AutoAcknowledge = false` and `MqttDeliveryCompletion` are untouched. `OnDisconnectedAsync` / `OnConnectingFailedAsync` token re-mints removed (moved into the loop). `StopAsync` cancels the loop, awaits it, disposes. |
| `Log.cs` | Add `MqttSubscriberResubscribed(topic)` and `MqttSubscriberRetryScheduled(delaySeconds, attempt)`. Keep the existing six MQTT messages. |

**The `StartAsync` constraint is load-bearing and easy to get wrong.** The plain
client's `ConnectAsync` throws on an unreachable broker. Awaiting it inside
`IHostedService.StartAsync` would fail the whole host — reintroducing #2038 from
the other direction (broker slow instead of Keycloak slow). The loop must be
launched as a background task and `StartAsync` must return. `US1-AC8` is the
scenario; `MosquittoConnectionFactoryTests` already guards the Keycloak half.

### ScenarioSimulator / Mqtt

| File | Change |
|---|---|
| `MqttPublisher.cs` | Plain `IMqttClient` + its own connect loop (same shape, no resubscribe). `EnqueueAsync` → `IsConnected` check + `PublishAsync`, with drop counting. `ConnectingFailedAsync` handler removed; token minted per attempt. |
| `Log.cs` | Add `MqttSamplesDropped(count, seconds)` at Warning. |

### Package + project files

| File | Change |
|---|---|
| `Directory.Packages.props:69-71` | `MQTTnet` → `5.2.0.1603`; **remove** the `MQTTnet.Extensions.ManagedClient` line; update the comment, which currently says "managed-client wrapper". |
| `…EventIngestion.Infrastructure.csproj:33` | Remove the `MQTTnet.Extensions.ManagedClient` reference. |
| `…ScenarioSimulator.csproj:23` | Same. |

### Tests

| File | Change |
|---|---|
| 7 integration/fixture files (spec table rows 4–10) | **Mechanical only:** drop `using MQTTnet.Client;`, `new MqttFactory()` → `new MqttClientFactory()`. |
| `MosquittoConnectionFactoryTests.cs` | Mechanical plus one navigation change — see the boundary below. |
| `MqttResubscribeAfterBrokerOutageIntegrationTests.cs` | **New** — design decision 1. |
| `MqttConnectionLoopTests.cs` | **New** — reconnect, resubscribe-per-connect, mint-per-attempt, backoff growth and reset. |
| `MqttPublisherDropAccountingTests.cs` | **New** — US2-AC1/2/3. |

---

## Boundary rules and conventions

- **No cross-context project reference is added.** The change stays inside
  `EventIngestion.Infrastructure` and the standalone `ScenarioSimulator` worker.
  `Architecture.Tests/BoundaryTests.cs` permits the MQTTnet dependency by the
  prefix `"MQTTnet"` (lines 204, 296, 322, 347), which still matches; phase 4
  confirms rather than assumes.
- **No `Shared.Contracts` change.** Nothing about the message shape moves.
- **Guards:** `Ensure.That(x).IsNotNull()` (ADR-0105). `ArgumentNullException.
  ThrowIfNull` is banned by `build/guards/BannedSymbols.txt` and fails the build.
- **Logging:** `[LoggerMessage]` source-gen in the existing `Log.cs` partial
  classes (ADR-0050). No `ILogger` string interpolation on a new path.
- **Async:** `CancellationToken` last (ADR-0049); no `ConfigureAwait`.
- **NRT on** (ADR-0141). New Infrastructure signatures may keep nullable
  references — `Option<T>` is advisory and scoped to Domain/Application.
- **Collections:** explicit type + collection expression where any is needed.
- **Private fields carry no underscore.**
- **No primitive obsession concern:** this is Infrastructure, outside §II's scope.

---

## Testing plan — which parts are red, which are characterisation

Constitution §Testing carries two obligations and this change triggers both. The
split is per gap, and **ambiguity resolves to red**.

### Characterisation — captured green *before* the port, green after

These assert behaviour that must be identical across a library change. They are
captured passing on `4.3.7.1207` first (the output quoted in the PR), then must
pass after.

`PlantFloor`, `DeadLetterFabScopingIntegrationTests`,
`PoisonDeliveryEscapeIntegrationTests`, `RestartLosesNothingIntegrationTests`,
`OutageRecoveryIntegrationTests`, `NFR002_MqttConnectAuthTests`,
`IngestThroughputMeasurementTests`, `MosquittoConnectionFactoryTests`.

**They cannot be literally unmodified, and that needs stating precisely rather
than waved through.** Seven of them will not compile until `using MQTTnet.Client;`
is dropped and `new MqttFactory()` becomes `new MqttClientFactory()`. So the rule
for this change is:

> **Permitted edits: the `using` line, and the factory type name. Nothing else.**
> No assertion, no expected value, no timeout, no delay, no arrangement, no
> `[Fact]`/`[Theory]` attribute, no skip. Any such edit is evidence the behaviour
> moved and is a **block**, not an adjustment.

**One narrowly-scoped exception, called out so it is not discovered mid-review.**
`MosquittoConnectionFactoryTests` reads
`connection.Options.ClientOptions.Credentials` (line 52). `ManagedMqttClientOptions`
wrapped a `ClientOptions`; `MqttClientOptions` is that object, so the path
collapses to `connection.Options.Credentials`. **The expected value is unchanged
— `ShouldBeEmpty()` stays exactly as written.** This is a navigation change
forced by a type collapsing, not a change to what is asserted. It is permitted.
A change to `ShouldBeEmpty()` itself would not be.

### New behaviour — must be observed red

| Gap | Test | Red against what |
|---|---|---|
| 1 reconnect | `MqttConnectionLoopTests` — reconnects after a drop; delay grows then caps; delay resets after a success | A build with no loop |
| 4 per-attempt mint | `MqttConnectionLoopTests` — a token is requested before *each* attempt, not once | A loop that mints once at start |
| **2 resubscribe** | `MqttConnectionLoopTests` — `SubscribeAsync` once per connect, so twice across a reconnect | A loop that subscribes once |
| **2 resubscribe** | `MqttResubscribeAfterBrokerOutageIntegrationTests` | **See "Proving the red" below** |
| 3 drop accounting | `MqttPublisherDropAccountingTests` — counts, one summary warning, narrow catch | A publisher with no counter |

### Proving the red — the ordering that makes gap 2's red mean something

Written naively, the outage test would go red before any of the work is done —
but for the wrong reason. With no reconnect at all it fails at gap 1, and a green
run after both fixes would not tell anyone whether the resubscribe was ever
necessary. That is a test whose red proves nothing about the thing it is named
for.

**So the sequencing is a requirement, not a preference:**

1. Implement gap 1 only — the loop reconnects, and deliberately does **not**
   resubscribe.
2. Run `MqttResubscribeAfterBrokerOutageIntegrationTests` and **observe it fail
   on that build**. The log at that moment shows a successful reconnect and event
   B never arriving — the exact production failure, reproduced.
3. **Quote that output verbatim in the PR.** It is the only artifact that shows
   the resubscribe is load-bearing rather than defensive.
4. Add the resubscribe. Re-run. Green.

This also discharges assumption A2: if the restarted broker *had* retained the
subscription, step 2 would pass and the assumption would be disproved out loud
rather than silently carried.

---

## Verification plan — FR-022 and NFR-005 on a live stack (ADR-0100)

Neither is provable from unit tests, and both are what a hand-rolled reconnect
path puts at risk. All of this runs against the Aspire fixture — real Mosquitto,
real go-auth plugin, real Keycloak (ADR-0103, no Testcontainers).

**Precondition:** ≥ 6 GB free on `C:` (16 GB at planning time; worktrees have
filled this disk before and taken Docker down with it). Stop any running AppHost
before building — a live host holds the service binaries and MSB3027 looks like a
broken build.

### How the broker outage is provoked

`ResourceCommandService.ExecuteCommandAsync("mosquitto",
KnownResourceCommands.StopCommand, …)`, then `StartCommand`, then
`WaitForResourceHealthyAsync("mosquitto", WaitBehavior.WaitOnResourceUnavailable,
…)`. This is the mechanism already proven in
`RestartLosesNothingIntegrationTests` and `LogTailDeliversIntegrationTests`,
reused rather than reinvented, including the `finally` that restores the resource
whatever happened.

**Not `docker stop` from the test.** The resource command is what the fixture
already owns, keeps Aspire's notion of resource state consistent with reality,
and gives a `WaitForResourceHealthy` to synchronise on. `docker stop` is the
right tool for the *manual* procedure in the spec, where there is no fixture.

**Not `setOffline` or a network trick.** Those leave the TCP connection in states
that do not resemble a broker restart; the point is a broker that comes back with
no session.

### FR-022 — channel-full backpressure / deferred ACK

The mechanism is `args.AutoAcknowledge = false` plus `AcknowledgeAsync` travelling
with the envelope. Both members are present and un-obsoleted on v5 (verified in
the spec's premise check), so the risk is behavioural, not compile-time.

**The strongest existing evidence is already live-stack, and it is not the
throughput test.** `OutageRecoveryIntegrationTests` drops one fab's partition,
publishes 20 events, asserts none is stored during the outage, restores storage,
and asserts all 20 arrive **exactly once**. That outcome is only reachable if the
ACK was withheld while the write could not succeed and the broker redelivered —
which is the deferred-ACK mechanism, observed end to end.
`RestartLosesNothingIntegrationTests` proves the same property across a service
restart. **Both must be green, and both are characterisation.**

`IngestThroughputMeasurementTests` is run in addition, for the load half.

### NFR-005 — as actually written

Spec 006's NFR-005 is *process restart resumes from the broker*, not
auto-reconnect (see the spec's premise correction 4). It is verified by
`RestartLosesNothingIntegrationTests`, which restarts the `event-ingestion`
resource and asserts nothing is lost. **Re-run it; do not substitute the new
broker-outage test for it.** They test different things and both are required.

### Auth — NFR002

`NFR002_MqttConnectAuthTests` gates on CONNECT → CONNACK timing and is the test
most exposed to a wire-level default change in a client major version. Run it
**twice** and record both figures; a single run after machine churn is not
evidence.

### The latency leg

`IngestThroughputMeasurementTests` and `NFR001_AuditIngestLatencyTests`, **each
run twice**, with the figures recorded in the phase-5 verification note against
the **Event → overlay state ≤ 200 ms** leg.

### Ordering

Full integration suite, not just the MQTT tests: the fixture is shared and a
broker stop/start in the new test could disturb neighbours if the `finally` is
wrong. That risk is precisely what a full-suite run detects.

---

## The three declarations

**1. Which engineer: `backend-engineer`.** The work is C# in
`EventIngestion.Infrastructure` and the `ScenarioSimulator` worker — a client
library port, a connect loop, guards, structured logging and xUnit tests. No
Aspire AppHost change, no CI workflow change, no Dockerfile, no Keycloak realm
edit, no Helm. The new integration test calls `ResourceCommandService`, but that
is consuming a fixture capability that already exists, not building
infrastructure.

**2. Behaviour-changing or -preserving: both, and the split is per gap.** The
*intent* is to preserve behaviour across a library change, but the mechanism is
new code, so declaring the whole thing "preserving" would let the reconnect loop
arrive green and untested.

- **Characterisation, observed green before and after:** the ingest happy path,
  dead-lettering, deferred ACK / storage-outage recovery, restart resumption,
  CONNECT auth, throughput, and the non-fatal startup mint. Permitted edits are
  the `using` line and the factory type name only, plus the one
  `Options.ClientOptions` → `Options` navigation change in
  `MosquittoConnectionFactoryTests` whose expected value is unchanged. Any other
  edit is a block.
- **New behaviour, observed red:** the reconnect loop (gap 1), the resubscribe
  (gap 2, with the ordering in "Proving the red"), per-attempt minting (gap 4),
  and the simulator's drop accounting (gap 3).

**3. Is any part a new ADR beyond the 0096 addendum? No — with one boundary
named.** Everything here implements decisions already taken: ADR-0096 chose the
library, the owner chose option (a), ADR-0036 governs the no-shared-abstraction
call, ADR-0100 fixes the auth model. The backoff shape, the two-loops call and
the accept-drops call are implementation choices recorded in this plan and in the
0096 addendum.

**The boundary:** if review decides the MQTT reconnect policy deserves a
first-class ADR of its own — a sibling to ADR-0143's HTTP retry policy, binding
future MQTT clients rather than describing this one — **that part is blocked**
for the autonomous lane (ADR-0144: it implements decisions, it does not make
them) and must be raised for a human to write. Nothing else here is.

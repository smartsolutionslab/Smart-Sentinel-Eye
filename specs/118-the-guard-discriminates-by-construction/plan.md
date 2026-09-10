# Plan — spec 118

## Approach

Three commits, each building on its own (ADR-0087 rebase-merge lands them
individually).

1. **Pin the premise.** `MqttClientWasConnectedContractTests` drives a real
   `MqttClient` against a loopback `TcpListener` that speaks MQTT 3.1.1 by hand
   (a CONNECT is read, a four-byte CONNACK is written), and reads
   `ClientWasConnected` off the args on three paths. No Docker, no broker, no
   Aspire stack — it is a socket and four bytes.

   This lands **first** because the whole fix rests on it. If a row had not
   held, the branch stops here.

2. **The seam and the red tests.** Both hand-written fakes gain
   `RaiseStaleDisconnectDuringNextConnect()`: a flag that makes the *next*
   `ConnectAsync` deliver a stale disconnect from inside itself, after
   `Task.Yield()` and before the connection is established. That is exactly the
   window the issue describes — after the loop attached its handler, before the
   `Connected` transition — and delivering it from inside the call makes the
   arrival order a fact of the test rather than a race it has to win.

   The `ScenarioSimulator` fake also gains `RaiseStaleDisconnectAsync()`; it had
   only `DropAsync()`, so it could not express a stale event at all.

3. **The fix.** Both `OnDisconnectedAsync` bodies read `args.ClientWasConnected`;
   both comments are rewritten to state the property the code has.

## Why a seam rather than argument alone

The issue permits shipping without the seam if it costs disproportionate test
machinery. It does not: the `ScenarioSimulator` fake already gates CONNECT
(`GateConnect`/`AllowConnect`), so the mid-connect window is an established idea
in this suite, and the delivery hook is one bool and four lines per fake. Built.

## What each new test can see

**EventIngestion** — its fake emulates `ThrowIfConnected`, so the defect's real
symptom is reachable: the loop wakes on a false drop, reconnects a live client,
and the refusal repeats. Assertion: zero "could not connect" lines while the
client is connected and subscribed.

**ScenarioSimulator** — its fake does **not** emulate `ThrowIfConnected` (a
second CONNECT simply succeeds), so the visible symptom there is the cycle
restarting rather than the refusal stream. Assertion: exactly one connect
announcement in a window that comfortably contains the 1 s backoff floor.
Making that fake throw is a faithfulness gap worth its own issue; it is not this
change.

## Risks

- **A test that cannot fail.** Both new tests are observed red against the
  unchanged production code, and the red is quoted. The contract test asserts
  three different values through one helper, so a helper returning a constant
  fails at least one row.
- **Timing.** Both new assertions are one-way: the fixed loop makes *no* further
  connects, so the window only has to be long enough for the buggy loop to make
  one. `Patient()` (100 ms floor) and the publisher's production 1 s floor are
  both well inside the windows chosen.

## Files

- `tests/EventIngestion.Infrastructure.Tests/MqttClientWasConnectedContractTests.cs` (new)
- `tests/EventIngestion.Infrastructure.Tests/Fakes/FakeMqttClient.cs`
- `tests/EventIngestion.Infrastructure.Tests/MqttConnectionLoopTests.cs`
- `tests/ScenarioSimulator.Tests/Fakes/FakeMqttClient.cs`
- `tests/ScenarioSimulator.Tests/MqttPublisherDropAccountingTests.cs`
- `src/EventIngestion/Infrastructure/Ingress/MqttConnectionLoop.cs`
- `src/ScenarioSimulator/Mqtt/MqttPublisher.cs`

No contention file (`Shared.Kernel`, `Shared.Contracts`, `AppHost.cs`) is touched.

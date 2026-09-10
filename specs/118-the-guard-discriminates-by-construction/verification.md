# Verification — spec 118 (#2130)

Phase 5 in the unit sense: what was observed, and with what.

**No Aspire stack was booted, and none was needed.** Every claim here is a
`DropSignal` decision or a fact about MQTTnet, both reachable without a broker.
The one thing that would have needed a real broker — the library's behaviour —
is reached instead by a loopback socket that speaks four bytes of MQTT 3.1.1.

## 1. The premise, measured

`MqttClientWasConnectedContractTests`, three facts, against a real
`MqttClient` from MQTTnet 5.2.0.1603:

| Path | Expected | Observed |
|---|---|---|
| refused CONNACK (return code `0x05`) | `false` | `False` |
| peer closes an established connection | `true` | `True` |
| public `DisconnectAsync` on a live client | `true` | `True` |

The issue's table came from a decompiler. All three rows hold against the
library. Had one not, the branch would have stopped here — the whole fix rests
on this.

The three facts share one helper and expect two different answers, so a helper
returning a constant fails at least one of them.

## 2. The mirror case, observed red then green

Red, against the unchanged loops:

```
MqttConnectionLoopTests.A_stale_disconnect_landing_mid_connect_does_not_end_the_connection_it_landed_in
  Shouldly.ShouldAssertException : loop.FailedConnects
    should be 0 but was 5

MqttPublisherDropAccountingTests.A_stale_disconnect_landing_mid_connect_does_not_restart_the_connect_cycle
  Shouldly.ShouldAssertException : publisher.Logger.Entries.Count(entry => Announces(entry.Message))
    should be 1 but was 2
```

Five refusals in a two-second window is the cycle the issue describes, not a
single spurious wake: `ThrowIfConnected` refusing to reconnect a live client,
with nothing closing that client.

Green after the change, with the test text unmodified.

## 3. The property that was already there is still there

`A_stale_disconnect_does_not_start_a_failed_connect_cycle_against_a_live_connection`
allows one spurious wake at the refused→success transition. It passes,
unmodified. The allowance is now unused rather than merely respected: a stale
event carries `ClientWasConnected=false` whenever it is delivered, so it
completes no wait at all.

`git diff origin/develop` on both test files removes **zero** lines. The change
to each is purely additive.

## 4. Suites

- `dotnet build -c Release` — **Build succeeded. 0 Warning(s), 0 Error(s).**
- 29 unit and architecture test projects (everything `coverage-check.ps1` runs;
  `Integration.Tests` excluded, as it excludes it) — **2408 passed, 0 failed,
  0 skipped.** Includes `Architecture.Tests` (360), which carries
  `PrimitiveBoundaryTests` and `HandlerDeconstructionTests`.
- Docker-bound integration tests were not run here; they are unaffected by a
  change confined to two `DropSignal` bodies, and CI runs them on the PR.

## 5. Latency (§IV)

**Not on the path.** MQTT reconnection is not one of the six legs. No leg is
re-measured and none is claimed. The change makes a rare permanent-deafness mode
less likely; it does not move a figure.

## 6. Findings not fixed here

`tests/ScenarioSimulator.Tests/Fakes/FakeMqttClient.cs` does not emulate
`MqttClient.ThrowIfConnected` — a CONNECT against its own live connection simply
succeeds. EventIngestion's fake of the same name does emulate it, and that is
what lets the subscriber's test see the permanent refusal cycle rather than one
extra connect. The simulator's copy of the same defect is therefore
under-modelled. Worth an issue; not this change, which would otherwise be a fix
and a test-infrastructure change travelling together.

# Spec 118 — the guard discriminates by construction

**Issue:** #2130
**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0139 (rules that fail the build), 0144 (autonomous lane),
0052/0053/0054 (test framework, naming, hand-written fakes), 0105 (guards),
0049 (`CancellationToken`), 0036 (smallest change).

## Problem

Two hand-written reconnect loops suppress a stale disconnect the same way:

- `src/EventIngestion/Infrastructure/Ingress/MqttConnectionLoop.cs` — `DropSignal.OnDisconnectedAsync`
- `src/ScenarioSimulator/Mqtt/MqttPublisher.cs` — `DropSignal.OnDisconnectedAsync`

Both complete the attempt's wait only when `!client.IsConnected`. That reads the
client's state **at handler time**, which is a different moment from the one the
event describes.

### The half the guard covers

A stale disconnect landing while the live attempt is **connected** is suppressed,
because `IsConnected` is true. This half is genuinely by construction:
`DisconnectIsPendingOrFinished` moves the status off `Connected` before
`DisconnectCore` runs, so a *genuine* drop cannot reach the handler with the
client still reporting a connection.

### The half it does not

A stale disconnect landing while the live attempt is **pre-connect or
mid-connect** sees `IsConnected == false` and completes that attempt's wait
before it has connected. The attempt then connects, subscribes, and its
`WaitAsync` returns immediately — a false drop. The next attempt runs
`ConnectAsync` against a live client, `MqttClient.ThrowIfConnected` refuses,
nothing closes the connection, and the refusal repeats: a permanent stream of
false "could not connect" errors on the only outage signal EventIngestion has,
with the attempt counter climbing to the 30 s cap that the next genuine outage
then waits out.

Reachability is low — the queued `Task.Run` continuation has to be starved past
attempt N+1's backoff delay, at least `0.8 × first` = 800 ms in production — and
it self-heals on the next genuine drop.

### Why it is worth fixing anyway

The comment at `MqttConnectionLoop.cs:271-279` claims the guard holds **by
construction**: *"a genuine drop therefore cannot reach this handler with the
client still reporting a connection."* That sentence is true, and it is a
**different proposition** from *"no stale event is mistaken for a drop."* The
guard actually holds by timing. A comment claiming a stronger property than the
code has is the failure mode this repository has had to correct repeatedly.

## The discriminator, verified against the library

`MqttClientDisconnectedEventArgs.ClientWasConnected` is captured at the moment of
the disconnect and carried on the args, so it has no race.

The issue's table was read from a decompiler. **It was re-verified here against
the real MQTTnet 5.2.0.1603 client** over a loopback socket speaking MQTT 3.1.1
(the version both production clients pin), by
`MqttClientWasConnectedContractTests`:

| Path | `ClientWasConnected` observed |
|---|---|
| refused CONNACK (3.1.1 return code `0x05`) | `False` |
| established connection, peer closes the socket | `True` |
| established connection, public `DisconnectAsync` (the subscribe-failure path) | `True` |

All three rows hold. `if (args.ClientWasConnected)` is therefore strictly
stronger than `if (!client.IsConnected)`, and the subscribe-failure path — which
disconnects a live client deliberately — reports `True`, so that drop is still
honoured with no fresh hot cycle.

## Scope

- Both `DropSignal.OnDisconnectedAsync` bodies discriminate on
  `args.ClientWasConnected`.
- Both comments say what the guard now actually guarantees.
- The library claim the fix rests on is pinned by a test, not by a comment.

**Out of scope:** driving the spurious-wake count to zero. During a run of
refusals the pre-completed wait is never awaited, so at most **one** spurious
wake occurs, at the refused→success transition.
`A_stale_disconnect_does_not_start_a_failed_connect_cycle_against_a_live_connection`
allows exactly one, and that allowance is unchanged.

**Out of scope:** extracting the duplicated loop. The two live in assemblies with
no common reference; see both files' comments and spec 079.

## Latency (§IV)

**Not on the path.** The six legs are camera→SFU, SFU→kiosk decode, presentation
buffer, event→overlay state, composite+render, headroom. MQTT *reconnection* is
none of them. The event→overlay leg's budget assumes an ingesting subscriber;
this change makes a rare permanent-deafness mode less likely rather than moving
any leg's figure. No leg is re-measured and none is claimed.

## Success criteria

1. A stale disconnect delivered from **inside** a CONNECT no longer ends the
   connection that CONNECT establishes — asserted on both loops, observed red
   against `IsConnected` and green against `ClientWasConnected`.
2. `MqttClientWasConnectedContractTests` observes all three rows off the real
   client, so the premise is checkable rather than quoted.
3. Every existing MQTT test passes **unmodified**.

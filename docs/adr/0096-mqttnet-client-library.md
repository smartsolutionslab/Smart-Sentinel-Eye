# ADR-0096: MQTTnet as the .NET MQTT client library

**Status:** Accepted
**Amended:** 2026-09-06 — MQTTnet 5 removed the managed client; a hand-rolled
reconnect wrapper replaces it (see *Amendment, 2026-09-06*). The provenance
rationale is unaffected
**Date:** 2026-05-28
**Supersedes:** —
**Superseded by:** —

## Context

ADR-0095 selects Mosquitto as the MQTT broker. EventIngestion
(spec 006) needs a .NET client to subscribe to the wildcard
`fab/+/+/+` topic and feed payloads into the bounded ingest
channel. The client lib is the second tech-stack addition for
spec 006 and so requires its own ADR per constitution §II.

Constraints:

- .NET 10 / `net10.0` target framework.
- Long-lived persistent session (QoS 1 + clean session = false).
- TLS-optional connection (TLS in prod, plaintext in dev).
- Async API that integrates with `IHostedService` + `Channel<T>`
  backpressure (we need an `await`able publish + a way to
  *defer* MQTT ACK until our persistence loop has succeeded).
- Active maintenance + a clear release cadence (the broker is on
  the critical path for the camera → event → overlay loop).
- MIT or equivalent permissive license (per constitution §II
  preference for permissive OSS in the data plane).

## Decision

**MQTTnet 4.3.x** (https://github.com/dotnet/MQTTnet) is the
.NET MQTT client library for EventIngestion.

- Released under MIT.
- Most-downloaded .NET MQTT lib on nuget.org by an order of
  magnitude.
- Maintained under the `dotnet` GitHub org (Microsoft adopted
  the project in 2024).
- v4 API surface is async-first and integrates cleanly with
  `IHostedService` lifecycles + `CancellationToken` chains
  (matches ADR-0049).
- `MQTTnet.AspNetCore` adds DI helpers that map naturally onto
  our per-context `Add<Context>Infrastructure()` extension method
  pattern (ADR-0051).

## Consequences

**Positive:**

- Battle-tested at scale (tens of thousands of GitHub-deps;
  used in production by IoT-platform vendors and industrial
  control systems).
- v5 of the MQTT spec is supported (we use v3.1.1 / v5
  capability set in v1 but room to grow).
- Async API maps naturally to our handler pattern.
- Connection-state observable so we can wire health checks /
  Aspire dashboard surfacing of the broker connection state.
- Per-message `ApplicationMessageReceivedAsync` callback lets
  us defer the ACK by awaiting our persistence loop — exactly
  what spec 006 §FR-022 needs for the "MQTT subscriber stops
  ACKing when channel is full" backpressure model.

**Negative:**

- Documentation is thinner than mainstream Microsoft libs;
  pattern discovery via the GitHub examples folder rather than
  rich docs.
- The library carries a small set of features beyond what we need
  (in-process broker, WebSocket transport) — we accept the
  unused surface; the assembly is small (~700 KB).

## Alternatives Considered

**M2Mqtt — REJECTED.** The original .NET MQTT lib (pre-MQTTnet).
Effectively unmaintained since ~2018. No .NET 10 build, lacks
async-first API.

**HiveMQ .NET client — REJECTED.** Commercial license required
for the production-grade variants; would conflict with
constitution §II's preference for permissive OSS in the data
plane. The community-licensed variant is feature-restricted.

**Raw `System.Net.Sockets` + a hand-rolled MQTT codec — REJECTED.**
Not worth the maintenance burden. MQTT v3.1.1 is non-trivial
(QoS state machines, session storage, keepalive heartbeats);
solving it ourselves would consume engineering bandwidth that
should go to the spec 006 domain work.

## Implementation Notes

- Added as `PackageReference Include="MQTTnet" Version="4.3.x"`
  to `EventIngestion.Infrastructure.csproj` (spec 006 task T011).
- The Aspire AppHost surfaces the broker endpoint as a connection
  string `ConnectionStrings:mosquitto = tcp://host:1883`; the
  `MosquittoConnectionFactory` (task T047) parses it into an
  `MqttClientOptions` builder.
- Reconnect-with-backoff is configured via `WithAutoReconnectDelay`
  on the MQTTnet `ManagedMqttClient` so transient broker outages
  recover without operator intervention (matches NFR-005).

## Amendment, 2026-09-06 — the managed client was removed upstream

**Decided by the repository owner. This addendum records that decision; it
does not make one.**

### What changed

`MQTTnet.Extensions.ManagedClient` **does not exist at 5.x**. It was removed
outright — not renamed, not folded into the core package. Verified against the
published artifacts rather than the release notes: the flat-container index for
`mqttnet.extensions.managedclient` stops at `4.3.7.1207`, and reflecting over
`MQTTnet.5.2.0.1603`'s `lib/net10.0/MQTTnet.dll` finds no `IManagedMqttClient`,
no `ManagedMqttClientOptions`, no `ManagedMqttClientOptionsBuilder` — no
"Managed" anything.

There was **no advisory pressure** to move: `dotnet restore -p:Configuration=Release`
on `4.3.7.1207` reported zero `NU1902`/`NU1903`. Staying put was safe, so this
was planned rather than rushed.

### The decision, and the two options declined

**Hand-roll a reconnect/resubscribe wrapper around `IMqttClient`, staying on the
official package.**

- *Adopt an unofficial third-party managed client* — declined. It would replace
  the part the `dotnet` org removed with a fork, contradicting the provenance
  reasoning in the Decision above rather than preserving it.
- *Stay on `4.3.7.1207` indefinitely* — declined. Viable while no advisory
  exists, but the drift compounds.

### The provenance rationale is unaffected, and that is worth stating

The removal was initially framed as undercutting this ADR's reason for choosing
MQTTnet. On inspection it does not. The core package is still `dotnet/MQTTnet`,
still MIT, still actively released. What was undercut is an assumption about the
**package set**, not about the maintainer. The decision above stands on its
original reasoning.

### What replaced the managed client

Four behaviours had to be rebuilt, because `IMqttClient` is not a drop-in:

1. **Reconnect** — `WithAutoReconnectDelay` has no equivalent. A connect-with-backoff
   loop: 1/2/4/8/16 s capped at 30 s, jitter [0.8, 1.2], **no attempt limit** (after
   a broker restart, go-auth may refuse CONNECT until it re-fetches the realm JWKS;
   a loop that gives up turns that into a permanent outage).
2. **Resubscribe** — the managed client re-subscribed after reconnect. `fab/+/+/+`
   is now re-subscribed explicitly after **every** connect. This is the dangerous
   one: a subscriber that reconnects without resubscribing is connected,
   healthy-looking, and receiving nothing.
3. **Offline queue** — `EnqueueAsync` buffered while disconnected; `PublishAsync`
   throws instead. The ScenarioSimulator **accepts the drop, counts it, and reports
   once per outage**. Buffering was rejected deliberately: a replayed sample carries
   a stale `occurredAt` at a live wall.
4. **JWT refresh** — `ConnectingFailedAsync` does not exist on the plain client. The
   token is minted **inside the loop, immediately before each `ConnectAsync`**, which
   is stronger than re-minting after a failure.

Two loops, not one: the callers sit in assemblies with no common reference, and
sharing would put an MQTT client in `Shared.Kernel`, which all nine contexts
reference.

### Two v4 defaults that changed silently, and both mattered

Recorded because a major-version port of a protocol library is exactly where
defaults move without a compiler error, and both of these shipped green through a
full CI run before review caught them.

- **`ProtocolVersion` V311 → V500.** Under MQTT 3.1.1, `cleanSession=false` *is*
  the persistent session. Under 5.0 it is half of one — the other half is
  `SessionExpiryInterval`, which defaults to `0` and discards the session on
  disconnect. The port silently traded away NFR-005 and FR-022. **Both clients now
  pin V311 explicitly**, restoring the wire spec 006 was measured against rather
  than making a protocol decision this work had no mandate for.
- **`ThrowOnNonSuccessfulConnectResponse` removed** (v4 default: `true`). A rejected
  CONNECT now returns normally instead of throwing, so a refusal read as success.
  Both loops now inspect `MqttClientConnectResult.ResultCode`.

### A stale claim in the Decision section above

The Decision cites `MQTTnet.AspNetCore` as adding DI helpers that map onto
ADR-0051's `Add<Context>Infrastructure()` pattern. **That package has never been
referenced by this repository.** The rationale was sound on its own without it;
the sentence is left in place with this correction beside it, because a rationale
resting on an unused package is the same clerical drift this repository has had to
correct in constitution §IV's leg table and in the phase-3 board gate.

### Version

`MQTTnet` `5.2.0.1603`. The Implementation Notes above say "4.3.x" and describe
`WithAutoReconnectDelay` on `ManagedMqttClient`; both describe the superseded
arrangement and are read through this amendment.

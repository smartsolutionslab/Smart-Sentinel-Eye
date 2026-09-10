# Spec 117 — the session that survives a disconnect

**Issue:** #2128
**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0139 (rules that fail the build), 0144 (autonomous lane),
0052/0053 (test framework and naming), 0105 (guards), 0049 (`CancellationToken`).
**Scope note:** the issue offers three routes. **This spec takes option 2 only.**

## Problem

Porting MQTTnet 4 → 5 destroyed the broker-side persistent session and every CI
check stayed green. `MqttClientOptionsBuilder` defaults to `ProtocolVersion.V311`
in v4 and `V500` in v5; under 5.0, `cleanSession=false` is only half a persistent
session, the other half being `SessionExpiryInterval`, which defaults to `0` —
*discard the session the moment the connection closes*.
`RestartLosesNothingIntegrationTests` published 500 events across a restart and
stored none. It is the only test that can see this, it carries
`Category=Disruptive`, and `ci.yml:179` excludes that category.

So spec 006's **NFR-005** — *process restart: subscriber resumes from the broker
(QoS 1, persistent session)* — has no coverage on the PR path.

### What the tree already has, and what the issue did not know

#1139 did not leave the configuration bare. It added **two** CI-visible pin
tests, one per client:

- `MosquittoConnectionFactoryTests.The_subscriber_speaks_MQTT_3_1_1_rather_than_the_v5_default`
- `MqttPublisherProtocolPinTests.The_publisher_speaks_MQTT_3_1_1_rather_than_the_v5_default`

Both assert `ProtocolVersion == V311` and nothing else. So the half of the defect
that has a guard is the *protocol*; the half that carries NFR-005 does not.

**The gap this spec closes:** `MosquittoConnectionFactory` calls
`.WithCleanSession(false)`, and **no test reads it**. Delete that line and every
check on a PR is green — the pin test passes, because the protocol did not move.
Under 3.1.1, `cleanSession=false` *is* the persistent session, so the unguarded
half is the load-bearing half.

Two further defects of the existing pins:

- They assert a **spelling**, not a property. A deliberate, correct move to MQTT
  5.0 with a non-zero `SessionExpiryInterval` would fail them — for being MQTT 5,
  not for losing the session.
- They have no counterfactual. Nothing in the suite demonstrates that the
  assertion can go red, and this repo has found assertions that could not
  (#199).

### One correction to the issue's record

#2128 dumps the two builders' defaults as

```
v5  ProtocolVersion=V500  CleanSession=False  SessionExpiryInterval=0
v4  ProtocolVersion=V311  CleanSession=False  SessionExpiryInterval=0
```

The protocol half is right and is the whole cause. **`CleanSession=False` is
not** what `MqttClientOptionsBuilder` produces — measured on 5.2.0.1603 while
proving this guard red, a builder with no `.WithCleanSession(...)` call yields
`V500 with cleanSession=True`. The dump almost certainly read a bare
`MqttClientOptions`, whose `CleanSession` is a `bool` sitting at its `default`.

It matters here rather than being pedantry: had the default been `false`,
deleting `.WithCleanSession(false)` would have been harmless and this spec would
be guarding nothing. It is `true`, so the deletion is a live regression — and
until this change it was a **green** one.

## Scope

**In:** one property — *the subscriber's session survives a disconnect* —
asserted against the options `MosquittoConnectionFactory` actually builds, plus
the counterfactual that shows the assertion rejecting the v5-default shape.

**Out, and named so this spec is not read as closing the issue:**

- **`ci.yml`'s category filter is untouched**, and no test gains or loses a
  `Category` trait. Both are gate changes and ADR-0144 forbids the lane to make
  them.
- **The MQTT client configuration is untouched.** #1139 fixed it; this spec
  guards it.
- Options 1 (running the Disruptive suite on CI) and 3 (a scheduled job) are CI
  policy decisions and are not taken here.
- **The publisher's `cleanSession=true` is not changed and not guarded as a
  persistent session.** The issue's phrasing — *"both MQTT clients negotiate
  3.1.1 with a session that survives disconnect"* — does not fit the tree: the
  publisher sets `cleanSession=true` deliberately, has no subscriptions, and has
  no session to lose. Its pin test is the right guard for it and already exists.

## Functional requirements

- **FR-001** A CI-visible test asserts that the options
  `MosquittoConnectionFactory` builds describe a session the broker keeps across
  a disconnect.
- **FR-002** That assertion is expressed as a **property**, not as the literal
  `V311`: under 3.1.1 it requires `CleanSession == false`; under 5.0 it requires
  `CleanSession == false` **and** `SessionExpiryInterval > 0`. A correct move to
  5.0 passes it; the v5 default does not.
- **FR-003** The same property, applied to options built the way MQTTnet 5's
  defaults build them (`V500`, `SessionExpiryInterval = 0`), is **refused** — the
  counterfactual, in the suite, so the guard cannot decay into an assertion that
  cannot fail.
- **FR-004** No broker, no Aspire stack, no Docker. The test runs in the
  Docker-free unit job in milliseconds.

## Non-functional / constraints

- **NFR-001** The guard lives in `EventIngestion.Infrastructure.Tests`, which
  already owns `MosquittoConnectionFactoryTests` and already runs on every PR.
  No new project, no new package, no shared MQTT helper — the production code
  refuses a shared MQTT client for the same reason (spec 079).
- **NFR-002** Latency budget (constitution §IV): **not touched.** MQTT ingest is
  not one of the six legs of the event-to-overlay path, and this change adds test
  code only — no production file is modified.

## What this does not cover

**NFR-005 still has no behavioural CI coverage after this spec.** The guard reads
the *configuration*; it does not observe a broker holding a session across a
process restart. Only `RestartLosesNothingIntegrationTests` does that, and it
remains excluded from CI. The issue says so in as many words — option 2 *"does
not substitute for 1 or 3, because it guards the configuration rather than the
behaviour"* — and #2128 should stay open for options 1 and 3.

Concretely, what stays invisible to CI: a broker whose own config discards
sessions, an ACL change that costs the subscription on reconnect, a QoS
downgrade at publish time, and any failure to *resume* that is not visible in the
client's options.

## Acceptance

- The new property test fails against a `MosquittoConnectionFactory` built the
  v5-default way, with the failure quoted (ADR-0139).
- It passes against the tree as it stands.
- `dotnet build -c Release` clean; the EventIngestion.Infrastructure suite green.
- `ci.yml` and every `Category` trait byte-for-byte unchanged.

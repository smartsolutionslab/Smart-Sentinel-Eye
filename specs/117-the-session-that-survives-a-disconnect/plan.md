# Plan — spec 117

## Design

One private predicate over `MqttClientOptions`, in the test assembly, and two
`[Fact]`s that use it.

```
SurvivesDisconnect(MqttClientOptions options) =>
    options.ProtocolVersion switch
    {
        V310 or V311 => !options.CleanSession,
        _            => !options.CleanSession && options.SessionExpiryInterval > 0,
    };
```

That is the whole mechanism. It is a property rather than a spelling — FR-002 —
and it costs one `switch` expression, which is the test the brief set: express
the property *unless* doing so would cost a framework.

Under 3.1.1 the persistent session is exactly `cleanSession=false`; there is no
expiry field on the wire, so reading `SessionExpiryInterval` there would be
reading a field the protocol does not carry. Under 5.0 both halves are required,
and `0` is the value that means *discard on disconnect* — which is why the
comparison is `> 0` and not `!= default`.

`V310` is admitted alongside `V311` because 3.1 has the same one-flag session
semantics. Nothing in the repo speaks it; excluding it would make the predicate
assert a version list by accident, which is the defect being removed.

### Where it goes

`tests/EventIngestion.Infrastructure.Tests/MosquittoConnectionFactoryTests.cs`,
beside the pin test it complements. The class already has a `Create(...)` helper
that builds the factory with a stubbed Keycloak and no network.

### What happens to the existing pin tests

**Both stay.** They are not redundant with the new one, they guard a different
claim: *this repo speaks 3.1.1 on purpose* — because the broker's ACL and auth
behaviour (ADR-0100) were proven on 3.1.1 and NFR-002's handshake figures were
measured there. Moving to 5.0 should have to delete that assertion deliberately,
which is what the production comment already says. Deleting it as part of adding
a property guard would be the lane making a protocol decision.

So after this change the subscriber carries two independent claims: *3.1.1 by
decision* (pin) and *a session that survives* (property). The second is the one
that fails if only `.WithCleanSession(false)` is removed.

### The counterfactual is part of the suite, not part of the evidence run

FR-003 puts the v5-default shape in a test of its own, asserting the predicate
**refuses** it. Without it the property test is green from the day it is written
and stays green forever whether or not it can fail; with it, a future edit that
loosens the predicate turns one of the two red. This is the shape #199 lacked.

The red required by ADR-0139 is produced separately and is stronger: the
production factory is temporarily reverted to the v5 default, the property test
observed failing, and the revert undone. That proves the guard reads production,
which a self-contained counterfactual cannot.

## Alternatives rejected

- **A source-scanning architecture test** asserting every
  `new MqttClientOptionsBuilder()` in `src/` is followed by an explicit
  `.WithProtocolVersion(...)`. It bans the category — this repo's preference —
  and would cover a *third* client nobody has written yet. Rejected: two call
  sites exist, both already pinned; a regex over source that must model builder
  chains is a framework for a population of two, and speculative generality is
  what ADR-0036 forbids. Revisit when there is a third client.
- **Hoisting the predicate somewhere both test assemblies can see it.** There is
  no shared test project, and the only common reference is `Shared.Kernel`, which
  all nine contexts reference and which must not learn about MQTT — the same
  reasoning `MqttPublisher`'s doc comment gives for duplicating the connect loop.
  Moot in any case: the property applies to the subscriber only.
- **Asserting `CleanSession == false` on its own.** Cheaper, and wrong the day
  the protocol moves: under 5.0 it would pass on exactly the configuration that
  lost the session.

## Verification

1. `dotnet build -c Release` — clean, warnings as errors.
2. `dotnet test tests/EventIngestion.Infrastructure.Tests` — full class green.
3. Red evidence: pin removed from `MosquittoConnectionFactory`, property test
   red, output quoted; file restored and its timestamp touched (MSBuild skips a
   rebuild for a restored file that kept its old mtime).
4. `git diff --stat` shows `.github/workflows/ci.yml` untouched and no `Trait`
   added or removed.

## Latency

Constitution §IV is **not** touched. The six legs run camera → SFU → kiosk
decode → presentation buffer → event → overlay state → composite/render; MQTT
ingest is not among them, and this change is test-only.

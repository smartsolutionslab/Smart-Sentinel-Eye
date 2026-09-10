# Verification — spec 117

**Issue:** #2128 (option 2 only)
**Branch:** `test/2128-the-session-that-survives-a-disconnect`
**No broker, no Aspire stack, no Docker.** The whole point of option 2 is that
the regression's signature is readable from the options; if this had needed a
stack it would have drifted into option 1.

## Phase 4a — red, twice, against real historical shapes

`MqttClientOptionsBuilder` is MQTTnet's, so the red is produced by putting
`MosquittoConnectionFactory` back into the two broken shapes and running the
guard against it. That proves the guard reads production, which a self-contained
counterfactual cannot.

### Red 1 — the v5 default that actually happened

`.WithProtocolVersion(MqttProtocolVersion.V311)` removed from
`MosquittoConnectionFactory.cs:70`, leaving MQTTnet 5's default:

```
  Failed SmartSentinelEye.EventIngestion.Infrastructure.Tests.MosquittoConnectionFactoryTests.The_subscribers_session_survives_a_disconnect [6 ms]
  Error Message:
   Shouldly.ShouldAssertException : SurvivesDisconnect(connection.Options)
    should be
True
    but was
False

Additional Info:
    the subscriber asked for V500 with cleanSession=False and sessionExpiryInterval=0, which the broker is free to discard the moment the connection closes — taking the subscription and every unacknowledged QoS 1 message with it. NFR-005 rests on this.

Failed!  - Failed:     2, Passed:     5, Skipped:     0, Total:     7, Duration: 204 ms
```

Two failed: the new property test and the pre-existing `V311` pin. Five passed —
including `The_v5_default_shape_is_not_a_session_that_survives_a_disconnect`,
which is the counterfactual reading the library's own default.

### Red 2 — the half nothing guarded

Pin restored; `.WithCleanSession(false)` removed instead. **This is the case the
existing pin cannot see**, and it is the reason this spec exists:

```
  Failed SmartSentinelEye.EventIngestion.Infrastructure.Tests.MosquittoConnectionFactoryTests.The_subscribers_session_survives_a_disconnect [46 ms]
  Error Message:
   Shouldly.ShouldAssertException : SurvivesDisconnect(connection.Options)
    should be
True
    but was
False

Additional Info:
    the subscriber asked for V311 with cleanSession=True and sessionExpiryInterval=0, which the broker is free to discard the moment the connection closes — taking the subscription and every unacknowledged QoS 1 message with it. NFR-005 rests on this.

Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 216 ms
```

**One** failure — the new test. The `V311` pin passed, because the protocol did
not move. Before this branch that deletion was green on every check a PR runs.

The message also settles a fact #2128 got wrong: the builder's `CleanSession`
default is **`True`**, not `False` as the issue's dump records. Had it been
`False` the deletion would have been harmless and the guard would be guarding
nothing.

Production file restored with `git checkout --` and its mtime touched — MSBuild
skips the rebuild for a restored file that kept its old timestamp, and the green
below would then be the red build's binary.

## Phase 4b — green

```
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 183 ms - SmartSentinelEye.EventIngestion.Infrastructure.Tests.dll (net10.0)
```

Whole suites, `-c Release --no-build`:

```
Passed!  - Failed:     0, Passed:    48, Skipped:     0, Total:    48 - SmartSentinelEye.EventIngestion.Infrastructure.Tests.dll
Passed!  - Failed:     0, Passed:   356, Skipped:     0, Total:   356 - SmartSentinelEye.Architecture.Tests.dll
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55 - SmartSentinelEye.ScenarioSimulator.Tests.dll
```

```
dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Gates untouched

```
$ git diff --stat
 .../117-the-session-that-survives-a-disconnect/spec.md |  20 +++++
 .../MosquittoConnectionFactoryTests.cs                 |  95 ++++++++++++++++++
```

`.github/` has no changed file on this branch. No `[Trait]` was added or removed;
the only `Category` string in the diff is inside a doc comment saying which
category stays excluded. No production file is modified.

## What is verified, and what is not

**Verified:** the subscriber's *configuration* describes a session that survives
a disconnect, and the assertion goes red on both shapes that have destroyed it.

**Not verified, and not verifiable this way:** that a broker actually holds a
QoS 1 backlog across a subscriber restart. That is NFR-005, that is
`RestartLosesNothingIntegrationTests`, and it carries `Category=Disruptive`
which `ci.yml:179` still excludes. **NFR-005 has no behavioural CI coverage after
this branch.** #2128 stays open for options 1 and 3.

## Latency (constitution §IV)

**Not touched.** MQTT ingest is not one of the six legs of the event-to-overlay
path, and this branch changes test code only.

# Tasks — spec 117

Tracked at feature granularity against #2128 (CLAUDE.md, Phase 3). No per-task
issues since spec 028.

**Phase 4a colour: RED.** New behaviour — an assertion that does not exist. The
red is reproducible from a real historical defect, so it is produced against the
v5-default configuration rather than against an empty file.

- [x] **T1** Verify the population. Count the MQTT clients built in `src/`
      rather than trusting the issue's "both". Result: two —
      `MosquittoConnectionFactory.cs:69` (subscriber) and
      `MqttPublisher.cs:120` (publisher). Every other
      `MqttClientOptionsBuilder` in the tree is in `tests/`.
- [x] **T2** Read what #1139 already left behind, so this spec adds a guard
      rather than a second copy of one. Result: both clients already carry a
      `ProtocolVersion == V311` pin test on the PR path; neither reads
      `CleanSession`.
- [x] **T3** Confirm `MqttClientOptions` exposes `CleanSession` and
      `SessionExpiryInterval` in MQTTnet 5.2.0.1603, so the property is readable
      without connecting.
- [x] **T4** Write the property predicate and
      `The_subscribers_session_survives_a_disconnect`.
- [x] **T5** Observe it **red** against the v5-default configuration: strip
      `.WithProtocolVersion(V311)` from the factory, run, quote the failure.
      Restore, touch the file, re-run green.
- [x] **T6** Write the counterfactual
      `The_v5_default_shape_is_not_a_session_that_survives_a_disconnect`, so the
      suite itself carries the proof that the property can be refused.
- [x] **T7** `dotnet build -c Release`; run the EventIngestion.Infrastructure
      suite; confirm `ci.yml` and every `Category` trait unchanged.
- [x] **T8** Record in `spec.md` that NFR-005 still has no behavioural CI
      coverage, and that #2128 stays open for options 1 and 3.

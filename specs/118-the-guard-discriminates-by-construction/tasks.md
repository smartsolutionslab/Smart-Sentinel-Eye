# Tasks — spec 118

Tracked at feature granularity against #2130 (CLAUDE.md, Phase 3). No per-task
issues since spec 028.

**Phase 4a colour: RED.** This changes behaviour in a case the existing tests
cannot reach, so the mid-connect seam is built and the mirror case is observed
failing against the unchanged loops. Characterisation would have passed quietly.

- [x] **T1** Verify the issue's `ClientWasConnected` table against the real
      MQTTnet 5.2.0.1603 client rather than a decompiler. Loopback socket,
      hand-written 3.1.1 CONNACK, three paths.
- [x] **T2** Land the contract test first, so the premise is checkable before
      anything depends on it.
- [x] **T3** Add `RaiseStaleDisconnectDuringNextConnect()` to both fakes, and
      `RaiseStaleDisconnectAsync()` to the `ScenarioSimulator` fake, which had
      no way to express a stale event.
- [x] **T4** Write `A_stale_disconnect_landing_mid_connect_does_not_end_the_connection_it_landed_in`
      (EventIngestion) and
      `A_stale_disconnect_landing_mid_connect_does_not_restart_the_connect_cycle`
      (ScenarioSimulator). Observe both **red**; quote the failures.
- [x] **T5** Change both `OnDisconnectedAsync` bodies to `args.ClientWasConnected`.
- [x] **T6** Rewrite both doc comments to state the property the code has —
      the args carry the answer, captured at the disconnect, so the guard no
      longer rests on when the handler runs.
- [x] **T7** Confirm
      `A_stale_disconnect_does_not_start_a_failed_connect_cycle_against_a_live_connection`
      and every other MQTT test passes **unmodified**.
- [x] **T8** `dotnet build -c Release`; run both suites; record counts.
- [x] **T9** Record the `ScenarioSimulator` fake's missing `ThrowIfConnected`
      emulation as a finding, not as work in this branch.

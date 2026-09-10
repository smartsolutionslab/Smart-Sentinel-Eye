# Spec 125 — A leg recorded as measured

**Issue:** #2192 (carries #2152's F15 — the same test)
**Branch:** `fix/2192-a-leg-recorded-as-measured`
**Status:** Phase 3 complete
**Lane:** autonomous (ADR-0144)
**ADRs:** 0037 (phases), 0144 (lane, 4a colour), 0036 (smallest change),
0139 (red first), 0103 (Aspire fixture, no Testcontainers), 0117 (the
leg-state table), 0084 (code metrics), 0052/0053 (xUnit + Shouldly,
sentence-style names), 0049 (`CancellationToken` last), 0105 (`Ensure.That`).
Constitution §IV (latency budget + the leg table), §VII (dashboard
obligation), §Testing.

## Problem

Constitution §IV records **Camera → SFU** as `Measured: yes (SFU metrics)`.
The only thing in the tree behind that claim is
`tests/Integration.Tests/StreamDistribution/SfuLatencyIsReadableTests.cs`,
and **neither of its two tests reads a timing figure or a media path.**

**Test 1**, `The_sfu_exposes_its_own_measurements`, asserts
`body.ShouldContain("paths", Case.Insensitive, …)`. `paths` is MediaMTX's
per-path **count** gauge, emitted whenever `metrics: yes` is set regardless
of which other families are present; as a case-insensitive substring it also
matches `paths_readers`, `paths_bytes_sent` and anything else containing
those five letters.

**Test 2**, `Turning_metrics_on_did_not_disturb_the_media_path`, says in its
own doc comment that *"'should' is not evidence"* and then supplies exactly
the "should" it names: one `GET /v3/paths/list` against the **control** API
and `EnsureSuccessStatusCode()`. No stream opened, no path inspected, no
byte counter read.

**Two regressions land green today:**

- A MediaMTX release that renames or drops per-path families while still
  emitting `paths`. Three containers ran a floating `latest` tag until
  #2103 pinned `1.21.0-ffmpeg` on 2026-09-10.
- A change to `src/AppHost/Resources/mediamtx.yml` that breaks RTP ingest
  for every path. `/v3/paths/list` still answers 200 with an empty or
  non-publishing list. **Video is dead on every wall and both tests pass.**

## The finding that changes what can be asked for

The issue asks test 1 to assert **a named RTP timing family** — "the one a
dashboard would actually plot for the 80 ms leg". Before writing any
assertion, the live endpoint was read. **There is no such family.**

Read from the running `bluenviron/mediamtx:1.21.0-ffmpeg` SFU
(`/metrics`, 2026-09-10): **123 metric families**. Twelve carry a time
dimension, and **not one of them belongs to the `paths` group**:

| Family | Scope |
|---|---|
| `rtsp_sessions_inbound_rtp_packets_jitter`, `rtsp_sessions_rtp_packets_jitter` | server-side RTSP **sessions** |
| `webrtc_sessions_inbound_rtp_packets_jitter`, `webrtc_sessions_rtp_packets_jitter` | server-side WebRTC **sessions** |
| `srt_conns_ms_rtt`, `srt_conns_ms_receive_buf`, `srt_conns_ms_send_buf`, `srt_conns_ms_receive_tsb_pd_delay`, `srt_conns_ms_send_tsb_pd_delay`, `srt_conns_us_snd_duration`, `srt_conns_us_packets_send_period`, `srt_conns_packets_received_avg_belated_time` | SRT — a protocol this system does not use |

The seven `paths_*` families are `paths`, `paths_readers`,
`paths_inbound_bytes`, `paths_outbound_bytes`,
`paths_inbound_frames_in_error`, `paths_bytes_received`,
`paths_bytes_sent`. **All volume and count. No time.**

**The jitter families do not rescue it either, and this was checked rather
than reasoned about.** A probe path was created on the running SFU pulling
`rtsp://camera-sim:8554/coiler`, and observed ready with H.264 tracks and
41 MB ingested. While that ingest was live the entire non-zero content of
the exposition was three lines:

```
paths{name="probe-2192",state="ready"} 1
paths_inbound_bytes{name="probe-2192",state="ready"} 2983355
paths_bytes_received{name="probe-2192",state="ready"} 2983355
```

`rtsp_conns` and `rtsp_sessions` both read `0`. **An RTSP *source pull* — which
is how every camera in this system is ingested — is neither a connection nor
a session in MediaMTX's accounting**, so the four jitter gauges are not
merely the wrong quantity for this leg, they are never populated by it.

**Therefore: the Camera → SFU leg cannot be measured from this endpoint as
configured, by duration or by jitter.** That is a stronger statement than
either #2192 or #2152 makes, and it is why item 1 of the issue cannot be
delivered as written.

## What this spec does

Replace both assertions with ones that correspond to what MediaMTX actually
returns, and record the finding as an executable claim rather than prose.

- **FR-001** Test 1 asserts the per-path ingest families **by exact family
  name at line start** — not a substring, not case-insensitively — for a
  path this test established, in `state="ready"` and with a non-zero byte
  count. A rename or a drop fails loudly and names the family it wanted.
- **FR-002** Test 1 also asserts that **no `paths_*` family carries a time
  dimension**, guarded against vacuity by first requiring that `paths_*`
  lines exist at all. This is the finding, executable: the day MediaMTX
  starts publishing per-path timing, this goes red and someone re-reads §IV.
- **FR-003** Test 2 establishes a path through the product's own flow —
  register a camera at `AspireFixture.RtspTestSourceUrl` (#198), wait for
  the stream to reach `Healthy` — and then asserts `/v3/paths/list` reports
  that path `ready`, carrying a track, with `inboundBytes > 0`.
- **FR-004** A control test proves the new predicate discriminates: a camera
  at an unreachable address produces a path that **is** in both surfaces and
  is **not** reported as ingesting. Without it a green FR-001/FR-003 is
  equally consistent with a predicate that says yes to everything — the same
  reasoning `RtspTestSourceHealthTests` gives for keeping its `Degraded` case
  beside its `Healthy` one.
- **FR-005** The file is renamed. `SfuLatencyIsReadableTests` asserts a claim
  this spec has just disproved.

## Non-goals

- **NFR-001 No constitution edit.** §IV's `Measured: yes (SFU metrics)` row
  is wrong and this spec says what it should say, in `verification.md`.
  Editing `.specify/memory/constitution.md` is a blocked outcome under
  ADR-0144. Filed by a human, not here.
- **NFR-002 No new measurement.** Nothing here makes the 80 ms leg
  measurable. It establishes that it is not, from this endpoint.
- **NFR-003 No AppHost edit.** `AppHost.cs` is a contention file (ADR-0109),
  and it carries a comment that repeats the same false claim. Reported, not
  touched.
- **NFR-004 Nothing moves out of the Docker-gated integration lane**, and no
  test gains a skip, category or condition.

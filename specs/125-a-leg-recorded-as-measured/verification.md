# Verification 125 — A leg recorded as measured

**Issue:** #2192 (+ #2152's F15) · **Branch:** `fix/2192-a-leg-recorded-as-measured`
**Phase 4a colour:** RED (behaviour-changing)
**MediaMTX:** `bluenviron/mediamtx:1.21.0-ffmpeg` (pinned by #2103,
`sha256:9d148b5f…4f19e`), read 2026-09-10.

## 1. What the live `/metrics` exposition actually contains

Read from the running SFU before writing any assertion, as the issue
required. Two readings: the idle container, and the same container while it
was ingesting a real H.264 source.

**Idle** (148 dev paths present): **1 195 lines, 123 metric families.**
Twelve families carry a time dimension, and **none of the twelve is in the
`paths` group**:

```
rtsp_sessions_inbound_rtp_packets_jitter      webrtc_sessions_inbound_rtp_packets_jitter
rtsp_sessions_rtp_packets_jitter              webrtc_sessions_rtp_packets_jitter
srt_conns_ms_rtt                              srt_conns_ms_receive_buf
srt_conns_ms_send_buf                         srt_conns_ms_receive_tsb_pd_delay
srt_conns_ms_send_tsb_pd_delay                srt_conns_us_snd_duration
srt_conns_us_packets_send_period              srt_conns_packets_received_avg_belated_time
```

The seven `paths_*` families are `paths`, `paths_readers`,
`paths_inbound_bytes`, `paths_outbound_bytes`, `paths_inbound_frames_in_error`,
`paths_bytes_received`, `paths_bytes_sent` — **all volume and count, no
time.**

**Ingesting.** A probe path was created on the running SFU pulling
`rtsp://camera-sim:8554/coiler`, and observed `ready: true`, `tracks:
["H264"]`, `inboundBytes: 41 194 131`. While that ingest was live, the
**entire non-zero content of the exposition** was three lines:

```
paths{name="probe-2192",state="ready"} 1
paths_inbound_bytes{name="probe-2192",state="ready"} 2983355
paths_bytes_received{name="probe-2192",state="ready"} 2983355
```

`rtsp_conns` and `rtsp_sessions` both read `0`. **An RTSP source pull — how
every camera in this system is ingested — is neither a connection nor a
session in MediaMTX's accounting**, so the four jitter gauges are not merely
the wrong quantity for this leg, they are never populated by it at all.

**Conclusion: there is no RTP timing family to name.** Item 1 of #2192
cannot be delivered as written. The camera → SFU leg **cannot be measured
from this endpoint as configured — not by duration, and not by jitter.**

## 2. Red, verbatim

Produced by pointing the tests' camera at `rtsp://10.0.6.1/h264` (a local
edit, reverted before the green run) — the real "path provisioned, nothing
ingesting" state the issue names, produced by the real server rather than a
hand-written string. The **old** tests were run against that same state in
the same invocation.

```
  Failed SfuIngestMetricsTests.The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family [25 s]
  Error Message:
   Shouldly.ShouldAssertException : sample
    should contain
"state="ready""
    but was actually
"paths{name="cam-01a08cec-437e-7195-9317-04e48510b837",state="notReady"} 1"

Additional Info:
    'paths' reports the path not ready
  Standard Output Messages:
 SFU exposition: 160 lines

  Failed SfuIngestMetricsTests.Turning_metrics_on_did_not_disturb_the_media_path [748 ms]
  Error Message:
   Shouldly.ShouldAssertException : state.GetProperty("ready").GetBoolean()
    should be
True
    but was
False

Additional Info:
    the SFU lists 'cam-01a08cec-9d54-76ea-b71a-4a715f396e38' but is not ingesting it, so metrics are on and the media path is dead

Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 30 s
```

Per-test outcomes from `red.trx`, which is the half that matters:

```
Failed  SfuIngestMetricsTests.The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family
Failed  SfuIngestMetricsTests.Turning_metrics_on_did_not_disturb_the_media_path
Passed  SfuIngestMetricsTests.A_path_whose_source_does_not_answer_is_not_reported_as_ingesting
Passed  SfuLatencyIsReadableTests.The_sfu_exposes_its_own_measurements
Passed  SfuLatencyIsReadableTests.Turning_metrics_on_did_not_disturb_the_media_path
```

**Against an SFU ingesting nothing on any path, both assertions this spec
replaces pass.** That is the issue's claim, observed rather than argued.

## 3. Green, verbatim

Camera restored to `AspireFixture.RtspTestSourceUrl`, rebuilt, re-run:

```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 18 s
```

Diagnostic output from the same run, which is what makes the
no-timing-family assertion non-vacuous:

```
SFU exposition: 160 lines
paths families: paths, paths_readers, paths_inbound_bytes, paths_outbound_bytes,
paths_inbound_frames_in_error, paths_bytes_received, paths_bytes_sent
```

Seven families present, none time-dimensioned.

## 4. What each test does and does not discharge for §IV

The leg is **Camera → SFU (RTP ingest), ≤ 80 ms**. No runtime code changed,
so no leg's budget moved.

| Test | Discharges | Does **not** discharge |
|---|---|---|
| `The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family` | that the named counters a dashboard would read still exist under those names, and that RTP is arriving on an established path | **any figure in milliseconds.** It asserts the opposite: that no per-path timing family exists to read one from |
| `Turning_metrics_on_did_not_disturb_the_media_path` | that `metrics: yes` leaves the media path ingesting — ready, a track announced, bytes received | anything about *how long* ingest takes |
| `A_path_whose_source_does_not_answer_is_not_reported_as_ingesting` | that the predicate above discriminates | — |

**Nothing here measures the 80 ms budget, and after this change nothing in
the tree claims to.** The leg is *watched* — a break in ingest now fails a
test — and it is *not measured*. Those are different columns in §IV's table
and this spec separates them.

## 5. What §IV's row should say — to be filed by a human

`.specify/memory/constitution.md` was **not edited**: a constitution change
is a blocked outcome under ADR-0144 (NFR-001). The row today reads

| Leg | Implemented | Measured | Dashboard |
|---|---|---|---|
| Camera → SFU | yes | yes (SFU metrics) | no |

and the `Measured` cell is wrong. What was verified above supports:

| Leg | Implemented | Measured | Dashboard |
|---|---|---|---|
| Camera → SFU | yes | **no — not measurable from the SFU as configured; see below** | no |

with the accompanying note:

> **Camera → SFU has never been measured, and cannot be from where the
> record said it was.** The `yes (SFU metrics)` this row carried rested on
> two integration tests, neither of which read a timing figure — one matched
> the substring `paths`, the other asserted only that the control API
> answered 200. Reading the endpoint settles it: MediaMTX 1.21.0-ffmpeg
> publishes **no time-dimensioned per-path family**. Its four RTP jitter
> gauges belong to server-side sessions and stay at zero during a source
> pull, which is how every camera here is ingested; the rest are SRT-only.
> The SFU measures ingest by volume, not by time. Spec 125 replaced both
> assertions with ones that hold the SFU to what it does publish, and the
> tests now fail when ingest stops — the leg is **watched, not measured**.
> Measuring it needs an instrument this endpoint does not contain.

This is the second row §IV has carried a discharge nobody earned, and the
first of the two the table's own closing paragraph anticipated: *"a leg
recorded as measured before anyone has read its figure claims a discharge
nobody earned."*

## 6. Build and test counts

```
dotnet build -c Release  →  Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test --filter FullyQualifiedName~SfuIngestMetricsTests
                         →  Passed: 3, Failed: 0, Skipped: 0, Total: 3
```

Test count in the file: 2 → 3. Both surviving assertions strengthened, one
control added, none deleted, none weakened, no category or skip added — the
three tests run in the same Docker-gated integration job as before
(`Category!=Measurement&Category!=Disruptive&Category!=Maintenance` matches
none of them).

## 7. Adjacent, not fixed here

`src/AppHost/AppHost.cs:156-157` carries the same false claim in a comment —
*"the SFU measures its own RTP ingest; exposing the endpoint is what makes
camera → SFU readable at all (§VII, ≤ 80 ms)"* — as does
`src/AppHost/Resources/mediamtx.yml`. `AppHost.cs` is a contention file
(ADR-0109) and this slice does not own it (NFR-003). Both wordings should
follow whatever §IV's row becomes.

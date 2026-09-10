# Tasks 125 — A leg recorded as measured

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #2192 (+ #2152 F15)
**Phase 4a colour:** RED — behaviour-changing (plan.md, *Phase 4a colour*)

Each task is independently verifiable, and each commit builds on its own.

## T001 — Read the live exposition before asserting anything

**Done when** the metric families of the running
`bluenviron/mediamtx:1.21.0-ffmpeg` SFU are enumerated from `/metrics` and
classified, and the question *"is there a per-path RTP timing family?"* is
answered from that reading rather than from documentation.

Includes creating a live ingest path on the SFU and re-reading, so the
answer covers the populated case and not only the idle one.

**Status:** done — see `spec.md`, *The finding that changes what can be
asked for*. Answer: no. 123 families, 12 time-dimensioned, none in the
`paths` group; an RTSP source pull registers as neither a connection nor a
session, so the jitter gauges are never populated by this leg.

## T002 — Red: the new assertions fail against a real non-publishing SFU

**Done when** both new tests are run against the Aspire stack with the
camera pointed at an unreachable address, observed **failing**, and the
output captured verbatim; and the assertions they replace are run against
the same state and observed **passing**.

Depends on T001 (the named family cannot be chosen before it is known).

## T003 — Strengthen the metrics assertion (FR-001, FR-002)

**Done when** `The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family`
asserts `paths`, `paths_inbound_bytes` and `paths_bytes_received` by exact
family name for the established path with `state="ready"` and a non-zero
byte count, and asserts no `paths_*` family is time-dimensioned, guarded
against vacuity.

## T004 — Give test 2 a media path to check (FR-003)

**Done when** `Turning_metrics_on_did_not_disturb_the_media_path` registers
a camera at `AspireFixture.RtspTestSourceUrl`, waits for `Healthy`, and
asserts `/v3/paths/list` reports that path `ready`, with a track and
`inboundBytes > 0`.

## T005 — Prove the predicate discriminates (FR-004)

**Done when** `A_path_whose_source_does_not_answer_is_not_reported_as_ingesting`
asserts the unreachable camera's path is present in both surfaces and is
reported not ingesting in both.

## T006 — Rename the file and class (FR-005)

**Done when** `SfuLatencyIsReadableTests` is `SfuIngestMetricsTests`, its
doc comment states the finding, and nothing else references the old name.

## T007 — Green, and the whole gate

**Done when** the three tests pass against the fixture stack with the
fixture source restored, `dotnet build -c Release` is clean, and
`verification.md` records: the verbatim red, the verbatim green, what each
test does and does not discharge for §IV's Camera → SFU leg, and the wording
§IV's row should carry — **to be filed by a human, not edited here**
(NFR-001).

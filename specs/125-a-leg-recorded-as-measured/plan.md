# Plan 125 — A leg recorded as measured

**Spec:** `spec.md` · **Issue:** #2192 (+ #2152 F15) · **Lane:** autonomous

## Phase 4a colour: RED (behaviour-changing)

The change is to what two tests assert, so "behaviour" here is the tests'
own discriminating power. ADR-0144 resolves ambiguity to red, and red is
available honestly: **the new assertions are run against a real MediaMTX
that exhibits the regression they claim to catch**, and observed failing,
while the assertions they replace are run against the same state and
observed passing.

The regression is reproducible without inventing a string: point the test's
camera at an address nothing answers. StreamDistribution still provisions
the path, MediaMTX still lists it, `/metrics` still emits its `paths_*`
lines — with `state="notReady"` and `paths_inbound_bytes 0`. That is exactly
the "`/v3/paths/list` answers 200 with a non-publishing list" state the issue
names, produced by the real server.

So the red procedure is:

1. Boot the Aspire fixture stack.
2. Point the new tests' camera at the unreachable address (a local edit,
   not committed) and run them. **Both fail**, naming the family and the
   path state they wanted. Capture verbatim.
3. Run the *old* assertions against the same stack. **Both pass.** That is
   the gap, demonstrated rather than argued.
4. Restore `AspireFixture.RtspTestSourceUrl` and run. **Green.**

Step 4's control is then kept permanently as FR-004, so the discrimination
proof lives in the tree instead of only in this document.

## Approach

One file, renamed:
`tests/Integration.Tests/StreamDistribution/SfuLatencyIsReadableTests.cs`
→ `SfuIngestMetricsTests.cs`.

**Why the name changes.** The current name asserts that SFU latency is
readable. It is not, and the file is the only evidence §IV's row rests on.
A file whose name states the conclusion the tests fail to reach is the same
defect one level up.

### The three tests

| Test | Asserts | Establishes a path? |
|---|---|---|
| `The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family` | `paths`, `paths_inbound_bytes`, `paths_bytes_received` present **by exact family name** for the camera's path, `state="ready"`, bytes > 0; and no `paths_*` family is time-dimensioned | yes |
| `Turning_metrics_on_did_not_disturb_the_media_path` | `/v3/paths/list` reports the camera's path `ready`, with ≥ 1 track and `inboundBytes > 0` | yes |
| `A_path_whose_source_does_not_answer_is_not_reported_as_ingesting` | the path **is** present in both surfaces, and is `state="notReady"` / `ready:false` with zero inbound bytes | yes, unreachable |

Test 2 keeps its name. The name was never the problem — the body was
empty, and after this change the name is finally true.

### Matching by family name, not substring

Prometheus exposition is one sample per line, `family{labels} value`.
Matching `line.StartsWith(family + "{")` is exact by construction:
`paths{` cannot match `paths_inbound_bytes{`, which is the whole failure of
the case-insensitive `ShouldContain("paths")` it replaces. A tiny private
helper does the split and the value parse; no parser library, no fixture
file, no new project reference.

### Time dimension

A `paths_*` family is time-dimensioned if its name contains any of `_us`,
`_ms`, `_seconds`, `_duration`, `_jitter`, `_rtt`, `_latency`, `_time` as a
segment. Today none does. The assertion is guarded by first requiring that
`paths_*` samples exist for the established path, so it can never pass
vacuously against an empty exposition — the exact failure mode this spec
exists to remove.

### Establishing a path

Mirrors `RtspTestSourceHealthTests`: `POST /cameras` with
`AspireFixture.RtspTestSourceUrl`, then poll
`GET /streams?cameraIdentifiers={id}` until `Healthy` (30 s budget, the
figure that file already uses and justifies). `InitializeAsync` resets
MediaMTX, StreamDistribution and CameraCatalog, as every other stream test
in the folder does.

The helpers are local copies of that file's, matching the precedent
`PlantFloor`'s own doc comment sets: collapsing the near-verbatim
registration helpers across six files is worth doing and **is not this
change**.

## Constitution and ADR check

- **§IV.** This test sits on the Camera → SFU leg and changes no runtime
  code, so it moves no leg's budget. What it changes is what the tree
  *claims* about that leg; the discharge statement is in `verification.md`.
- **§II / primitives.** Test code, no domain model. No value object needed.
- **ADR-0103.** Integration lane, Aspire fixture, no Testcontainers.
- **ADR-0084.** One file, three facts plus three private helpers; each
  method under 30 LOC, ≤ 4 params, depth ≤ 3. Watched, not assumed.
- **ADR-0049.** No public async surface added; helpers are private and take
  the fixture's clients. Nothing gains a cancellation-token parameter it
  would ignore.
- **ADR-0105.** No argument guards added — nothing here takes an argument
  from outside the file.

## Risks

- **Three path establishments per run, ~30 s each worst case.** Accepted:
  the folder already pays this twice in `RtspTestSourceHealthTests`, and
  the alternative is a shared-state class fixture that makes one test's
  failure another's.
- **The control test depends on the unreachable address staying
  unreachable.** It reuses `rtsp://10.0.6.1/h264`, the address family this
  suite already relies on for exactly this, in two files.

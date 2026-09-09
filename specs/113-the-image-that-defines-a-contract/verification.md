# Verification: The image that defines a contract

**Phase 5 of ADR-0037** · Issue #2103 · Branch `chore/2103-the-image-that-defines-a-contract`

| Commit | |
|---|---|
| `84e8d2d8` | `docs(113): a spec, a plan and tasks for pinning the image MediaMTX is` |
| `cc0a6884` | `chore(apphost): pin MediaMTX to the release the stack is running` |
| `03337482` | `test(apphost): a floating container tag fails the build` |

---

## 1. What the pin is, and the evidence it changes nothing

`bluenviron/mediamtx:latest-ffmpeg` → `bluenviron/mediamtx:1.21.0-ffmpeg` at
`src/AppHost/AppHost.cs:153` (`mediamtx`), `:196` (`fixture-video`), `:590` (`camera-sim`),
and at `scripts/generate-sim-clips.sh:99`, which runs FFmpeg out of the same image because
the host has none.

The two tags resolve to one manifest, checked against the registry rather than assumed:

```
== latest-ffmpeg
"architecture":"amd64"  "digest":"sha256:a19679d30b1a565bc1a95911de50248480f06b972b765ba27f4d2d9bfa6c9b07"
"architecture":"arm64"  "digest":"sha256:7a3c8d6cf0611c1a4efe3a23e9a3320f04d738c322c5b37a9021ffb6bb91ef56"
"digest":"sha256:9d148b5f29906618dee627ea3232c75d4ed60da8d40a3ad7276ca87c1ec4f19e"
== 1.21.0-ffmpeg
"architecture":"amd64"  "digest":"sha256:a19679d30b1a565bc1a95911de50248480f06b972b765ba27f4d2d9bfa6c9b07"
"architecture":"arm64"  "digest":"sha256:7a3c8d6cf0611c1a4efe3a23e9a3320f04d738c322c5b37a9021ffb6bb91ef56"
"digest":"sha256:9d148b5f29906618dee627ea3232c75d4ed60da8d40a3ad7276ca87c1ec4f19e"
```

`sha256:9d148b5f…` is the image the running stack holds
(`docker inspect bluenviron/mediamtx:latest-ffmpeg` → the same `RepoDigest`), and the one
today's WHEP-hook verification ran against. The container reports `v1.21.0`; the published
tag has no `v` prefix, which is why the pin reads `1.21.0-ffmpeg`.

## 2. Phase 4a — characterisation, observed green *before* the change

`AppHostMediaMtxImageTests`, run against the **unpinned** tree:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 869 ms - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

The same file, **unmodified**, after the pin:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 760 ms - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

No assertion in it names a tag, which is why it did not have to move. It asserts that every
MediaMTX container in the application model — three in run mode, two in the fixture shape —
resolves to one image reference.

## 3. Phase 4a — the guard, observed red *before* the change

`ContainerImagePinTests`, run against the **unpinned** tree, verbatim:

```
[xUnit.net 00:00:01.61]     SmartSentinelEye.Architecture.Tests.ContainerImagePinTests.No_container_image_in_the_app_host_runs_a_floating_tag [FAIL]
  Failed SmartSentinelEye.Architecture.Tests.ContainerImagePinTests.No_container_image_in_the_app_host_runs_a_floating_tag [85 ms]
  Error Message:
   Shouldly.ShouldAssertException : floating
    should be empty but had
3
    items and was
[Pin { Name = mediamtx, Image = bluenviron/mediamtx, Tag = latest-ffmpeg, Line = 134 }, Pin { Name = fixture-video, Image = bluenviron/mediamtx, Tag = latest-ffmpeg, Line = 177 }, Pin { Name = camera-sim, Image = bluenviron/mediamtx, Tag = latest-ffmpeg, Line = 571 }]

Additional Info:
    3 container image(s) in src/AppHost/AppHost.cs run a floating tag:
  src/AppHost/AppHost.cs:134 mediamtx → bluenviron/mediamtx:latest-ffmpeg
  src/AppHost/AppHost.cs:177 fixture-video → bluenviron/mediamtx:latest-ffmpeg
  src/AppHost/AppHost.cs:571 camera-sim → bluenviron/mediamtx:latest-ffmpeg
Name the release instead. A floating tag changes what runs on the next pull, on whichever machine pulls first, with no diff and no PR — so the failure surfaces as a red run on a branch that changed nothing (#2103).

Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 173 ms - SmartSentinelEye.Architecture.Tests.dll (net10.0)
```

**Two of the three assertions passed on the unpinned tree**, and that is reported rather
than buried: they guard *agreement*, which held while all three sites floated together.
Their evidence is §4, not this run — an assertion whose first observed state is green is
exactly the kind this session has already caught claiming more than it checks.

After the pin, all three:

```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 98 ms - SmartSentinelEye.Architecture.Tests.dll (net10.0)
```

## 4. Counterfactuals — each assertion made to fail, one at a time

Run on the committed tree by mutating a file and re-running with `--no-build` (the guard
reads source from disk, so no rebuild is involved). Every mutation was reverted.

**(a) All three sites back on `latest-ffmpeg`.** Only the floating-tag assertion fails:

```
    3 container image(s) in src/AppHost/AppHost.cs run a floating tag:
  src/AppHost/AppHost.cs:153 mediamtx → bluenviron/mediamtx:latest-ffmpeg
  src/AppHost/AppHost.cs:196 fixture-video → bluenviron/mediamtx:latest-ffmpeg
  src/AppHost/AppHost.cs:590 camera-sim → bluenviron/mediamtx:latest-ffmpeg
Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3
```

**(b) `camera-sim` alone moved to `1.20.1-ffmpeg`** — a *pinned* tag, so the floating-tag
assertion passes and the agreement assertion is the one that fires:

```
    the MediaMTX containers name 2 different tags (1.20.1-ffmpeg, 1.21.0-ffmpeg):
  src/AppHost/AppHost.cs:153 mediamtx → 1.21.0-ffmpeg
  src/AppHost/AppHost.cs:196 fixture-video → 1.21.0-ffmpeg
  src/AppHost/AppHost.cs:590 camera-sim → 1.20.1-ffmpeg
Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3
```

The second failure is the written-reference assertion declining to judge against an
ambiguous set and naming the assertion that explains it — designed, not incidental.

**(c) Only the script's `docker run` line moved**, both AppHost and the comment left alone:

```
    the MediaMTX containers run '1.21.0-ffmpeg', and these references name something else:
  scripts/generate-sim-clips.sh:99 names '1.20.1-ffmpeg'
Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3
```

Three mutations, three distinct failures, each isolated to the assertion that claims it.

## 5. Build

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:01:57.93
```

`dotnet build -c Release`, whole solution, TreatWarningsAsErrors in force.

## 6. The stack on the pinned image

The Aspire integration fixture was booted (Docker, full stack, `E2ETests` mode) and the
three MediaMTX-facing integration classes were run against it. **No image was pulled** —
`1.21.0-ffmpeg` names the digest already in the local store, so the containers were created
from layers that were already present.

The containers Aspire created, beside the older run-mode ones left over from a previous
boot, which still carry the tag they were created with:

```
fixture-video-fjshbjrv   bluenviron/mediamtx:1.21.0-ffmpeg   Up 2 minutes
mediamtx-hcssgbcb        bluenviron/mediamtx:1.21.0-ffmpeg   Up About a minute
camera-sim-18bcf406      bluenviron/mediamtx:latest-ffmpeg   Up 9 hours     (run-mode leftover)
mediamtx-18bcf406        bluenviron/mediamtx:latest-ffmpeg   Up 12 hours    (run-mode leftover)
```

What the pinned SFU says on start-up — the version, and the bind-mounted config that
carries `authHTTPExclude`:

```
2026/09/09 18:35:45 INF MediaMTX v1.21.0, linux, amd64
2026/09/09 18:35:45 INF configuration loaded from /mediamtx.yml
2026/09/09 18:35:45 INF [metrics] started with listener on :9998 (TCP/HTTP)
2026/09/09 18:35:45 INF [RTSP] started with listeners on :8554 (TCP/RTSP), :8000 (UDP/RTP), :8001 (UDP/RTCP)
2026/09/09 18:35:45 INF [WebRTC] started with listeners on :8889 (TCP/HTTP), :8189 (UDP/ICE), :8189 (TCP/ICE)
2026/09/09 18:35:45 INF [API] started with listener on :9997 (TCP/HTTP)
```

Ten tests, all green:

```
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 47 s - SmartSentinelEye.Integration.Tests.dll (net10.0)
```

- `WhepAuthIntegrationTests` (6) — the authorization hook end to end, including
  `Authorize_a_publish_with_a_valid_admin_token_returns_403`, which is #2094's
  action-aware refusal and the 403-not-401 choice that MediaMTX's challenge behaviour
  forced.
- `RtspTestSourceHealthTests` (2) — a camera pointed at `fixture-video` reaches `Healthy`;
  one at an unreachable address reaches `Degraded`. Both read real MediaMTX state.
- `MediaMtxReconcilerIntegrationTests` (2) — orphan paths removed, a missing path re-added
  from the persisted source URL, against the pinned SFU's HTTP API.

The whole `Architecture.Tests` suite was also run, to confirm the new guard sits beside the
existing ones without disturbing them:

```
Passed!  - Failed:     0, Passed:   356, Skipped:     0, Total:   356, Duration: 17 s - SmartSentinelEye.Architecture.Tests.dll (net10.0)
```

## 7. The bump question, left open

The issue offers "a decision on how it is bumped" as one form of *done*. The tree was
searched for a convention to follow:

- **No `.github/dependabot.yml`, no `renovate.json`**, under any spelling, anywhere.
- **GitHub Actions** are pinned to commit SHAs with a `# v4` comment and bumped by hand.
- **Container images** are pinned by version tag and bumped by hand — `2.27.1-pg17`,
  `4-management-alpine`, `debian:bookworm-slim`, `golang:1.23-bookworm`,
  `ARG MOSQUITTO_VERSION=2.0.18`, `eclipse-mosquitto:2.0`.
- **Keycloak and MinIO are not ours to pin**: their tags come from the Aspire hosting
  packages, so they are already pinned transitively by `Directory.Packages.props`.

So the convention is *pin by hand, bump by hand*, and this change follows it. Whether that
should become automation is a **decision**, which the autonomous lane may not make
(ADR-0144). It is left open, with the search above as the evidence, and nothing was
invented to close it.

## 8. What was **not** verified

- **Whether upstream ever re-pushes a released tag.** A version tag is weaker than a digest
  against that, and no evidence either way was gathered. The digest is recorded in the
  AppHost comment, so switching form later costs one line.
- **The four behaviours themselves are not re-derived here.** This change pins the image
  they depend on; it does not test that MediaMTX still composes `action`, still excludes
  `api`/`metrics`/`pprof` from the hook, still refuses a publish to a static-`source` path,
  or still treats 401 as a challenge. #2094's tests cover the parts of that this repository
  owns.
- **CI has not run this branch.** The build and tests above are local.
- **The developer run-mode stack on this machine was not recreated.** Its `mediamtx` and
  `camera-sim` containers are persistent and were created 9–12 hours before the pin, so they
  still run `latest-ffmpeg` and will keep doing so until they are removed. A persistent
  container keeps the image it was created with; `docker rm` of those two (they carry **no**
  volumes — do not touch `postgres-data` or `keycloak-data`) is what makes a developer's next
  `aspire run` pick the pin up. The fixture stack above was unaffected, because its
  containers are created fresh, which is also why CI needs nothing done to it.
- **A second Aspire stack was live during the fixture run** — a run-mode AppHost started from
  the IDE at 20:11, not by this work. The fixture booted and all ten tests passed regardless,
  so it did not distort the result; it is recorded because a concurrent boot is this
  machine's known source of `FailedToStart` failures that read like code defects, and a green
  run under that condition is worth more if the condition is stated.

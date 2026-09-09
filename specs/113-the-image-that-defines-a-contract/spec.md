# Feature Specification: The image that defines a contract

**Feature Branch**: `chore/2103-the-image-that-defines-a-contract`

**Created**: 2026-09-09

**Status**: Draft — phase 1 (Specify) of ADR-0037, run in the ADR-0144 autonomous lane.

**Input**: #2103 — *"Three MediaMTX containers run a floating `latest` tag, and four
load-bearing behaviours depend on it."* Filed with its own caveat: *"Not urgent, and not
evidence anything is broken… The point is that an upstream change lands the next time an
image is pulled, on whatever machine pulls it first, with no diff and no PR."*

**Scope**: **the image reference only.** No change to `mediamtx.yml`, to the WHEP
authorization hook, to `WhepAuthValidator`, or to any MediaMTX-facing code. No new
container. No bump automation is introduced — see §5, which records that question as
open rather than answering it.

**ADRs**: [ADR-0037](../../docs/adr/0037-guided-phased-development.md) (the phases),
[ADR-0144](../../docs/adr/0144-an-autonomous-delivery-lane.md) (the lane),
[ADR-0011](../../docs/adr/0000-initial-decisions.md) rows 011–012 (the SFU),
[ADR-0139](../../docs/adr/0139-rules-that-fail-the-build-not-the-review.md) (a rule that
fails the build rather than the review — why this ships with a guard and not a comment).

---

## 1. The problem, stated as a failure mode

`bluenviron/mediamtx:latest-ffmpeg` is a moving reference. Three containers resolve it:

| Resource | Site | Lane |
|---|---|---|
| `mediamtx` | `src/AppHost/AppHost.cs:134` | every lane — the SFU itself |
| `fixture-video` | `src/AppHost/AppHost.cs:177` | run mode **and** the integration fixture (spec 076) |
| `camera-sim` | `src/AppHost/AppHost.cs:571` | run mode with the simulator enabled |

A fourth reference is not a container at all: `scripts/generate-sim-clips.sh:99` runs
`ffmpeg` out of the same image, because the host has none.

The failure this creates is not "MediaMTX breaks". It is **a red CI run on a branch that
changed nothing**, on a machine that pulled a newer image than the one the developer
holds. CI pulls fresh; a local image can be months old. The two disagree, and the
disagreement reads as machine-specific flakiness rather than as an upstream release.

## 2. Why this image, and not "every image"

MediaMTX is not only a dependency here. It **composes inputs this repository's code
reads**, and it decides outcomes this repository's code relies on. Four of them:

1. **The external-auth hook payload.** `POST /streams/authorize` reads `token`, `path`
   and — since #2094 — `action`, all three composed by MediaMTX. A renamed or dropped
   field changes what the handler receives. #2094 chose to **fail closed** on a missing
   `action` *partly because the image floats*.
2. **`authHTTPExclude` semantics** in `src/AppHost/Resources/mediamtx.yml`, which keep
   `api`, `metrics` and `pprof` away from the hook.
3. **Publisher refusal on a path with a static `source`.** MediaMTX's `doAddPublisher`
   answers *"can't publish to path … since 'source' is not 'publisher'"*. That refusal is
   what makes #2094's remaining gap non-exploitable today, and **it is upstream
   behaviour, not ours**.
4. **401-is-a-challenge.** MediaMTX treats 401 as an invitation to re-authenticate, which
   is why #2094 answers **403** for a refused action.

None of the four is expressed in a file in this repository. All four can change in a
release that arrives with no diff and no PR.

There is a fifth cost, already paid: **spec 112's architect could not name a MediaMTX
version** when reasoning about what MediaMTX does with a deny response, because the tag
floats. A pinned tag answers that question by reading one line.

## 3. What the running stack is, today

Established before this spec was written, from the image the stack is running and the one
today's WHEP-hook verification ran against:

| | |
|---|---|
| version | **v1.21.0** |
| manifest digest | `sha256:9d148b5f29906618dee627ea3232c75d4ed60da8d40a3ad7276ca87c1ec4f19e` |
| image created | 2026-09-05T21:36:18Z |

Docker Hub resolves **`1.21.0-ffmpeg`** and **`latest-ffmpeg`** to that same manifest
digest (checked per-tag against the registry, including the four per-architecture
digests). So naming the version changes the bytes not at all. There is no `v`-prefixed
tag: the repository publishes `1.21.0-ffmpeg`, not `v1.21.0-ffmpeg`.

## 4. Requirements

- **FR-001** — All three MediaMTX containers name a fixed upstream release rather than a
  floating tag.
- **FR-002** — The three name the **same** release. `fixture-video`'s comment states, as
  a cost argument, that its image is "the same tag the ungated `mediamtx` above already
  pulls"; three independent literals can drift and silently make that false, which costs
  an image pull in every integration run.
- **FR-003** — The clip-generation script names the same release, so the repository has
  one MediaMTX version rather than nearly one.
- **FR-004** — A **guard fails the build** when a container image tag in the AppHost
  floats. A comment saying "keep this pinned" is the class of rule ADR-0139 removed.
- **FR-005** — No behaviour changes. The pinned reference is the image already running.

### Out of scope

- Bumping MediaMTX to any newer release.
- Introducing Dependabot, Renovate, or any other automation (§5).
- Pinning images this repository does not choose — Keycloak and MinIO's tags come from
  their Aspire hosting packages, and are already pinned *transitively*, by the package
  version in `Directory.Packages.props`.

## 5. The one question this spec leaves open

**How a pinned image gets bumped.** The issue offers "a decision on how it is bumped" as
part of *done*. The repository was searched for an existing convention:

- **No `.github/dependabot.yml`, no `renovate.json`**, under any spelling, anywhere in
  the tree.
- **GitHub Actions are pinned to commit SHAs** with a readable `# v4` comment, and are
  bumped by hand.
- **Container images are pinned by version tag and bumped by hand**:
  `timescale/timescaledb:2.27.1-pg17`, `rabbitmq:4-management-alpine`,
  `debian:bookworm-slim` and `golang:1.23-bookworm` in the Mosquitto Dockerfile,
  `ARG MOSQUITTO_VERSION=2.0.18`, `eclipse-mosquitto:2.0` in the Helm chart.

So the convention that exists is *pin by hand, bump by hand*, and this change follows it.
Whether that should become automation is a **decision**, and the autonomous lane may not
make one (ADR-0144). It is recorded here as open, and nothing is invented to close it.

## 6. Two claims in the issue that the tree does not support

- **The line numbers have moved.** The issue cites `:134`, `:166` and `:552`; the sites
  are at `:134`, `:177` and `:571`. Only the first is still right.
- **Mosquitto is not digest-pinned.** The brief lists "a digest-pinned `mosquitto`" among
  the repository's existing pins. What a `docker ps` shows is
  `mosquitto:94d4ae6c5f9b2e6c7a30e239aa0bbb073b77281d` — **Aspire's content tag for the
  image it builds from `src/AppHost/mosquitto/Dockerfile`**, not a pin anyone wrote. The
  repository contains **no `sha256:` container pin at all**. This matters because it was
  the only evidence offered for digests being a house pattern here.

## 7. Success criteria

- **SC-001** — `grep latest-ffmpeg src/AppHost/AppHost.cs scripts/` returns nothing.
- **SC-002** — The new guard fails when any one of the three sites is reverted to
  `latest-ffmpeg` (demonstrated, not asserted — see `verification.md`).
- **SC-003** — `dotnet build -c Release` is clean.
- **SC-004** — The MediaMTX-dependent behaviour observed before the change is observed
  again after it, from the same tests, unmodified.

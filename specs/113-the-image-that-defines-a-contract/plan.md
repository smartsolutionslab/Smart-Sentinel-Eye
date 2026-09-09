# Implementation Plan: The image that defines a contract

**Spec**: [spec.md](spec.md) · **Issue**: #2103 · **Phase 2 of ADR-0037**

**Phase 4a colour (declared here, per ADR-0144)**: **both, and each where it belongs.**

- The **pin itself is behaviour-preserving** — `1.21.0-ffmpeg` and `latest-ffmpeg` resolve
  to one manifest digest, so the containers run the same bytes. Its evidence is
  **characterisation, observed green before the change and unmodified after**.
- The **guard is new behaviour** — the repository does not fail a build on a floating tag
  today. Its evidence is **red first**, then green.

Declaring one colour for the whole issue would have forced a lie in one direction or the
other. The split is not a loophole: the characterisation tests may not be edited, and the
guard must be seen failing.

---

## 1. The form of the pin: a version tag, not a digest

Both were considered. The tag wins on four counts, and the digest's one advantage is
kept anyway.

**It is the house pattern, and the counter-example was a misreading.** Every image this
repository pins, it pins by version tag: `timescale/timescaledb:2.27.1-pg17`,
`rabbitmq:4-management-alpine`, `debian:bookworm-slim`, `golang:1.23-bookworm`,
`ARG MOSQUITTO_VERSION=2.0.18`, `eclipse-mosquitto:2.0`. There is **no `sha256:` container
pin anywhere in the tree** (spec §6).

**It closes the failure the issue describes.** That failure is an upstream *release*
changing a contract with no diff. `1.21.0-ffmpeg` cannot become 1.22.0. A digest would
additionally close a re-push of an existing release tag — a different, rarer failure, and
one nothing in this repository has observed.

**It is readable at the point of use, which is the fifth cost in spec §2.** Spec 112's
architect needed *a version number* to reason about MediaMTX's deny behaviour and check it
against a changelog. `1.21.0-ffmpeg` answers that from the source line;
`@sha256:9d148b5f…` sends the reader to a registry.

**Aspire's own shape prefers it.** `AddContainer(name, image, tag)` takes a tag as its
third argument, beside `.WithImageTag("2.27.1-pg17")` twenty lines above. A digest needs a
different call and would make the three MediaMTX sites the odd ones out in their own file.

**The digest is kept, in a comment**, so the exact artifact stays recoverable and this
spec's §3 evidence is reachable from the code. That is the pairing `ci.yml` already uses
for actions — an exact reference plus a human-readable name — with the emphasis matching
the AppHost's existing form rather than the workflow's.

## 2. Three literals, not one constant

The alternative was a `const string MediaMtxImageTag` used at all three sites. Rejected,
narrowly:

- The AppHost is a literal-heavy composition file with no constants today, and it is a
  **high-contention file** (ADR-0109) — a declaration at line 130 read at line 571 is a
  longer-range coupling than three visible literals.
- FR-002's real requirement is *the three agree*, and a guard enforces agreement whether
  the value is shared syntactically or not. With the guard, a constant buys nothing the
  guard does not already buy; without it, a constant is the weaker of the two.
- A literal is what the guard can read most simply. A source-scanning guard that has to
  resolve an identifier has one more way to match nothing and pass — the failure mode
  `LogTailCoverageTests` names in its own doc comment.

## 3. The guard

`tests/Architecture.Tests/ContainerImagePinTests.cs`, in the pattern of
`LogTailCoverageTests` and `FoundingDecisionRecordTests`: reads the tree from disk, no
project reference to the AppHost, no Docker, runs in the unit + architecture job.

Three assertions, each independently failable:

1. **No floating tag in the AppHost.** Every literal tag — the third argument of
   `AddContainer`, and every `.WithImageTag("…")` — must not be `latest`, `latest-…`, or
   `…-latest`. Guards containers that do not exist yet, which is the half of the problem
   pinning three sites does not address.
2. **The MediaMTX containers agree.** Every `AddContainer(…, "bluenviron/mediamtx", "…")`
   names one tag, at least three sites found. Fails on drift between the three, which is
   FR-002 and is what `fixture-video`'s "no image pull at all" comment rests on.
3. **The scripts agree with the AppHost.** Any `bluenviron/mediamtx:<tag>` under
   `scripts/` must name the tag the AppHost names.

Each carries a **scan-found-something** assertion, because a source-scanning guard that
matches nothing passes, and a passing guard that checks nothing is indistinguishable from
one that holds. Both prior guards learned that the hard way and say so.

**The guard bans a category, not a value.** It does not assert `1.21.0-ffmpeg`. Bumping
MediaMTX must not require editing the guard — a guard that obstructs the legitimate change
it governs is deleted within a month, taking its protection with it
(`FoundingDecisionRecordTests`, in as many words).

## 4. The characterisation

`tests/Integration.Tests/AppHostMediaMtxImageTests.cs`,
`[Trait("Category", "FixtureLogic")]` — required by `IntegrationTestSelectionTests`, and
true: it builds the application model with `DistributedApplicationTestingBuilder` and
**starts nothing**, so it costs no container and is safe beside a live stack, exactly as
`AppHostE2ESwitchTests` documents.

It asserts the property that must survive the change, in a form whose **assertions do not
move when the tag does**:

- run mode with the simulator resolves **three** MediaMTX containers;
- the integration-fixture shape resolves **two** (`camera-sim` is dev-only);
- in each shape, every MediaMTX container carries an **identical image reference**.

Before the change that reads `bluenviron/mediamtx:latest-ffmpeg` three times; after, it
reads `bluenviron/mediamtx:1.21.0-ffmpeg` three times. The test passes both times,
unmodified. An assertion pinning the literal tag would have had to be edited by the very
change it was guarding — which ADR-0144 calls evidence the behaviour moved, and blocks.

## 5. Order of work, and why the tree is never red

ADR-0144 wants the red output observed; ADR-0087 rebase-merges each commit onto `develop`
individually, so a commit that leaves the suite red breaks `git bisect` for everyone
afterwards. Both are satisfied by observing red in the **working tree** and committing in
an order that never records it:

1. Write the characterisation; run it against the **unpinned** tree; capture green.
2. Write the guard; run it against the **unpinned** tree; capture **red** — the verbatim
   output is the phase-4a artifact and goes in the PR body.
3. Apply the pin. Commit it. The tree is green at this commit — the guard is not in it.
4. Commit the guard and the characterisation. Green.

A counterfactual closes the loop: after both are committed, one site is reverted to
`latest-ffmpeg` in the working tree and the guard is watched failing on exactly that
site — proof the assertion can fail *for the reason it claims*, not merely that it once
did before the file it reads was written.

## 6. Risk

**The stack must be rebooted for the pin to take.** A persistent container keeps the image
it was created with; changing the tag needs `docker rm` of the container, **keeping the
volume** — a dropped `postgres-data` breaks the TimescaleDB preload and a dropped
`keycloak-data` silently keeps a stale realm. Only the MediaMTX containers need removing,
and none of the three has a volume.

**No pull is expected.** `1.21.0-ffmpeg` is the digest already in the local image store,
so the tag resolves against a layer set that is present. Disk is ~11 GB, which is why this
matters.

# AppHost dev resources

## `clips/` — loop clips for `camera-sim` and `fixture-video` (ADR-0111, spec 044, spec 056)

**Two resources mount this directory, and only one of them is dev-only.**

**Scenario Simulator — dev-only.** `camera-sim` (the second, config-clean
MediaMTX) bind-mounts this **directory** at `/media` and loops one clip per
path via the per-path `runOnDemand` FFmpeg hook the `scenario-simulator` worker
provisions. Each scenario asset names the clip it plays (`Camera.Clip` in
`Scenarios/*.json`); a camera belonging to no asset gets `sim-loop.mp4` with
its name drawn on it. Never reached by CI/E2E/prod: the container and the
worker are both gated `isRunMode && !isE2ETests && isScenarioSimulatorEnabled`
in `AppHost.cs`, and the end-to-end job boots with `ScenarioSimulator=false`
(#2013).

**`fixture-video` — not dev-only.** It is gated `isRunMode && !isE2ETests`,
deliberately *without* that third conjunct, and bind-mounts the same directory
at `/media`; `Resources/fixture-video.yml` publishes `/media/sim-loop.mp4` on
start via `runOnInit`. So `sim-loop.mp4` **is** the video source the CI
end-to-end job serves, and the only picture
`kiosk-shows-a-label-over-video.spec.ts` has to assert over. Do not prune this
directory as dev-only cruft, and do not skip `generate-sim-clips.sh` on the
grounds that CI has no video — since spec 056 it does, from here.

The host has no FFmpeg, so clips are generated from the MediaMTX image
`scripts/generate-sim-clips.sh` names. Run once and commit the results:

```bash
bash scripts/generate-sim-clips.sh              # all clips
bash scripts/generate-sim-clips.sh electronics- # only names matching a prefix
```

Each writes a 1280x720, ~20 s, H.264 baseline, 25 fps excerpt. Two are shorter
than 20 s because their sources are (`electronics-smd-line` 16.5 s,
`electronics-conveyor` 18 s); they loop regardless.

If the directory is missing, `aspire run` fails a bind mount before the stack
comes up: `fixture-video`'s in any run-mode boot, and `camera-sim`'s as well
when the simulator is enabled. Generate it first.

### Licensing

**Every clip has a matching `<name>.ATTRIBUTION.txt` beside it, and must.**
So does `DejaVuSans.ttf`, which is here because the MediaMTX image ships no
fonts at all — without it `drawtext` fails and a labelled camera never streams. The
eight spec-044 clips come from Wikimedia Commons' *Sounds of Changes* project
under **CC BY 3.0** (attribution only). `sim-loop.mp4` is older and is
**CC BY-SA 3.0** — share-alike — so it is the one with the stricter terms.

Take a licence from the Commons **file page**, never from the `extmetadata`
API: they disagree, and the page is authoritative.

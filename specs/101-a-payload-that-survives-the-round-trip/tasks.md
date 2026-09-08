# Tasks 101 — A payload that survives the round trip

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issues:** #587 (fixture), #588 (test)
**Engineer:** `backend-engineer` · **Colour:** behaviour-preserving → characterisation, **observed green**
**Scope guard:** no file under `src/` may be created, modified, or deleted by this slice.

All tasks belong to the single user story **US1** (P1). They are sequential —
T003 needs T002's output, T004 needs T003 — so **no task is marked `[P]`**
*within* this slice. The slice as a whole is `[P]` against other work: it touches
no ADR-0109 contention file (plan §5).

---

## US1 — An inference payload survives ingestion with its meaning intact

### [T001] Fixture — the ~4 KB inference payload  *(issue #587)*

Create `tests/Integration.Tests/EventIngestion/Fixtures/inference-sample.json`.

- Invented, modelled on YOLO-family detector output; say so in the PR body.
  Fill to ~4 KB with additional plausible detections, **not** filler.
- **Must contain:** arrays nested ≥ 3 deep (`detections[].track.history[][]`);
  arrays whose order is not their sorted order; an S3 URL containing `&`;
  non-ASCII text; a 19-digit integer; fixed-scale decimals (`0.1000`, `1.10`).
- **Must not contain:** exponent-notation numbers (`1e5`) — `jsonb` rewrites them
  and the expectation stops being hand-reviewable; duplicate keys; nesting near
  62 levels.
- **Verify before moving on:** every number in the file is a `jsonb` fixed point.

**Depends on:** nothing.

---

### [T002] The test class, both halves  *(issue #588)*

Create `tests/Integration.Tests/EventIngestion/InferencePayloadRoundTripIntegrationTests.cs`.

- `[Collection(AspireCollection.Name)]`. **No `Category` trait** — one would
  exclude it from CI (`ci.yml:179`), and its absence is what puts it in the
  `integration` job.
- One `[Fact]`:
  `Inference_payload_with_nested_arrays_survives_the_round_trip`.
- Load both fixtures with the `RepositoryRoot()` walk
  (`IntegrationTestSelectionTests.cs:395-406`). **Do not** add a `<None>` /
  `CopyToOutputDirectory` item to the csproj.
- Publish to `fab/munich/inference/cam-0447`, QoS 1, reusing
  `PlantFloor.PublishRawAsync` (`Fixtures/PlantFloor.cs:49`) rather than adding a
  sixth copy of the token-mint-and-connect block. `kind` must match
  `^[A-Z][A-Za-z0-9]{0,127}$`.
- Poll `events` for the identifier to a deadline; read the row via
  `AspireFixture.CreateEventIngestionDbContextAsync()`.
- **Half A** — recursive semantic comparison: arrays positional and same length;
  objects compared by key **set**; strings ordinal; numbers as `decimal`. Thread a
  path string so a failure names `detections[0].track.history[1][0]`.
- **Half B** — `stored.Value.ShouldBe(expectedText)`.
- **Forbidden:** computing the Half B expectation at runtime as
  `Payload.From(File.ReadAllText(fixture)).Value`. That is the vacuity this design
  exists to avoid (spec §4).
- Respect ADR-0084: keep the comparator a small mutually-recursive pair.

At this point `inference-sample.expected.json` does not exist. Run the test, and
**capture the actual `Payload.Value` verbatim** — that is T003's input.

**Depends on:** T001.

---

### [T003] Generate the expected canonical form, then **review it by the rule**

Create `tests/Integration.Tests/EventIngestion/Fixtures/inference-sample.expected.json`
from T002's captured output.

**Generating it is the easy half; the review is the task.** Before checking it in,
confirm by inspection that it differs from `canonical(inference-sample.json)` by
**exactly one transformation**:

> every object's keys sorted by (UTF-8 byte length, then ordinal) — and nothing else.

Walk each object in the file and confirm its key order. Then confirm that
**nothing else moved**: every array in the fixture's order, every number's text
identical (`1.10` still `1.10`, `0.1000` still `0.1000`, the 19-digit integer
unchanged), `&` present as `&`, non-ASCII present as `\uXXXX`.

**If anything else moved, stop and report it.** A payload genuinely reshaped
beyond the key sort is a **production bug and a separate issue** — do not encode
it into the expectation. A test issue that quietly becomes a bug fix is two
issues (ADR-0144).

Re-run: the test must now be **green**.

**Depends on:** T002.

---

### [T004] The counterfactual — mandatory, and reported against the prediction

Prove the test can fail for the right reason. Without this, an approval test
generated from its own output is green by construction.

1. Apply the mutation from spec §9 to `src/EventIngestion/Domain/Event/Payload.cs`
   — in `From(JsonDocument)`, replace `document.WriteTo(writer)` with a recursive
   walk that is faithful for every kind except numbers, written as
   `writer.WriteNumberValue(element.GetDouble())`.
2. Run the new test **and** the tests listed in spec §9's prediction table.
3. Record the observed result against that table, in the PR body, verbatim.
   Expected: the new test **red** (Half B on three fields, Half A on
   `capturedAtNanos` only); everything else **green**.
4. **Revert the mutation.** Confirm `git diff -- src/` is empty. Never commit it.

**If any other test also reddens**, the mutation proves less than it appears to —
report that and choose a different one rather than shipping the weaker claim.

**Depends on:** T003.

---

### [T005] Final gates

- `git diff --stat -- src/` is **empty** (SC-004).
- `git diff --stat -- .github/ Directory.Packages.props global.json tests/Integration.Tests/*.csproj`
  is **empty** — no contention file, no csproj edit.
- `IntegrationTestSelectionTests` passes (the new class declares where it runs).
- Format and analyzers clean; ADR-0084 metrics respected.
- The test runs in the CI `integration` job — confirm it appears in the run, not
  merely that the run was green.

**Depends on:** T004.

---

## Dependency graph

```
T001 ──▶ T002 ──▶ T003 ──▶ T004 ──▶ T005
(fixture) (test)  (expected,  (counter-  (gates)
                   reviewed)   factual)
```

Strictly sequential. Foundational work that could be fanned out: **none** — this
slice adds no `Shared.Kernel` / `Shared.Contracts` / AppHost surface.

## Phase 3 gate

Add the **feature-level** issue to Project #13 by hand (CLAUDE.md §Workflow):

```sh
gh project item-add 13 --owner smartsolutionslab --url <issue-url>
```

#587 and #588 are one slice (spec §1); #587's own audit says it is not worth an
issue on its own. Deliver and close them together. Do **not** run
`/speckit-taskstoissues`.

## Follow-ups to file separately (spec §7)

| # | Finding |
|---|---|
| 1 | `MqttSubscriberHostedService.cs:226-233` catches only `ArgumentException`; a message with no `payload` property makes `GetRawText()` throw `InvalidOperationException`, which is never ACKed and never dead-lettered — QoS 1 redelivers it forever. **A bug, not part of this slice.** |
| 2 | No ADR records that `events.payload` is `jsonb` and therefore does not preserve key order, duplicate keys, or exponent notation. Spec 101 §2.4 is currently the only measured account of it in the repo. |
| 3 | Five near-verbatim copies of the MQTT publish helper should collapse onto `PlantFloor` (`PlantFloor.cs:22-27` already says so). |

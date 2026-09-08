# Spec 101 — A payload that survives the round trip

**Issues:** #587 (T053, fixture) + #588 (T054, test) — delivered as one slice.
**Origin:** spec 006 `tasks.md` T053/T054 (commit `d0b07d33`), never implemented.
**ADRs:** ADR-0103 (Aspire-only integration tests), ADR-0109 (contention files),
ADR-0084 (code metrics), ADR-0052/0053/0054 (test stack, naming, data),
ADR-0144 (autonomous lane; one issue is not two).
**Latency budget:** N/A — test-only. No production code changes, so no leg of
constitution §IV is touched.

---

## 1. Why this slice exists

Preserving a third-party payload is the central promise of an ingestion context.
`Payload.From` canonicalises in the middle of that path, and **a payload whose
nested arrays were silently reordered or whose numbers were re-encoded would pass
every test now on `develop`** — every EventIngestion integration test asserts row
*counts*, existence, or a single scalar field (`kind`, `device`). Verified: §2.

## 2. Premise verification (all four questions answered before specifying)

### 2.1 Is the path genuinely untested? — **Yes.**

Searched by mechanism, not by filename: `Payload`, `payload`, `GetByIdentifier`,
`jsonb`, `->>`, `::text`, `JsonDocument`, `JsonNode`, `DeepEquals`, `canonical`,
`verbatim`, `roundtrip`, `round_trip`, `byte-for-byte`, `s3://`, `amazonaws`,
across `tests/`, `apps/`, and `e2e/`.

**No test anywhere reads `events.payload` back and compares it to what was sent.**
Sixteen tests touch a payload's content; all are in-memory value-object or
handler tests, or use the payload as a *locator* rather than an assertion. The two
nearest misses:

- `tests/Integration.Tests/EventIngestion/DeadLetterFabScopingIntegrationTests.cs:85,98`
  — real content equality through Postgres, but on `dead_letters.raw_payload`, a
  **`text`** column (`DeadLetterConfiguration.cs:42`), seeded with deliberately
  non-JSON strings. Says nothing about `jsonb` or canonicalisation.
- `tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs:126,164`
  — extracts `payload->>'sequence'` and `payload->>'publishedAt'` from real
  `jsonb`, but only as sort keys and timings, never compared to the input; and it
  is `[Trait("Category", "Measurement")]`, excluded from CI.

### 2.2 Is a ~4 KB nested payload reachable through the MQTT ingress? — **Yes.**

| Constraint | Value | Verdict |
|---|---|---|
| Payload size | 64 KB, checked twice (`Payload.cs:32`, `:45`) | 4 KB is 6% of the ceiling |
| Nesting depth | no explicit `MaxDepth` anywhere on this path; STJ default 64, applied at two offsets → **~62 usable levels inside `payload`** | fine |
| Schema validation | **none** — `Payload.cs:8-14` states the payload is opaque | fine |
| Array-length / string-length caps | **none** | fine |
| Broker message size | none configured; `mosquitto.conf` sets only `max_inflight_messages`; MQTT 3.1.1 has no `MaximumPacketSize` | fine |

Two **real traps**, both of which have cost previous specs a false "the event never
arrived":

- **Broker ACL** (`src/AppHost/mosquitto/acl.txt`): `scenario-simulator` may write
  only to `fab/{munich,dresden,berlin,hamburg}/…`. Any other fab is *silently
  discarded*. → publish to `munich`.
- **`kind`** must match `^[A-Z][A-Za-z0-9]{0,127}$` (`Kind.cs:19-27`).

### 2.3 What does the read return? — **A re-canonicalised domain `Payload`, not raw bytes.**

`EventRepository.GetByIdentifierAsync` (`EventRepository.cs:16-23`) is a plain EF
query, and the EF value converter runs **`Payload.From(value)` on read**
(`EventConfiguration.cs:73`). So `@event.Payload.Value` is
`Payload.From(<jsonb text output>)` — canonicalised a second time. A byte-level
assertion *is* possible through this API; it just is not an assertion about the
raw stored bytes.

### 2.4 `jsonb` or `json`? — **`jsonb`. This invalidates #588 as written.**

`EventConfiguration.cs:72` — `.HasColumnType("jsonb")`, matching the migration.

Measured against PostgreSQL 17 (throwaway container, since retired):

| Property | `jsonb` | Consequence |
|---|---|---|
| Object key **order** | **destroyed** — re-sorted by (key byte length, then bytewise) | fatal to "byte-for-byte vs the input" |
| Duplicate keys | **de-duplicated**, last wins | — |
| Numbers | normalised via `numeric`: `1e5`→`100000`, `-0.0`→`0.0`, `2.5e-3`→`0.0025`; **trailing-zero scale preserved** (`1.10`, `0.1000` survive) | fixture must avoid exponent literals |
| String escapes | **unescaped** in output (`&`→`&`) | unobservable end-to-end |
| Whitespace | discarded | unobservable end-to-end |
| **Array order and length** | **preserved** | ← *the property this slice exists to protect* |

Worked example, measured end-to-end on a realistic fragment:

```
input canonical : {"schemaVersion":…,"model":{"name","revision","classes"},"capturedAtNanos":…,"detections":[…],"iou":1.10,…}
after jsonb     : {"iou":…,"line":…,"model":{"name","classes","revision"},"snapshot":…,"detections":[…],"schemaVersion":…,"capturedAtNanos":…}
```

This fragment is an **earlier draft**, not the shipped fixture: it carries an
`iou` of `1.10` at the top level, where the fixture has `thresholds.iou: 0.4500`
and `camera.lensCorrection: 1.10`. Left as measured rather than rewritten — it is
a recorded PostgreSQL output, and editing its literals would falsify a
measurement. §9's prediction, which *did* describe the fixture, is corrected
there.

Every object's keys moved — the top level *and* nested `model` and each
`detections[i]`. Every array (`classes`, `bbox`, `history`, `detections`) kept its
order exactly.

> **Finding — #588 cannot be satisfied as written.** "Assert the JSONB column
> equals the canonical form of the input byte-for-byte" is **false** for any
> payload whose object keys are not already in `jsonb`'s sort order, and a
> realistic inference payload never is. It would fail for a right-looking wrong
> reason. The strongest *true* assertion is specified in §4.
>
> The good news is that the audit's stated worry — *nested arrays silently
> reordering* — targets the one thing `jsonb` **does** preserve. The test that
> matters is achievable in full.

### 2.5 What canonicalisation actually does

`Payload.From(JsonDocument)` = `JsonDocument.WriteTo` into a default
`Utf8JsonWriter` (`Payload.cs:24-38`). No options object is constructed, so
`WriteIndented = false` and `Encoder = JavaScriptEncoder.Default`. Measured on
.NET 10.0.400:

| Aspect | Behaviour |
|---|---|
| Whitespace | **stripped** (the only case `PayloadTests` documents, `:17-21`) |
| Object key order | **preserved** |
| Duplicate keys | **preserved** (both survive) |
| Number literals | **preserved verbatim, as raw text** — `1.0`, `1e5`, `1.10`, `0.1000`, `-0.0`, and a 30-digit integer all pass through unchanged |
| Strings | **unescaped, then re-escaped** by `JavaScriptEncoder.Default`: `&`→`&`, `+`→`+`, `<`→`<`, `'`→`'`, and **all non-ASCII**→`\uXXXX` (uppercase hex). `/` is **not** escaped |
| Idempotent | yes, in every case tried |

So canonicalisation is *minify + ASCII-only re-escape*. It changes bytes — which
is exactly why an assertion against the raw fixture bytes would be false.

On the MQTT path the **string** overload is used, on the verbatim wire text:
`MqttSubscriberHostedService.cs:228` — `Payload.From(payload.Payload.GetRawText())`.

---

## 3. User story

**US1 (P1) — An inference payload survives ingestion with its meaning intact.**

> As an integrator shipping camera-inference events into Smart Sentinel Eye, I
> need the payload I published to come back with every nested array in the order
> I sent it, every value unchanged, and the snapshot URL intact — so that a
> downstream consumer reading bounding boxes gets the boxes I detected, in the
> order I detected them.

### Acceptance scenarios

**Happy path**
```gherkin
Given a ~4 KB inference payload with nested arrays, a large integer, fixed-scale
      decimals, a non-ASCII line name, and an S3 URL containing "&"
When  it is published to fab/munich/inference/cam-0447 as a well-formed envelope
Then  an events row is stored for that event identifier
And   reading it back yields a payload whose every array matches the fixture's
      positionally, element for element, at every depth
And   whose every object has exactly the fixture's key set at every depth
And   whose every scalar equals the fixture's — the S3 URL character for
      character, each number numerically
And   whose canonical string form equals the checked-in expected canonical form
      byte for byte
```

**Structure is not merely "some JSON"** (guards a vacuous pass)
```gherkin
Given the payload read back
Then  detections has exactly 11 elements in the fixture's order
And   detections[0].track.history is [[110,46],[111,47],[112,48]] in that order
And   the stored payload is not the empty object
```

> **Corrected during phase 6 (2026-09-08).** This scenario said **2** elements
> while the fixture carries **11** and the shipped test asserts 11. The fixture
> is right: §5 requires ~4 KB filled with *plausible* detections rather than
> filler, and two realistic detections cannot reach 4 KB. Recorded rather than
> silently edited — a committed acceptance scenario the shipped test contradicts
> is the defect class this repo keeps having to correct.

**Bad request** — covered by existing tests on `develop`
```gherkin
Given a message whose payload is not valid JSON
Then  it is dead-lettered, not stored
```
Already asserted by `PoisonDeliveryEscapeIntegrationTests.cs:73,117`. **Not
re-asserted here** — this slice adds no coverage there.

**Auth**
```gherkin
Given the publishing client authenticates to mosquitto with a Keycloak
      client-credentials token for scenario-simulator
When  it publishes to a fab outside its ACL
Then  the broker silently discards the message
```
This is the trap in §2.2, not a behaviour under test. The test publishes to
`munich`, inside the ACL. Anonymous refusal is already covered by
`AnonymousIngestIsRefusedTests`.

### Independent end-to-end test procedure

1. Boot the Aspire stack (`AspireFixture`, ADR-0103 — Docker required).
2. Mint a `scenario-simulator` token from Keycloak; connect MQTTnet v3.1.1 to
   `aspire.App.GetEndpoint("mosquitto", "mqtt")`.
3. Publish the envelope carrying the fixture to `fab/munich/inference/cam-0447`
   at QoS 1, with a fresh Guid v7 event identifier and a unique `kind`.
4. Poll `events` for that identifier until present or the deadline expires.
5. Read the row through the EF context (same query and same read converter as
   `EventRepository.GetByIdentifierAsync`) and run both assertions of §4.

---

## 4. The assertion design

Two halves. **Neither alone is honest**, and they fail for different reasons.

### Half A — semantic, compared to the parsed fixture

Walk the fixture and the read-back payload together:

- **arrays** — same length, compared **positionally**, recursively;
- **objects** — same **key set** (order-insensitive), compared recursively;
- **strings** — exact ordinal equality of the *decoded* value;
- **numbers** — equal as `decimal`;
- booleans/nulls — equal.

*Risk it avoids: **falsity**.* It asserts only what the chosen column type
actually guarantees. Key order is compared as a set precisely because `jsonb`
legitimately sorts it — asserting order there would fail on correct code. It also
gives a **diagnosable** failure (`detections[0].bbox[2]`), not "two 4 KB strings
differ at offset 1183".

*What it cannot see, by design:* anything that only changes the encoding —
`0.1000` becoming `0.1` compares numerically equal. That is Half B's job.

### Half B — byte-level, against a **checked-in** expected canonical form

Assert `@event.Payload.Value` equals the bytes of
`Fixtures/inference-sample.expected.json` exactly.

*Risk it avoids: **vacuity**.* The expectation is a reviewed literal on disk. It
must **not** be computed at runtime as `Payload.From(File.ReadAllText(fixture))` —
that would compute the expectation with the very function under test and would
prove only that `Payload.From` is deterministic, nothing about the database.

*Risk it accepts:* it encodes `jsonb`'s key-sort order, so a PostgreSQL change to
that rule would redden it. The rule has been stable since `jsonb` shipped in 9.4
and is documented; the failure would be loud and immediately diagnosable.

### Why the expectation is reviewable rather than a blind paste

An approval test is only worth its expectation's review. Here the expectation
reduces to **exactly one transformation**, verified against an independent oracle:

> **expected = canonical(fixture) with every object's keys sorted by
> (UTF-8 byte length, then ordinal)** — and nothing else changed.

Numbers, escaping, whitespace and array order all pass through untouched, because
§5 constrains the fixture to `jsonb` fixed points. A reviewer checks the file by
walking each object and confirming its keys are in that order. The **check is
mandatory** (SC-003); "by eye" is optimistic for one 4011-character line across
40 objects, and in practice a reviewer re-derives the file with a throwaway
script and diffs it. Either way the rule being confirmed is the single one
above — script-assisted review is still review, and skipping it is not an
option.

### Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Assert stored bytes == raw fixture bytes | **False.** Canonicalisation legitimately minifies and re-escapes. |
| Assert stored bytes == `Payload.From(fixture)` at runtime | **Vacuous.** Proves `Payload.From` is deterministic; the database never enters the assertion. |
| Half A only | Blind to re-encoding — the `0.1000`→`0.1` family passes. |
| Half B only | Undiagnosable, and carries the whole load alone. |
| Craft the fixture as a `jsonb` fixed point (keys pre-sorted) so #588's words come true | Makes #588 literally true, but at the cost of an unrealistic fixture — real inference output does not have keys sorted by length — and it is the kind of cleverness that makes a test look like it proves more than it does. |
| Also assert `SELECT payload::text` | A second checked-in expectation for little gain, and it is the more version-brittle of the two. Half B already exercises the same bytes one converter later. |

### Two things no assertion can reach — stated so nobody adds them later

`jsonb` erases **string escaping** and **whitespace** before anything can observe
them (`&` and `&` are the same stored value; both come back re-escaped
identically). A test asserting on either would be asserting on `Payload.From`'s
determinism, not on the round trip.

---

## 5. The fixture (#587)

`tests/Integration.Tests/EventIngestion/Fixtures/inference-sample.json`, ~4 KB.

**Provenance: invented.** No real customer payload was available. It is modelled
on YOLO-family detector output — `schemaVersion`, `model{name,revision,classes}`,
`detections[]` each with `class`, `confidence`, `bbox[4]`, `track{id,history[][]}`,
plus a `snapshot.url` — and is filled to ~4 KB with additional plausible
detections, not with filler.

**Required content** (each earns its place):

| Element | Why |
|---|---|
| Nested arrays ≥ 3 deep (`detections[].track.history[][]`) | the property the slice exists to protect |
| Arrays whose order is not their sorted order (`bbox`, `history`) | so a sort-based mutation is visible |
| An S3 URL containing `&` | exercises `JavaScriptEncoder.Default`; realistic |
| Non-ASCII (`Kühlmittel-Linie 3`) | exercises the `\uXXXX` path; realistic for a German fab |
| A 19-digit integer (`capturedAtNanos`) | exceeds `double` precision — the number-fidelity canary |
| Fixed-scale decimals (`0.1000`, `1.10`) | trailing-zero scale is preserved by `jsonb` and destroyed by a `double` round trip |

**Constraints:**

- **No exponent-notation numbers** (`1e5`, `1.7573e9`). `jsonb` rewrites them, which
  would add a second rule to §4's derivation and make the expectation
  hand-unreviewable. All numbers must be `jsonb` fixed points.
- No duplicate keys (`jsonb` drops them).
- Nesting well under 62 levels.

**Loading.** There is **no `.json` fixture anywhere under `tests/` today**, and no
test `.csproj` contains `CopyToOutputDirectory`, `EmbeddedResource`, `<Content>` or
`<None>`. Use the repo's established idiom instead: the `RepositoryRoot()` walk
(`tests/Architecture.Tests/IntegrationTestSelectionTests.cs:395-406`), which reads
from the source tree at runtime and **needs no csproj edit** — and therefore
touches no ADR-0109 contention file.

---

## 6. Locked choices

| Concern | Choice |
|---|---|
| Integration host | `AspireFixture` via `[Collection(AspireCollection.Name)]` — ADR-0103, Docker required |
| Category trait | **none** — a trait would exclude the test from CI (`ci.yml:179`) |
| Publish | `PlantFloor.PublishRawAsync`-style MQTTnet v3.1.1, QoS 1 (reuse the existing helper) |
| Read back | EF context, the same query and read converter as `EventRepository.GetByIdentifierAsync` |
| Assertions | Shouldly (ADR-0052) |
| Naming | sentence-style with underscores (ADR-0053) |
| Scope | **test-only.** No production file is modified. |

---

## 7. Out of scope — findings to file separately

1. **A latent redelivery wedge.** `MqttSubscriberHostedService.cs:226-233` catches
   only `ArgumentException` around `payload.Payload.GetRawText()`. A message with
   no `payload` property leaves `JsonElement` as `Undefined`, and `GetRawText()`
   throws `InvalidOperationException` — uncaught, so the delivery is never ACKed
   and never dead-lettered, and QoS 1 redelivers it forever. The block above it
   (`:220`) *does* catch that type. Found while tracing; **not fixed here** — a
   test issue that quietly becomes a bug fix is two issues (ADR-0144).
2. **`jsonb`'s consequences are recorded nowhere.** No ADR states that
   `events.payload` is `jsonb` and therefore does not preserve object key order,
   duplicate keys, or exponent notation. The choice is only visible in
   `EventConfiguration.cs:72`. See §9.
3. **Five near-verbatim copies of the MQTT publish helper** exist; `PlantFloor.cs:22-27`
   already records that they should collapse onto it.

---

## 8. Declarations (ADR-0144)

1. **Engineer:** `backend-engineer`. C#/xUnit, EF, Postgres, the Aspire fixture.
2. **New ADR?** **No.** This slice implements a decision rather than making one.
   Recommend a *separate, non-blocking* documentation issue for §7.2 — the `jsonb`
   trade-off deserves a written home, and this spec's §2.4 is currently the only
   place in the repo where it is measured.
3. **Colour: behaviour-preserving → characterisation, observed green.**
   No production code changes, so nothing can go red for the right reason; a
   compile error is not a red test (spec 061, `24e6fc4c`). The test's power is
   proven by the counterfactual in §9, which is **mandatory, not optional** —
   without it, an approval test generated from its own output is green by
   construction.

---

## 9. The counterfactual — written prediction, recorded before the run

**Mutation** (applied to production, observed, then reverted — never committed):
in `src/EventIngestion/Domain/Event/Payload.cs`, `From(JsonDocument)`, replace
`document.WriteTo(writer)` with a recursive walk that is faithful for every kind
except numbers, which are written as `writer.WriteNumberValue(element.GetDouble())`.

This is the "canonicaliser silently re-encodes the payload" family the audit
names. It is chosen over a key-reordering mutation because **key reordering would
be invisible** — `jsonb` re-sorts keys anyway — and a counterfactual that cannot
fail proves nothing.

**Prediction (measured on .NET 10.0.400 + PostgreSQL 17 before writing this):**

| Test | Prediction | Why |
|---|---|---|
| **New test, Half B (byte)** | **RED** | expected `"lensCorrection":1.10`, `"confidence":0.1000`, `"capturedAtNanos":1757318400123456789`; actual `1.1`, `0.1`, `1757318400123456800` — and ten further fixed-scale decimals with it |
| **New test, Half A (semantic)** | **RED**, on `capturedAtNanos` only | `1757318400123456789` ≠ `1757318400123456800` as `decimal`; `1.10` vs `1.1` and `0.1000` vs `0.1` compare numerically **equal** and pass — which is precisely why Half B exists |
| `EventIngestion.Domain.Tests/Event/PayloadTests.cs` (all 6) | **GREEN** | measured: `{"a":1}`, `{"cycleId":"abc"}`, `{"a":1,"b":[true,false]}` are byte-identical under the mutation |
| `EventIngestion.Domain.Tests/Event/EventTests.cs:24` | **GREEN** | payload `{"cycleId":"abc"}` — no numbers |
| `OutageRecoveryIntegrationTests`, `RestartLosesNothingIntegrationTests`, `MqttResubscribe…`, `PoisonDeliveryEscape…` | **GREEN** | assert row counts; payloads are `{"note":"…"}`; counts are unaffected regardless |
| `IngestThroughputMeasurementTests` | **GREEN** | `payload->>'sequence'` is a small integer, unchanged by a `double` round trip (and CI-excluded) |
| `EventIngestedDomainEventHandlerTests:46`, `DtoSmokeTests:28`, `FabEventIngestedV1Tests:36` | **GREEN** | payloads `{"cycleId":"abc"}` / `{}` |

> **Corrected during phase 6 (2026-09-08).** The Half B row above named
> `"iou":1.10` and `"threshold":0.1000`. **The shipped fixture contains
> neither** — it has `camera.lensCorrection: 1.10`, `thresholds.confidence:
> 0.1000` and `thresholds.iou: 0.4500`, and no `threshold` key at all. The
> fixture is right and the prediction was wrong: a 1.10 IoU is not plausible
> detector output, and §5 required realism.
>
> The mutation's reach was **re-derived, not re-run** — each of the fixture's
> **171 number literals** compared against its shortest round-trippable `double`
> rendering, which is what `WriteNumberValue(element.GetDouble())` emits.
> **13 differ:** the 19-digit `capturedAtNanos`, plus **12 decimals whose
> written scale a `double` drops** — `0.0`, `24.0`, `12.00`, `1.10`, `2.10`,
> `3.20`, `18.40`, `23.70`, `184.50`, `0.1000`, `0.4200`, `0.4500`. The Half B
> row names two of those twelve; the other ten redden with them.

**The claim under test:** the new test reddens and *nothing else does*. If any
other test also reddens, the mutation proves less than it appears to and a
different one must be chosen. Record the observed result against this table.

---

## 10. Success criteria

- **SC-001** The new test passes on unmodified `develop`, in the CI `integration`
  job, with no `Category` trait.
- **SC-002** Under the §9 mutation, the new test fails and the table in §9 is
  reproduced exactly; any deviation is reported, not adjusted away.
- **SC-003** The checked-in expected file differs from the fixture's canonical
  form by key-sorting alone, and a reviewer confirms this by inspection.
- **SC-004** No file under `src/` is modified.

## 11. Assumptions (unavoidable guesses, marked)

- **A1.** Npgsql renders a `jsonb` column read into a `string` as PostgreSQL's own
  `::text` output. Believed certain, and self-confirming: if it is not, the
  generated expectation simply differs and the derivation rule in §4 will not
  hold — which the reviewer would catch.
- **A2.** `jsonb`'s key-sort rule (length, then bytewise) is stable across the
  PostgreSQL versions this project runs. Documented and unchanged since 9.4.

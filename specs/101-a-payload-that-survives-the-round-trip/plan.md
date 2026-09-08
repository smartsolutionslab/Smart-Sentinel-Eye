# Plan 101 — A payload that survives the round trip

**Spec:** `spec.md` (this directory) · **Issues:** #587, #588
**Scope:** test-only. No `src/` file is created, modified, or deleted.

---

## 1. Bounded context and layers

**Context:** EventIngestion. **Layers touched: none.** This slice adds no domain
type, no use case, no adapter and no endpoint. It adds one integration test class
and two data files that *observe* the existing write→store→read path.

Nothing crosses a context boundary, so the NetArchTest boundary rules
(`tests/Architecture.Tests`) are unaffected. No `Shared.Contracts` message is
added or changed.

## 2. The path under observation

```
MQTT publish (test)
  └─ MqttSubscriberHostedService.OnMessageReceived            Infrastructure/Ingress
       ├─ JsonSerializer.Deserialize<MqttIngressPayload>       envelope only; payload stays a JsonElement
       └─ Payload.From(payload.Payload.GetRawText())           ← canonicalisation #1  (Payload.cs:24-38)
  └─ BoundedIngestChannel                                      Application/Ingress
  └─ PersistenceLoopHostedService  →  IngestEventBatchCommandHandler
  └─ Event.Ingest  →  IEventRepository.Add  →  SaveAsync
  └─ EF converter  payload => payload.Value  →  column "payload" jsonb   ← reshaping   (EventConfiguration.cs:71-75)
──────────────────────────────────────────────────────────────────────────────────────
  └─ EF converter  value => Payload.From(value)                ← canonicalisation #2
  └─ @event.Payload.Value                                      ← what the test asserts on
```

Note the normal MQTT path runs the **batch** handler
(`PersistenceLoopHostedService.StoreArrivalsAsync:176`), not
`IngestEventCommandHandler`; both converge on `IEventRepository.Add`.

## 3. What each hop does to the bytes — the design's foundation

| Hop | Preserves | Changes |
|---|---|---|
| `Payload.From` (write) | key order, duplicate keys, **number raw text**, array order | strips whitespace; re-escapes strings ASCII-only (`&`→`&`, non-ASCII→`\uXXXX`) |
| `jsonb` column | **array order and length**, number *value* incl. trailing-zero scale, nesting | **re-sorts object keys** (byte length, then bytewise); de-duplicates keys; rewrites exponent notation; unescapes; discards whitespace |
| `Payload.From` (read) | whatever `jsonb` handed back | re-minifies; re-escapes ASCII-only |

**Net, for a fixture constrained to `jsonb` fixed points (spec §5):**

> `@event.Payload.Value` == canonical(fixture) with every object's keys sorted by
> (UTF-8 byte length, then ordinal).

Verified against an independent oracle: a hand-written recursive key-sorter
applied to `canonical(fixture)` reproduced the measured read-back string exactly.
Whitespace, escaping, number text and array order all pass through untouched.
That single-rule derivation is what makes the checked-in expectation reviewable
rather than a blind paste.

## 4. Files

### New

| Path | Purpose |
|---|---|
| `tests/Integration.Tests/EventIngestion/Fixtures/inference-sample.json` | #587 — the ~4 KB invented inference payload |
| `tests/Integration.Tests/EventIngestion/Fixtures/inference-sample.expected.json` | the reviewed expected canonical form (Half B) |
| `tests/Integration.Tests/EventIngestion/InferencePayloadRoundTripIntegrationTests.cs` | #588 — the test |

**Naming deviation from #588, deliberate.** The issue names the class
`MqttIngressTests`. That name comes from spec 006's plan, which also specified
`tests/EventIngestion.Integration.Tests/` (a project that has **never existed** —
not in the working tree, the solution, or the index) and Testcontainers
(superseded by ADR-0103). `MqttIngressTests` would be a new grab-bag class in a
directory whose twenty-one existing classes are each named for the behaviour they
assert. Use `InferencePayloadRoundTripIntegrationTests`, matching the neighbours.

### Modified

**None.** In particular:

- `SmartSentinelEye.Integration.Tests.csproj` is **not** edited. It is
  `Microsoft.NET.Sdk` with default `Compile` globs, so a new `.cs` file is picked
  up automatically; the fixtures are read via the `RepositoryRoot()` walk rather
  than copied to the output directory, so no `<None>` item is needed.
- `.github/workflows/ci.yml` is **not** edited. A class carrying
  `[Collection(AspireCollection.Name)]` and no `Category` trait is already inside
  the `integration` job's filter.
- `Directory.Packages.props` is **not** edited. Shouldly, xUnit,
  `Microsoft.EntityFrameworkCore` and `Npgsql.EntityFrameworkCore.PostgreSQL` are
  already referenced; `System.Text.Json` is in-box.

## 5. ADR-0109 contention check

The contention list (`docs/adr/0109-parallel-agent-worktree-workflow.md:32-42`) is
`src/Shared.Kernel/*`, `src/Shared.Contracts/*`, `src/AppHost/AppHost.cs`,
`apps/shared/*`, `e2e/support/*` and any shared spec, `.github/workflows/ci.yml`,
`Directory.Packages.props`, `global.json`.

**None is touched.** `tests/Integration.Tests/*.csproj` is not on the list and is
not edited anyway. The slice is a clean `[P]` candidate against any other work
that does not also add files under `tests/Integration.Tests/EventIngestion/`.

## 6. Gates the new files must satisfy

| Gate | Requirement |
|---|---|
| `IntegrationTestSelectionTests.Every_integration_test_class_declares_where_it_runs` (`:84-98`) | the class **must** carry `[Collection(AspireCollection.Name)]` or one of exactly four `Category` values. Carry the collection; carry **no** `Category`. |
| Same test, `:44-45` | it pins `ci.yml:72` and `ci.yml:179` **by line number**. Inserting lines into `ci.yml` above 179 would break it — another reason not to touch that file. |
| ADR-0084 | ≤ 300 LOC/file, ≤ 30 LOC/method, ≤ 4 params, complexity ≤ 10, depth ≤ 3. The recursive comparator is the risk: keep it a small mutually-recursive pair, not one nested switch. |
| ADR-0053 | sentence-style test names with underscores. |
| Constitution §II | not engaged — no domain model is added. |

## 7. Test structure

```
InferencePayloadRoundTripIntegrationTests            [Collection("Aspire")]  no Category trait
├─ Inference_payload_with_nested_arrays_survives_the_round_trip   [Fact]
│    arrange  fixture + expected loaded via RepositoryRoot()
│             fresh Guid v7 identifier; kind = "Inference" + short suffix
│    act      publish envelope to fab/munich/inference/cam-0447, QoS 1
│             poll events for the identifier to a deadline
│             read the row through the EF context
│    assert   Half A — AssertSemanticallyEqual(fixtureRoot, storedRoot)
│             Half B — stored.Value.ShouldBe(expectedText)
└─ private static AssertSemanticallyEqual(JsonElement expected, JsonElement actual, string path)
     arrays   length equal, then positional recursion
     objects  key SET equal (ordinal), then recursion per key
     strings  ordinal equality of decoded values
     numbers  decimal.Parse(...GetRawText()) equality
     path     threaded through so a failure names detections[0].track.history[1][0]
```

**Half A compares numbers as `decimal`, not as raw text — on purpose.** Raw-text
comparison would collapse Half A into Half B and lose its reason to exist:
Half A's job is "the meaning survived, and the failure message says where",
Half B's is "the encoding did not drift". `decimal` is exact for both the 19-digit
`capturedAtNanos` and the fixed-scale fractions.

### Reuse before invention

- `Fixtures/PlantFloor.cs:49` `PublishRawAsync(string payload)` already does the
  Keycloak token mint, MQTTnet v3.1.1 connect and QoS 1 publish. Its own comment
  (`:22-27`) records that five near-verbatim copies exist and should collapse onto
  it. **Use it.** If its hard-coded topic (`fab/munich/plc/station-4`) does not fit,
  prefer a minimal parameter over a sixth copy — but note the ACL allows
  `fab/munich/inference/#`, so `inference` is available.
- `AspireFixture.Db.cs:25-41` `CreateEventIngestionDbContextAsync()` for the read.
- `RestartLosesNothingIntegrationTests.cs:184-195` for the poll-to-deadline shape.
- `IntegrationTestSelectionTests.cs:395-406` `RepositoryRoot()` for fixture loading.

## 8. Messaging

No domain event and no integration event is added. `EventIngestedDomainEvent` →
`FabEventIngestedV1` continues to fire as it does today; this slice does not
assert on it. (Whether the *integration event's* payload survives is a separate,
arguably worthwhile question — deliberately out of scope here, and noted so it is
not mistaken for something this slice covered.)

## 9. Risks

| Risk | Mitigation |
|---|---|
| The expected file is generated from the run and pasted green | The §9 counterfactual is a **mandatory task**, not a nicety; and the single-rule derivation (§3) makes the file reviewable by eye. |
| A fixture number in exponent form makes the derivation two-rule and the file unreviewable | Spec §5 forbids exponent literals. Check the fixture before generating the expectation. |
| Publishing outside the broker ACL reads as a lost event | Publish to `munich`; `acl.txt` grants `fab/munich/inference/#`. |
| Flaky wait: the batch loop has not drained when the read happens | Poll to a deadline with a clear failure message, as the neighbours do. |
| A future PostgreSQL changes the key-sort rule | Accepted (spec §11 A2). The failure is loud and names the file. |

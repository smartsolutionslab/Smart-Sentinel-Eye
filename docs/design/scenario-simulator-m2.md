# Scenario Simulator — M2 design (turnkey auto-demo + correlated billet narrative)

**Phase:** 1 (Specify) — design only. No code, no `plan.md`/`tasks.md`, no issues.
**Status:** DRAFT — awaiting product-owner review at the Phase-1 gate.
**Owner:** architect.
**Elaborates:** ADR-0111 (Scenario Simulator) §"M2 — Sensor / events (designed, not coded)".
**References:** ADR-0095 (Mosquitto MQTT), ADR-0100 (mosquitto go-auth JWT), ADR-0099 (AEL),
ADR-0073/0040 (versioned integration events), ADR-0088 (per-module Wolverine queue isolation),
ADR-0050 (`[LoggerMessage]`), ADR-0105 (`Ensure.That`), ADR-0048 (NRT-off / `Option<T>`),
ADR-0112 (multi-tile layouts — **resolves O1**; the rolling-mill wall is its named downstream consumer),
ADR-0028 (GitFlow → `develop`), ADR-0087 (rebase-only), ADR-0109 (parallel worktree agents),
constitution §IV (latency budget), §III (bounded-context isolation).

> **2026-06-04 refresh.** ADR-0112 (multi-tile layouts) merged to `develop` — spec 010 shipped the
> N-tile Layout aggregate, the management-web wall designer, and the kiosk grid renderer with
> per-tile overlay highlight. This **resolves the old blocking open question O1**: a "wall" is now
> **one multi-tile Layout**, not N single-tile layouts. M2 is revised to seed **one 2×2 rolling-mill
> wall** (four tiles = the four stations) instead of four single-cell layouts. The billet timeline
> then lights the four tiles **in sequence on one screen** — the headline demo, now achievable inside
> M2 with zero LayoutComposition change. Edits are focused: §2 (multi-tile fact), §4 Phase D (one
> wall), §6 (concrete set + the now-obsolete "multi-tile limitation"), §12 (slice notes), §14 A1,
> §15 O1. Everything else (the 3 seed clients, the billet engine + 5 behaviour strategies, the MQTT
> publisher, the realm-scope expansion, the MQTT ACL) carries over and was re-verified against
> current code.

> **Artifact-placement note.** M1 shipped under ADR-0111 + GitHub issues, *not* a `specs/NNN`
> folder — `specs/001–009` are the nine **product** bounded contexts, and a dev-only simulator
> must not consume `specs/010`. M2 is an *implementation elaboration* of an already-accepted ADR
> decision (ADR-0111 explicitly scoped M2), so re-opening the ADR is the wrong altitude. This
> focused design doc is the resumption point; tasks become GitHub issues at the Phase-3 gate.
> (Recommendation discussion: see "Why a design doc, not an ADR amendment" below.)

---

## 1. Goal and "done"

**Goal.** With zero manual setup, `aspire run` (dev, gated
`isRunMode && !isE2ETests && isScenarioSimulatorEnabled`) makes the
rolling-mill scenario light up end to end: a simulated **billet** travels the line and, as it
reaches each station, that station's **tile on the single seeded 2×2 rolling-mill wall** visibly
highlights on the kiosk (overlay-keyed highlight on one screen). The run loops continuously.

**"Done" (verifiable success criterion).** After a clean `aspire run` against an empty stack:

1. The EventIngestion event list (and audit log) shows a steady, **correlated** stream of
   `plc`/`inference` events keyed by station device, ordered as the billet advances.
2. On the kiosk rendering the **single seeded 2×2 rolling-mill wall**, the **Station-4 tile's
   overlay highlights** when the billet enters roughing, then the **Station-7 tile**, then the
   **cooling-bed tile**, then the **coiler tile** — in narrative order on one screen, repeating each
   loop. (Four distinct overlays, one per tile, so each highlight lands on exactly its tile per
   ADR-0112's highlight-all-matching semantic.)
3. Re-running the simulator (worker restart) produces **no duplicate** cameras / overlays / rules /
   layouts (idempotent seeding).
4. The simulator remains **invisible to CI/E2E/prod** — nothing added here runs under `E2ETests`,
   under the end-to-end job's `ScenarioSimulator=false` (#2013), or in published Helm output.

If any of (1)–(4) fails, M2 is not done.

---

## 2. Pipeline, as verified (ground truth)

The design is built on the actual code paths, not assumptions:

| Stage | Contract / entry point | Verified shape |
|---|---|---|
| MQTT ingress | `MqttSubscriberHostedService` (EventIngestion) subscribes `fab/+/+/+` QoS 1 | topic `fab/{fabId}/{source}/{deviceId}`; body `MqttIngressPayload { eventId:Guid, kind:string, occurredAt:DateTimeOffset, payload:JsonElement }`; payload ≤ 64 KB; `eventId` = idempotency; bad shape/JSON/size → dead-letter |
| MQTT auth | `mosquitto.conf` + `jwt_auth.go` (ADR-0100) | password = Keycloak RS256 JWT; plugin requires **`aud` contains `smart-sentinel-eye-api`** (spec 090), **`azp == MQTT username`** and `iss` ends in `/realms/smart-sentinel-eye`. **ACL (`acl.txt`) still applies per username** |
| Persist + fan-out | EventIngestion → `FabEventIngestedV1` (`Shared.Contracts/EventIngestion`) | primitives only; `Source ∈ plc\|inference\|manual\|webhook` |
| Rule eval | `FabEventIngestedV1Handler` → `RuleEvaluator` (Automation) | candidates keyed by **`(source, kind)`**; AEL predicate over `$.source`,`$.kind`,`$.device`,`$.payload.*`; effects → `OverlayHighlightRequestedV1(OverlayIdentifier, DurationMs)` or `SystemVariableValueRequestedV1` |
| Highlight delivery | `OverlayHighlightRequestedV1Handler` (LayoutComposition) | SignalR `OverlayHighlightChanged` on `/hubs/layouts`; kiosk applies `ssE-overlay-highlight` CSS for `DurationMs` to **every tile whose overlay matches** (ADR-0112 §5 highlight-all-matching) |
| Overlay binding | highlight targets an **`OverlayIdentifier`**, never a camera | camera↔overlay link is the **layout tile** (now one tile of an N-tile grid, ADR-0112) |

**Critical structural facts that shape the design:**

- **AEL** (`AelLexer`) supports string literals (`'…'`/`"…"`), `== != < <= > >=`, `+ - * / %`,
  `&& ||`. So predicates like `$.payload.temperatureC >= 1100` and
  `$.device == 'station-4-roughing' && $.kind == 'temperature'` are expressible today. No AEL
  change is required.
- **A Layout now binds 1..N tiles in a grid** (ADR-0112 / spec 010, merged). `CreateLayoutRequest
  { Name, Grid { Rows, Cols }, Tiles: [ { CameraIdentifier, OverlayIdentifier?, Row, Col } ] }`
  (verified: `src/LayoutComposition/Api/Requests/CreateLayoutRequest.cs`). The grid cap is
  `MaxTiles = 4` / `2×2` (`GridDimensions` constant) — **exactly the four rolling-mill stations**.
  A single highlight lights **every tile bound to that overlay** (highlight-all-matching). This
  **resolves the former blocking constraint**: M2 seeds **one 2×2 wall** and the four tiles light in
  sequence on one screen. See §6; O1 is **resolved**.
- **Camera IDs are minted dynamically** and arrive at the worker as `CameraRegisteredV1.Camera`
  (Guid). The worker already learns every camera's Guid in `CameraRegisteredSimHandler`. That is
  the natural correlation seam for layout binding — no extra "list cameras" round-trip needed.
- Overlay create returns `OverlayIdentifier` (Guid) in the `201` body at revision 1; publish is a
  second call (`POST /overlays/{id}/revisions/1/publish`). **Layout** is the same draft→publish
  shape: `POST /layouts` returns the `LayoutIdentifier` (Guid) in the `201` body, then `POST
  /layouts/{id}/revisions/1/publish` (verified `LayoutEndpoints.cs`). The layout body embeds **both
  the camera ID and the overlay ID per tile**, so the wall is created only after all four cameras'
  IDs and all four overlays' IDs are known (§4 Phase D).
- Rule create (`POST /rules`) takes `{ Name, TriggerSource, TriggerKind, Predicate, ActionType,
  OverlayIdentifier?, DurationMs? }`; publish is `POST /rules/{name}/publish`. A
  `HighlightOverlay` rule **embeds the OverlayIdentifier** → overlays must be seeded *before* rules.

---

## 3. Where seeding lives, and why

**Decision: all seeding stays in the `src/ScenarioSimulator` worker**, over the same HTTP APIs M1
already uses, with the same `scenario-simulator` client_credentials token. Rationale:

- M1 already proved the pattern (catalog seeding via `CameraCatalogClient` + `KeycloakTokenProvider`).
- ADR-0111 explicitly rejected splitting the scenario across multiple homes ("a standalone worker
  owns the whole scenario end to end").
- It keeps cross-context seeding **out of every product context** (no context gains demo code), and
  keeps the dev-only gate in one place (AppHost already gates the worker).

The worker grows three thin HTTP clients mirroring `CameraCatalogClient`: `OverlayDesignerClient`,
`AutomationRulesClient`, `LayoutCompositionClient`. Each is idempotent (treat name-conflict `409`
as "already seeded", like M1). All authenticate with the existing token provider — but that token
now needs **more scopes** (§7).

---

## 4. The deterministic seeding sequence + ID-correlation strategy

This is the central hard problem: cameras, overlays, rules, and layouts are minted in different
contexts at different times, and IDs must be threaded across them. The ordering below is forced by
the data dependencies (rules embed overlay IDs; layouts embed camera **and** overlay IDs; camera
IDs only exist after registration).

**Phase A — Overlays first (no external dependency).**
For each asset, `POST /overlays` → capture `OverlayIdentifier` → `POST
/overlays/{id}/revisions/1/publish`. Hold an in-memory map `assetKey → OverlayIdentifier`.
Overlays have no dependency on cameras or rules, so they go first and their IDs become the
correlation key everything else references. *Idempotency:* overlay name = stable per asset
(`"rolling-mill/station-4-roughing"`); on `409` (name exists) the worker **reads it back**
(`GET /overlays?name=…` / list-and-filter) to recover the existing `OverlayIdentifier` rather than
minting a new one. (See O3 — read-back path must be confirmed against the query endpoint shape.)

**Phase B — Rules next (depend on overlay IDs + the asset's device/kind).**
For each asset's narrative trigger, `POST /rules` with `TriggerSource`, `TriggerKind`, an AEL
`Predicate` over `$.device`/`$.payload.*`, `ActionType = "HighlightOverlay"`, and the
`OverlayIdentifier` captured in Phase A → `POST /rules/{name}/publish`. Rule name = stable per
asset (`"rolling-mill-station-4-highlight"`) for idempotency.

**Phase C — Cameras (M1, unchanged) → camera IDs arrive asynchronously.**
The existing `ScenarioSeeder` registers cameras; `CameraRegisteredV1` flows back over RabbitMQ and
is handled by `CameraRegisteredSimHandler`. M2 **extends that handler** (or a sibling subscriber) to
record `assetKey → CameraIdentifier` (derived from the camera path, which already maps 1:1 to the
asset — `RtspPath.TryExtract` already yields the path = asset key). This is the only context that
learns camera IDs, so **wall seeding is triggered from here**, but now only once **all four** assets
have both their overlay and camera IDs (the single-wall join, §4 Phase D) — not once per asset.

**Phase D — ONE multi-tile wall last (depends on ALL four cameras' IDs + all four overlays' IDs).**
This is the O1-resolved change. Instead of four single-cell layouts, the worker seeds **one 2×2
rolling-mill wall** once, when **every** asset has both its `OverlayIdentifier` (Phase A) and its
`CameraIdentifier` (Phase C). One `POST /layouts` with the multi-tile body, then one publish:

```
POST /layouts
{
  "name": "rolling-mill-wall",
  "grid": { "rows": 2, "cols": 2 },
  "tiles": [
    { "cameraIdentifier": <S4 cameraId>, "overlayIdentifier": <S4 overlayId>, "row": 0, "col": 0 },
    { "cameraIdentifier": <S7 cameraId>, "overlayIdentifier": <S7 overlayId>, "row": 0, "col": 1 },
    { "cameraIdentifier": <CB cameraId>, "overlayIdentifier": <CB overlayId>, "row": 1, "col": 0 },
    { "cameraIdentifier": <CO cameraId>, "overlayIdentifier": <CO overlayId>, "row": 1, "col": 1 }
  ]
}
→ POST /layouts/{id}/revisions/1/publish
```

Tile→station mapping (fixed, from the scenario `Assets` order / per-asset `Tile { Row, Col }`):
`station-4-roughing → (0,0)`, `station-7-finishing → (0,1)`, `cooling-bed → (1,0)`,
`coiler → (1,1)`. Each tile binds **that station's camera + that station's distinct overlay**, so a
single `HighlightOverlay` lights exactly one tile (four distinct overlays, no accidental
multi-tile match). Layout name = stable `"rolling-mill-wall"` for idempotency (one wall per
scenario). The grid is exactly `2×2 = MaxTiles`, the ADR-0112 cap — no headroom needed.

**Correlation strategy, summarised.** The **asset `key`** is still the join key for overlays, rules,
cameras, and the per-station tile coordinate. The worker keeps a small in-process correlation table
keyed by `assetKey`, populated as each ID becomes known:

```
assetKey → { OverlayIdentifier?, CameraIdentifier?, RuleName, DeviceId, TileRow, TileCol }
```

Plus one scenario-level slot for the **single** wall: `LayoutName = "rolling-mill-wall"`,
`LayoutCreated: bool`. Overlay and rule IDs are captured synchronously during seeding; each camera
ID is captured asynchronously on its `CameraRegisteredV1`. The wall is created when **all four
assets** have both IDs — a join over the whole table, not per-asset. Ordering is **enforced by data
availability, not by sleeps**: every `CameraRegisteredV1` arrival re-evaluates "are all four
asset rows now complete?"; the first time the answer is yes, the wall is POSTed once and
`LayoutCreated` flips. (`TileRow`/`TileCol` come from the scenario `Tile` block per asset — §10.)

**Why ordering is safe across restarts.** Overlays/rules are seeded on startup (synchronous,
idempotent). Camera registration replays `CameraRegisteredV1` (Wolverine redelivery is already
idempotent in M1). The wall-create join is guarded by **two** idempotency checks: the in-process
`LayoutCreated` flag, and — authoritative across a cold restart — a read-back
(`GET /layouts?state=...` filtered by `name == "rolling-mill-wall"`). A redelivered
`CameraRegisteredV1` after a restart that completes the table finds the wall already published and
skips the create. Because all four camera IDs must be present, the join naturally waits for the
slowest camera registration before firing — no four-way race, one POST.

---

## 5. The billet timeline engine

A new hosted service in the worker (`BilletTimelineHostedService`) drives the narrative. It is the
M2 occupant of the "EXTENSION POINT" comment in `ScenarioSeeder`, but lives in its own file
(timeline ≠ seeding; different lifecycles).

**Model (config-bound where sane, code where JSON would be absurd):**

- **Run = one billet traversal.** A run visits stations in scenario order, dwelling at each for a
  configured `DwellMs`, then advancing. After the last station, wait `LoopGapMs` and start the next
  run. Loops forever.
- **Per-station emission.** While the billet dwells at a station, the engine emits MQTT events for
  that asset's sensors at a configured `TickMs` cadence. Each sensor's **`Behaviour`** (already in
  the scenario JSON: `ramp` / `burst` / `steady` / `decay` / `step`) selects a small **value
  generator**:
  - `ramp` — linear rise from `Min` to `Max` across the dwell (Station-4 temperature).
  - `burst` — baseline with short spikes to `Peak` (Station-4 rolling-force).
  - `steady` — `Mean` ± `Jitter` (Station-7 strip-speed).
  - `decay` — exponential fall from `Start` toward `Floor` (cooling-bed temperature).
  - `step` — flat `Before`, single jump to `After` at a configured fraction of the dwell
    (coiler coil-weight).
- **The generators are code** (a `SensorBehaviour` strategy per behaviour name — 5 tiny pure
  functions). **Their parameters are config** — the scenario JSON's `SensorDefinition` gains
  optional numeric fields (`Min/Max/Peak/Mean/Jitter/Start/Floor/Before/After/StepAtFraction`).
  Encoding a temperature curve as raw JSON points would be absurd; a named behaviour + a handful of
  numbers is the right line.
- **Topic + payload mapping.** For each emitted sample:
  - `fabId` = `munich` (matches existing seeds + realm group). *(See O2 — confirm fab id.)*
  - `source` = `plc` for physical-process sensors (temperature, rolling-force, strip-speed,
    coil-weight); `inference` reserved for vision-derived kinds (none in rolling-mill v1, but the
    mapping is per-sensor so a future `inference` sensor just sets `Source` in JSON).
  - `deviceId` = a **stable id derived from the asset key**, equal to `asset.Camera.Path`
    (e.g. `station-4-roughing`). One device per asset; reused across that asset's sensors. This is
    what the seeded rule's `$.device == '…'` predicate matches.
  - `kind` = the sensor's `Kind` (`temperature`, `rolling-force`, …) — matches the rule's
    `TriggerKind`.
  - `payload` = `{ "value": <number>, "unit": "<unit>", "station": "<assetKey>" }`. The rule
    predicate reads `$.payload.value`.
  - `eventId` = a fresh Guid v7 per sample (idempotency; redeliveries dedup in EventIngestion).
  - `occurredAt` = `TimeProvider.GetUtcNow()` at emission.

**MQTT client.** The worker adds an `MqttPublisher` built on MQTTnet (the same library EventIngestion
uses), connecting with username `scenario-simulator` and password = the **Keycloak JWT** from the
existing `KeycloakTokenProvider` (the go-auth plugin validates `azp == username`). The publisher
must refresh the password when the token rotates — MQTTnet reconnect uses fresh credentials via a
connection factory that pulls the current token. *(See O4 — MQTT credential rotation detail.)*

**Why this drives the narrative.** The billet engine sequences which station emits *when*; each
station's `kind`+`device`+`payload.value` matches exactly one seeded rule; that rule highlights
exactly that station's overlay; the seeded layout binds that overlay to that station's camera tile.
Correlation is end to end via the asset key, exactly as ADR-0111 promised.

---

## 6. What the seeded rules / overlays / layout *are* (concrete)

**Four overlays + four rules + ONE 2×2 wall** (O1-resolved). Per asset, one overlay (distinct, so a
highlight lands on exactly its tile) and one rule; the four assets' tiles compose a single wall:

| Asset key | Tile (row,col) | Overlay (label text) | Rule trigger `(source,kind)` | Rule predicate (AEL) | Highlight |
|---|---|---|---|---|---|
| `station-4-roughing` | (0,0) | "ROUGHING — HOT BILLET" | `(plc, temperature)` | `$.device == 'station-4-roughing' && $.payload.value >= 1100` | overlay S4, 4000 ms |
| `station-7-finishing` | (0,1) | "FINISHING — STRIP" | `(plc, strip-speed)` | `$.device == 'station-7-finishing' && $.payload.value >= 8` | overlay S7, 4000 ms |
| `cooling-bed` | (1,0) | "COOLING BED" | `(plc, temperature)` | `$.device == 'cooling-bed' && $.payload.value <= 700` | overlay CB, 4000 ms |
| `coiler` | (1,1) | "COILER — COIL READY" | `(plc, coil-weight)` | `$.device == 'coiler' && $.payload.value >= 20` | overlay CO, 4000 ms |

The four tiles are **one published layout** `"rolling-mill-wall"` on a `2×2` grid (= the ADR-0112
`MaxTiles = 4` cap, exactly). Each tile binds that station's camera + that station's **distinct**
overlay, so each `HighlightOverlay` lights exactly one tile (no accidental highlight-all-matching —
that semantic only fires if an overlay is reused across tiles, which this seed deliberately avoids).
The billet lights S4 → S7 → CB → CO **in sequence on one screen**.

(Station-4 also has a `rolling-force` `burst` sensor; in v1 it emits events for *visible event-list
realism* but drives no separate highlight rule — keeping one highlight per station. A second
"force spike" rule is a trivial later addition. See O5.)

Overlay `Label` geometry (normalized 0..1) is parameterised in the scenario JSON per asset so the
highlight box lands sensibly **within each tile** (e.g. centred banner). Durations (4000 ms)
comfortably exceed the dwell so the highlight is clearly visible.

**Multi-tile story is now native (no LayoutComposition change).** The former blocking O1 — "is a
wall one layout or N layouts?" — is **resolved by ADR-0112**: a wall is one multi-tile Layout. M2
seeds exactly one and needs **no** change in LayoutComposition, the kiosk grid renderer, or the
overlay-keyed highlight path (all shipped by spec 010). The only M2 work is the seeding sequence
(§4 Phase D) emitting the multi-tile body once.

---

## 7. Identity / realm scope expansion

The `scenario-simulator` Keycloak client currently has `defaultClientScopes` =
`[basic, profile, email, roles, sse.cameras.write]`. M2 seeding calls overlays, rules, and layouts,
all of which `RequireAuthorization` on their respective write scopes. The realm seed
(`src/AppHost/Realms/smart-sentinel-eye-realm.json`) must grant the client:

- `sse.overlays.write`
- `sse.rules.write`
- `sse.layouts.write`

(All three scopes already exist in `Scope.All` and the realm's scope catalogue — this is purely
adding them to the `scenario-simulator` client's default scopes.) No new scopes are invented.

**MQTT ACL.** `src/AppHost/mosquitto/acl.txt` currently has no entry for `scenario-simulator`. The
JWT plugin authenticates the CONNECT, but Mosquitto's ACL still authorises each PUBLISH per
username. The simulator publishes to **many** device topics under one username, so it needs a
pattern/wildcard write grant:

```
user scenario-simulator
topic write fab/munich/plc/#
topic write fab/munich/inference/#
```

*(See O6 — confirm the go-auth plugin defers ACL to `acl_file`, i.e. JWT clients are still
ACL-checked. If the plugin grants topic access itself, this file edit may be unnecessary or
shaped differently.)*

---

## 8. Latency-budget impact (constitution §IV)

M2 exercises the **Event → overlay state ≤ 200 ms** leg (RabbitMQ + projection: EventIngestion →
`FabEventIngestedV1` → Automation → `OverlayHighlightRequestedV1` → LayoutComposition SignalR). It
**adds no new code on that leg** — it only *feeds* it with realistic traffic. The simulator's own
MQTT publish cadence is upstream of "event arrival" and is not part of the 800 ms budget (the budget
starts at event arrival). M2 is therefore **budget-neutral**; it is, in fact, the first realistic
**load generator** for measuring that leg. No latency regression risk; a latency *measurement*
opportunity.

---

## 9. Verification (what proves M2 live; what is automatable vs dev-only-manual)

**Dev-only-manual (the demo itself).** `aspire run`; open the kiosk picker, tap the single seeded
**2×2 rolling-mill wall**; watch its four tiles highlight S4→S7→CB→CO each loop on one screen.
Cross-check the EventIngestion event list / audit log for the correlated event stream. This is the
canonical "is the demo alive" check and is **inherently dev-only** (the whole simulator is gated off
under E2E/CI).

**Automatable (without violating the gate).** Unit-level, in CI, with **no live stack**:

- Behaviour generators (`ramp/burst/steady/decay/step`) are pure functions → unit tests assert curve
  shape (monotonic ramp, single step, bounded jitter, decaying decay) with a fake `TimeProvider`.
- The seeding-sequence orchestration (overlay→rule→**single-wall** join on the correlation table) is
  testable with fakes for the four HTTP clients and four fed `CameraRegisteredV1` → assert
  idempotency (second run issues no creates), ordering (**no wall create before ALL four** assets'
  camera+overlay IDs are known), and the **exactly-one-POST** property (the wall is created once even
  though four camera events arrive), plus the correct tile→(row,col) mapping in the POST body.
- Topic/payload mapping → unit test that an asset+sensor sample produces the exact
  `fab/munich/plc/station-4-roughing` topic and `{value,unit,station}` payload the seeded rule
  predicate matches.

**Explicitly NOT in CI.** No end-to-end "billet lights the kiosk" test runs in CI/E2E — that would
require booting the simulator, which is gated off there by design. The end-to-end proof is the
dev-run demo. (This respects the M2 constraint: zero CI/E2E/prod impact.)

---

## 10. Scenario JSON extension (the contention file)

`src/ScenarioSimulator/Scenarios/rolling-mill.json` and the `SensorDefinition` /`AssetDefinition`
options classes gain (all optional, additive — M1 binding still works):

- `SensorDefinition`: optional `Source` (default `plc`), numeric behaviour params
  (`Min/Max/Peak/Mean/Jitter/Start/Floor/Before/After/StepAtFraction`).
- `AssetDefinition`: optional `Overlay` block (label text + normalized geometry + font size),
  optional `Highlight` block (trigger kind, comparison, threshold, duration) so the rule/overlay
  per asset is **data-driven** rather than hard-coded, and an optional `Tile { Row, Col }` block —
  the asset's coordinate on the single 2×2 wall (S4→(0,0), S7→(0,1), CB→(1,0), CO→(1,1)). Where a
  curve or rule would be absurd as JSON, it stays code (the 5 behaviour strategies; the
  comparison-operator switch).
- A new top-level `Timeline` block on the scenario (`DwellMs`, `TickMs`, `LoopGapMs`) and a new
  top-level `Wall` block (`Name` = `"rolling-mill-wall"`, `Rows`, `Cols`) for the single seeded
  wall. The billet visits stations in `Assets` order, which is also the narrative highlight order.

This file plus `AppHost.cs`, the realm JSON, `acl.txt`, and `Shared.Contracts` are the
**single-owner contention files** for the parallel slices (§12).

---

## 11. House-rule conformance checklist (for Phase 2/4)

- Value objects + `Ensure.That(...)` guards (ADR-0105); no `ArgumentNullException.ThrowIfNull`.
- NRT-off + `Option<T>` (ADR-0048) — but note the worker is dev-only and already follows the repo
  style; mirror existing `ScenarioSimulator` files.
- `Result<T, Error>` only where the worker surfaces typed failures; HTTP seed clients mirror M1's
  `bool`/throw-on-unexpected style for simplicity (dev-only).
- No cross-context project refs — the worker references **`Shared.Contracts`** only (for
  `CameraRegisteredV1`), and talks to every context over **HTTP/MQTT**, never project refs.
- Wolverine per-module queue isolation (ADR-0088) — the camera-registered subscriber keeps its
  `scenario-simulator.<EventType>` queue namespacing.
- `[LoggerMessage]` source-gen (ADR-0050) — extend `Log.cs` with billet/seed events.
- One type per file; ≤ 300 LOC/file; ≤ 30 LOC/method.
- GitFlow base `develop` (ADR-0028); rebase-only (ADR-0087).

---

## 12. Proposed slice decomposition for parallel worktree agents (ADR-0109)

Slices are cut on **disjoint files**. Contention files are single-owner and must be merged first.

**Foundational (single-owner; blocks the rest — do first, one agent):**

- **S0 — Contracts/config + realm + ACL.** Owns the contention files:
  `Scenarios/rolling-mill.json` extension (sensor params + per-asset `Overlay`/`Highlight`/`Tile`
  blocks + top-level `Timeline` + `Wall` blocks), `ScenarioOptions.cs`/`SensorDefinition`/
  `AssetDefinition` new fields (`TileDefinition`, `WallDefinition`, `TimelineDefinition`),
  `Realms/smart-sentinel-eye-realm.json` (add the 3 write scopes to the `scenario-simulator`
  client's `defaultClientScopes` — currently only `sse.cameras.write`), `mosquitto/acl.txt`
  (add `scenario-simulator` wildcard topic grants), and any `AppHost.cs` wiring (e.g. MQTT host env
  on the worker, `WaitFor` mosquitto). Everything else depends on these shapes. **No
  `Shared.Contracts` change is expected** (we reuse existing events) — if one is needed it lives
  here too.

**Parallel after S0 (disjoint file sets):**

> **Superseded by the Phase-2 plan's P6–P7** (below) after the O1 refresh — P7 is the authoritative,
> dispatch-ready cut. The §12 sketch is retained for narrative continuity; where it differs (notably
> `Log.cs`, `Program.cs`, `SimulatorOptions.cs` are **S0-owned**, not S1/S2), **P6/P7 win**.

- **S1 — Billet timeline engine + MQTT publisher.** `BilletTimelineHostedService.cs`, the 5
  `SensorBehaviour` strategy files, `MqttPublisher.cs` + its connection factory, topic/payload
  mapper. Calls S0-provided `Log.cs` methods (does not own `Log.cs`). Touches no seeding-client files.
- **S2 — Cross-context seeding (overlays + rules + ONE wall) + correlation table.**
  `OverlayDesignerClient.cs`, `AutomationRulesClient.cs`, `LayoutCompositionClient.cs` (POSTs the
  single multi-tile `{ grid, tiles }` body + publish; read-back-by-name for idempotency), the
  `AssetCorrelationTable.cs` (per-asset rows + the scenario-level `LayoutCreated` flag), and the
  extension of `ScenarioSeeder`/`CameraRegisteredSimHandler` to capture camera IDs and fire the
  **single** wall create once **all four** assets are complete. Touches no timeline files.
- **S3 — Automatable tests.** Behaviour-generator unit tests, mapper tests, seeding-orchestration
  idempotency/ordering tests with fakes. Owns only test files; can start against S1/S2 interfaces
  once their public shapes land.

**Contention files (single-owner, never split):** `AppHost.cs`,
`Realms/smart-sentinel-eye-realm.json`, `mosquitto/acl.txt`, `Scenarios/rolling-mill.json`,
`Scenario/ScenarioOptions.cs`, `Configuration/SimulatorOptions.cs`, `Log.cs`, `Program.cs`, and (if
touched) `Shared.Contracts`. All assigned to **S0** (see P6 for the full table). The orchestrator
commits/pushes/PRs (subagent push is sandbox-unreliable per ADR-0109); the CI e2e gate stays green
because the simulator is gated off there.

**Suggested order:** S0 → (S1 ∥ S2) → S3 → manual dev-run verification.

---

## 13. Why a design doc, not an ADR amendment

ADR-0111 already **decided** M2 (sensor/events on a timeline, correlated by asset key). M2 here is
the *how*, not a new architectural decision — it introduces no new context, no new locked tech, no
deviation from constitution. Per the constitution's governance rule, an ADR is for *decisions*; an
amendment would be ceremony over an already-accepted scope. A focused `docs/design/` doc that cites
ADR-0111 is the right altitude and is the Phase-1 resumption artifact. **If the gate review surfaces
a genuinely new decision** — most likely the multi-tile layout question (O1) — *that* warrants its
own ADR, and this doc flags it rather than silently inventing the architecture.

---

## 14. Assumptions (flagged for product-owner correction at the gate)

- **A1 (resolved).** **One 2×2 wall** with four tiles (one per station), four distinct overlays,
  four rules — highlighting one tile at a time in narrative order on one screen. Enabled by ADR-0112
  (multi-tile layouts, merged); no LayoutComposition change needed. (Supersedes the original
  four-single-cell-layouts assumption; O1 resolved.)
- **A2.** `fabId = munich` for all simulated devices (matches the existing `station-4`/`camera-12`
  seeds and the realm `/fabs/munich` group). (→ O2)
- **A3.** All rolling-mill sensors are `source = plc`; `inference` is wired in the mapping but unused
  in v1 (no vision-derived kinds in this scenario). (→ O5)
- **A4.** Thresholds in §6 (1100 °C, 8 m/s, ≤700 °C, ≥20 t) are plausible-but-invented demo numbers,
  chosen so each behaviour curve clearly crosses its trigger during the dwell. They are tuning knobs,
  not domain truth.
- **A5.** Highlight duration 4000 ms and the dwell/tick/loop-gap timings are demo-feel guesses; they
  do not affect correctness, only how the demo *looks*.
- **A6.** The worker authenticates to MQTT with its Keycloak JWT (username `scenario-simulator` so
  `azp == username`), reusing the existing token provider — no second credential. (→ O4, O6)
- **A7.** Overlay/rule/layout idempotency uses **stable names per asset**; on `409` the worker reads
  the existing artifact back to recover its ID. (→ O3)
- **A8.** No `Shared.Contracts` change is needed — the existing `CameraRegisteredV1`,
  `FabEventIngestedV1`, `OverlayHighlightRequestedV1` cover M2.

---

## 15. Open questions (need product-owner / cross-context confirmation before Phase 2)

- **O1 — RESOLVED (2026-06-04) by ADR-0112 / spec 010 (merged).** A "wall" is **one multi-tile
  Layout** (1..N tiles, `MaxTiles = 4` / 2×2). M2 seeds **one 2×2 rolling-mill wall**; the four
  tiles light in sequence on one screen. The headline demo is achievable **inside M2** with no
  LayoutComposition change. (Design revised throughout — see the 2026-06-04 refresh note.)
- **O2.** Confirm `fabId` (`munich`) and that no other fab id is expected for the demo.
- **O3 — partially verified, one residual.** Read-back by name is feasible: **overlays** expose
  `GET /overlays` → `{ Chains, Published }` (filter by name in-process); **layouts** expose
  `GET /layouts?state=...` → `{ Chains, Published }` (filter by `name == "rolling-mill-wall"`);
  **rules** publish by **name** (`POST /rules/{name}/publish`), so a rule never needs ID recovery
  for the highlight binding — only the *overlay* ID is embedded in the rule's create body, and that
  is captured in Phase A before the rule create, or recovered via the overlay list. **Residual:** the
  overlay/layout list responses must actually carry the `name` and the `OverlayIdentifier`/
  `LayoutIdentifier` on each chain entry (DTO shape) — confirm the `*Dto` fields at Phase 2 start so
  the read-back filter compiles. (Both list endpoints return chains keyed by identifier; the field
  presence is the only thing to verify.)
- **O4.** MQTT credential rotation: confirm MQTTnet managed-client reconnect can supply a freshly
  minted JWT on each (re)connect (token TTL vs. session lifetime). M1 never connected to MQTT, so
  this path is new.
- **O5.** Do you want Station-4's `rolling-force` `burst` to drive a **second** highlight/variable
  (force-spike), or only emit for event-list realism in v1?
- **O6.** Confirm the go-auth plugin defers authorization to Mosquitto's `acl_file` for JWT-authed
  clients (so the `scenario-simulator` ACL grant in §7 is both necessary and sufficient). If the
  plugin authorises topics itself, the ACL edit changes shape.
- **O7.** Should the seeded demo overlays/rules/layouts be **visibly tagged** (e.g. name prefix
  `rolling-mill/…`) so an operator can tell demo artifacts from real ones and bulk-archive them? (My
  default: yes, stable `rolling-mill-*` names — which doubles as the idempotency key.)

---

# Implementation Plan (Phase 2) — one-wall M2

**Phase:** 2 (Plan). Produced after the §-1–15 design refresh resolved O1. No code, no issues —
stops at the Phase-2 gate. Scope is the **dev-only** `src/ScenarioSimulator` worker; **zero**
CI/E2E/prod surface (gated `isRunMode && !isE2ETests && isScenarioSimulatorEnabled` in
`AppHost.cs`).

## P1. Bounded-context posture (no new context; HTTP/MQTT only)

The simulator is **not** a bounded context — it is a dev harness that *drives* the nine product
contexts from outside, over their public HTTP APIs and the MQTT ingress. It keeps the one allowed
project reference it already has: **`Shared.Contracts`** (for `CameraRegisteredV1`). It introduces
**no** new `Shared.Contracts` type (A8 holds: `CameraRegisteredV1`, `FabEventIngestedV1`,
`OverlayHighlightRequestedV1`, and the merged `LayoutRevisionPublishedV2` all already exist).
NetArchTest is unaffected — the worker is outside the product-context graph. House ADRs that bind
here: `Ensure.That` guards (ADR-0105), `Option<T>` / NRT-off (ADR-0048), `Result<T,Error>` only
where the worker surfaces a typed failure (ADR-0047), per-module Wolverine queue isolation
(ADR-0088, already in place), `[LoggerMessage]` (ADR-0050), one-type-per-file / ≤300 LOC / ≤30 LOC
method (ADR-0084).

## P2. Component map (what the worker grows, by folder)

```
src/ScenarioSimulator/
  Scenario/ScenarioOptions.cs        [S0] + SensorDefinition numeric fields, AssetDefinition.Overlay
                                          /Highlight/Tile, ScenarioDefinition.Timeline/Wall
  Configuration/SimulatorOptions.cs  [S0] + OverlayDesignerUrl, AutomationUrl, LayoutCompositionUrl,
                                          MqttHost (resolved in Program.cs BindRuntime from services:*:http:0)
  Scenarios/rolling-mill.json        [S0] sensor params, per-asset Overlay/Highlight/Tile, Timeline, Wall
  OverlayDesigner/OverlayDesignerClient.cs     [S2] POST /overlays + publish + list-by-name read-back
  Automation/AutomationRulesClient.cs          [S2] POST /rules + POST /rules/{name}/publish
  LayoutComposition/LayoutCompositionClient.cs [S2] POST /layouts (multi-tile body) + publish + list-by-name
  Seeding/AssetCorrelationTable.cs             [S2] per-asset rows + scenario-level LayoutCreated flag
  Seeding/ScenarioSeeder.cs                    [S2] extend: Phase A overlays, Phase B rules (was M1 stub)
  EventHandlers/CameraRegisteredSimHandler.cs  [S2] extend: capture cameraId, fire single-wall join
  Timeline/BilletTimelineHostedService.cs      [S1] run loop: dwell per station, emit per tick
  Timeline/SensorBehaviour/{Ramp,Burst,Steady,Decay,Step}Behaviour.cs  [S1] 5 pure strategies
  Timeline/SensorBehaviour/ISensorBehaviour.cs [S1] strategy marker (value→time→value)
  Mqtt/MqttPublisher.cs                        [S1] MQTTnet publish, JWT password, reconnect factory
  Mqtt/MqttSampleMapper.cs                     [S1] asset+sensor+value → topic + {value,unit,station}
  Log.cs                                       [S0] extend [LoggerMessage] partial up-front (see P6 contention note)
  Program.cs                                   [S0]/[S1]/[S2] DI wiring (see P6 contention note)
```

## P3. Data flow (one screen, four tiles)

1. **Startup (synchronous, idempotent — Phase A+B).** `ScenarioSeeder` POSTs the four overlays
   (capture each `OverlayIdentifier`, publish), then the four rules (embedding the matching overlay
   ID, publish). `409` → list-by-name read-back to recover the ID. Correlation rows for the four
   assets now hold `{ OverlayIdentifier, RuleName, DeviceId, TileRow, TileCol }`.
2. **Cameras (M1, unchanged) → async camera IDs (Phase C).** `ScenarioSeeder` registers the four
   cameras; each `CameraRegisteredV1` lands in `CameraRegisteredSimHandler`, which (a) provisions
   camera-sim (M1, unchanged) and (b) records `assetKey → CameraIdentifier`.
3. **Single-wall join (Phase D).** After recording each camera ID, the handler checks: are **all
   four** asset rows complete (overlay + camera)? First time yes → `LayoutCompositionClient` POSTs
   the **one** multi-tile `{ grid:2×2, tiles:[4] }` body, publishes, flips `LayoutCreated`. Cold-
   restart-safe via list-by-name read-back.
4. **Billet timeline (independent lifecycle).** `BilletTimelineHostedService` loops: dwell at each
   station in `Assets` order, every `TickMs` emit each sensor's value (via its behaviour strategy)
   as MQTT `fab/munich/{source}/{device}` with `{value,unit,station}`. The seeded rule for that
   station fires `HighlightOverlay` → its distinct overlay → that one tile lights on the wall.

The billet engine can start emitting **before** the wall exists (highlights simply have no rendered
tile yet); it does not block on Phase D. Convergence is eventual and self-healing.

## P4. Key decisions locked by this plan

- **Exactly one `POST /layouts`** for the whole demo (the wall), fired on the four-way join — not
  four POSTs. This is the single biggest behavioural change from the parked design.
- **Four distinct overlays** (not one reused) so each highlight lands on exactly one tile — the
  ADR-0112 highlight-all-matching semantic is deliberately *not* exercised (it would light multiple
  tiles). This is a correctness requirement, called out in §6.
- **Tile coordinates are data** (`AssetDefinition.Tile{Row,Col}` in the scenario JSON), so the
  S4→(0,0)/S7→(0,1)/CB→(1,0)/CO→(1,1) mapping is declarative and testable, not hard-coded.
- **Idempotency = stable names + read-back**, no worker-side persistence. `rolling-mill-*` overlay/
  rule names and the single `rolling-mill-wall` layout name are the idempotency keys (O7 default).
- **MQTT auth reuses the existing Keycloak token** (username `scenario-simulator`, `azp == username`)
  — no second credential; reconnect supplies a fresh JWT (O4 to confirm at Phase 4).

## P5. Verification posture (automatable vs dev-run)

- **Automatable, stack-free, in CI** (does not boot the simulator, so the gate is honoured):
  behaviour-curve unit tests (`ramp` monotonic, `step` single jump, `decay` bounded-decreasing,
  `steady` jitter-bounded, `burst` baseline+spike) with a fake `TimeProvider`; `MqttSampleMapper`
  topic/payload tests; seeding-orchestration tests with the four HTTP clients faked + four fed
  `CameraRegisteredV1` asserting (i) no wall create before all four complete, (ii) exactly one wall
  POST, (iii) idempotent re-run, (iv) correct per-tile `(row,col)` in the body. → **slice S3.**
- **Dev-run-only (the demo):** `aspire run` → kiosk → tap the one wall → S4→S7→CB→CO light in
  sequence each loop; cross-check the EventIngestion list. Inherently dev-only (gated off in CI).

## P6. Contention-file ownership (single-owner, never split) — ADR-0109

| File | Owner | Why single-owner |
|---|---|---|
| `Scenarios/rolling-mill.json` | **S0** | one JSON document; concurrent edits conflict |
| `Scenario/ScenarioOptions.cs` | **S0** | shared options classes (`AssetDefinition`/`SensorDefinition`/new `Tile`/`Wall`/`Timeline`) consumed by S1+S2 |
| `Realms/smart-sentinel-eye-realm.json` | **S0** | add 3 write scopes to the `scenario-simulator` client |
| `mosquitto/acl.txt` | **S0** | add `scenario-simulator` topic-write grants |
| `AppHost.cs` | **S0** | worker block (lines ~353–363) gains `.WithReference(overlayDesigner)`, `.WithReference(automation)`, `.WithReference(layoutComposition)`, `.WithReference(mosquitto.GetEndpoint("mqtt"))` + the matching `.WaitFor(...)`; the worker currently references only camera-catalog/camera-sim/rabbitmq/keycloak. `mosquitto`, `overlayDesigner`, `automation`, `layoutComposition` are already top-level vars in scope |
| `Log.cs` | **S0** | one `[LoggerMessage]` partial both S1 and S2 extend → S0 lands all new log methods up front so S1/S2 only *call* them |
| `Program.cs` | **S0** | DI registrations for S1 (publisher, timeline hosted service) + S2 (3 typed `HttpClient`s, correlation table) against agreed shapes, **plus** `BindRuntime` resolving the new `SimulatorOptions` URLs (`services:overlay-designer:http:0`, `services:automation:http:0`, `services:layout-composition:http:0`) and the MQTT host from the mosquitto endpoint |
| `Shared.Contracts` | **S0** (if ever) | none expected (A8) |

**Note on `Log.cs` and `Program.cs`:** both are touched by S1 and S2, so to keep slices disjoint
**S0 owns them** and lands the full set of log-method signatures + DI registrations against the
interfaces S1/S2 will implement. S1/S2 then implement those interfaces in their own files and only
*reference* the already-registered log methods. If a late log/registration is unavoidable, it
returns to S0, not the parallel slice (per ADR-0109 disjoint-files rule).

## P7. Slice dispatch list (ready for the orchestrator)

| Slice | Files (disjoint) | Depends on | Parallel? |
|---|---|---|---|
| **S0** (foundational) | `rolling-mill.json`, `Scenario/ScenarioOptions.cs`, `Configuration/SimulatorOptions.cs`, realm JSON, `acl.txt`, `AppHost.cs`, `Log.cs`, `Program.cs` | — | no — runs first, alone |
| **S1** (timeline + MQTT) | `Timeline/BilletTimelineHostedService.cs`, `Timeline/SensorBehaviour/*.cs`, `Mqtt/MqttPublisher.cs`, `Mqtt/MqttSampleMapper.cs` | S0 (options + Log + DI) | **yes — ∥ S2** |
| **S2** (seeding + correlation) | `OverlayDesigner/OverlayDesignerClient.cs`, `Automation/AutomationRulesClient.cs`, `LayoutComposition/LayoutCompositionClient.cs`, `Seeding/AssetCorrelationTable.cs`, `Seeding/ScenarioSeeder.cs`, `EventHandlers/CameraRegisteredSimHandler.cs` | S0 (options + Log + DI) | **yes — ∥ S1** |
| **S3** (automatable tests) | `tests/.../ScenarioSimulator.Tests/*` (behaviour, mapper, orchestration) | S1+S2 public shapes | starts once S1/S2 shapes land |

**Order:** `S0 → (S1 ∥ S2) → S3 → manual dev-run verification`. The orchestrator
commits/pushes/PRs each slice (subagent push is sandbox-unreliable per ADR-0109); base `develop`,
rebase-only (ADR-0087). The CI e2e gate stays green because the simulator is gated off there.

## P8. Latency budget (constitution §IV) — unchanged

Budget-neutral, exactly as §8: M2 adds no code on the `event → overlay state ≤ 200 ms` leg; it
*feeds* that leg realistic traffic. ADR-0112 already accounted for the wall's decode/composite legs
(`SFU → kiosk decode ≤ 120 ms`, `composite + render ≤ 50 ms`) at the 2×2 cap; M2 renders that same
wall and adds nothing to those legs. The first realistic load generator for the highlight leg — a
measurement opportunity, not a regression.

## P9. Phase-2 gate (stop here)

Open for the product owner to confirm before Phase 3 (`/speckit-tasks` → issues): the one-wall
seeding sequence (P3), the four-distinct-overlays decision (P4), the slice cut (P7), and residual
open questions O2/O3-residual/O4/O5/O6/O7. No tasks issued and no code written until this gate
passes.

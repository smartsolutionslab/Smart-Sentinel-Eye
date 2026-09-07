# Spec 087 — the census: commands, populations, raw output

Every count in `spec.md` is reproduced here with the command that produced it.
Run from the repository root with `export LC_ALL=C` (without it, `grep -P`
fails and `grep -E` character classes behave differently on this box).

**Why this file exists.** Issue #2141's strongest instruction is *count before
planning*, and it lists three counts in this family that were wrong on first
telling: #2096 filed as "~28 endpoints" and measured **three**; #1999 filed as
"two files" and measured **eleven**; 23 sites measured at 22. A count nobody can
re-run is a count that will be wrong again.

For each detection: the **population counted**, the **population deliberately
excluded**, the command, and its raw output.

---

## Detection 1 — tests excluded from CI by category

**Counted:** `[Trait("Category", …)]` attributes in `tests/` whose value is one
of the three categories excluded at `.github/workflows/ci.yml:179`.

**Excluded from the population:** `FixtureLogic` (3 sites). That category is
*selected* at `ci.yml:72`, not excluded — counting it would inflate the figure
with tests that run in the cheapest job there is.

### 1a — the category vocabulary, so the population is not assumed

```sh
grep -rhoE 'Trait\("Category", ?"[^"]+"' --include=*.cs tests/ | sort | uniq -c | sort -rn
```

```
      8 Trait("Category", "Measurement"
      4 Trait("Category", "Disruptive"
      3 Trait("Category", "FixtureLogic"
      1 Trait("Category", "Maintenance"
```

The same grep over `src/` and `apps/` returns nothing — no strays outside
`tests/`.

### 1b — attribute sites → test methods

A class-level trait excludes every `[Fact]` in the class; a method-level trait
excludes one. Counting attribute sites alone would under-count. Here all six
class-level traits sit on classes holding exactly one `[Fact]`, so the two
figures coincide at 13 — but the script computes it rather than assuming it.

```sh
find tests/Integration.Tests -name '*.cs' | sort | while read f; do
  cls=$(grep -cE '^\[Trait\("Category", ?"(Measurement|Disruptive|Maintenance)"' "$f")
  mth=$(grep -cE '^[[:space:]]+\[Trait\("Category", ?"(Measurement|Disruptive|Maintenance)"' "$f")
  [ "$cls" -eq 0 ] && [ "$mth" -eq 0 ] && continue
  if [ "$cls" -gt 0 ]; then n=$(grep -cE '^\s*\[(Fact|Theory)' "$f"); else n=$mth; fi
  echo "$n ${f#tests/}"
done | awk '{print; s+=$1} END{print "TOTAL excluded test methods: " s}'
```

```
2 Integration.Tests/AuditObservability/ClockOffsetIntegrationTests.cs
2 Integration.Tests/AuditObservability/NFR001_AuditIngestLatencyTests.cs
1 Integration.Tests/AuditObservability/RunModeIngestAttributionTests.cs
1 Integration.Tests/AuditObservability/RunModeVariableResidueSweep.cs
1 Integration.Tests/Automation/FirstEventSplitTests.cs
1 Integration.Tests/Automation/FirstPublishPerTypeTests.cs
1 Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs
1 Integration.Tests/EventIngestion/MqttResubscribeAfterBrokerOutageIntegrationTests.cs
1 Integration.Tests/EventIngestion/OutboxSurvivesAKillTests.cs
1 Integration.Tests/EventIngestion/RestartLosesNothingIntegrationTests.cs
1 Integration.Tests/Fixtures/LogTailDeliversIntegrationTests.cs
TOTAL excluded test methods: 13
```

### 1c — is the verdict read anywhere, ever?

Searched: `specs/**`, `docs/**`, `.github/**`, `scripts/**`, `.specify/**`,
every `*.md`, and `src/**` — by test-method name, by class name, and by each
test's distinctive printed figures (the figure search is what matters: several
are read by number while the class name appears nowhere).

**Result: 12 READ, 1 NOT READ.** Full evidence table in `spec.md`. The single
negative:

`RunModeVariableResidueSweep.Archive_the_measurement_variables_a_run_mode_stack_still_holds`
— zero hits for the method name, the class name, or the filename anywhere in
the worktree outside its own source file. Its printed figures (`residue found`,
`archived`, `residue remaining`) appear nowhere. Its git history is a single
commit and it has no spec directory.

### 1d — does any automation run these categories?

```sh
grep -rn "Measurement\|Disruptive\|Maintenance\|Category=\|--filter" scripts/
ls .github/workflows/
```

`scripts/` → **zero hits** for any of the three categories. `.github/workflows/`
contains **exactly one file**, `ci.yml`. There is no nightly, scheduled or
`workflow_dispatch` job that runs them.

Four human-typed invocations exist in prose
(`specs/026-…/quickstart.md:83`, `specs/027-…/quickstart.md:72`,
`specs/027-…/tasks.md:40`, `specs/027-…/verification.md:161`). Nothing invokes
them automatically.

---

## Detection 2 — assertion against a threshold no observation approaches

**Counted:** every test asserting an upper bound on a *measured* quantity.
**Excluded:** the ~40 `ShouldBeLessThan` sites in `*.Domain.Tests` — those are
value-object ordering comparisons (`TimestampOrderingTests`,
`FabIdentifierTests`), not budgets.

### 2a — the budget constants

```sh
grep -rnE 'const (int|double|long) [A-Za-z]*(Budget|Ceiling|Threshold|Slo|Limit|Max)[A-Za-z]*\s*=' --include=*.cs tests/
```

```
Automation.Application.Tests/Ael/AelInterpreterBenchmarkTests.cs:30:    private const double MedianBudgetMilliseconds = 500;
Automation.Application.Tests/Ael/AelInterpreterBenchmarkTests.cs:33:    private const double CeilingMilliseconds = 1_000;
EventIngestion.Infrastructure.Tests/MqttConnectionLoopTests.cs:34:    private const int SpinCeiling = 20;
EventIngestion.Infrastructure.Tests/MqttConnectionLoopTests.cs:43:    private const int FlapCeiling = 16;
Integration.Tests/AuditObservability/IngestRunShape.cs:84:    public const double SlotIntervalMs = 1000d / TargetRatePerSecond;
Integration.Tests/AuditObservability/IngestRunShape.cs:90:    public const double MaximumAcceptableRate = TargetRatePerSecond * (1 + RateTolerance);
Integration.Tests/CameraCatalog/CommandLatencyTests.cs:18:    private const int BudgetMilliseconds = 200;
Integration.Tests/LayoutComposition/SignalRRevocationIntegrationTests.cs:18:    private const int RevocationBudgetMilliseconds = 1000;
Integration.Tests/OverlayDesigner/OverlayPushIntegrationTests.cs:19:    private const int PushBudgetMilliseconds = 1000;
Integration.Tests/OverlayDesigner/ReconnectReconcileIntegrationTests.cs:24:    private const int ReconcileBudgetSeconds = 5;
Integration.Tests/SystemVariables/NFR_VariableResolutionLatencyTests.cs:59:    private const int LegBudgetMs = 800;
Integration.Tests/SystemVariables/ResolvedTextReachesItsFabTests.cs:133:    private const long PushCeilingMs = 5_000;
```

Plus, under different constant names:
`WhepHandshakeLatencyTests.P95BudgetMilliseconds = 3000`,
`NFR001_AuditIngestLatencyTests.P99BudgetMs = 50`,
`NFR002_MqttConnectAuthTests.P50BudgetMilliseconds` / `P99CeilingMilliseconds`.

### 2b — the one the issue names, verified

`tests/Integration.Tests/StreamDistribution/WhepHandshakeLatencyTests.cs:20`

```csharp
private const int P95BudgetMilliseconds = 3000;
```

The class carries `[Collection(AspireCollection.Name)]` and **no category
trait**, so it is selected by the `integration` job on every PR. It was not
removed by spec 077.

The 60× figure is spec 077's own, at
`specs/077-click-to-first-frame-measured-not-proxied/plan.md:237`:

> Predicted p95 was 1.4–2.2 s against a 3000 ms threshold — real margin, but not
> the 60× the existing proxy enjoys.

### 2c — the margins

**No test was run to produce this section.** Another agent holds the stack, and
a figure obtained by guessing would be exactly the defect this issue is about.
Every observed figure below is quoted from the repository; where none is
written down the answer is **UNKNOWN**, which is itself the finding.

| Test | Threshold | Observed | Source of observed | Margin | Class |
|---|---|---|---|---|---|
| `NFR_VariableResolutionLatencyTests` | 800 ms | 6 ms (worst 8) | `specs/014-…/tasks.md:238-239` | **133×** | VACUOUS |
| `EndToEndIngestionIntegrationTests` | 20 000 ms de-facto | 85 ms | `NFR001_…:167-169,182` | **235×** | VACUOUS as timing |
| `WhepHandshakeLatencyTests` | 3000 ms | — | only a margin claim, `specs/077-…/plan.md:237` | ~60× claimed | VACUOUS / UNKNOWN |
| `ResolvedTextReachesItsFabTests` | 5000 ms | 555, 758 ms | `ResolvedTextReachesItsFabTests.cs:126-128` | 6.6× | COMFORTABLE |
| `NFR001_JwtValidationLatencyTests` p50 | 500 µs | ~70 µs prod-path, 96–100 µs CI | `…:22,25-26,88-90` | 5–7× | COMFORTABLE |
| `PostgresConnectionBudgetIntegrationTests` count | < 378 | 97 pre-cap | `docs/adr/0125-…:23-24,61-62` | ~3.9× | COMFORTABLE |
| `NFR001_JwtValidationLatencyTests` p99 | 50 000 µs | 3–21 ms | `…:25-26` | 2.4× | TIGHT |
| `PostgresConnectionBudgetIntegrationTests` max_conns | ≥ 500 | 500 | `docs/adr/0125-…:57` | 1.0× | TIGHT by construction |
| `NFR002_MqttConnectAuthTests` p50 | 15 ms | **17.58 ms** | `specs/021-…/verification.md:212` | 0.85× | TIGHT — **has gone red** |
| `NFR001_AuditIngestLatencyTests` p99 | 50 ms | 85 ms best | `…:182`; `specs/009-…/tasks.md:377` | 0.59× | inverted — cannot pass |
| `SfuLatencyIsReadableTests` | none — `ShouldContain("paths")` | n/a | `…:35-39` | n/a | **no latency assertion at all** |
| `EventToOverlayLatencyTests` | none — `ShouldBe(180, tol 1)` | n/a, `FixedClock` | `…:54` | n/a | unit guard, not a budget |

### 2d — the nine UNKNOWNs, and the four that were promised

Enforced against a number recorded nowhere in the tree:

1. `WhepHandshakeLatencyTests` 3000 ms — no run's figure is recorded anywhere,
   not in the test, not in `specs/002`, not in the landing commit.
2. **`CommandLatencyTests` 200 ms** — `specs/001-register-camera/tasks.md:169`
   required it: *"Report measured value in the PR body's 'Latency budget impact'
   section (per ADR-0031)."* The PR body is not in the tree.
3. **`SignalRRevocationIntegrationTests` 1000 ms** —
   `specs/003-layout-composition/plan.md:75`: *"PR will report measured
   archive-to-force-disconnect from the integration test."*
4. **`OverlayPushIntegrationTests` 1000 ms** —
   `specs/004-overlay-designer/plan.md:73`: *"PR will report measured
   `publish-overlay → kiosk-render` on SC-002."* The only number in the file is
   the cold-start cost the warm-up *excludes*.
5. `ReconnectReconcileIntegrationTests` 5 s — see 2e.
6. `NFR002_AuditSearchLatencyTests` 200 ms p99 over 100 000 rows — notable
   because its sibling NFR-001 is the most thoroughly measured test in the repo.
7. `NFR002_MqttConnectAuthTests` p99 ceiling 50 ms — the remarks assert a
   *relation* to it without a number (`:76-78`).
8. `AelInterpreterBenchmarkTests` 500 / 1000 ms — the "≈ 100 ms per batch"
   at `:27-28` is labelled **expected**, and traces to the requirement
   (`docs/adr/0099-hand-rolled-ael.md:26`), not to a run.
9. `PostgresConnectionBudgetIntegrationTests` post-cap fixture count.

Items 2, 3 and 4 are the sharpest: a spec promised the figure, the workflow
gate passed, and the figure was never written anywhere durable.

### 2e — a new instance, not on the issue's list

`ReconnectReconcileIntegrationTests` asserts `elapsed < ReconcileBudgetSeconds`
(5 s) at `:107-109`, inside a poll loop at `:91-103` bounded by the **same**
`CancellationTokenSource budget` constructed at `:88` from that same constant.
`elapsed` therefore cannot materially exceed 5 s by construction. If
reconciliation never happens the loop simply exits and
`observedState.ShouldBe("Archived")` (`:106`) fails first; the timing assertion
can only fire in the sliver where the final HTTP call overruns the token.

**A timing tripwire wired to its own timeout.** This is the purest example of
the class the issue describes, and it was found by census rather than by
accident — which is the issue's own argument for doing the census.

### 2f — the one that does it correctly

`ResolvedTextReachesItsFabTests.cs:121-131` states threshold, observation and
arithmetic together, and names the exact failure mode 2e exhibits:

> Phase 5 measured this exact server-side leg at **555 ms and 758 ms on a cold
> stack**, so 5 s sits roughly **6.6x** above the worst figure anyone has
> observed … Both margins are deliberate: a bound at `FrameWindow` would assert
> nothing at all, because the wait has already ended by then, and a bound near
> the observed figures … would flake on a cold stack and be deleted by the next
> person.

This is the template a future fix for F1/F11/F12 should follow.

### 2g — constitution §IV mapping

§IV's budget table is `.specify/memory/constitution.md:131-138`; the per-leg
state table is `:147-154`.

| §IV leg | §IV state | Tests mapping to it |
|---|---|---|
| Camera → SFU ≤ 80 ms | measured yes, dashboard no | `SfuLatencyIsReadableTests` — but it reads **no latency figure**, only that the endpoint answers and names `paths` |
| SFU → kiosk decode ≤ 120 ms | in part | none |
| Presentation buffer ≤ 200 ms | recorded, not yet observed | none |
| **Event → overlay state ≤ 200 ms** | **recorded, not yet readable** | `NFR_VariableResolutionLatencyTests` (133×), `ResolvedTextReachesItsFabTests` (6.6×), `EventToOverlayLatencyTests` (instrument guard) |
| Composite + render ≤ 50 ms | yes | none |
| Headroom ≤ 150 ms | arithmetic remainder | none |

The two loosest live margins in the population sit on the one leg §IV already
records as not yet readable. Everything else maps to **no** §IV leg and must not
be read as if it did — `specs/003-layout-composition/plan.md:75` is explicit:
the SignalR revocation path is an operator-action latency, "**not** the 800 ms
event-to-overlay budget".

---

## Detection 3 — assertions gated on an environment variable

**Counted:** every `Environment.GetEnvironmentVariable` in `tests/` that gates
an assertion, plus the adjacent skip mechanisms, because "gated" is broader than
"env var".

**Excluded:** env-var reads that are *inputs to the measurement* rather than
gates on its verdict — `Logging__LogLevel__Default` (read to *report* the run's
configuration, at `NFR001_AuditIngestLatencyTests.cs:99` and
`RunModeIngestAttributionTests.cs:46`) and `RunModeStackAddress.cs:53-55` (reads
the stack address; its absence makes the test **fail**, not pass silently).

```sh
grep -rnE 'Environment\.GetEnvironmentVariable\("GITHUB_ACTIONS"\)' --include=*.cs tests/
```

```
Integration.Tests/Identity/NFR002_MqttConnectAuthTests.cs:81:        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
sites: 1
```

```sh
echo "xUnit Skip= : $(grep -rhoE '\[(Fact|Theory)\([^)]*Skip' --include=*.cs tests/ | wc -l)"
echo "Assert.Skip : $(grep -rhoE 'Assert\.Skip|Skip\.If|SkipException' --include=*.cs tests/ | wc -l)"
echo "frontend    : $(grep -rhoE '\b(test|it|describe)\.(skip|fixme|todo)\b' --include=*.ts --include=*.tsx apps/ | wc -l)"
```

```
xUnit Skip= : 0
Assert.Skip : 0
frontend    : 0
```

**Answer to the issue's "How many others?": none.** There is exactly one
env-gated assertion site and there are no skipped tests anywhere in the
repository.

---

## Detection 4 — fakes that cannot produce the failure the test names

**Not mechanically decidable. Everything here is a proxy, labelled as one.**

**Counted:** hand-written doubles, and absence-assertions fed from one.
**Excluded by design:** `Integration.Tests` (49 files with absence assertions —
they use real services) and `Architecture.Tests` (25 — they read real source and
assemblies, and contain zero doubles).

```sh
grep -rnE '\b(class|record|struct)\s+(Fake[A-Za-z0-9_]*|Stub[A-Za-z0-9_]*|[A-Za-z0-9_]*Fake|[A-Za-z0-9_]*Stub|Recording[A-Za-z0-9_]*|[A-Za-z0-9_]*Double|Test[A-Za-z0-9_]*Provider)\b' --include='*.cs' tests apps src e2e | wc -l
# 73   (all 73 in tests/; 0 in apps/, src/, e2e/)

grep -rnE 'private\s+sealed\s+(class|record)\s+[A-Za-z0-9_<>,() ]+\s*:\s*[IA-Z]' --include='*.cs' tests apps src e2e | wc -l
# 73   (69 in tests/; the 4 in src/ are production types, not doubles)

grep -rnE 'ShouldBeEmpty|ShouldNotContain|ShouldBeNull|ShouldBeFalse|ShouldNotBe\(|ShouldBe\(0\)' --include='*.cs' tests apps src e2e | wc -l
# 622  ShouldBeEmpty 181 | ShouldNotContain 83 | ShouldBeNull 67
#      ShouldBeFalse 148 | ShouldNotBe( 83    | ShouldBe(0) 60
```

Absence-assertions whose asserted value comes from a hand-written double,
outside `Integration.Tests` and `Architecture.Tests`: **77**.

**All 77 were hand-read. 71 are falsifiable** — they use *recording* doubles
(`FakeEventBus.Published`, `FakeRtspGateway.AddCalls`,
`RecordingJourneyOrigin.Open`, `RecordingCompletion.Stored`, `StubHandler.Calls`)
whose lists genuinely fill when the code under test misbehaves.

**6 candidates remain**, listed in `spec.md`. The dominant root cause is
structural rather than per-test: `IRuleCache.LookupActive(fab, source, kind)` is
a **keyed lookup**, so the fab filter lives in the double and not in the code
under test — four of the six are that one seam. `RuleEvaluator.Evaluate`
(`src/Automation/Application/Evaluation/RuleEvaluator.cs:34`) does no fab
filtering of its own.

**Both of the issue's calibration instances are already fixed on `develop`:**
`tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs:58-67` (the fake ready
check now carries a description *and* a `data` entry) and both `FakeMqttClient`s
(`EventIngestion.Infrastructure.Tests/Fakes/FakeMqttClient.cs:188-198`,
`ScenarioSimulator.Tests/Fakes/FakeMqttClient.cs:143-153`, both now returning
`NotAuthorized`).

### What this proxy cannot detect

1. A double that *can* produce the failure but is never asked to. The proxy
   inspects capability, not per-test wiring.
2. **Fidelity drift** — a double modelling the wrong contract. This was the
   actual root of both calibration bugs, and no absence-assertion pattern
   reveals it.
3. Over-narrow arrange data: `ShouldNotContain(";")` is falsifiable in
   principle and theatre if no input contains `;`. Input coverage is invisible
   to grep.
4. **Interface-level unfalsifiability**: candidates 1–4 are a property of
   `IRuleCache`'s *shape*. Detecting the class requires reading the interface
   and the SUT together. Two seams were traced by hand; ~40 were not.
5. Multi-assert dilution — one vacuous assertion among five real ones.

---

## Detection 5 — Docker-free classes with no `[Trait]`

**Counted:** files under `tests/Integration.Tests` containing at least one
`[Fact]`/`[Theory]`, lacking `[Collection(AspireCollection.Name)]`, and lacking
any `[Trait("Category", …)]`.

**Excluded:** files with no facts (helper types such as `AttributionVerdict.cs`,
`IngestRunShape.cs`, `RunModeStackAddress.cs` — a first pass wrongly included
seven of these).

**Comments must be stripped before matching.** Without stripping, *this
command* counts 23 and not 34 — see the two traps below. (The guard built from
it anchors its attribute match at the start of a line, and that anchor refuses
the doc-comment case on its own; stripping is load-bearing there for a
commented-out attribute inside a `/* … */` block. `plan.md` §"The two traps".)

**`obj/` and `bin/` must be pruned**, as the guard does explicitly and as
`LogTailCoverageTests:165-166` does. The original command below did not, and
was right by luck: the three generated `.cs` files under that tree carry no
`[Fact]`, so the fact filter dropped them. A future generated fixture would not
be dropped, and would be reported by a path nobody can fix.

```sh
strip() { sed -e 's|//.*||' -e '/^\s*\*/d' -e '/^\s*<\/\?/d' "$1"; }
find tests/Integration.Tests -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' \
  | sort | while read f; do
  facts=$(grep -cE '^\s*\[(Fact|Theory)' "$f"); [ "$facts" -eq 0 ] && continue
  strip "$f" | grep -qE '\[Collection\(AspireCollection\.Name\)\]' && continue
  strip "$f" | grep -qE 'Trait\("Category"' && continue
  echo "$facts ${f#tests/Integration.Tests/}"
done | awk '{print; s+=$1; n++} END{print "TOTAL: " n " classes, " s " test methods"}'
```

```
7 AuditObservability/AttributionVerdictTests.cs
13 AuditObservability/IngestAttributionTests.cs
11 AuditObservability/RunModeDriverTests.cs
3 EventIngestion/ListEventsTranslationTests.cs
TOTAL: 4 classes, 34 test methods
```

### The two counting traps, both hit

1. **Keying on "mentions `AspireFixture`" missed `RunModeDriverTests`** (11
   facts). That class names the fixture in reflection code *because its job is
   to assert it does not acquire it*:
   `typeof(RunModeIngestAttributionTests).GetCustomAttribute<CollectionAttribute>()`
   … `collection.ShouldBeNull(...)`. Count would have been 3 classes / 23 tests.

2. **Keying on `[Collection(AspireCollection.Name)]` without stripping comments
   missed it too** — the literal string appears inside its `<c>…</c>`
   doc-comment at `RunModeDriverTests.cs:22`. Count would again have been 23.

Both traps produce the same wrong number, 23, by different routes — though, as
the correction below shows, it is a third shape, not either trap, that makes
comment-stripping a requirement of the guard rather than an implementation
detail.

**Trap 2 is refused twice over in the guard as built, independently of each
other.** Phase 4a confirmed by counterfactual that the guard still reports
4 / 34 with stripping removed: the anchor alone refuses it, because the `[` in
`/// <c>[Collection(…)]</c>` never begins its line. But stripping alone would
also refuse it without the anchor's help, because `StripComments` deletes the
whole `///` line before either match runs. Neither mechanism is individually
necessary for trap 2. FR-003 is still a requirement — for the one shape neither
of those two facts covers, where an attribute is commented out inside a
`/* … */` block: it *does* begin its own line, so the anchor credits it exactly
as it credits a live one, and only stripping tells the two apart.

**Line numbers in this document are as at the census**, before the four
`[Trait]` attributes were added. Each attribute was inserted immediately above
its class declaration, so only a cite **below** that point shifts — by one line
— and the four doc-comment quotes below (`:8`, `:4`, `:10-11`, `:18-21`) all sit
**above** it and are unchanged. The one cite in this file that does sit below an
insertion point is the one two paragraphs up: `RunModeDriverTests.cs:20` is now
`:22`, and has been corrected there.

### Why "no `[Collection]`" does not mean "Docker-free"

Two classes lack the collection **and** need a live run-mode stack:
`RunModeVariableResidueSweep` and `RunModeIngestAttributionTests`. Both
correctly carry a category, so both are outside this population — but a guard
that inferred "no collection ⇒ must be `FixtureLogic`" would demand the wrong
declaration of them. The guard therefore requires *a* declaration, not a
specific one. (`plan.md`.)

### The four classes each already say they need no stack

| Class | Its own words |
|---|---|
| `AttributionVerdictTests` | "**These need no stack.**" (`:8`) |
| `IngestAttributionTests` | "the arithmetic of the breakdown, tested without a stack" (`:4`) |
| `RunModeDriverTests` | "**These are cheap, they need no stack**" (`:10-11`) |
| `ListEventsTranslationTests` | "Offline by design … no `AspireCollection` / Docker dependency" (`:18-21`) |

The information needed to place all four correctly was already written down in
the files themselves. Nothing read it — which is the argument for a derivation
test rather than a convention.

# Spec 087 — Twelve of thirteen verdicts are read

**Issue:** #2141 — *Which assertions in this repository cannot fail? Five found by accident in one day*
**Branch:** `chore/2141-which-assertions-cannot-fail`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0139 (rules that fail the build, not the review — the governing
decision for the one guard this spec builds), ADR-0103 (integration tests are
Aspire-fixture-only; the reason a Docker-free class is a special case at all),
ADR-0052 (xUnit + Shouldly), ADR-0065 (coverage gates), ADR-0084 (code
metrics), ADR-0109 (parallel markers), ADR-0036 (smallest change; no
speculative generality), ADR-0037 (phased workflow), ADR-0144 (autonomous
lane — phase 4a has two colours and no exemption; no ADR is written here).

**Constitution:** §Testing — "**New behaviour:** TDD red-green-refactor. The
test is written first, is **observed failing**, and that failure is quoted in
the PR body." That sentence is what makes the phase-4a question in §"Phase 4a"
below answerable rather than a matter of taste.

**No new ADR is required, and none is written.** The select-by-trait mechanism
this spec guards was decided at issue level (#2064) and is already implemented
in `ci.yml`. The decision that a convention should fail the build rather than
rely on a reviewer is ADR-0139, which already exists. This spec enforces two
decisions that are already made; it makes none. (ADR-0144 blocks the lane from
writing one in any case.)

---

## The issue as filed, and what survives contact with the repository

The issue makes five factual claims that can be checked without running
anything. **Four hold. One is stale, and one more is stale in the other
direction — the issue understates the problem.** Both are recorded here because
this spec's whole subject is claims that nobody checked.

| Claim | Verdict |
|---|---|
| `ci.yml:179` is the category-exclusion filter | **Correct**, exactly. `--filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"` is on line 179 of a 277-line file, and it is the only such filter. |
| `WhepHandshakeLatencyTests` asserts `p95 < 3000 ms` with a 60× margin | **Correct on the figure, stale on the tense.** See below. |
| #2127 — NFR-001's budget is asserted by a test that fails locally and is excluded from CI | **Correct, and deliberate, and documented.** |
| #2134 — a Docker-free class carries no `[Trait]` | **Correct, and understated.** |
| `A_reconnect_after_a_success_does_not_inherit_the_previous_backoff` uses a 4 ms cap against a 500 ms window | **Correct.** |

### The one that is stale: "Replaced by a real measurement in #199 / spec 077"

`WhepHandshakeLatencyTests` **still exists, still asserts `p95 < 3000`, and
still runs in CI.** Spec 077 *added* a real click-to-first-frame measurement; it
did not remove the proxy. The file is at
`tests/Integration.Tests/StreamDistribution/WhepHandshakeLatencyTests.cs` with
`private const int P95BudgetMilliseconds = 3000` at line 20, it carries
`[Collection(AspireCollection.Name)]` and no category trait, so it is selected
by the integration job on every PR.

The 60× figure is not the issue's invention and is not `3000 / 200`. It is spec
077's own, at `specs/077-click-to-first-frame-measured-not-proxied/plan.md:237`:

> Predicted p95 was 1.4–2.2 s against a 3000 ms threshold — real margin, but not
> the 60× the existing proxy enjoys. That is the correct state for a threshold:
> close enough to matter, far enough not to sing.

Spec 077 not only left it standing, it *documented* it standing —
`specs/077-…/spec.md:115-118`:

> It times `POST /streams/authorize` — 20 iterations — and asserts
> `p95 < 3000 ms`. … **An assertion of `< 3000 ms` against a quantity budgeted at
> a fraction of 200 ms is a tripwire that cannot trip.** It is not wrong about
> anything; it simply cannot fail for the reason it exists.

— and filed retiring or narrowing it as a **P2 human decision** (`spec.md:252-262`)
which it explicitly did not act on.

So the issue's first census row is **live, not historical**, and was already
known and filed rather than discovered by accident. Row F1 of the findings table
reflects that.

### The one the issue understates: #2134

#2134 says "a Docker-free class carries no `[Trait]`". The measured population is
**four classes and 34 test methods**, not one. Detection 5 below.

### The one this spec could not check without a stack

**#2133** — "the logging default about to ship has never been measured; the
figures on record measure a different pair." Deciding this requires running the
measurement against a booted stack. Another agent holds the stack and this spec
does not take it. **Left unmeasured and unclaimed**; #2133 owns it.

---

## The census, measured

Every count below is reproducible from a clean checkout. The exact commands and
their raw output are in `census.md` next to this file; the population counted
**and the population excluded** are stated for each, because that is where the
issue records three previous counts going wrong (#2096: "~28 endpoints",
measured 3; #1999: "two files", measured 11).

### Detection 1 — excluded from CI: **13 test methods, 12 of which are read**

Population: every `[Trait("Category", …)]` in `tests/` whose value is one of the
three `ci.yml:179` excludes. Excluded from the population: `FixtureLogic` (3
sites — that category is *selected*, at `ci.yml:72`, not excluded).

13 trait attribute sites resolve to 13 test methods (6 class-level traits on
classes holding exactly one `[Fact]` each, plus 7 method-level traits).

**The pivotal sub-question — "is its verdict read anywhere, ever?" — answers
READ for 12 of 13.** This inverts the issue's implied expectation. Two of them
are not merely read; **ADR-0135 and ADR-0136 are, substantially, their printed
output**. One measurement's red is quoted in *production* source
(`src/EventIngestion/Infrastructure/Ingress/MqttConnectionLoop.cs:216-219`).

| # | Test | Read? | Where |
|---|---|---|---|
| 1 | `Where_the_first_events_seconds_go` | READ | figures verbatim, `specs/023-first-event-cold-start/verification.md:35-37` |
| 2 | `Whether_the_cost_is_paid_once_per_message_type` | READ | `specs/023-…/verification.md:47,56-57,65` |
| 3 | `Measure_sustained_rate_latency_and_order` | READ | before/after tables in specs 020 and 021 verification notes |
| 4 | `An_event_published_after_a_broker_outage_is_still_ingested` | READ | its red quoted in production source, `MqttConnectionLoop.cs:216-219` |
| 5 | `Announcements_committed_before_a_restart_are_delivered_after_it` | READ (name only) | `specs/021-transactional-outbox/verification.md:236` — no printed figure quoted anywhere |
| 6 | `A_restart_mid_drain_stores_every_event_exactly_once` | READ | output verbatim, `specs/020-…/verification.md:61-63` |
| 7 | `The_shared_server_can_be_read_precisely_enough_to_matter` | READ | `specs/053-…/verification.md:56-61` → ADR-0135 |
| 8 | `A_process_measured_against_itself_shows_no_skew_worth_reporting` | READ | `specs/053-…/verification.md:239-243` |
| 9 | `Ingest_p99_from_publish_to_row_stays_under_50ms` | READ | 5 ADRs, spec 009 T072, `scripts/mint-spec-009-issues.sh:104` |
| 10 | `Where_the_ingest_span_goes` | READ | **ADR-0135 is its output** |
| 11 | `Where_the_ingest_span_goes_in_run_mode` | READ | **ADR-0136 is its output** |
| 12 | `Archive_the_measurement_variables_a_run_mode_stack_still_holds` | **NOT READ** | zero hits for method, class or filename anywhere outside its own file |
| 13 | `A_restarted_resource_keeps_delivering_to_its_log_tail` | READ | "test C", `specs/059-…/verification.md:36-66,181-184` |

**Nothing in `scripts/` or `.github/` runs any of the three excluded
categories.** `ci.yml` is the only workflow in the repository. Four
`Category=Measurement` invocations exist in quickstart/verification prose for a
human to type; no automation invokes them.

**Verdict: 12 deliberate, 1 finding.** The excluded population is overwhelmingly
*documentation that is genuinely read* — which the issue allows for
("a measurement test excluded from CI may be correct if something else reads its
number") but did not expect to be the dominant case. The single genuine finding
is #12.

### Detection 2 — threshold no observation approaches: **16 budgets, 9 with no recorded observation**

Population: every test asserting an upper bound on a *measured* quantity — 16.
Excluded: the ~40 `ShouldBeLessThan` sites in `*.Domain.Tests`, which are
value-object ordering comparisons, not budgets.

**The dominant finding is not a loose threshold. It is that nine of sixteen
budgets are enforced against a figure that exists nowhere in the repository.**
Four of those nine sit under a spec that explicitly promised to record it —
e.g. `specs/003-layout-composition/plan.md:75`, "PR will report measured
archive-to-force-disconnect from the integration test." The PR body is not in
the tree; the number was never written down. A threshold whose observation was
never recorded cannot be known to be a guard or a decoration.

| Test | Threshold | Observed | Margin | Class |
|---|---|---|---|---|
| `NFR_VariableResolutionLatencyTests` | 800 ms | **6 ms** | **133×** | **VACUOUS** |
| `EndToEndIngestionIntegrationTests` (de-facto 20 s ceiling) | 20 000 ms | 85 ms | **235×** | VACUOUS *as a timing tripwire*; its content assertions are sound |
| `WhepHandshakeLatencyTests` | 3000 ms | not recorded | ~60× (repo's own claim) | VACUOUS, observation UNKNOWN |
| `ResolvedTextReachesItsFabTests` | 5000 ms | 555 / 758 ms | 6.6× | COMFORTABLE |
| `NFR001_JwtValidationLatencyTests` p50 | 500 µs | ~70–100 µs | 5–7× | COMFORTABLE |
| `PostgresConnectionBudgetIntegrationTests` count | < 378 | 97 | ~3.9× | COMFORTABLE |
| `NFR001_JwtValidationLatencyTests` p99 | 50 000 µs | 3–21 ms | 2.4× | TIGHT |
| `PostgresConnectionBudgetIntegrationTests` max_conns | ≥ 500 | 500 | 1.0× | TIGHT (by construction) |
| `NFR002_MqttConnectAuthTests` p50 | 15 ms | **17.58 ms** | **0.85×** | TIGHT — **has gone red** |
| `NFR001_AuditIngestLatencyTests` p99 | 50 ms | 85 ms | **0.59×** | inverted — cannot pass; excluded (#2127) |
| `CommandLatencyTests`, `SignalRRevocationIntegrationTests`, `OverlayPushIntegrationTests`, `ReconnectReconcileIntegrationTests`, `NFR002_AuditSearchLatencyTests`, `NFR002_MqttConnectAuthTests` p99, `AelInterpreterBenchmarkTests` ×2 | various | **never recorded** | — | **UNKNOWN** |

**`NFR_VariableResolutionLatencyTests` is looser than the case the issue leads
with** — 133×, against the 60× that opens the issue's table. Its own task note
declared it at the time: `specs/014-system-variable-fab-scoping/tasks.md:221-223`
records "median 9 ms, worst 11 ms … against a 200 ms budget — ~20x headroom",
and the test then asserts at 800 ms, four times looser again.

**A new instance of the class, not on the issue's list.**
`ReconnectReconcileIntegrationTests` asserts `elapsed < 5 s` inside a poll loop
bounded by *the same* `CancellationTokenSource budget` the 5 s comes from
(`:88`, `:91-103`). `elapsed` cannot materially exceed the threshold by
construction; if reconciliation never happens, the loop exits and a *different*
assertion fails first. **A timing tripwire wired to its own timeout** — the
purest form of the defect this issue is about, and it was found by this census
rather than by accident.

**One test does this correctly and is worth copying.**
`ResolvedTextReachesItsFabTests.cs:121-131` writes threshold, observation and
arithmetic in one place, and names the failure mode of a bound set at its own
timeout — which is exactly what `ReconnectReconcileIntegrationTests` does.

**Constitution §IV mapping.** Only three of the sixteen touch a §IV leg, all on
*event → overlay state* (`NFR_VariableResolutionLatencyTests`,
`ResolvedTextReachesItsFabTests`, and `EventToOverlayLatencyTests` as the
instrument's own guard). §IV records that leg as "recorded, not yet readable".
The two loosest live margins in the whole population — 133× and 6.6× — sit on
it. `SfuLatencyIsReadableTests` maps to *Camera → SFU* but reads **no latency
figure at all**; it asserts only that the metrics endpoint answers.
The rest map to no §IV leg and must not be read as if they did —
`specs/003-layout-composition/plan.md:75` says so explicitly for the SignalR
revocation path ("**not** the 800 ms event-to-overlay budget").

### Detection 3 — assertions gated on an environment variable: **1 site, deliberate**

Population: every `Environment.GetEnvironmentVariable` in `tests/` that gates an
assertion. **Exactly one exists**, and the issue already names it:
`NFR002_MqttConnectAuthTests.BudgetsApplyHere` (`…:80-81`), gating two
assertions in one test method.

The issue asks "How many others?" — **none.**

Adjacent mechanisms, counted because "gated" is broader than "env var":
`[Fact(Skip=…)]` **0**; `Assert.Skip` / `Skip.If` / `SkipException` **0**;
frontend `test.skip` / `it.skip` / `.fixme` / `.todo` **0**. There are no
skipped tests in this repository.

**Verdict: deliberate, and the opposite of a defect.** The gate is
`GITHUB_ACTIONS == "true"` — so the thresholds are **live in the one environment
whose verdict CI reads**, and inert only locally, where the class remarks
(`…:70-79`) explain that container networking makes the numbers non-portable
(#1905). This is the mirror image of #2127, where the test fails locally *and*
is excluded from CI. Both were filed as the same shape; they are not.

### Detection 4 — fakes that cannot produce the failure: **6 candidates, not mechanically decidable**

**Labelled a proxy.** This detection cannot be decided by grep, and the spec
says so rather than reporting a number that looks derived. The proxy: a
hand-written double whose seam is a *keyed lookup* or a *pre-filtered list*,
feeding a test that asserts absence.

Both of the issue's calibration instances are **already fixed on `develop`** —
the health-payload fake now carries a description and a `data` entry
(`tests/ServiceDefaults.Tests/DefaultEndpointsTests.cs:58-67`), and both
`FakeMqttClient`s now return `NotAuthorized`. Everything below is new.

Inventory: 73 named doubles + 69 in-file `private sealed` doubles, all under
`tests/` (zero in `src/`, `apps/`, `e2e/`). 77 absence-assertions are fed from a
double; **71 of the 77 are falsifiable** — they use *recording* doubles whose
lists genuinely fill when the code misbehaves.

**Six candidates, and four of them are one root cause.**
`IRuleCache.LookupActive(fab, source, kind)` is a keyed lookup, so the fab
filter lives *in the double*, not in the code under test. `RuleEvaluator` does no
fab filtering of its own — a munich lookup can never return the dresden rule, so
the regression each test names is unreachable through that seam. (Contrast
`IRuleQuerySource`, which exposes `IQueryable`; the equivalent tests there *are*
falsifiable.)

| # | Site | Why it cannot fail | Confidence |
|---|---|---|---|
| 1 | `Automation.Application.Tests/Evaluation/RuleEvaluatorTests.cs:208` | keyed-lookup seam; dresden rule never leaves the fake | CONFIDENT |
| 2 | `…/RuleEvaluatorTests.cs:251` | guaranteed key miss | CONFIDENT |
| 3 | `Automation.Application.Tests/EventHandlers/FabEventIngestedV1HandlerTests.cs:146` | same seam, one layer up | CONFIDENT |
| 4 | `…/FabEventIngestedV1HandlerTests.cs:207` | "evaluating everything" has no representation in `IRuleCache` | UNSURE |
| 5 | `Identity.Application.Tests/KeycloakAdmin/KioskPrivilegeSweepTests.cs:104` | the fake implements the boundedness the test claims to check | CONFIDENT |
| 6 | `SystemVariables.Application.Tests/EventHandlers/SystemVariableValueRequestedV1HandlerTests.cs:108` | the SUT has no create path at all, so the repo is empty for any input | CONFIDENT (different mechanism) |

**Mitigating fact, recorded so the finding is not overstated:** the production
`InMemoryRuleCache` *is* covered directly
(`tests/Automation.Infrastructure.Tests/Cache/InMemoryRuleCacheTests.cs:66,122-123,136`),
so the fab-scoping guarantee is not unguarded overall — only those four tests
are.

### Detection 5 — Docker-free classes with no `[Trait]`: **4 classes, 34 test methods**

This is #2134's population, measured for the first time.

| Class | Facts | What the class itself says |
|---|---|---|
| `AuditObservability/AttributionVerdictTests.cs` | 7 | "**These need no stack.**" |
| `AuditObservability/IngestAttributionTests.cs` | 13 | "the arithmetic of the breakdown, tested without a stack" |
| `AuditObservability/RunModeDriverTests.cs` | 11 | "**These are cheap, they need no stack**" |
| `EventIngestion/ListEventsTranslationTests.cs` | 3 | "Offline by design … no `AspireCollection` / Docker dependency" |

Every one of the four **documents in its own doc-comment that it needs no
stack**, and none carries the trait that would let the cheap CI step run it. The
information needed to place them correctly was already written down; nothing
read it.

**Two counting traps, both hit and both corrected — recorded because this
detection is the one that becomes a guard, and a guard derived from a wrong
count is worse than none.**

1. A first pass keyed on "mentions `AspireFixture`" and **missed
   `RunModeDriverTests`** (11 facts), which names the fixture in reflection code
   and doc-comments precisely because its job is to assert the class does *not*
   acquire it. Count would have been 3 classes / 23 tests.
2. A second pass keyed on `[Collection(AspireCollection.Name)]` **also missed
   it** — the literal string appears inside its `<c>…</c>` doc-comment. Comments
   must be stripped before matching. Count would again have been 23.

The correct count is **4 classes / 34 test methods**, from a comment-stripped
scan.

**These tests are not unrun.** They carry no excluded category, so the
30-minute Docker `integration` job does run them. The defect is that a failure
that could be known in the cheap job — before Docker, before the 30 minutes — is
deferred to the expensive one, which is exactly the omission #2064 built the
trait mechanism to prevent.

**And "no `[Collection]`" does not imply "Docker-free."** Two classes lack the
collection *and* need a live run-mode stack
(`RunModeVariableResidueSweep`, `RunModeIngestAttributionTests`). They are
correctly outside detection 5's population because they carry a category — but
this is the fact that shapes the guard's rule in `plan.md`, and getting it wrong
would produce a guard that demands `FixtureLogic` on a test that needs Docker.

---

## The findings table

The issue asks for "a table — assertion, why it cannot fail, and whether that is
deliberate."

| # | Assertion | Why it cannot fail | Deliberate? | Owner |
|---|---|---|---|---|
| F1 | `WhepHandshakeLatencyTests` `p95 < 3000` | ~60× margin over the observed figure; spec 077 says so in writing | **No** — and still live, contrary to the issue | **New issue** (see Scope) |
| F2 | `RunModeVariableResidueSweep` | excluded from CI *and* its verdict is read nowhere — the only one of 13 | **No** | **New issue** |
| F3 | 12 excluded measurement tests | excluded from CI, but verdicts read in specs/ADRs | **Yes** | — |
| F4 | `NFR002_MqttConnectAuthTests` env gate | inert off-CI only; live where the verdict is read | **Yes**, documented (#1905) | — |
| F5 | `NFR001` `p99 < 50 ms` | fails (85 ms observed) and is excluded | **Yes**, documented | #2127 |
| F6 | 4 Docker-free classes, 34 tests | run only in the expensive Docker job | **No** | **This spec** (subsumes #2134) |
| F7 | `IRuleCache` keyed-seam tests ×4 | the fake implements the filter the test claims to check | **No** | **New issue** |
| F8 | `KioskPrivilegeSweepTests:104` | the fake implements the boundedness | **No** | **New issue** |
| F9 | `SystemVariableValueRequestedV1HandlerTests:108` | the SUT has no create path | **No** | **New issue** |
| F10 | `A_reconnect_…_backoff` 4 ms cap vs 500 ms window | passes whether or not the reset happens | **No** | **New issue** |
| F11 | `ReconnectReconcileIntegrationTests` `elapsed < 5 s` | the poll loop is bounded by the same token the threshold comes from — **a tripwire wired to its own timeout** | **No** — and **not on the issue's list**; found by this census | **New issue** |
| F12 | `NFR_VariableResolutionLatencyTests` 800 ms vs 6 ms | **133×** — looser than the 60× case the issue leads with | **No**, though declared at the time | **New issue** |
| F13 | 9 of 16 budgets | the observation was **never written down**; 4 sit under a spec that promised to record it | **No** | **New issue** |
| F14 | `NFR002_MqttConnectAuthTests` p50 15 ms vs 17.58 ms | the inverse defect — threshold *below* observation, has gone red | **No** | **New issue** |
| F15 | `SfuLatencyIsReadableTests` | maps to §IV *Camera → SFU* but reads **no latency figure at all** | unclear | **New issue** |

---

## User stories

### US-1 (P1) — A Docker-free integration test declares where it runs

**As** the CI pipeline, **I need** every test class under
`tests/Integration.Tests` to declare whether it needs the Aspire stack, **so
that** a class needing no stack cannot silently fall outside the cheap
Docker-free step and defer its verdict to the 30-minute Docker job.

This is the whole of the shippable slice. It is independently buildable,
observable end-to-end (the guard goes red on four real classes and green after
four one-line edits), and it touches one file plus four test classes.

**Everything else in this spec is a census.** No other finding is fixed here.

---

## Functional requirements

- **FR-001** A test class under `tests/Integration.Tests` that does **not**
  carry `[Collection(AspireCollection.Name)]` MUST carry a
  `[Trait("Category", …)]` attribute.
- **FR-002** The guard MUST derive its population from source on disk, not from
  a hand-maintained list, so that a newly added class is covered without anyone
  remembering to register it.
- **FR-003** The guard MUST ignore attribute occurrences inside comments —
  both `//` line comments and XML doc-comments. (Trap 2 above; without this the
  guard is green on a class it should fail.)
- **FR-004** The guard MUST report offending classes with `/` separators
  regardless of host platform, so a failure message is identical on Windows and
  on Linux CI.
- **FR-005** The guard's failure message MUST name the two legitimate
  declarations — `FixtureLogic` for a class needing no stack, or one of the
  excluded categories for a class needing a stack CI does not boot — because the
  correct fix differs and the wrong one is silent.
- **FR-006** The four classes in detection 5 MUST be given
  `[Trait("Category", "FixtureLogic")]`, joining the existing Docker-free CI
  step at `ci.yml:72`.
- **FR-007** The guard MUST live in `tests/Architecture.Tests`, which takes no
  project reference on `Integration.Tests` and runs without Docker.

**Non-requirement, stated so it is not added:** the guard does **not** attempt
to decide whether a class *really* needs Docker. It checks that the class has
*declared*. See "What the guard cannot do".

---

## What the guard cannot do, stated before it is built

1. **It cannot tell a Docker-free class from a run-mode class.** Both lack
   `[Collection]`. The guard forces a declaration; it cannot audit the
   declaration's truth. A Docker-free class mis-declared as `Measurement`
   satisfies the guard and runs nowhere in CI. That hole is real and is not
   closed here.
2. **It does not prove a test can fail.** It proves a test is *selected*.
   Detections 2 and 4 are about falsifiability and are censused, not guarded.
3. **It covers `tests/Integration.Tests` only.** Other test projects have no
   Docker/no-Docker split and no category filter, so the rule has no meaning
   there.
4. **A commented-out `[Collection]` reads as absent** — which is the safe
   direction (the class is then required to declare a category), and is
   asserted rather than assumed.

---

## Acceptance scenarios

### Happy — the derived population agrees with the declarations

```gherkin
Given every test class under tests/Integration.Tests
When the guard derives the set of classes lacking [Collection(AspireCollection.Name)]
Then every class in that set carries a [Trait("Category", ...)] attribute
And the guard passes
```

### Conflict — a Docker-free class added without a trait

```gherkin
Given a new test class under tests/Integration.Tests with a [Fact]
And it carries neither [Collection(AspireCollection.Name)] nor any [Trait("Category", ...)]
When the guard runs
Then it fails
And the message names the class by its repository-relative path with / separators
And the message names both legitimate declarations
```

### Bad request — the attribute appears only inside a comment

```gherkin
Given a test class whose XML doc-comment contains the literal text
  "[Collection(AspireCollection.Name)]" but which carries no such attribute
When the guard runs
Then it fails that class
And it does not credit the commented occurrence
```

*(This is not hypothetical: `RunModeDriverTests` is exactly this shape today,
and it is why the measured count is 34 and not 23.)*

### Auth / boundary — the population moves with the source, not with a list

```gherkin
Given a class is removed from tests/Integration.Tests
When the guard runs
Then the population shrinks by derivation
And no hand-maintained list needs editing
```

### No soft edge — a helper file without facts is not demanded to declare

```gherkin
Given a file under tests/Integration.Tests containing no [Fact] and no [Theory]
When the guard runs
Then that file is not in the population
And the guard does not require a trait on it
```

---

## Independent end-to-end test procedure

Runs without Docker and without Aspire.

1. `dotnet test tests/Architecture.Tests -c Release` → the new guard **fails**,
   naming 4 classes.
2. Add `[Trait("Category", "FixtureLogic")]` to the four classes.
3. `dotnet test tests/Architecture.Tests -c Release` → **passes**.
4. `dotnet test tests/Integration.Tests -c Release --filter "Category=FixtureLogic"`
   → the selected count rises from the current 3 classes to 7 classes, and the
   34 newly-selected tests execute and pass **without Docker running**. This is
   the observable end-to-end behaviour: the cheap step now reads verdicts it
   previously skipped.
5. Counterfactual (per the standing "prove a guard by counterfactual" rule):
   remove the trait from one class again and confirm the guard reddens for
   exactly that class.

---

## Phase 4a — how the colour is obtained

**Red.** This is behaviour-changing, and the two-colour rule resolves cleanly:

- The **guard** is new behaviour. It is written first and observed failing on
  the four classes that exist today. That failure — the verbatim output naming
  four classes — is the phase-4 evidence, quoted in the PR body.
- The **trait additions** then turn it green. They also change what CI selects,
  which is a behaviour change to the pipeline, not a refactor.

**Why the census does not need a colour of its own.** The brief asked this to be
argued rather than asserted. A phase-4a colour is a property of a *change to
behaviour*; the census changes none — it is the evidence that motivates the
guard, and it is delivered as `census.md` inside the spec directory, which is a
phase-1/2/3 artifact. ADR-0144's "no exemption" forbids shipping *behaviour*
without a colour, and this spec ships exactly one piece of behaviour, in red.
Had the deliverable been a census alone, option (a) would have required arguing
that phases 4–6 are skipped under ADR-0037's trivial-change clause — and a
census is not a trivial change, it is *no* change, which the clause does not
cover. **That gap is the reason option (b) was chosen over (a):** it makes the
phase-4a obligation real rather than argued away.

---

## Latency budget

**N/A.** No leg of the constitution §IV event-to-overlay path is touched. This
spec adds one architecture test and four attributes; no runtime code, no
message, no query, no rendering path changes. Nothing in `src/` or `apps/` is
modified.

---

## Non-functional

- The guard runs in the `backend` CI job, which needs no Docker. It reads ~110
  files from disk; expected runtime well under a second.
- ADR-0084 metrics apply: ≤ 300 LOC in the new file, ≤ 30 LOC per method.
- ADR-0065 coverage gates are unaffected — `Architecture.Tests` and
  `Integration.Tests` are both outside the covered projects.

---

## Assumptions, marked

- **A1** `FixtureLogic` is the correct category for all four classes. Grounded,
  not guessed: the CI step it selects is *named* "Docker-free fixture logic
  tests" (`ci.yml:67`), so the category already means "needs no Docker" in this
  repository's usage. The name is a slight stretch for pure arithmetic tests
  (`AttributionVerdictTests`); **renaming the category is deliberately not done
  here** — it would touch `ci.yml` and three existing classes for a cosmetic
  gain, against ADR-0036.
- **A2** No test class under `Integration.Tests` legitimately needs to be
  outside both the collection and every category. Verified against all 8 such
  classes today; 4 declare correctly, 4 are the finding.
- **A3** #2133 is left unmeasured. Deciding it needs a booted stack, which this
  spec does not take.

---

## Scope — what this spec does not do, and who owns it

The issue's own strongest constraint is **"do not bulk-fix"**. Held to
literally: of ten findings, **one** is fixed.

| Not done here | Owner |
|---|---|
| Fix `NFR001`'s excluded failing budget | **#2127** |
| Add CI coverage for NFR-005 | **#2128** |
| Measure the logging default | **#2133** |
| The Docker-free-class omission | **#2134 — subsumed by this spec.** The guard plus FR-006 closes #2134's entire population. Stated explicitly so the two issues do not both claim it; **#2134 should be closed by this PR**, and this spec should not be merged claiming otherwise. |
| Re-tighten `WhepHandshakeLatencyTests`' 3000 ms (F1) | **New issue.** Not touched: the issue forbids widening a threshold, and *tightening* one is equally a change to what CI blocks on. It is spec 002's SLO and changing it is a human decision at spec level (spec 077 `plan.md` §4 says exactly this). |
| Delete or wire up `RunModeVariableResidueSweep` (F2) | **New issue.** |
| The four `IRuleCache` keyed-seam tests (F7) | **New issue.** The fix is an interface-shape question, not a test edit. |
| `KioskPrivilegeSweepTests` (F8), `SystemVariableValueRequested` (F9) | **New issue** (may share F7's). |
| The 4 ms / 500 ms backoff test (F10) | **New issue.** |
| Any threshold change anywhere | **Nobody, here.** No threshold is raised or lowered by this spec. |

**Not doing** — stated because class-level issues sprawl: no test is deleted, no
threshold moved, no analyzer narrowed, no suppression added, no category
renamed, no ADR written, no second guard built for detections 2/3/4.

---

## Gate (Phase 1)

Spec reviewed; no `[NEEDS CLARIFICATION]` remains. Two items are marked as
explicit non-answers rather than guesses: **A3** (#2133 unmeasured, needs a
stack) and **detection 4** (labelled a proxy, not a derivation).

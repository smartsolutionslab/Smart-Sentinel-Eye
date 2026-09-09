# Implementation Plan: The instrument before the latency

**Spec**: [`spec.md`](./spec.md) · **Issue**: #1956 · **Branch**:
`perf/1956-the-instrument-before-the-latency`

**Phase**: 2 (Plan) of ADR-0037.

---

## 1. Shape of the change

**This feature adds no domain artefact.** There is no aggregate, no value object, no
invariant, no command, no query, no domain event and no integration event. Saying so
explicitly is the plan's first job, because the template's centre of gravity is a
bounded-context slice and this is not one.

| Layer | Touched? | Why |
|---|---|---|
| `AuditObservability/Domain` | **No** | `AuditEvent` already carries `handler_entered_at`, `received_at`, `written_at`. Nothing new is persisted. |
| `AuditObservability/Application` | **No** | `AuditingMessageHandler:47-48` already stamps behind the switch. The switch's default stays `false` (`AuditMeasurementOptions.cs:23`). |
| `AuditObservability/Infrastructure` | **No** | Listener topology is ADR-0124/0126's and is not a variable here. |
| `AuditObservability/Api` | **No** | No endpoint. |
| `Shared.Kernel` / `Shared.Contracts` | **No** | No message, no DTO, no version bump. |
| `src/AppHost` | **Yes, one env line** | US1 — inside the existing `if (isE2ETests)` block at `AppHost.cs:410-430`. |
| `tests/Integration.Tests` | **Yes** | US2 and US3 — the facts and the broker sampler. |

**Boundary rules hold trivially.** No cross-context project reference is added; the
measurement drives `system-variables` over **HTTP** and reads `audit-db` over its own
`DbContext`, which is what `IngestSpanMeasurement` already does. NetArchTest's rules are
not engaged by a change confined to `AppHost` and `tests/`.

**Messaging is unchanged.** No domain event becomes an integration event, no queue is
added, no routing convention moves. `WolverineDefaults.cs:104-130` is **read**, not
edited.

---

## 2. US1 — the fixture can stamp

### The seam

`src/AppHost/AppHost.cs:410` opens `if (isE2ETests)`, the block the **integration
fixture** alone enters (`E2ETests=true` is set by `AspireFixture`), and it already
contains a precedent for exactly this shape at `:429`:

```csharp
auditObservability.WithEnvironment("AuditObservability__Retention__TickInterval", "00:00:03");
```

US1 adds one sibling line for `AuditObservability__Measurement__RecordIngestBreakdown`.

### Why there and not in `AspireFixture`

The fixture builds the AppHost through
`DistributedApplicationTestingBuilder.CreateAsync<Projects.SmartSentinelEye_AppHost>`
(`AspireFixture.cs:247-249`) and does not enumerate resources, so it has no handle on
`auditObservability`. The AppHost's own `isE2ETests` branch is the only place that both
knows the resource and knows it is the test stack.

### The consequence this buys, named rather than discovered

**Every audit row written by the integration suite will carry the stamps**, not only the
measurement facts'. Per row that is one extra `clock.UtcNow` and two non-null columns —
negligible, and the `TickInterval` line beside it already sets a fixture-only behaviour
this way.

**What it costs is a comparability property.** Spec 053 established that the switch's own
cost cannot be measured inside one run — it is the difference between two runs — and the
unpaced fact was kept unpaced precisely so its figures stayed comparable with historic
ones. Once the fixture is always stamped, the fixture's figures are comparable with each
other and **not** with any pre-053 fixture figure. That is an acceptable trade, because
those historic figures were produced by defect (A) and defect (B) of §1.2 and are not
evidence about NFR-001 anyway. It is recorded so nobody later reads a shift as a
regression.

**The alternative was rejected:** a per-test switch. The fixture is a collection fixture,
booted once for the whole assembly; a test process cannot re-configure a service that is
already running. The only per-test alternative is a second stack, and *one machine, one
Aspire stack*.

### Non-negotiable

`AuditMeasurementSwitchTests.cs:96` — `new AuditMeasurementOptions().RecordIngestBreakdown
.ShouldBeFalse()` — must pass **unmodified**. The type default is the production default;
this change configures a resource, it does not move a default.

---

## 3. US2 — the verdict states its rate

### What changes

`tests/Integration.Tests/AuditObservability/NFR001_AuditIngestLatencyTests.cs` gains one
fact and loses one claim:

1. **A paced verdict fact.** It drives through `IngestSpanMeasurement.RunAsync` — which
   already builds 50 writers and paces to `IngestRunShape.TargetRatePerSecond`
   (`IngestSpanMeasurement.cs:95-121`) — and guards `Conditions.RateWasMet`, exactly as
   `Where_the_ingest_span_goes` does at `:329-332`. It reports
   `RequirementSpanFloorMs`, `RequirementSpanCeilingMs` and `RequirementSpanWidthMs` from
   `IngestAttribution.cs:69-84`.
2. **The existing `Ingest_p99_from_publish_to_row_stays_under_50ms` stops asserting
   NFR-001's budget.** It keeps its shape — one sequential writer, `NoPacing` — because
   that shape is the only thing that makes its figure comparable with the ones on the
   issue. What it loses is the verdict: it reports the end-to-end span and the rate it
   achieved, and names what it is not.

### The assertion the verdict fact makes, and the one it must not

The budget comparison is **against the interval, not against a point**. §1.0's two runs
put the requirement span at 6.2-3107.4 ms and at 2.5-17.5 ms respectively, at the same
paced rate — the budget inside the first interval and outside the second. So the
fact asserts:

- the rate was met (else the number answers a different question);
- every row was stamped (else the parts are absent);
- the per-row residual is zero (the apparatus check that `PartsCoverEveryRow` already
  encodes);
- and it **reports** floor / ceiling / width, stating whether the budget falls inside the
  interval.

It **must not** assert `p99 < 50` against the ceiling. That is the assertion that
produced every superseded verdict on this issue, and re-making it in a paced run would
reproduce the defect with better manners.

**This is the point where a reviewer should push back if it drifts.** A fact that turns
green by comparing the budget against the *floor* is the mirror-image error and is
equally forbidden.

### Naming

Sentence-style with underscores (ADR-0053). The verdict fact's name must say what it
measures — it names the requirement's span, the rate, and that the answer is an
interval — rather than promise a pass. `[Trait("Category", "Measurement")]` stays: the
fixture at 100 ev/s is bistable (ADR-0136) and CI is not where a bistable figure belongs.
Widening `ci.yml:179` is out of scope (spec §2).

---

## 4. US3 — locating the handover

### The mechanism

Sample RabbitMQ's management API for `messages_unacknowledged` on the `audit-observability.*`
queues (`ContextName` + `.` + the event FQN — `WolverineDefaults.cs:95-96`; **not**
`wolverine_audit`, which is the Postgres outbox schema, `AuditObservabilityInfrastructureModule.cs:24`), once a second, through a paced run. Under
`ProcessInParallelWithNativeAcks()` (`WolverineDefaults.cs:126-129`) a delivery is held
unacknowledged at the broker for the whole handler, so that count is the population
inside NFR-001's leg. `mean(unacked) / drain rate` is the mean time in the leg — read
from the broker, crossing no clock.

The connection details come from the fixture the same way the existing RabbitMQ-facing
integration tests obtain them; the sampler reads, it does not provision.

### The order of work, which is the point

**Task 1 is the assumption check, not the sampler.** A1 in the spec is an inference from
ADR-0126's crash test. If `messages_unacknowledged` does not behave as assumed — because
Wolverine's prefetch settles differently from the reading — every number downstream is
meaningless. So: observe it zero when quiescent and non-zero mid-handler **before**
building anything that divides by it.

This ordering is deliberate and mirrors the failure that produced ADR-0126's own
correction: a count was read, an inference drawn, and the inference was wrong because the
count answered a different question from the one asked of it.

### The two outcomes, both of which are done

- **Unacked near zero.** The handover is at handler entry; the requirement's span is the
  floor; NFR-001 holds on its own leg while the end-to-end span — which has no budget
  anywhere — does not. The finding says so, and names the missing end-to-end budget as
  the follow-up.
- **Unacked deep.** The prefetch buffer holds the wait inside the leg; NFR-001 is
  genuinely missed; the finding names a per-row transport-receipt stamp as the next step
  and **stops**. That stamp is production-path apparatus beyond ADR-0135's scope and is
  not taken in this pass.

**Observed: the deep branch, and the plan did not anticipate that it would be
bimodal.** Phase 5 drove this over 14 boots and phase 4b re-ran it after the review
fixes. On the **first paced drive of a boot** the implied leg reads 237.8 / 794.1 /
402.2 / 306.6 / 248.3 ms with 64–115 deliveries in flight — deep, 5 of 5. Three drives
in it reads 11.4 / 11.7 / 16.8 / 22.5 ms — and once 51.8 ms, deep again. So the
requirement's leg is now **readable** and reads **12–52 ms warm, 248–794 ms cold**;
neither bullet above is the whole answer, and **"NFR-001 is met" is not this feature's
claim**. The deep branch's stated next step, a per-row transport-receipt stamp, stands
and is **not taken in this pass**.

Phase 4b's own first pair of runs (10.1 and 19.6 ms) were both warm and both at the
bottom of the warm range, and were briefly reported as "near zero". That is the failure
this section's own warning describes, one level up: a count read twice, an inference
drawn, and the inference wrong because both reads came from the same mode.

### Where this stops and raises an ADR instead

If locating the handover turns out to need a **Wolverine listener policy or middleware**
— anything that runs for every message in every context rather than a read of broker
state — the work **stops and reports**. That is shared messaging infrastructure, ADR-0088
and ADR-0126 territory, and the lane may not write an ADR (ADR-0144). A broker read is
not that; a pipeline hook is.

---

## 5. Constitution and ADR alignment

| Gate | Status |
|---|---|
| §II value objects / primitive ban | Not engaged — no domain model changes. |
| §IV latency budget | **N/A.** Audit is on none of the six legs (spec §6). §VII's dashboard obligation is not engaged. |
| §V spec-driven | This spec + plan + tasks; the feature issue (#1956) is on Project #13. |
| §VI Aspire is the composition root | US1's one line goes in `AppHost`, not into a hand-wired connection string. |
| §VII no event sourcing without justification | Not engaged. |
| §Testing — refactor green, new behaviour red | Phase 4a colour is **red**; see §6. |
| ADR-0088 per-module queue isolation | **Read, not changed.** |
| ADR-0124 / ADR-0126 listener topology | **Read, not changed.** |
| ADR-0127 | Stays rejected. |
| ADR-0135 measurement switch off by default | The type default stays `false`; only the fixture resource is configured. |
| ADR-0141 `Option<T>` in Domain/Application | Not engaged — no Domain or Application signature is added. |
| ADR-0086 no `Co-Authored-By` | Commits carry none. |

**No new ADR is required for the planned work**, and none may be written by the lane.
Two things that *would* need one are explicitly out of scope: dropping
`AutoApplyTransactions` for audit endpoints (ADR-0127's untried alternative, `:173-176`)
and any per-message hook in the Wolverine listener pipeline.

---

## 6. Phase 4a — the colour, and how red is obtained

**Behaviour-changing → red** (constitution §Testing, ADR-0144). Ambiguity resolves to
red, and this is not even ambiguous: US1 changes what the fixture stack does, US2 and US3
add facts that assert things nothing asserts today.

Red is available for each story without contrivance, because each asserts something
demonstrably false right now:

| Story | The red, before the change |
|---|---|
| US1 | With no shell export, `Where_the_ingest_span_goes` fails: *"N rows arrived without the measurement stamps"* — the exact failure #2127 pasted. **Not reachable from a cold stack**: run alone it dies at `DefineAsync` with `Polly.Timeout.TimeoutRejectedException … '00:00:10'`, which is infrastructure, not the refusal. Another fact must warm `system-variables` first (spec A7). |
| US2 | A fact guarding `RateWasMet` on the unpaced driver fails, reporting 45.1-57.5 ev/s against a target of 100 (measured at phase 4a; the earlier ~15-20 came from ADR-0136's row for a different machine). |
| US3 | **No red is available, and the PR must say so rather than imply a gate it never passed.** The sampler and the fact asserting on it are the same artefact in the same test project: a fact written against a sampler that does not exist is a compile error, which ADR-0139 does not accept as red, and a fact that samples inline is green on its first run. So US3 is one task (T102), and its evidence is two runs reported unaveraged. Neither the absence nor a compile error is dressed as a red. |

**A test that arrives green here is a phase-4 failure, not a shortcut.** The trap to name
in advance: US2's paced verdict could be made green by comparing the budget against the
floor. That is not the assertion; §3 says which assertion it is.

`test-writer` returns the verbatim failure output; the engineer receives it as the brief
and may not edit the tests to pass.

---

## 7. Risks

| Risk | Handling |
|---|---|
| **The fixture is bistable at 100 ev/s** (ADR-0136: 267 -> 7362 ms across seven runs). | Every measurement task runs at least twice and reports both, unaveraged. No task's success criterion is a latency value. |
| **The first run after machine churn reads as a regression.** | Same handling; it is why "twice" is a task requirement rather than advice. |
| **Disk headroom.** The workstation was at 96% of C: (11 GB free) when this plan was written. | Do not create worktrees for this feature. A full disk stops the Docker engine and only a GUI restart recovers it. |
| **One machine, one Aspire stack.** Two concurrent boots produce `FailedToStart` that reads exactly like a code defect. | Stop the AppHost before any build (MSB3027) and never run two measurement tasks at once. No `[P]` marker on any task that boots the fixture. |
| **A1 is an inference.** | US3's first task verifies it before anything divides by it. |
| **The Aspire structured-log search is unreliable** — ADR-0127 recorded zero hits for events demonstrably landing. | Evidence comes from Postgres and from the broker's HTTP API, never from a log search. |

---

## 8. Definition of done

1. `Where_the_ingest_span_goes` passes under a plain `dotnet test` with **no environment
   variable exported by the runner**, printing `measurement switch: ON` and
   `0 missing stamps`.
2. The NFR-001 verdict fact reports an achieved rate inside `IngestRunShape`'s tolerance
   of 100 ev/s, and reports the requirement span's floor, ceiling and width.
3. No fact asserts `p99 < 50 ms` against the end-to-end ceiling, and none asserts it
   against the floor.
4. US3 reports mean unacknowledged, drain rate and implied leg time from **two** runs,
   unaveraged, with A1 verified first.
5. `AuditMeasurementSwitchTests` passes **unmodified**.
6. `git diff src/` shows one added `WithEnvironment` line and nothing else.
7. A **finding** is written to the issue: what the instrument could not say, what it can
   say now, and which of the two US3 outcomes was observed. NFR-001's 50 ms and its
   100 ev/s are unchanged in `specs/009-audit-observability/spec.md`.

# Feature Specification: A boot that does not pass over a corpse

**Feature Branch**: `fix/2066-a-wait-asks-how-it-finished`

**Created**: 2026-09-07 · **Re-scoped**: 2026-09-07 after phase 4a

**Status**: Draft — re-specified. The first version of this document is
**withdrawn**; its central premise was disproved by its own phase 4a.

**Issues**: #2066 (re-scoped) · **Remainder**: #2146

**ADRs**: ADR-0103 (integration tests boot the real Aspire stack), ADR-0068
(the fixture), ADR-0037 (phase gates), ADR-0139 / constitution §Testing (new
behaviour starts red), ADR-0144 (the lane, and what it may not do), ADR-0084
(code metrics — `S104` is `NoWarn`'d for test projects, `Directory.Build.props:108`).

**Predecessor**: `specs/062-a-wait-that-asks-how-it-finished/` (#2064, merged as
#2065).

> **The directory name is now wrong and is kept anyway.** `084-a-wait-that-names-what-died`
> names the mechanism this spec no longer ships. Renaming it would break every
> commit that cites the path; the correction lives in the title and in §1 instead.
> The **issue** title should change — see §"What the board should say".

> **Every `AspireFixture.cs:NNN` in this document is a merge-base line number**,
> describing the tree this change starts from and not the tree it produces.
> `:277` is the `catch (… OperationCanceledException or TaskCanceledException)`
> in `InitializeAsync`; `:184` is the migrations gate. This change inserts new
> members above both, so both numbers are wrong at the branch tip — as 062's
> were, which is why `develop`'s tip is `8437de96 docs(specs): 062 — a false
> claim, an invented figure, and drifting line numbers`, whose own remedy was to
> *"re-anchor to member names rather than refresh to numbers that will drift
> again"*. **The construct is the reference; the line number is a convenience
> that expires.** The implementation and its tests name constructs only, and
> `verification.md` keeps the numbers it read on the day and says so.

---

## 1. The premise this spec was built on is false, and here is the disproof

The withdrawn version asserted:

> *"A resource that dies during startup never reaches `Running`."*

**For a crash-on-startup it does.** DCP publishes `Running` when the process
launches; the crash follows a second or two later. `WaitForResourceAsync(name,
Running, …)` matches that transient `Running` and returns satisfied.

This is not a race that a careful predicate could win. `AppHost.cs:424` gives all
nine services `dependent.WaitForCompletion(migrations)`, so each launches at the
instant the migrations gate releases — exactly when the fixture subscribes its
next wait. The `Running` transition is observed in the ordinary case, not the
unlucky one.

**Therefore the planned mechanism** — a predicate of `Running || IsFatalStartupState(state)`
at each of the twelve waits — **would have caught neither provocation.** The
disjunction matches `Running` first and the wait returns; the death arrives
afterwards, and nothing is subscribed to read it.

### What was actually observed, four boots, two resources, no timeout

| Provocation | Observed |
|---|---|
| `camera-catalog` killed on startup | `InitializeAsync` **completed normally**. The failure surfaced in the *test body* at `00:02:51.92` as `Polly.Timeout.TimeoutRejectedException` → `TaskCanceledException` → `IOException`, on the `POST /cameras` that only runs after the fixture has declared the stack ready. |
| `identity` killed on startup | The filtered test **passed. Twice.** A fifth boot, filtered onto a test that talks to identity, confirmed identity really was dead. |

**The defect is not that the fixture is slow to name what died. The defect is
that it pronounces the boot healthy over a corpse.** A wait that hangs is slow
and legible; a suite that reports green with a dead service is neither. The
second is strictly worse, and it is what #2066 now exists to fix.

### Why none of the existing diagnostics fired

Every report this family has built — #2061's cause line and state list, #2038's
snapshot section, #2064's migrations message — hangs off the
`catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)`
at `AspireFixture.cs:277`. **No timeout, no catch, no report.** The whole
apparatus is bypassed by the failure mode it was built for.

---

## 2. The mechanism: assert liveness once, after the waits

Reaching the end of `InitializeAsync` means every wait *returned*. It does not
mean every resource is *alive*. So before handing the stack to the tests, read
each waited-for resource's current state once and refuse to proceed if any of
them is dead.

```
ResourceNotifications.TryGetCurrentState(name, out ResourceEvent current)
  -> IsFatalStartupState(current.Snapshot.State?.Text)
  -> fetch that resource's log -> throw InvalidOperationException
```

`TryGetCurrentState` is **synchronous and already used in this file**
(`TryResolveResourceId`); the decompile of 13.5.3 confirms it returns
`ResourceNotificationState.LastSnapshot`, i.e. the current state, not a replay.
So a healthy boot pays twelve in-memory state reads and nothing else — no watch,
no timer, no HTTP.

**Not twelve *dictionary lookups*, though**, and the earlier wording said so.
`TryGetCurrentState`'s parameter is a resource **id** (`camera-catalog-thwaubpm`),
so an app-model name misses the keyed fast path every time and falls through to
the `Where` beneath it — a scan of the forty-odd tracked resources with a
duplicate-name check. Twelve scans, still microseconds against a three-second
watch: the cost argument survives the correction by six orders of magnitude, the
word "lookup" does not.

### Why this is a *sample*, and why the sample is sound here

Sampling a state once cannot see a resource that is briefly healthy and then dies
**after the sample**. That is true, and it is the permanent limit of any
non-continuous check (§11). What makes the sample sound at *this* point is that
it is taken at the last possible instant of the boot — after all twelve waits and
all three HTTP pollers — so it sees every death that occurred during startup,
which is the population #2066 is about. In both provoked runs the death happened
roughly two minutes before that instant.

### Why it is not the fatal-state watchdog, and needs no ADR

#2146 proposes a continuous `WatchAsync` over the whole boot that cancels the
shared token. That **turns the fixture from a sequence of waits into a supervised
boot**, which is why it is filed as probably needing an ADR. This does not: it
adds one assertion at the end of a sequence of waits, reading a snapshot the
fixture already reads on its timeout path, and refusing to return. It is the same
shape as #2064 — *read what you already have, and do not proceed on a state that
means failure* — and applies ADR-0103/ADR-0068 rather than amending them.

---

## 3. The three mechanisms, and what each actually covers

Written out because the withdrawn spec claimed coverage it did not have, and
because #2146 must inherit an accurate table.

| | death **before** its wait subscribes | death **after** `Running`, during the boot | death of a resource **nothing waits on** | shortens the 8-minute budget |
|---|---|---|---|---|
| **A** — per-wait `Running \|\| fatal` predicate | yes | **no** — the disproof in §1 | no | yes, when it fires |
| **B** — liveness assertion after the waits (**this spec**) | yes | **yes** | no (§7) | no — the boot completes at its normal length and then fails |
| **C** — fatal-state watchdog (#2146) | yes | yes | yes | yes |

**A's honest coverage is four resources, not twelve.** For the nine services the
window between launch and subscribe is effectively zero (`WaitForCompletion`
again), so the only way one is dead at subscribe is a *dependency* failure —
`FailedToStart`, i.e. #2062, **which #2064's migrations gate already stops at
1 m 38 s**. The window is real only for `keycloak` (subscribed at t≈0, container
takes 60–90 s), `mediamtx` and `fixture-video` (waits subscribe 90–150 s after
the containers start), and `migrations` (wait subscribes after keycloak's).

**A is therefore deferred, not shipped** — see §8.

---

## 4. What `WaitForResourceHealthyAsync` would do, measured rather than assumed

The withdrawn plan rejected it on the grounds that it is "a stricter gate than
`Running` — it additionally waits for `HealthStatus.Healthy`". Re-examined
against the disproof, because that stricter property is exactly what would catch
a process that reached `Running` and then died.

**The rejection survives, and the stated reason turns out to be false for eleven
of the twelve.** From the 13.5.3 decompile
(`Aspire.Hosting.ApplicationModel.CustomResourceSnapshot`):

```csharp
internal static HealthStatus? ComputeHealthStatus(
    ImmutableArray<HealthReportSnapshot> healthReports, string? state)
{
    if (state != KnownResourceStates.Running) { return null; }
    return healthReports.Length == 0
        ? HealthStatus.Healthy
        : (healthReports.MinBy(r => r.Status)?.Status).GetValueOrDefault();
}
```

**A resource with no health reports is `Healthy` the instant it is `Running`.**
And this AppHost declares no health checks:
`grep -rn "WithHttpHealthCheck\|WithHealthCheck" src/` matches only four
hosting-integration binaries, never a source file.

So, among the twelve:

- **`keycloak` alone has a health report** — `AddKeycloak` calls
  `WithHttpHealthCheck("/health/ready", …, "management")` internally
  (`Aspire.Hosting.Keycloak` 13.5.3-preview.1.26425.3). For keycloak,
  `WaitForResourceHealthyAsync` *is* genuinely stricter than `Running` — and the
  fixture already has a stronger, hand-rolled check for it in
  `WaitForKeycloakRealmAsync`, which polls the realm's OIDC discovery document.
- **The nine projects, `mediamtx` and `fixture-video` have none.** For them
  `HealthStatus` flips to `Healthy` in the same snapshot that publishes
  `Running`, so `WaitForResourceHealthyAsync` matches the identical transient and
  returns. **It would not have caught either provocation.**

The withdrawn plan's other three objections stand unchanged: its message names
the resource and nothing else (no state, no exit code, no log); it misses
`Terminated`; and its default `WaitBehavior` is the one we are trying to leave.

**Could it be made to work?** Only by adding `WithHttpHealthCheck("/health")` to
the nine projects in `AppHost.cs` — production composition-root code changed to
serve a test fixture, impossible for `fixture-video` (`fixture-video.yml`
declares no `api:` block, so there is nothing to poll), and *still* blind to a
resource that becomes healthy and later dies. **Rejected.**

---

## 5. Corrections carried forward from the withdrawn spec

These were measured and remain true; they are the reason the fatal-state
vocabulary survives even though its call site does not.

- **Twelve `Running` waits, not eleven**; **nineteen** awaits share the one
  `CancellationTokenSource`, not eighteen.
- **Three of the twelve are containers** — `keycloak`, `mediamtx`,
  `fixture-video` — where #2066 as filed says none is.
- **The container branch of `ResourceSnapshotBuilder` maps `ExitCode == -1` to
  `null`**, so a dead container arrives with **no exit code** and `ExitedNonZero`
  is `false` for it. **Any exit-code-gated design silently skips all three.** The
  gate must be the *state*, with the exit code as detail.
- **`Terminated` is not a `KnownResourceStates` constant.**
  `KnownResourceStates.TerminalStates` is exactly `{ Finished, FailedToStart, Exited }`.
  `Terminated` exists only as `Aspire.Hosting.Dcp.Model.ExecutableState.Terminated`,
  which is `internal`, and reaches the snapshot because
  `ResourceSnapshotBuilder.ToSnapshot(Executable, …)` copies the state text
  verbatim. It can only ever be a string literal.
- **The two vocabularies are disjoint**: `Finished` is not a container state,
  `Exited` is not an executable state. Both words are needed.
- **`Unknown` is deliberately excluded — for the fail-open asymmetry, not for an
  invented exit code.** The withdrawn wording said reading `Unknown` as death
  "would invent" an exit code. It would not: `FormatResourceDeathMessage` takes
  `int?` and renders `(no exit code recorded)` exactly so that a death with no
  code is reportable, which is the container case this spec insists on
  elsewhere. The sound reason is the one the implementation's own doc gives: a
  false positive fails every integration run on every machine, a false negative
  leaves the behaviour that existed before this check, and `Unknown` — "no
  longer tracked" — has never been observed during a startup here. The
  asymmetry decides the default. What that choice costs is recorded as
  `plan.md` limit 7, not hidden in this bullet.

---

## 6. User Scenarios & Testing

### User Story 1 — the engineer whose green suite was lying (P1)

**As** an engineer reading an `integration tests (Docker)` job,
**I want** the fixture to refuse to hand the tests a stack in which a resource it
waited for is dead, and to say which one and how,
**so that** a dead service produces a named failure instead of a green run or an
unattributable `IOException` in an unrelated assertion.

This is the whole slice: one array, one loop, one predicate, one formatter, one
throw.

#### Acceptance scenarios (Gherkin)

**The observed defect — a service that reaches `Running` and then dies**

```gherkin
Scenario: a service crashes after its wait was satisfied
  Given the AppHost is booting in E2ETests mode
  And "camera-catalog" reaches Running and then exits during startup
  And every wait in InitializeAsync therefore returns
  When InitializeAsync reaches the end of its startup sequence
  Then it throws before returning
  And the exception names "camera-catalog"
  And the exception names the state it reached
  And the exception carries "camera-catalog"'s own log
  And the exception is not a TimeoutException or an OperationCanceledException
```

**The worse half of the same defect — a suite that passes**

```gherkin
Scenario: a dead resource no test happens to touch
  Given "identity" reaches Running and then exits during startup
  And the tests selected for this run do not call identity
  When InitializeAsync reaches the end of its startup sequence
  Then it throws naming "identity"
  And no test in the collection reports a pass
```

**A death with no exit code still fires — the container case**

```gherkin
Scenario: a container is dead and Aspire recorded no exit code
  Given "mediamtx" is in state "Exited" with ExitCode null at the end of startup
  When the liveness assertion reads its snapshot
  Then the fixture throws naming "mediamtx" and the state "Exited"
  And the message says no exit code was recorded
  And the message does not claim a non-zero exit
```

**More than one death is reported, not just the first**

```gherkin
Scenario: two resources died
  Given "camera-catalog" and "identity" are both in a fatal state
  When the liveness assertion runs
  Then the exception names both
  And each carries its own state and its own log
```

**Fail-open — an unobserved state must not be read as a death**

```gherkin
Scenario: "Unknown" is not a death
  Given a resource's snapshot reports state "Unknown"
  When the liveness assertion evaluates it
  Then it does not match
  And the boot proceeds
```

**`migrations` is excluded, because `Finished` is how it succeeds**

```gherkin
Scenario: a successful migration runner is not read as a death
  Given "migrations" is in state "Finished" with exit code 0
  When the liveness assertion runs
  Then "migrations" is not among the resources it inspects
  And no exception is thrown
```

**The regression to avoid — the healthy path is untouched**

```gherkin
Scenario: a boot where every resource is alive behaves exactly as before
  Given the AppHost is booting in E2ETests mode
  And all twelve waited-for resources are Running at the end of startup
  When InitializeAsync runs
  Then no exception is thrown
  And the whole integration suite passes
  And the added cost is twelve in-memory state reads
```

---

## 7. Functional Requirements

- **FR-001** — `InitializeAsync` MUST NOT return successfully while any resource
  it waited for is in a fatal state. The check runs **after** the twelve waits
  and after the three HTTP pollers, at the last point before the `HttpClient`
  properties are assigned.
- **FR-002** — The fatal set is exactly
  `{ Finished, Exited, FailedToStart, RuntimeUnhealthy, Terminated }`, compared
  `OrdinalIgnoreCase`. `Unknown`, `NotStarted`, `Running` and every intermediate
  state MUST NOT match. The one state with no `KnownResourceStates` constant —
  `Terminated` — MUST carry a comment saying so and naming both
  `KnownResourceStates.TerminalStates` and
  `Aspire.Hosting.Dcp.Model.ExecutableState`.
- **FR-003** — The inspected set is the **twelve waited-for resources**.
  `migrations` is excluded and MUST carry a comment saying why: `Finished` is how
  it succeeds, and its own gate at `AspireFixture.cs:184` already read its exit
  code (#2064).
- **FR-004** — The state MUST be read with
  `ResourceNotifications.TryGetCurrentState`, synchronously. A `false` return
  MUST be treated as benign and commented as defensive: at this point every one
  of the twelve has published a snapshot, because its wait matched one.
- **FR-005** — The check MUST NOT judge the exit code. The **state** is the gate;
  the exit code is reported as detail. `ExitedNonZero` is not consulted here, and
  its single copy of the `is null or 0` rule MUST NOT be restated.
- **FR-006** — The exception MUST be `InvalidOperationException` — not
  `TimeoutException`, not any `OperationCanceledException` subtype, or the
  `catch` at `AspireFixture.cs:277` reclassifies it as "did not start within 8
  minutes".
- **FR-007** — For each dead resource the message MUST contain, in this order:
  the resource name; the state it reached; the exit code, **or an explicit
  statement that none was recorded**; a sentence saying the fixture stopped
  rather than reporting a healthy boot over a dead resource; then that
  resource's own log. It MUST NOT print the forty-five-resource state list.
- **FR-008** — **Every** dead resource is reported, not the first one found.
- **FR-009** — Logs MUST be fetched only for resources found dead, so a healthy
  boot pays nothing (`CaptureOneResourceLogAsync`, bounded 5 s, reused).
- **FR-010** — The twelve `WaitForResourceAsync` calls, their order and their
  comments MUST be left exactly as they are. This slice adds a check; it does not
  rewrite the waits.

## Non-functional

- **NFR-001** — No new package. `ilspycmd` is a diagnostic, not a dependency.
- **NFR-002** — `IsFatalStartupState` and `FormatResourceDeathMessage` MUST be
  `internal static` and pure, so the `Category=FixtureLogic` Docker-free suite
  (`ci.yml:72`) exercises every state without a boot.
- **NFR-003** — Cost on a healthy boot MUST be twelve `TryGetCurrentState`
  reads — each a scan of the notification table rather than a keyed lookup, §2.
  No `WatchAsync`, no delay, no HTTP.

## Out of scope

- **Mechanism A** — the per-wait fatal-state predicate. §8.
- **Mechanism C** — the watchdog, and the shared `CancellationTokenSource`.
  **#2146.**
- **A resource nothing waits on.** `rabbitmq`, `postgres`, `mosquitto`, `minio`
  have no wait; if one dies the nine services sit in `Waiting`, the fixture
  blocks on the `camera-catalog` wait, and the 8-minute budget is spent before
  this check is ever reached. **#2146.**
- **A death after `InitializeAsync` returns.** A boot-time assertion cannot cover
  the test run; nothing in this family can.
- **Widening the `migrations` wait** to `FailedToStart`. Real (it hangs the boot
  for 8 minutes) but unobserved, and it is mechanism A. §8.
- The three HTTP pollers, and Linux.

## Latency budget impact

**N/A.** Test-fixture startup only. No leg of the event-to-overlay path is
touched (constitution §IV).

---

## 8. What this deliberately does not ship, and why

**Mechanism A is deferred to #2146's ADR, not filed as its own issue.** Three
reasons, in order of weight:

1. Its only confirmed family — #2062's nine `FailedToStart` services — is
   already stopped at 1 m 38 s by #2064. Its residual family (a container dead
   before its wait subscribes; `migrations` reaching `FailedToStart`) has never
   been observed.
2. Evidencing it costs Docker boots that the disproved provocations cannot buy.
   The `Program.cs` scratch line provokes a death *after* `Running`, which is
   precisely the case A misses. A needs a different provocation — a broken
   `mediamtx.yml`, or a migration runner pointed at an unreachable database —
   on a machine with ~16 GB free on `C:`.
3. **#2146's ADR must decide A's fate anyway.** Its own question 2 is *"what
   happens to the per-wait predicates #2066 ships — do they stay as a fast path,
   or does the watchdog subsume them?"* Shipping A now so that an ADR can
   consider removing it is churn.

**#2146 should be updated** with §3's corrected coverage table and with the fact
that the fixture can pass over a corpse — its current body inherits the false
premise, describing #2066 as covering "the family that has actually been
observed".

---

## 9. Independent end-to-end test procedure

Not the committed tests — those cannot answer it. Deleting the new call site
leaves the `Category=FixtureLogic` suite green, exactly as 062 recorded for its
own change.

**The red is already in hand and costs no boot.** Phase 4a's two provocations are
the failing observation:

- `camera-catalog` dead → `InitializeAsync` completes; `Polly.Timeout.TimeoutRejectedException`
  → `TaskCanceledException` → `IOException` at `00:02:51.92` in the test body.
- `identity` dead → **the filtered test passes, twice.**

Those verbatim blocks are the phase-4 red evidence for the PR body. If the
phase-4a agent did not retain them, one boot re-provokes the `identity` case,
which is the more damning of the two.

**Green, three boots:**

1. **`identity` provocation, post-change.** Scratch line in
   `src/Identity/Api/Program.cs`:

   ```csharp
   // SCRATCH (#2066 phase 5) — provoke a startup death. REVERT.
   if (Environment.GetEnvironmentVariable("PATH") is not null)
   {
       throw new InvalidOperationException("SCRATCH: deliberate identity failure.");
   }
   ```

   **`PATH`, not `args.Length >= 0`** — the latter trips `S3981` under
   `-c Release` with `TreatWarningsAsErrors` (062 paid for this twice).

   ```sh
   dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj -c Release \
     --filter "FullyQualifiedName~A_camera_registration_reaches_the_camera_catalog_log_tail"
   ```

   Expect `InvalidOperationException` naming **`identity`**, its state, its exit
   code (or "no exit code recorded"), and its log — where before the change this
   same filter **passed**.

2. ~~**`camera-catalog` provocation, post-change.**~~ **Withdrawn** — see
   `tasks.md` T010 and `verification.md` §5. It would have moved the scratch line
   to `src/CameraCatalog/Api/Program.cs` to show the check is not hard-wired to
   one resource; the sweep has no name-dependent branch, and phase 4a's
   pre-change camera-catalog provocation already observed a dead camera-catalog
   reaching this point. A boot is not spent on it.

3. **Revert the scratch line. One full clean run**, `-c Release`, green, with
   **CI's filter** — `Category!=Measurement&Category!=Disruptive&Category!=Maintenance`,
   **not** "no filter": `RunModeIngestAttributionTests` is `Category=Measurement`
   and needs `SSE_RUNMODE_*`, so an unfiltered run is not green on this machine
   and never was. This is the healthy-path evidence and the discharge for "twelve
   surfaces at once".

Read elapsed time, where it is quoted at all, from the **`[xUnit.net HH:MM:SS.ss]`
marker on the `[FAIL]` line** — **never `Duration:`**, which read `8 ms` for a
nine-minute boot in 062.

Record it in `verification.md` with the verbatim blocks.

**Docker budget:** `C:` ~16 GB. Three E2E-mode boots at ~0.25 GB each. **Do not
boot below 8 GB free; abort below 5 GB.** Stop the AppHost process directly.

---

## 10. Cost — stated without a number, deliberately

The withdrawn spec projected **"~8 m 50 s → ~2 m, ≈ 6–7 minutes per occurrence"**.
**That figure is withdrawn.** It was arithmetic over a mechanism that does not
fire, and no occurrence of the modelled failure was ever reproduced.

This change saves no time. A boot with a dead resource takes exactly as long as
it does today (~2 m 50 s in both provocations) and then fails. **What it changes
is the verdict**, and the verdict is currently wrong in two ways:

- a **false pass** — the `identity` run, green with a dead service;
- a **misattributed failure** — the `camera-catalog` run, a Polly timeout inside
  an assertion about camera registration, naming no resource, with none of
  #2061's, #2038's or #2064's reporting reached.

A false pass in CI is the worst outcome this repository's test apparatus can
produce, and it is the only one none of the four preceding issues addressed.

---

## 11. What is not known, and is not guessed

- **Whether DCP ever restarts a crashed project resource.** If it did, the
  restarted resource would read `Running` or `Starting` at the sample and the
  check would miss it. `FormatLikelyCause`'s existing comment contemplates a
  resource that "crashed and restarted inside the watch window". Both phase-4a
  provocations stayed dead — the test body got connection failures ~1 minute
  after the death — so no restart was observed here. Marked as a guess.
- **Whether `Terminated` or `RuntimeUnhealthy` occur during startup.** Both are
  included on decompile evidence, not observation. `RuntimeUnhealthy` needs the
  Docker engine killed mid-boot, which on this machine is a documented way to
  lose the engine entirely.
- **Linux.** 062's observations are Windows / Aspire 13.5.3, and CI's
  `(no logs captured)` emptiness (#2061) is still unexplained.

---

## What the board should say

**#2066 is re-scoped in place, not closed and re-filed.** It is one defect —
*the fixture does not tell you what died* — whose cause turned out to be bigger
than the mechanism first proposed. Splitting it would leave a gate whose own
cited scenario walks past it, which is the failure mode ADR-0037's phase gates
exist to prevent.

Suggested title: **"The fixture reports a healthy boot with a dead service"**.

**No new issue is needed.** The remainder already has a number: **#2146**, which
should absorb §3's coverage table and mechanism A.

*This spec does not touch the board.*

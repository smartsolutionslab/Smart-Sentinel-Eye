# Implementation Plan: A boot that does not pass over a corpse

**Feature**: 084 | **Issue**: #2066 (re-scoped) | **Spec**: `./spec.md`
**Branch**: `fix/2066-a-wait-asks-how-it-finished`
**Re-planned**: 2026-09-07, after phase 4a disproved the previous plan's mechanism.

---

## Bounded context and layers

**None.** This is the integration-test fixture (ADR-0103, ADR-0068). No domain,
no application, no infrastructure, no contract, no message, no migration. The
NetArchTest boundary rules are untouched because nothing crosses a boundary.

The only entities involved are Aspire's, read and never modified:
`ResourceEvent`, `CustomResourceSnapshot` (`State.Text`, `ExitCode`),
`ResourceNotificationService.TryGetCurrentState(string, out ResourceEvent?)`,
`ResourceLoggerService`.

---

## What changed from the withdrawn plan

The withdrawn plan put the fatal-state test **inside each of the twelve wait
predicates**. Phase 4a proved the predicate never gets the chance: `Running` is
published when the process launches, the disjunction matches it, and the wait
returns before the death exists to be seen.

**The vocabulary was right; the call site was wrong.** `IsFatalStartupState` and
`FormatResourceDeathMessage` survive unchanged in shape. They move from twelve
predicates — where `Running` shadows them — to **one sweep after the waits**,
where nothing does.

---

## The shape of the change

Three new fields, two new pure functions, one new private method, one new call —
all in `tests/Integration.Tests/Fixtures/AspireFixture.cs`, nothing removed and
no existing line reordered.

**242 lines added, of which 69 are code.** That figure is measured off the diff,
not estimated: an earlier draft of this plan said "roughly 45 lines", which was
wrong before a line was written and is the same species of defect as the
invented figure `develop`'s tip corrects for 062. The rest is XML documentation,
which is where this change keeps its reasoning.

### 1. The list of what was waited for

```csharp
// The twelve resources InitializeAsync waits for. `migrations` is deliberately
// absent: `Finished` is how it succeeds, and its own gate above already read its
// exit code (#2064).
private static readonly string[] GatedResources =
[
    "keycloak", "camera-catalog", "mediamtx", "fixture-video",
    "stream-distribution", "layout-composition", "overlay-designer",
    "audit-observability", "event-ingestion", "system-variables",
    "automation", "identity",
];
```

Declared with an explicit type and a collection expression, per house rules.

**One list, two obligations, and they must not drift.** Adding a thirteenth wait
without adding its name here silently narrows the check. Nothing enforces that —
say so in the comment rather than pretending otherwise, the way
`TailedResources` above it already does.

### 2. `IsFatalStartupState(string? stateText) -> bool` — pure, `internal static`

Unchanged from the withdrawn plan, and the reason its tests survive.

- `true` for `Finished`, `Exited`, `FailedToStart`, `RuntimeUnhealthy`,
  `"Terminated"`, compared `OrdinalIgnoreCase`.
- `false` for `null`, `Running`, `Unknown`, `NotStarted`, `Waiting`, `Starting`,
  `Stopping`, `Pending`, `Building`, `Paused`, `Hidden`, and anything
  unrecognised.

Four of the five are `KnownResourceStates` constants. The fifth is a string
literal and carries the comment saying why: `KnownResourceStates` has no
`Terminated`, its `TerminalStates` is `{ Finished, FailedToStart, Exited }`, and
`Aspire.Hosting.Dcp.Model.ExecutableState.Terminated` is `internal` and cannot be
referenced. **Do not paraphrase this into "Aspire is missing a constant"** — name
the two types, so the next reader can check it in one `ilspycmd` run.

The citation for four of the five is Aspire's own
`ResourceNotificationService.IsContinuableState(StopOnResourceUnavailable, …)`:
`Running | Finished | Exited | FailedToStart | RuntimeUnhealthy`. We take that set
and add the one it cannot express.

**Fail-open is deliberate.** Everything unrecognised is benign. A false positive
here fails every integration run on every machine; a false negative leaves
today's behaviour. The asymmetry decides the default.

### 3. `FormatResourceDeathMessage(string, string, int?, string) -> string` — pure

`int?`, **not** `int`. This is the one place the design cannot reuse
`FormatMigrationFailureMessage`, whose signature is `int` precisely because #2064
had already proven the code non-null at its only call site. Here the container
branch of `ResourceSnapshotBuilder` maps `ExitCode == -1` to `null`, so a dead
container legitimately arrives with no code. Rendering `null` through an
`int`-shaped formatter yields `"exited with code  — …"`; rendering it as `0` would
claim success.

Wording mirrors `FormatLikelyCause` and `FormatMigrationFailureMessage` so a
reader who has seen either recognises this one — **with one sentence changed from
the withdrawn plan**, because the claim it makes has changed:

```
camera-catalog reached FailedToStart with exit code 134 — a resource that ends
during startup is a failure, not a start.
The fixture stopped here rather than reporting a healthy boot over a dead
resource.
camera-catalog log:
<log>
```

and, with no code:

```
mediamtx reached Exited (no exit code recorded) — …
```

The withdrawn sentence — *"The startup wait stopped here rather than spending the
remaining budget"* — is now false: this check spends the whole budget's worth of
boot and then refuses to return. Saying otherwise would be the second invented
claim in this spec's history.

**No forty-five-line state list.** The resources that are fine say nothing about
the failure; that is the noise #2061 removed.

### 4. `ThrowIfAnyGatedResourceDiedAsync()` — private, and it takes no token

```
for each name in GatedResources:
    if TryGetCurrentState(name, out current) and IsFatalStartupState(current.Snapshot.State?.Text):
        record (name, state, exit code)
if nothing recorded: return
for each recorded: CaptureOneResourceLogAsync(...)
throw new InvalidOperationException(join of FormatResourceDeathMessage(...))
```

- **`TryGetCurrentState`, not `WatchAsync`.** It is synchronous, returns
  `ResourceNotificationState.LastSnapshot` (13.5.3 decompile), and is already the
  fixture's way of reading one resource's current snapshot
  (`TryResolveResourceId`). `CaptureResourceStateMapAsync` would also work and is
  already written — but it costs a bounded 3-second watch on **every** boot,
  where this costs twelve in-memory state reads. **Not twelve *dictionary
  lookups***, as the first draft of this plan said: the parameter is a resource
  *id*, so an app-model name misses the keyed fast path and falls through to a
  scan of the forty-odd tracked resources. Twelve scans, microseconds against
  three seconds — the cost argument survives, the word does not. Prefer the
  cheaper one; a healthy boot must not pay for a check that almost never fires.
- **A `false` return is benign**, and commented as defensive rather than
  reachable: every one of the twelve has published a snapshot by now, because its
  own wait matched one. This mirrors the existing defensive branch in
  `CaptureOneResourceLogAsync` and its comment.
- **All of them, not the first.** Two resources can die on one boot — in #2062
  nine did. Reporting only the first would reintroduce the misattribution the
  family exists to remove.
- **Logs only on the failing branch** (FR-009), reusing `CaptureOneResourceLogAsync`
  (bounded 5 s each) — the #2064 pattern. Twelve of those bound the failing path
  at **60 seconds**, an aggregate worth stating even though it is paid only once
  something is already known to be dead.
- **No `CancellationToken` parameter** — a deliberate departure from this plan's
  first draft, which specified one. ADR-0049's rule is *"every **public** async
  method"*, and this is `private`; its siblings `CaptureFailedResourceLogsAsync`
  and `CaptureOneResourceLogAsync` take none for the same reason. The stronger
  argument is what threading the only token in scope would do. That token is
  `cts.Token`, the eight-minute startup budget: a boot that spent 7 m 58 s and
  then found a dead resource would surface an `OperationCanceledException` from
  here, meet the `when` clause in `InitializeAsync`, and be reported as *"did not
  start within 8 minutes"* — **precisely the misattribution FR-006 exists to
  prevent**. The token would not merely be dead weight; it would reopen the
  defect this change closes.

### 5. The call site

One line at the end of the `try` block in `InitializeAsync`, after
`WaitForServiceHealthAsync("overlay-designer")`:

```csharp
// Every wait returned. That is not the same as every resource being alive: DCP
// publishes `Running` when a process launches, so a service that crashes a second
// later has already satisfied its wait. Phase 4a of #2066 provoked exactly that
// twice — camera-catalog dead and InitializeAsync completed normally; identity
// dead and the filtered test passed. Sampled here, at the last instant of the
// boot, because a resource that dies later is #2146's problem, not this one's.
await ThrowIfAnyGatedResourceDiedAsync().ConfigureAwait(false);
```

**Inside the `try`, and that is not free.** The
`catch (… OperationCanceledException or TaskCanceledException)` in
`InitializeAsync` filters on those two types, and `InvalidOperationException` is
neither — so it escapes uncaught, exactly as #2064's migration throw does two
hundred lines above.

The placement is right, but **not for the reason this plan first gave**.
*"Placed after the `catch` it would lose the `cts.Token`"* is vacuous now the
method takes no token (§4). It belongs inside the `try` because it reads as the
last step of the boot, which is what it is, and because the migrations gate
throws the same type from the same block — one place to look for a startup
verdict, not two.

---

## The two alternatives, and why they are still rejected

### `WaitForResourceHealthyAsync(name, WaitBehavior.StopOnResourceUnavailable)`

**Reconsidered in light of the disproof, and the rejection survives — but the
withdrawn plan's stated reason was wrong, and that matters.**

It claimed the method "additionally waits for `HealthStatus.Healthy`", making it
strictly stronger than `Running`. `CustomResourceSnapshot.ComputeHealthStatus`
(13.5.3) says otherwise: with **no** health reports, `HealthStatus` is `Healthy`
the instant the state is `Running`. This AppHost declares no health check on any
project or `AddContainer` resource, so for eleven of the twelve the two waits
match the same snapshot. **It would not have caught either provocation.**

`keycloak` is the exception — `AddKeycloak` registers
`WithHttpHealthCheck("/health/ready")` internally — and for keycloak the fixture
already has something stronger: `WaitForKeycloakRealmAsync`, which polls the
realm's OIDC discovery document rather than a liveness endpoint.

The remaining objections stand: its exception names the resource and nothing else
(no state, no exit code, no log, and says "failed to start" for a service that ran
a minute and then exited); it misses `Terminated`; and its default `WaitBehavior`
is the one we are trying to leave, so the fix would depend on remembering an
argument.

**Making it work by adding `WithHttpHealthCheck` to the nine projects** is
rejected on three counts: it edits the production composition root to serve a
test fixture; it cannot cover `fixture-video`, whose `fixture-video.yml` declares
no `api:` block; and even then a resource that becomes healthy and later dies
still passes.

### A `CancellationTokenSource` per wait

Unchanged from the withdrawn plan, and now doubly wrong. It does not improve
attribution — it converts one 8-minute `TimeoutException` naming the blocked
resource into a shorter one naming the same blocked resource — it creates a
budget-allocation problem the fixture does not have, and it does nothing at all
for the failure phase 4a observed, where **no wait ever blocks**. Not now, not as
a follow-up. See #2146.

---

## Boundary and convention rules that apply

- **`Ensure.That`** (ADR-0105) — the new private method takes no arguments at
  all (§4); the names come from a `static readonly` array. A guard here is
  speculative generality. **No guard**, consistent with
  `CaptureOneResourceLogAsync` and `TryResolveResourceId`.
- **`ExitedNonZero` is not consulted** (FR-005). The **state** is the gate. Its
  single copy of `is null or 0` stays untouched and must not be restated —
  #2061's review blocker was a mutant that dropped it.
- **`OrdinalIgnoreCase`** everywhere state text is compared, matching
  `Aspire.StringComparers.ResourceState`, which is `internal` and unborrowable.
- **`Option<T>`** (ADR-0141) — Domain and Application only. This is a test
  project; `string?` and `int?` are correct here.
- **Collection expressions** — `GatedResources` is `private static readonly
  string[] … = [...]`, not `new[]`. `dotnet_style_prefer_collection_expression`
  is at `warning` and fails the Release build.
- **`var`** — allowed either way; match the file, which spells types out.
- **Deconstruction rule** — no handlers here. N/A.
- **`S104` / `S138` / `S1541`** are `NoWarn`'d for test projects
  (`Directory.Build.props:108`), so file length is not a gate. The change is
  242 lines net positive (69 of them code), where the withdrawn plan was net
  negative — worth saying, since "fewer lines" was one of its stated benefits
  and is no longer one.

## Messaging

None. No domain event, no integration event, no `Shared.Contracts` change.

---

## The honest limits

1. **Nothing committed can prove the fixture behaves.** Delete the one call to
   `ThrowIfAnyGatedResourceDiedAsync` and the `Category=FixtureLogic` suite stays
   green — the same limit 062 recorded, and the reason the runtime provocation in
   `spec.md` §9 is the gate rather than an afterthought. Note this limit is now
   **sharper**, not softer: the withdrawn design had twelve call sites to delete,
   this has one.
2. **A source-scanning guard is not an acceptable substitute.** #2054 was a green
   guard sitting over a diagnostic that had never once worked, and the memory note
   *"Guards that read the design artefact"* says why: such a guard proves the
   design was written down, not that it holds. If a later issue wants
   `GatedResources` kept in step with the waits, that is a runtime observation or
   nothing.
3. **The check is a sample, not a watch.** A resource that dies after the sample —
   during the test run — is invisible to it, and always will be. #2146.
4. **A resource nothing waits on is out of reach.** If `rabbitmq` dies, the nine
   services sit in `Waiting`, the fixture blocks on the `camera-catalog` wait, and
   the sample is never reached. #2146 is the only mechanism that covers this.
5. **`Terminated` and `RuntimeUnhealthy` will not be observed.** Included on
   decompile evidence alone. Provoking `RuntimeUnhealthy` means killing the Docker
   engine mid-boot, a documented way to lose the engine entirely on this machine.
   Covered by unit test over `IsFatalStartupState` only.
6. **Linux is unaddressed**, exactly as #2066 states.
7. **A container that reaches `Unknown` or `Stopping` is not caught.** `Unknown`
   is fail-open **by choice** (`spec.md` §5, and the predicate's own doc): a
   false positive fails every integration run on every machine. But it is a real
   DCP container state, and **three of the twelve are containers** — `keycloak`,
   `mediamtx`, `fixture-video` — **none of which was provoked**. So a dead
   `mediamtx` that reports `Unknown` still yields a healthy-boot verdict. Said
   plainly rather than buried in the exclusion's rationale.
8. **`api-gateway` reaches the sample and nothing gates it.** `AppHost.cs` adds
   it (ADR-0106) and it runs single-replica under `isE2ETests`, but
   `InitializeAsync` never waits on it, so it is correctly **not** in
   `GatedResources` — sampling a resource the fixture never waited for risks
   reading "has not started yet" as death. Two tests wait on it themselves, in
   `CreateGatewayClientAsync`, with `WaitForResourceAsync("api-gateway",
   KnownResourceStates.Running, …)` — **the exact construct phase 4a disproved**:
   `GatewayRateLimitIntegrationTests` and `GatewayRoutingIntegrationTests`. A
   dead gateway satisfies their wait and then fails inside the test body with a
   socket error naming nothing. That is the original defect, still live, in the
   two tests most likely to meet it. **This is not limit 4**, which is about a
   resource *nothing* waits on; here the wait exists and is in the wrong place.
   Carried to #2146; the tests are deliberately not touched here.

---

## Risks

| Risk | Mitigation |
|---|---|
| **A false positive fails every integration run.** The blast radius is the whole suite on every machine. | `IsFatalStartupState` fails open on everything unrecognised, and the twelve are all `Running` at this point on a healthy boot by construction — each one's wait matched `Running` minutes earlier. Discharged by T009: one full clean `-c Release` run, green. |
| A crashed resource is restarted by DCP and reads `Running` at the sample | Not observed — both phase-4a provocations stayed dead. Recorded as a guess in `spec.md` §11 rather than designed around. If it happens, the symptom is the old behaviour, not a new failure. |
| `GatedResources` drifts from the waits when a thirteenth is added | Stated in the comment; no guard, because a source-scanning guard would prove only that the list was written (limit 2). |
| The new exception is swallowed by the existing `catch` | FR-006. `InvalidOperationException` is neither `OperationCanceledException` nor `TaskCanceledException`, so the `when` clause in `InitializeAsync` does not fire. Verified by observing the type in the phase-5 provocation (`verification.md` §3). |
| The scratch line fails the Release build | `Environment.GetEnvironmentVariable("PATH") is not null`, never `args.Length >= 0` (`S3981`). 062 paid for this twice. |
| A scratch line is committed | T010 reverts it and `git status` under `src/` is checked before the PR. |
| Disk / Docker | Three E2E-mode boots at ~0.25 GB. `C:` ~16 GB. Check free space before each; **do not boot below 8 GB, abort below 5 GB.** Stop the AppHost process directly, not the container set. |

# Spec 093 — Plan

**Phase:** 2 (Plan) — ADR-0037
**Spec:** `spec.md` · **Issue:** #2129
**Branch:** `fix/2129-a-retried-register-applies-once`

---

## Bounded context and layers

**None, and that is the shape of the work.**

No bounded context is touched. Nothing under `src/` changes behaviour. There is
no aggregate, no value object, no invariant, no command, no query, no domain
event and no integration event in this spec — so the usual entity/VO and
messaging sections below say so explicitly rather than being omitted, because an
omitted section reads as an oversight.

| Layer | Change |
|---|---|
| Domain | none |
| Application | none |
| Infrastructure | none |
| Api | none |
| `Shared.Kernel` / `Shared.Contracts` | none |
| Test infrastructure (`tests/Integration.Tests/Fixtures`) | the change |
| Architecture guard (`tests/Architecture.Tests`) | the drift protection |

### Entities, value objects, invariants

None introduced. The one invariant this spec establishes is a property of a DI
registration, not of a model: **every `HttpClient` the integration fixture hands
out retries only methods RFC 9110 §9.2.2 calls idempotent.** It is asserted
behaviourally (T003) and defended textually (T005), in that order of authority.

### Messaging — domain event → integration event

None. No message is published, consumed, renamed or versioned. Wolverine,
RabbitMQ and the outbox are untouched.

### Boundary rules

`BoundaryTests` and `NetArchTest` are unaffected — no cross-context reference is
added or removed, and no bounded context gains a dependency.

The one reference question this plan does answer: **`Integration.Tests` can
already see `SmartSentinelEye.ServiceDefaults`**, transitively through
`Identity.Infrastructure` (`src/Identity/Infrastructure/*.csproj:8`), and four
integration test files already `using SmartSentinelEye.ServiceDefaults.*`
(`EventIngestion/OutboxBacklogIsVisibleTests.cs`,
`EventIngestion/WebhookIntegrationConcurrencyIntegrationTests.cs`,
`LayoutComposition/AggregateVersionConflictIntegrationTests.cs`,
`ServiceDefaults/PostgresConnectionBudgetIntegrationTests.cs`). **No `csproj`
is edited.** If a task finds itself adding a `ProjectReference` or a
`PackageReference`, the design has drifted — stop and re-read this paragraph.

---

## The change, in three moves

### Move 1 — make the fixture's client defaults nameable (behaviour-preserving)

Today the registration is an anonymous lambda inside a 1 200-line fixture:

```
tests/Integration.Tests/Fixtures/AspireFixture.cs:213
    builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
```

Nothing outside `InitializeAsync` can reach it, and `InitializeAsync` boots the
whole Aspire stack. **That is the sole reason this defect has no cheap test**:
the object under discussion cannot be constructed without Docker.

Extract it to a small named type in the same folder:

```
tests/Integration.Tests/Fixtures/FixtureHttpClients.cs

    internal static class FixtureHttpClients
    {
        internal static void Configure(IHttpClientBuilder http) => …
    }
```

and the fixture becomes

```csharp
builder.Services.ConfigureHttpClientDefaults(FixtureHttpClients.Configure);
```

**`void`, so a method group suffices.** All three
`AddStandardResilienceHandler` overloads in
`Microsoft.Extensions.Http.Resilience` 10.9.0 document their return as *"the
value of `builder`"*, so there is nothing useful to hand back and no reason for
the signature to change between Move 1 and Move 2.

**How the test avoids the real backoff.** The retry defaults are 3 retries on a
2 s exponential base, so observing the four-attempt `GET` case at real backoff
costs ~14 s, twice over. The test zeroes it **after** calling
`FixtureHttpClients.Configure(builder)`, with the same named-options pattern
`IdempotentRetry.RetryEveryMethod` uses:

```csharp
services.Configure<HttpStandardResilienceOptions>(
    $"{builder.Name}-standard",
    options => { options.Retry.Delay = TimeSpan.Zero; options.Retry.UseJitter = false; });
```

The later `Configure` wins — `IdempotentRetryTests.Build` relies on exactly that
ordering, and
`A_client_that_opts_back_in_retries_its_POSTs_again` passes today, so the key
and the ordering are both already demonstrated in this repository rather than
assumed. The **attempt count** is what these assertions are about, and it is the
one thing the backoff does not change. If the key turns out not to bind, report
it — do not invent a second route.

Move 1 changes **no behaviour**: the body is the same bare
`AddStandardResilienceHandler()` it is today. That is deliberate — see
§"Sequencing" below.

### Move 2 — apply ADR-0143 (behaviour-changing)

The body of `FixtureHttpClients.Configure` becomes, verbatim, what
`src/ServiceDefaults/Extensions.cs:52` does:

```csharp
http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);
```

**Reuse, not reimplementation.** `IdempotentRetry.RetryIdempotentMethodsOnly` is
a `public static void` taking `HttpStandardResilienceOptions` and is already the
whole of ADR-0143. Writing a second predicate here would create two spellings of
one decision that could drift apart silently — the exact failure mode CLAUDE.md
records for §II and §IV. A new predicate is a review blocker.

The call site carries a short comment saying **why the fixture needs the same
narrowing as a service host**: it does not call `AddServiceDefaults`, so nothing
else gives it one. That is a *why* comment, which the Karpathy rules permit; do
not narrate *what* the call does.

### Move 3 — guard the tree the original fix did not reach (drift protection)

`tests/Architecture.Tests/ResilienceRegistrationTests.cs` already owns this
subject and already reads source from disk. It reads `src` only. Add one fact
that reads `tests/Integration.Tests` and asserts: **every file containing
`AddStandardResilienceHandler(` also contains
`IdempotentRetry.RetryIdempotentMethodsOnly`.**

Three properties this fact must have, each learned the hard way in this repo:

- **It must fail if the scan finds nothing.** A source-scanning guard that
  matches an empty population passes, and a passing guard that checks nothing is
  indistinguishable from one that holds. Assert at least one registration was
  found before asserting they are all narrowed —
  `IntegrationTestSelectionTests` does exactly this and says why.
- **It must be shown to discriminate.** A second fact runs the same predicate
  over a synthetic source that registers the bare handler and asserts it is
  flagged. Otherwise the guard's claim rests on its own comment. (Memory:
  *"Prove a guard by counterfactual."*)
- **It must normalise path separators.** `Path.GetRelativePath` returns the
  platform separator; a backslash literal is green on Windows and red on Linux
  CI. `ReadSources()` already does `.Replace('\\', '/')` — the new reader must
  too, and the new reader is a *parameterisation of the existing one on its root
  directory*, not a copy of it.

Do **not** broaden the existing
`The_standard_resilience_handler_is_called_exactly_once_across_src` fact to
include `tests`. It asserts a single call site, and after this change there are
legitimately two — one per tree, for different reasons. Broadening it would make
a correct state fail.

---

## Sequencing, and why Move 1 comes first as its own commit

Constitution §Testing carries two obligations and this spec triggers both.
Splitting them across commits is what keeps each honest, and ADR-0087 requires
each commit to build on its own in any case.

1. **Move 1 (behaviour-preserving)** lands first, alone. The extraction must not
   change what the fixture does. Its covering evidence is the Docker integration
   selection, which exercises every fixture-built client — but that is a
   thirty-minute job, so in practice the extraction is verified by T003's red
   *reporting the old number*: if the extraction had changed the behaviour, the
   red would not say 4.
2. **T003 (the red)** lands next, against the extracted-but-unchanged shape. It
   compiles, runs, and **fails with 4 attempts against an expected 1**. That
   verbatim output is the phase-4 evidence quoted in the PR (ADR-0139).
3. **Move 2 (behaviour-changing)** turns it green.
4. **Move 3 (the guard)** lands last; it is red before Move 2 and green after,
   so writing it before Move 2 would make it a second red rather than a guard.
   Landing it after keeps the two kinds of evidence distinct.

**The red cannot come before Move 1.** A test written against a
`FixtureHttpClients` that does not exist does not fail — it does not compile,
and a non-compiling test is not a red test. This is called out because getting
it backwards is the most likely way this spec produces a fabricated gate.

---

## Test design

### The deterministic observation (T003)

`tests/Integration.Tests/Fixtures/FixtureRetryPolicyTests.cs`

- `[Trait("Category", "FixtureLogic")]`, **no `[Collection(AspireCollection.Name)]`**.
  It must not touch the fixture instance — it builds a bare `ServiceCollection`,
  so it needs no Docker and `ci.yml:72` reads its verdict in seconds. This is
  also what `IntegrationTestSelectionTests` requires of any class without the
  collection.
- Shape follows `tests/ServiceDefaults.Tests/Resilience/IdempotentRetryTests.cs`
  exactly — a `CountingHandler : HttpMessageHandler` that increments and answers
  503 (or throws `HttpRequestException`), plugged in with
  `ConfigurePrimaryHttpMessageHandler`. Hand-written fake, no Moq (ADR-0054;
  and the existing one is hand-written).
- It calls `FixtureHttpClients.Configure` on a **named client builder** rather
  than through `ConfigureHttpClientDefaults`, so `builder.Name` is a known
  string and the delay override above has a key to bind to.
- Four facts, matching `spec.md`'s first four scenarios: POST → 1, thrown POST →
  1, GET → 4, PUT → 4.

**Why this is not redundant with `IdempotentRetryTests`.** That suite proves the
*predicate* behaves; this proves the *fixture is wired to it*. The defect was
never in the predicate — it was that one caller never received it. A test of the
predicate would have been green throughout the entire period the bug existed,
which is precisely the reason it must not be the evidence here.

### The characterisation obligation

Nothing under `src/` changes, so the population that must stay green is the
existing suite. Named in `spec.md` §"Acceptance scenarios": the four facts of
`IdempotentRegistrationIntegrationTests`, plus `TokenAudienceIntegrationTests`
and `RegisteredClientConcurrencyIntegrationTests`. **They must pass unmodified.**
An assertion that has to be edited is evidence behaviour moved — block, do not
adjust.

`NFR002_MqttConnectAuthTests.cs` is **not edited**. `git diff --stat` naming it
is a review blocker.

---

## Code standards that bind here

- ADR-0084 metrics: `ResilienceRegistrationTests.cs` is ~145 lines today and
  gains ~45; the 300-line ceiling holds with room. `FixtureHttpClients.cs` is a
  handful of lines. `FixtureRetryPolicyTests.cs` mirrors a 150-line neighbour.
- ADR-0105: `Ensure.That(x).IsNotNull()` for argument guards on any new public or
  internal entry point — `FixtureHttpClients.Configure` takes an
  `IHttpClientBuilder`, so it guards it. Never `ArgumentNullException.ThrowIfNull`.
- ADR-0053: sentence-style test names with underscores.
- Private fields carry no leading underscore; collections are declared with an
  explicit type and a collection expression.
- ADR-0049: `CancellationToken` last where one is taken. None of the new code
  takes one.
- No drive-by comments. The one comment that earns its place is at the call site
  in Move 2, saying why the fixture needs the narrowing that
  `AddServiceDefaults` gives everyone else.

---

## What could go wrong

| Risk | Signal | Response |
|---|---|---|
| A `POST` somewhere in the suite was relying on the retry to survive warm-up. | The Docker integration selection turns red on a `POST` that used to pass. | Fix by **waiting on the resource**, not by restoring the retry. `RetryEveryMethod()` on the fixture is the one answer that is forbidden — it reinstates the defect with a comment attached. T006. |
| The extraction (Move 1) changes behaviour by accident. | T003's red reports something other than 4 attempts. | Stop. The red's *number* is load-bearing evidence, not just its colour. |
| The guard's scan matches nothing after a folder rename. | The non-emptiness assertion fails. | It is built to fail rather than pass — that is the design. |
| Someone reads this as "the endpoint needed a key". | — | `spec.md` §"The two questions" is the correction. The endpoint has one; the caller did not use it, and the caller should not have needed to. |

---

## Explicitly not done

- No ADR is written or amended (ADR-0144 blocks it; ADR-0142 and ADR-0143
  already decide the posture).
- No gate is weakened: no test deleted, no threshold lowered, no suppression
  added, no analyzer narrowed. The change **removes** an unjustified retry and
  **adds** a guard.
- `NFR002_MqttConnectAuthTests` gains neither a retry nor an `Idempotency-Key`.
- The Keycloak/Postgres orphan is filed, not fixed (T007).
- The stack is not booted during phases 1–3.

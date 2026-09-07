# Spec 093 — A retried register applies once

**Issue:** #2129 — *POST /devices/register applies twice under load, returning
409 for a fresh Guid v7*
**Branch:** `fix/2129-a-retried-register-applies-once`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** **ADR-0143** (retry only idempotent methods — the decision this spec
finishes applying), **ADR-0142** (idempotency keys for non-idempotent POSTs —
already applied to this endpoint, and *not* the defect), ADR-0088 (the standard
resilience handler is the per-client default this narrows), ADR-0103
(integration tests run against the Aspire fixture; no Testcontainers — the
reason the fixture's HTTP client is the object under discussion), ADR-0052
(xUnit + Shouldly), ADR-0053 (sentence-style test names), ADR-0084 (code
metrics), ADR-0105 (`Ensure.That` guards), ADR-0109 (parallel markers),
ADR-0036 (smallest change), ADR-0139 + ADR-0144 (phase 4a has two colours and
no exemption).

**Constitution:** §Testing — new behaviour is observed failing first and that
failure is quoted in the PR; a behaviour-preserving change is captured green
first and must pass unmodified. Both obligations are live here, on different
tasks, and `tasks.md` assigns them per task. §VIII (safe by default at trust
boundaries) is the section ADR-0142's replay serves; nothing in this spec
changes it.

**No new ADR is required, and none is written.** See §"The ADR verdict".

---

## The ADR verdict

**Not an ADR. Phases 1–3 proceed.**

ADR-0143 already decides the posture in as many words: *"The standard handler
retries only methods RFC 9110 §9.2.2 calls idempotent … `POST` and `PATCH` get
one attempt. A client whose non-idempotent calls are idempotent in fact opts
back in with `RetryEveryMethod()`, and says why at the call site."* ADR-0142
already decides the caller-side remedy, and it is already wired into this
endpoint.

What this spec does is apply ADR-0143 to a call site the change that introduced
it did not reach. Nothing is being decided between; a decision is being
finished. ADR-0144 blocks the autonomous lane from writing an ADR in any case,
and this spec does not need the exemption.

---

## The two questions, answered from source

The brief says these decide the shape. They do — and both answers differ from
the two branches the issue anticipates.

### 1. Does the fixture's HTTP client carry `RetryEveryMethod()`?

**No — and the plain "no" is the wrong answer to stop at.**

`tests/Integration.Tests/Fixtures/AspireFixture.cs:213`:

```csharp
builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
```

Every client the integration suite uses comes from
`App.CreateHttpClient(resourceName, "http")`
(`tests/Integration.Tests/Fixtures/AspireFixture.Auth.cs:29`), built by the
testing host's `IHttpClientFactory` — which the line above configures.

`RetryEveryMethod()` is the **opt-back-in for a client that already went
through ADR-0143's narrowing**. The narrowing lives inside `AddServiceDefaults`:

```
src/ServiceDefaults/Extensions.cs:44   builder.Services.ConfigureHttpClientDefaults(http =>
src/ServiceDefaults/Extensions.cs:52       http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);
```

**No test project calls `AddServiceDefaults`.** The fixture registers the bare
handler, so it carries the library's own predicate —
`HttpClientResiliencePredicates.IsTransient`, which looks only at the outcome
and never at the method. The observable behaviour is identical to
`RetryEveryMethod()`: a failing `POST` gets four attempts. The provenance is
not, and the difference is the whole finding — **there is no `RetryEveryMethod()`
to grep for, so a search for the opt-in returns a clean "no" on a client that
retries `POST` anyway.**

The five genuine opt-ins are all in `src` and all irrelevant here
(`EventIngestion/Infrastructure/EventIngestionInfrastructureModule.cs:128`,
`Identity/Infrastructure/IdentityInfrastructureModule.cs:146`,
`ScenarioSimulator/Program.cs:41`,
`StreamDistribution/Infrastructure/StreamDistributionInfrastructureModule.cs:104`
and `:162`).

**Why it was missed.** `tests/Architecture.Tests/ResilienceRegistrationTests.cs`
is the guard over this exact hazard, and it reads `src` only — `ReadSources()`
builds its dictionary from `Path.Combine(root.FullName, "src")`. The fixture's
registration was never in the population ADR-0143 fixed, and is not in the
population the guard defends. The line itself predates ADR-0143 by months (it
arrives in `6b2282ad`, the main→develop content import).

### 2. Does `POST /devices/register` have an idempotency key?

**Yes. It is one of the nine, and the key handling is sound.**

- `src/Identity/Api/DevicesEndpoints.cs:23` — `RegisterEndpoint` route identity.
- `:124` — `IdempotencyHeaders.TryRead(http.Request, out …)`.
- `:141` — `IdempotentRequest.ExecuteAsync(new IdempotentExecution(…))`, with
  the create branch and the replay branch both supplied.
- `src/Identity/Infrastructure/Persistence/Migrations/20260903071655_AddIdempotencyKey.cs`
  — the store's table.
- `tests/Integration.Tests/Identity/IdempotentRegistrationIntegrationTests.cs` —
  four integration tests against the real stack: a keyed repeat replays the same
  secret, a keyless repeat is still 409, a *different* key for the same device is
  still 409, a malformed key is 400.

Nine endpoints call `IdempotencyHeaders.TryRead` today (Automation rules,
CameraCatalog cameras, EventIngestion writes, Identity devices, Identity kiosks,
Identity webhook rotation, LayoutComposition layouts, OverlayDesigner overlays,
SystemVariables). **CLAUDE.md's "nine of the ten creates and rotations carry a
key" is correct and needs no correction.**

### So neither of the issue's two branches is the answer

The issue offers: *no key → give it one*, or *has a key and still
double-applied → the key handling is the defect, and that affects all nine*.

**It has a key, the key handling is sound, and it double-applied anyway —
because ADR-0142 is opt-in and the caller did not opt in.**
`NFR002_MqttConnectAuthTests.cs:137–146` builds its registration request and
sets exactly one header, `Authorization`. No `Idempotency-Key`. ADR-0142 is
explicit that no key means no change, *including the 409 a genuine duplicate has
always earned* — and `A_repeat_without_a_key_is_still_refused_as_a_duplicate`
exists specifically to hold that line.

Both mechanisms exist. Neither was engaged on this path: one because the caller
did not ask, the other because the caller was never narrowed.

---

## The mechanism, end to end

Stated in full because the diagnosis has to be checkable without a run, and
because one step of it explains the otherwise-impossible symptom.

1. `IngestThroughputMeasurementTests` drives 5 000 events/s for 30 s
   (`TargetRatePerSecond`, `DurationSeconds`) against the same stack, in the
   same `AspireCollection`, so it serialises with everything else in the suite.
   The machine, Postgres and Keycloak are saturated.
2. `NFR002_MqttConnectAuthTests.RegisterDeviceAsync` sends
   `POST /devices/register?fabId=munich` with a fresh
   `deviceIdentifier = "nfr002-{Guid.CreateVersion7():N}"`.
3. The attempt exceeds the standard handler's per-attempt timeout. The defaults
   are recorded in
   `tests/ServiceDefaults.Tests/ResilienceHandlerNestingTests.cs:70–72` —
   `MaxRetryAttempts` 3, `AttemptTimeout` 10 s, `TotalRequestTimeout` 30 s.
4. The resulting `TimeoutRejectedException` is transient, and the fixture's
   predicate never looks at the method. **The `POST` is sent again.**
5. Attempt 1 had already reached Identity. Server-side,
   `RegisterDeviceCommandHandler` ran its `GetByClientIdAsync` pre-check
   (nothing there), then called `keycloak.CreateClientAsync`. **That call is not
   transactional with the Postgres save and does not roll back** when the client
   disconnects and `RequestAborted` fires.
6. Attempt 2 re-runs the pre-check — still nothing in Postgres, because attempt
   1's `SaveAsync` never committed — and then calls Keycloak, which answers
   *already exists*. `RegisterDeviceCommandHandler` catches
   `KeycloakClientAlreadyExistsException` and returns
   `RegisterDeviceFailures.DeviceAlreadyRegistered(ex.ClientId)` → **409
   `DEVICE_ALREADY_REGISTERED`**.

Step 6 is the part worth keeping. **The collision is in Keycloak, not in our
table** — which is exactly why a brand-new Guid v7 can produce it, and why
searching `registered_clients` after a failure finds nothing to explain it. Any
account of this defect that puts the collision in Postgres is wrong.

### The residue, and why it is not in this spec

Step 5 leaves a **Keycloak client with no `RegisteredClient` row** — an orphan
credential that nothing in the system knows about, that `GET /devices` will not
list, and that a re-registration of the same device identifier would keep
colliding with. That is a real durability defect and it is **not fixed here**:
this slice stops the second attempt from being sent, which prevents new orphans,
but it does not clean up existing ones and does not make the handler
transactional across Keycloak and Postgres.

**T007 files it as a separate issue.** It is a different failure (partial
application under abort) with a different remedy (a compensating delete, or a
reserve-then-create ordering), and folding it in would make this slice neither
smallest nor independently shippable.

---

## What the issue gets wrong

Ten issues have been checked this session and seven carried a false premise.
#2129 is better-evidenced than most — four reproductions across two commits, and
the mechanism it hypothesises is the right one. Three claims still do not hold.

### 1. The reproduction command cannot run the test it names

The issue says:

```sh
dotnet test tests/Integration.Tests -c Release --filter "Category=Measurement"
```

**`NFR002_MqttConnectAuthTests` carries no `[Trait("Category", …)]`** — grep the
file; there is none, and having none is legal because
`IntegrationTestSelectionTests` accepts `[Collection(AspireCollection.Name)]` as
the alternative declaration, which NFR002 does carry.

`--filter "Category=Measurement"` selects only tests carrying that trait. Six
classes do:

```
tests/Integration.Tests/AuditObservability/ClockOffsetIntegrationTests.cs
tests/Integration.Tests/AuditObservability/NFR001_AuditIngestLatencyTests.cs
tests/Integration.Tests/AuditObservability/RunModeIngestAttributionTests.cs
tests/Integration.Tests/Automation/FirstEventSplitTests.cs
tests/Integration.Tests/Automation/FirstPublishPerTypeTests.cs
tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs
```

NFR002 is not among them. **As written, the command runs the burst and not the
test.** The observation is still real — an unfiltered local run does run both,
in one serialised collection — but the repro line must be corrected, or the next
reader concludes the defect is unreproducible.

This also means **CI has never seen this failure and cannot**: `ci.yml:179` runs
`--filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"`,
so the burst is excluded from the very job NFR002 runs in. The defect is
local-run-only today, which is why it survived to be found by hand.

### 2. "Check whether the fixture's client opts in" is the wrong question

Asked exactly as phrased, the answer is *no* — and *no* reads as exonerating.
The question that finds the defect is **"was the fixture's client ever
narrowed?"**, and it was not. Recorded because the issue's phrasing would have
closed the investigation on a true statement and a wrong conclusion.

### 3. The two branches it offers do not contain the answer

Covered above. The third branch — *has a key, sound key handling, caller engaged
neither mechanism* — is the true one, and it is the case ADR-0143 was written
for: *"this protects the ones nobody has got to"*.

### Not acted on

ADR-0143 §"The four opt-ins, and why each is justified" heads a table whose two
rows cover **five** clients (the four token clients as one row, plus
`MediaMtxRtspGateway`), and five is what the source and CLAUDE.md both say.
Noted for the record; **amending an ADR is a blocked outcome** (ADR-0144) and
nothing here touches it.

---

## Two things this must not be fixed by

### Not by retrying the test

Restated from the issue because it is right, and because a later reader will
reach for it. A test that registers a device and retries on 409 would hide the
double-apply rather than remove it. **The 409 is the symptom that something
worked twice**; suppressing it discards the only signal.

### Not by giving NFR002 an `Idempotency-Key` either

This is the subtler wrong fix, and it looks *more* legitimate than the first
because it invokes a real ADR.

Giving `NFR002_MqttConnectAuthTests` a key would make the retry harmless **on
that one call site**, and would leave every other `POST` in a ~300-test
integration suite retried by an unnarrowed handler. Worse, it would make the
suite **permanently insensitive** to the retry being reintroduced: with a key on
the noisiest caller, the next un-narrowing of the fixture would produce no 409
anywhere, and nobody would learn of it.

The key is the remedy for **callers we do not configure** — browsers, operator
tooling, third-party integrations. The fixture is a caller we own, and ADR-0143's
posture is that **retrying is the behaviour that needs justifying**. The fixture
has never justified it.

`NFR002_MqttConnectAuthTests` is therefore **not modified by this spec at all**,
and that is the cleanest evidence available that the fix is not a suppression:
the test that failed is byte-identical afterwards.

---

## The scoping trap

CLAUDE.md records that an idempotency key's scope must include the authenticated
caller, because keys are strings callers invent and `"1"` will collide.

**No key is added by this spec, so nothing new is scoped.** For the reader's
benefit, the scope already in force on this endpoint (`DevicesEndpoints.cs:141`)
is:

```csharp
IdempotencyScope.For(supplied, RegisterEndpoint, actingOperator.Value.ToString())
```

— the supplied key, **plus** the stable route identity `"POST /devices/register"`,
**plus** the acting operator's identifier. Both halves of the trap are already
closed: a key reused across endpoints does not cross-talk, and a key reused
across operators does not hand one caller another's device secret. Stated
explicitly so that "we did not add a key" is not mistaken for "we did not think
about scope".

---

## Scope

**In:** the integration fixture's HTTP-client resilience registration, and a
guard that keeps it narrowed.

**Out:** the endpoint (already correct), the nine keyed endpoints, `NFR002`
itself, the Keycloak/Postgres orphan (T007 files it), the `src` opt-ins, ADR
text.

---

## User stories

### US-1 (P1) — An integration test's `POST` is sent once

*As* the integration suite, *I* send a non-idempotent `POST` to a service that is
slow, *so that* a lost response does not become a second application of the same
effect.

Independently shippable on its own; US-2 is drift protection for it and can
follow later.

### US-2 (P2) — The fixture's registration cannot silently un-narrow

*As* a reviewer, *I* want the build to fail if a resilience handler is registered
under `tests/Integration.Tests` without ADR-0143's predicate, *so that* the next
person to add a fixture client does not reopen this by writing the obvious thing.

---

## Acceptance scenarios

### Happy path — the retry that must not happen

```gherkin
Scenario: a failing POST from a fixture client is attempted once
  Given an HttpClient built with the integration fixture's client defaults
  And a server that answers every request with 503 Service Unavailable
  When the client sends a POST
  Then the server records exactly 1 attempt
```

```gherkin
Scenario: a transport failure on a POST is not retried either
  Given an HttpClient built with the integration fixture's client defaults
  And a transport that throws HttpRequestException on every attempt
  When the client sends a POST
  Then the server records exactly 1 attempt
```

### The retries that must survive

```gherkin
Scenario: a failing GET from a fixture client is still retried
  Given an HttpClient built with the integration fixture's client defaults
  And a server that answers every request with 503 Service Unavailable
  When the client sends a GET
  Then the server records exactly 4 attempts
```

```gherkin
Scenario: a failing PUT is still retried, because PUT is idempotent
  Given an HttpClient built with the integration fixture's client defaults
  And a server that answers every request with 503 Service Unavailable
  When the client sends a PUT
  Then the server records exactly 4 attempts
```

### Conflict — the endpoint's 409 is unchanged

```gherkin
Scenario: a keyless repeat registration is still refused as a duplicate
  Given a device identifier already registered without an Idempotency-Key
  When the same registration is sent again without an Idempotency-Key
  Then the response is 409 DEVICE_ALREADY_REGISTERED
```

```gherkin
Scenario: a keyed repeat still replays the original credentials
  Given a registration sent with Idempotency-Key "k"
  When the identical registration is sent again with Idempotency-Key "k"
  Then the response is 201 Created
  And the clientSecret is the one the first attempt returned
```

### Bad request — key validation is unchanged

```gherkin
Scenario: a malformed idempotency key is refused rather than ignored
  Given an authenticated operator with sse.identity.devices.write
  When a registration is sent with Idempotency-Key "has spaces and *stars*"
  Then the response is 400 Bad Request
```

```gherkin
Scenario: an unparseable fabId is still a bad request
  When a registration is sent with fabId "not a fab"
  Then the response is 400 DEVICE_INVALID_INPUT
```

### Auth — the scope gate is unchanged

```gherkin
Scenario: a caller without the write scope is forbidden
  Given a token that authenticates but lacks sse.identity.devices.write
  When a registration is sent
  Then the response is 403 Forbidden
  And no Keycloak client is created
```

```gherkin
Scenario: a caller outside the fab is forbidden
  Given an operator authenticated for fab "berlin"
  When a registration is sent with fabId "munich"
  Then IFabAuthorizationGuard forbids it with 403
```

The last six scenarios are **already covered and already green** —
`IdempotentRegistrationIntegrationTests`, `TokenAudienceIntegrationTests`,
`RegisteredClientConcurrencyIntegrationTests`. They are written out because they
are the population this change must leave untouched, and "untouched" is a claim
that needs a named population to be checkable. They are the characterisation
obligation of constitution §Testing, not new work.

---

## Independent end-to-end test procedure

A reader must be able to confirm this without trusting the spec.

**Deterministic, no stack, seconds** — this is the primary proof, and it is what
phase 5 should cite:

```sh
dotnet test tests/Integration.Tests -c Release --filter "Category=FixtureLogic"
```

Before the fix, `A_failing_POST_from_a_fixture_client_is_attempted_once` reports
4 attempts against an expected 1. After, it passes, and the GET/PUT cases still
report 4 — so the change is a narrowing and not a disabling.

```sh
dotnet test tests/Architecture.Tests -c Release
```

Before the fix, the US-2 guard names the fixture's registration file as
unnarrowed. After, it names none.

**Against the live stack, tens of minutes, needs Docker and the burst** —
confirms the original symptom is gone. **Requires the operator's stack token;
this spec was produced without booting anything.**

```sh
dotnet test tests/Integration.Tests -c Release \
  --filter "FullyQualifiedName~IngestThroughputMeasurementTests|FullyQualifiedName~NFR002_MqttConnectAuthTests"
```

Note the filter: `Category=Measurement` as the issue writes it does **not**
select NFR002 (§"What the issue gets wrong"). Run it twice — a first run after
machine churn looks like a regression regardless.

Expected: NFR002 registers its device, reports its p50/p99, and no
`DEVICE_ALREADY_REGISTERED` appears. A single green run is weak evidence here
because the defect is load-dependent; the deterministic proof above is the
strong one, and phase 5 should say so rather than lean on the run.

---

## Locked tech choices

- `Microsoft.Extensions.Http.Resilience` standard handler (ADR-0088), narrowed
  by
  `SmartSentinelEye.ServiceDefaults.Resilience.IdempotentRetry.RetryIdempotentMethodsOnly`
  (ADR-0143). **No new predicate is written** — the existing one is reused
  verbatim, which is also what makes `IdempotentRetryTests`' seven existing facts
  count as evidence about the shared predicate.
- xUnit + Shouldly (ADR-0052), sentence-style names (ADR-0053), hand-written
  fakes (ADR-0054) — the counting handler follows
  `IdempotentRetryTests.CountingHandler`.
- Aspire fixture, no Testcontainers (ADR-0103).
- The new fixture-logic test carries `[Trait("Category", "FixtureLogic")]` so
  `ci.yml:72` reads its verdict in the Docker-free step, as
  `IntegrationTestSelectionTests` requires.
- `ServiceDefaults` is already reachable from `Integration.Tests` transitively
  (`Identity.Infrastructure` → `ServiceDefaults`), and four integration test
  files already `using SmartSentinelEye.ServiceDefaults.*`. **No new project or
  package reference.**

---

## Latency budget impact

**N/A.** No leg of the event→overlay path is touched. The change is confined to
the integration test project's HTTP client; nothing under `src/` changes
behaviour.

Recorded rather than omitted, because constitution §IV is a standing obligation:
the six legs are Camera→SFU, SFU→kiosk decode, presentation buffer,
event→overlay state, composite+render, headroom. This spec affects none of them.

Second-order and worth one line: removing three retries from a failing `POST`
**shortens** the suite's worst case by up to tens of seconds per failing call
(three retries at exponential backoff plus attempt timeouts), and removes nothing
from the success path, where no retry ever fires.

---

## Risk, and the one thing that must be checked before merge

**A `POST` that today survives a warm-up blip will stop surviving it.**

Some integration test may be relying, unknowingly, on the retry to get a `POST`
through while a service is still settling. The fixture waits for resources to
reach `Running` before tests execute, which is the main reason to expect none —
but "expect" is not "checked", and this is the one way this change could turn a
green suite red.

**T006 checks it** by running the Docker integration selection once after the
change and reading the verdict, and it is a merge blocker. If a `POST` does turn
out to need resilience, the correct answer is a **wait on the resource**, not a
retried `POST` — and certainly not `RetryEveryMethod()` on the fixture, which
would restore the defect with a justification comment attached to it.

---

## Out of scope, filed instead

- **The Keycloak/Postgres orphan** (§"The residue"). T007.
- **`ci.yml` never runs the burst**, so CI cannot regress this. Not changed: the
  burst is excluded deliberately (*"a saturating burst on a shared runner
  measures the runner"*), and the deterministic tests give CI a verdict without
  it.
- **The eight other endpoints' keys** — sound, untouched.
- **ADR-0143's "four opt-ins" heading over five clients** — an ADR edit, which
  the lane may not make.

---

## Success criteria

- **SC-001** A failing `POST` from a fixture-configured client is attempted
  exactly once; a failing `GET` and `PUT` are attempted four times. Observed by a
  Docker-free test in seconds.
- **SC-002** No `AddStandardResilienceHandler` registration under
  `tests/Integration.Tests` lacks ADR-0143's predicate; the build fails if one
  appears.
- **SC-003** `NFR002_MqttConnectAuthTests.cs` and
  `IdempotentRegistrationIntegrationTests.cs` are unchanged, and the latter's
  four facts still pass.
- **SC-004** The Docker integration selection is green after the change (T006).

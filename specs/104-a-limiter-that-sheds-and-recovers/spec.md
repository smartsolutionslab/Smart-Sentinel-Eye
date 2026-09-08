# Spec 104 — A limiter that sheds and recovers

**Issue:** #625 (`[T091]` — *"Burst above drain rate returns 429 then recovers"*), **first half only**
**Branch:** `test/625-a-limiter-that-sheds-and-recovers`
**Worktree:** `D:/Github/wt-625`
**Phase:** 1 (Specify) · **Date:** 2026-09-08
**Feature bucket:** spec `specs/020-durable-ingest-ack/` (FR-013), context `EventIngestion`
**ADRs:** ADR-0036 (smallest change, no speculative generality), ADR-0052 / ADR-0053 /
ADR-0054 (xUnit + Shouldly, sentence-style names, hand-written data), ADR-0103 (Aspire
fixture, not Testcontainers — and why this slice needs neither), ADR-0105 (`Ensure.That`),
ADR-0084 (code metrics), ADR-0109 (disjoint files for parallel slices), ADR-0139 /
ADR-0144 (two testing obligations; phase 4a has two colours and no exemption),
ADR-0037 (the phased workflow).
**Constitution:** §Testing, §IV (latency budget — **N/A**, see §6).

---

## 0. What this slice is, and what it is not

Issue #625 as written is two features wearing one task ID. **This spec delivers
the first and files the second.**

| Half | Mechanism | Status here |
|---|---|---|
| **1 — HTTP direct writes** | `IngestWriteLimiter`, a 64-slot semaphore; refusal becomes `429 EVENT_INGEST_BACKPRESSURE` | **This spec.** Test-only. |
| **2 — MQTT broker path** | `BoundedIngestChannel`, `FullMode = Wait`; **blocks**, never rejects | **Not this spec.** A design question with no ADR. §7 states the case both ways for filing. |

Half 1 stands entirely on its own: it is the path that actually answers 429, and
nothing in it depends on how half 2 is eventually decided.

### 0.1 The task text is wrong in two places, and this spec says so

`[T091]`'s wording predates spec 020 and describes a system that no longer exists.

1. **"asserts some requests get 429"** — attributed by the task to a burst against the
   ingest **channel**. Since spec 020 the direct write paths do not touch the channel at
   all (`EventsEndpoints.Writes.cs:105`, `:171` call `StoreOrRefuseAsync`, which goes
   straight to the command handler). The 429 comes from `IngestWriteLimiter`
   (`EventsEndpoints.Writes.cs:344-350`), and the channel — `FullMode = Wait` — is
   **structurally incapable** of producing one.
2. **"final `COUNT(*)` matches accepted ingress + accepted MQTT ACKs"** — an accounting
   identity that assumes every writer is either accepted or rejected. On the MQTT path a
   saturating writer is **neither**: it blocks until a slot frees, then proceeds. The
   clause has no meaning for a path with no rejection outcome. Restated for what the
   system does: *on the HTTP path, an event is stored exactly when the limiter granted a
   slot and the write committed; a 429 stores nothing and counts nothing.* Since #622,
   `sse.ingest.events` is the counter that makes the second half checkable —
   `IngestVolume.Record` is called inside `IngestEventCommandHandler.HandleAsync:67`,
   i.e. **after** the lease is taken, so a refused request is never counted. That is a
   useful fact about the accounting clause. It is **not** an assertion this slice adds
   (see §5.3).

---

## 1. The premise, checked before specifying

**The limiter is genuinely untested.** Grepping the mechanism rather than one file —
`IngestWriteLimiter`, `TryAcquire`, `IngestWriteLease`, `EVENT_INGEST_BACKPRESSURE`
across all of `tests/` **and** `e2e/` — returns **zero** hits. Every hit is in `src/`:
the type itself, three endpoint references, and the DI registration
(`EventIngestionInfrastructureModule.cs:101`).

The audit's statement of value holds exactly as written:

> *a limiter that never rejected, or one that leaked leases and rejected everything, would
> both pass the suite as it stands.*

**A test that only proves rejection happens is half a test**, so this spec covers both
directions and proves it covers both with two counterfactuals (§4).

### 1.1 `IngestThroughputMeasurementTests` is not a safety net

Confirmed, both halves of the audit's claim:

- `tests/Integration.Tests/EventIngestion/IngestThroughputMeasurementTests.cs:52` carries
  `[Trait("Category", "Measurement")]`.
- `.github/workflows/ci.yml:179` filters
  `Category!=Measurement&Category!=Disruptive&Category!=Maintenance`.
- Its **only** assertion, in 338 lines, is line 185:
  `total.ShouldBe(0, "per-source order was lost under load")`. It asserts nothing about
  429, about the limiter, or about recovery.

So it is excluded from CI *and* would not catch either failure direction if it ran.
This slice does not model itself on it and **does not add that trait**.

---

## 2. User story (P1, and the only one)

**As** the operator of an ingest endpoint under burst,
**I want** the write limiter to refuse work it cannot absorb and to start accepting again
the moment a slot frees,
**so that** overload produces a fast, retryable refusal rather than an unbounded queue —
and so that a regression in either direction is caught by the build rather than in a fab.

---

## 3. Acceptance scenarios (Gherkin)

### AS-1 — Saturation refuses (the "never rejects" direction)

```gherkin
Given a write limiter with a capacity of 2
And both slots are held by undisposed leases
When a third writer asks for a slot
Then it is refused
And the refusal is immediate — the writer is not left waiting
```

### AS-2 — Release restores capacity (the "leaks leases" direction)

```gherkin
Given a write limiter with a capacity of 2 whose slots are both held
And the next writer has been refused
When one of the held leases is disposed
Then the next writer is granted a slot
```

### AS-3 — A refusal releases nothing (the refusal path's own `using`)

```gherkin
Given a write limiter with a capacity of 1 whose only slot is held
When a refused lease is disposed
Then the limiter's capacity is unchanged
And a subsequent writer is still refused
```

*Why this one exists:* `StoreOrRefuseAsync` wraps the result of `TryAcquire()` in a
`using` **before** checking `Acquired`, so the refused lease is disposed on every 429.
`IngestWriteLease.Refused` is `default`, whose `slots` is null and whose `Dispose` is
therefore a no-op. If that ever stopped being true, each refusal would `Release()` a slot
it never took and the semaphore would inflate past its own capacity — or throw
`SemaphoreFullException` under load. Neither is visible today.

### AS-4 — The default the endpoint contract assumes

```gherkin
Given a write limiter built with the parameterless constructor DI registers
When 64 writers each take a slot
Then all 64 are granted
And the 65th is refused
```

### Bad request / auth

**Not applicable, and deliberately so.** `IngestWriteLimiter` is a leaf concurrency
primitive with no request, no principal and no fab. Authentication, fab scoping and input
validation on these endpoints are already covered by `AnonymousIngestIsRefusedTests`,
`ManualIngestFabScopingIntegrationTests`, `WebhookBearerValidationIntegrationTests` and
`DirectWriteHonestyIntegrationTests`. Restating them here would be duplication, not
coverage.

---

## 4. Phase-4a colour, and the evidence for it

**Behaviour-preserving → characterisation, observed green.**

This is not ambiguity resolved to green. ADR-0144 resolves *ambiguity* to red because red
fails loudly, and the ambiguity it guards against is "is this new behaviour or not?".
Here **no production line changes at all** — the slice is test-only — so there is no new
behaviour for a red test to describe. The production code already behaves correctly, so a
correct test passes on its first run. Spec 061 (`24e6fc4c`) already settled that a compile
error is not a red test; manufacturing one here would be the same theatre.

The evidence that these tests are worth anything is therefore **the counterfactual, not a
red run**. Two mutations, because one mutation proves one direction:

### CF-A — "the limiter never rejects"

In `src/EventIngestion/Application/Ingress/IngestWriteLimiter.cs:36-37`, drop the gate:

```csharp
public IngestWriteLease TryAcquire() => new IngestWriteLease(slots);
```

**Written prediction:**

- `A_saturated_limiter_refuses_the_next_writer` (AS-1) **fails** — `Acquired` is `true`
  where `false` is expected.
- `A_released_slot_is_handed_to_the_next_writer` (AS-2) **fails** at its refusal
  precondition.
- `The_default_limiter_bounds_writes_at_sixty_four` (AS-4) **fails** on the 65th.
- `A_refused_lease_releases_nothing` (AS-3) **fails**, differently: there is no refused
  lease to construct, and disposing the surplus lease pushes the count above `maxCount`.
- **Existing tests that redden: none.** Zero references in `tests/` or `e2e/`; the
  measurement test asserts only ordering and is CI-excluded; no integration test saturates
  64 concurrent writes. CF-A is invisible to the entire suite today.

### CF-B — "the limiter never releases"

In the same file, empty the lease's `Dispose` (line 53):

```csharp
public void Dispose() { }
```

**Written prediction:**

- `A_released_slot_is_handed_to_the_next_writer` (AS-2) **fails** — after the dispose the
  next `TryAcquire()` is still refused.
- `A_saturated_limiter_refuses_the_next_writer` (AS-1) **still passes.** That is the whole
  reason there are two tests: a suite that only proves rejection is green on a limiter
  that has jammed shut.
- `A_refused_lease_releases_nothing` (AS-3) passes trivially; AS-4 passes on a fresh
  limiter.
- **Existing tests that redden: none deterministically.** The limiter is a DI
  **singleton**, so under CF-B the running service degrades to permanent 429 after 64
  cumulative direct writes. Some `Integration.Tests` run against a shared Aspire stack
  would eventually go red — but *which* one depends on run order and on how many writes
  the session performs, and the symptom is a cascade of unexplained 429s rather than a
  named failure. That is a diffuse, order-dependent signal, not a safety net.
  `IngestThroughputMeasurementTests` would not help: it is CI-excluded (§1.1).

CF-B is the direction the audit called out as the one nobody would notice until
production, and §5.2 records that it does **not** occur today.

---

## 5. Test level: where these live, and why

### 5.1 Unit, in `tests/EventIngestion.Application.Tests/Ingress/`

All four scenarios are **unit** tests against `IngestWriteLimiter` directly, alongside its
neighbours `BoundedIngestChannelTests` and `IngestVolumeTests`. They construct their own
limiter at capacity 1 or 2, need no fixture, no Docker and no Aspire stack, and run in the
`backend` CI job on every PR. Unlike `IngestVolumeTests` they need **no** xUnit collection:
the limiter is an instance, not a static meter, so the tests are parallel-safe by
construction.

### 5.2 How a 429 is actually provoked — and why not through the endpoint

At **unit** level the threshold is trivially reachable: build the limiter with capacity 2,
hold two leases, ask for a third.

At **integration** level it is not. The limiter is registered as
`AddSingleton<IngestWriteLimiter>()` via its **parameterless** constructor
(`EventIngestionInfrastructureModule.cs:101`) — 64 slots, with no configuration knob to
lower it. Reaching 64 genuinely-concurrent in-flight writes through HTTP means firing
several hundred concurrent POSTs and hoping enough overlap; on a warm stack a write
commits in single-digit milliseconds and the burst may never reach 64. That is precisely
the flaky 30-second load test `[T091]` asked for, and it would assert *"some requests get
429"* — a condition that can fail for reasons that have nothing to do with the limiter.

**Decision: unit tests only.** The endpoint side of the wiring is two lines
(`EventsEndpoints.Writes.cs:344-350`: take a lease, and if it was refused return
`429 EVENT_INGEST_BACKPRESSURE`), and no cheap deterministic test covers them — the Aspire
fixture drives the service over HTTP as a separate process, so a test cannot reach into its
DI container to saturate the singleton. This spec records that gap explicitly rather than
covering it with a load test that would fail for the wrong reasons. §7.3 files the one
change that would close it.

### 5.3 What this slice does **not** assert

- **No `sse.ingest.events` assertion.** The counter from #622 exists and is relevant to the
  accounting clause (§0.1), but a metrics assertion on a backpressure test would be there
  because the counter exists, not because backpressure needs it. `IngestVolumeTests`
  already covers the meter.
- **No load or throughput assertion**, and **no `[Trait("Category", "Measurement")]`**.
- **No parallel-`TryAcquire` race test.** `SemaphoreSlim` guarantees the count; a test of it
  tests the BCL and buys flakiness for no information (ADR-0036).

---

## 6. Latency budget

**N/A.** Test-only; no production code changes; nothing on the event-to-overlay path is
touched. The limiter does not sit on any of constitution §IV's six legs — it bounds
concurrency on the HTTP *write* path, which is ingress, not the event→overlay leg.

---

## 7. Half 2 — the case, stated both ways, for filing

**Not decided here.** `BoundedIngestChannel.cs:32` sets
`FullMode = BoundedChannelFullMode.Wait`, so a saturated channel **blocks the MQTT
subscriber** rather than rejecting. Its own unit test says so in its name:
`BoundedIngestChannelTests.cs:101`, `WriteAsync_blocks_when_full_until_a_slot_frees`.
**This is current, deliberate behaviour and this spec does not imply it is a defect.**

### 7.1 The case for keeping `Wait` (the status quo)

- It is the documented intent, not an accident: spec 006 FR-021/FR-022 asks the subscriber
  to stop taking deliveries so **the broker holds queue depth**. The class comment states
  this, and the unit test asserts it.
- MQTT is a protocol with real backpressure. An unacknowledged delivery is retained and
  redelivered; a dropped one is simply lost. Blocking converts overload into broker queue
  growth, which is durable, observable and bounded by broker policy. Shedding converts it
  into data loss with no acknowledgement anywhere.
- The single-reader FIFO drain that preserves per-source order (spec 020) depends on the
  writer not skipping items. `DropOldest` / `DropWrite` would silently break ordering
  guarantees the system currently makes.
- There is no caller to tell. HTTP has a client that can retry; the channel writer is our
  own subscriber loop.

### 7.2 The case for changing it

- Blocking the subscriber delays **acks**, and a broker with a bounded in-flight window
  stops delivering. Under sustained overload the queue grows until the broker's own
  retention limit sheds — so events are lost anyway, just further away and with less
  telemetry.
- A blocked subscriber can trip MQTT keepalive and provoke a **reconnect**, which is a
  worse failure than a refusal: `MqttResubscribeAfterBrokerOutageIntegrationTests` exists
  because resubscription is a real cost.
- The system has no visibility into how long writers block. `CurrentDepth` is exposed;
  wait time is not.
- `[T091]` was written expecting *"429 then recovers"* on this path. Whatever the answer,
  the current behaviour is recorded only in a class comment and a test name — **there is
  no ADR**, so the next person to read the task text will make the same wrong assumption
  the task did.

### 7.3 A second, smaller item worth filing

Making `IngestWriteLimiter`'s concurrency configurable (one constructor argument bound from
options in `EventIngestionInfrastructureModule.cs:101`) would let an integration test set
it to 1 and provoke a **deterministic** 429 through the real endpoint, closing the §5.2
gap without a load test. It is a production change, so it is out of scope for this
test-only slice (ADR-0036, ADR-0144).

---

## 8. Independent end-to-end test procedure

1. `dotnet test tests/EventIngestion.Application.Tests/` — four new tests green, no fixture,
   no Docker, under a second.
2. Apply **CF-A** to `IngestWriteLimiter.cs:36-37`; re-run. Expect the failures predicted in
   §4. Revert.
3. Apply **CF-B** to `IngestWriteLimiter.cs:53`; re-run. Expect AS-2 red and AS-1 green —
   the asymmetry is the evidence that two tests were needed. Revert.
4. `git diff --stat src/` is empty: nothing outside `tests/` changed.

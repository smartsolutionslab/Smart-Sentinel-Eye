# Spec 104 — Plan

**Phase:** 2 (Plan) · **Date:** 2026-09-08 · **Spec:** `spec.md`

---

## 1. Bounded context and layers

**Context:** `EventIngestion`. **Layer under test:** `Application` — specifically
`Application/Ingress/`, which is where `IngestWriteLimiter` lives.

No domain model, no entity, no value object, no invariant is added or changed.
`IngestWriteLimiter` is a leaf concurrency primitive, not an aggregate: it holds a
`SemaphoreSlim` and hands out a `readonly struct` lease. It is exempt from constitution
§II by construction — it exposes no state at all, primitive or otherwise, only
`TryAcquire()` and `Dispose()`.

## 2. Messaging

**None.** No domain event, no integration event, no `Shared.Contracts` change. The limiter
sits on the synchronous HTTP write path and publishes nothing.

## 3. Boundary rules

- No cross-context project reference is added. The new test project reference already
  exists: `tests/EventIngestion.Application.Tests` already references
  `src/EventIngestion/Application`, which is how `BoundedIngestChannelTests` and
  `IngestVolumeTests` compile today.
- **No new test project.** Testing the endpoint's `private static StoreOrRefuseAsync`
  would need an `EventIngestion.Api.Tests` project that does not exist; standing one up
  for one private method is speculative generality (ADR-0036) and would not work anyway
  (§5.2 of the spec).
- `NetArchTest` rules in `tests/Architecture.Tests` are untouched.

## 4. Files

| File | Action | Owner |
|---|---|---|
| `tests/EventIngestion.Application.Tests/Ingress/IngestWriteLimiterTests.cs` | **new** | this slice |
| `specs/104-a-limiter-that-sheds-and-recovers/{spec,plan,tasks}.md` | **new** | this slice |
| everything under `src/` | **unchanged** | — |

**ADR-0109 contention check: clean.** None of `src/Shared.Kernel/*`,
`src/Shared.Contracts/*`, `src/AppHost/AppHost.cs`, `apps/shared/*`, `e2e/support/*`,
`.github/workflows/ci.yml`, `Directory.Packages.props` or `global.json` is touched. The one
new file is in a directory this slice creates nothing else in, so it is disjoint from any
concurrent slice by construction.

## 5. Test design

Four `[Fact]`s in one class, ADR-0053 sentence-style names, Shouldly assertions
(ADR-0052), no builders needed (the limiter takes an `int`, so ADR-0054 has nothing to say
here), no fixture, no collection attribute.

The shape each test uses:

```
IngestWriteLimiter limiter = new(concurrency: 2);
IngestWriteLease first = limiter.TryAcquire();
...
```

Leases are disposed explicitly where the test is *about* disposal (AS-2, AS-3) and via
`using` where they merely need to be held. Nothing in the tests calls
`IngestWriteLimiter.Dispose()` while a lease is outstanding — that would throw
`ObjectDisposedException` on release and would be testing shutdown order, not the limiter.

## 6. CI placement

The `backend` job. `tests/EventIngestion.Application.Tests` is a plain unit project already
in the solution; the new file needs no workflow change. **No trait is added** — a traited
test would be filtered out of the `integration` job's run and would not run in `backend`
either way, and `Measurement` specifically is the trait that hid
`IngestThroughputMeasurementTests` from CI (spec §1.1).

## 7. Coverage gate

ADR-0065 requires Application ≥ 80%. `IngestWriteLimiter.cs` currently contributes
uncovered lines; these tests move it to full coverage of `TryAcquire`, both lease
constructors and both `Dispose` paths. The gate can only improve.

## 8. Constitution and ADR alignment

- **§Testing** — the behaviour-preserving obligation. Characterisation tests, captured
  green, must pass **unmodified** after any later change; the counterfactuals in spec §4
  are how their value is demonstrated in the absence of a red run.
- **ADR-0036** — smallest possible change: test-only, four tests, no production line
  touched, two findings filed rather than fixed here.
- **ADR-0144** — the lane may not write an ADR. Half 2 needs one (spec §7); this slice
  reports it and delivers half 1, which does not depend on it.

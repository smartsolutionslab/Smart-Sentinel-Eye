# Spec 104 — Tasks

**Phase:** 3 (Tasks) · **Date:** 2026-09-08 · **Issue:** #625 (half 1)
**Engineer:** `backend-engineer` · **Phase-4a colour:** behaviour-preserving →
characterisation, **observed green**, evidenced by two counterfactuals (spec §4).

All tasks are **test-only**. Any task that would edit a file under `src/` is out of scope
and must be reported instead.

---

## US1 (P1) — The limiter sheds under saturation and recovers when a slot frees

One file, so nothing here is `[P]`: every task writes to
`tests/EventIngestion.Application.Tests/Ingress/IngestWriteLimiterTests.cs`. Parallelism
for this slice is *between* slices (ADR-0109), not within it — spec §4's contention check
confirms the file is disjoint from any concurrent branch.

| ID | Task | Depends on |
|---|---|---|
| **T001** | Create `tests/EventIngestion.Application.Tests/Ingress/IngestWriteLimiterTests.cs` with the class shell and no test bodies. Confirm it compiles — the project already references `EventIngestion.Application` (see `BoundedIngestChannelTests`), so no `.csproj` edit is needed. **If a `.csproj` edit turns out to be required, stop and report it**: it would mean the plan's file list is wrong. | — |
| **T002** | AS-1 — `A_saturated_limiter_refuses_the_next_writer`. Capacity 2, hold both leases, assert the third has `Acquired == false`. This is the *never rejects* direction. | T001 |
| **T003** | AS-2 — `A_released_slot_is_handed_to_the_next_writer`. Capacity 2, hold both, assert refusal, dispose one, assert the next acquires. This is the *leaks leases* direction, and it is the reason there are two tests rather than one. | T001 |
| **T004** | AS-3 — `A_refused_lease_releases_nothing`. Capacity 1, hold the slot, dispose the refused lease, assert the next request is still refused. Covers the `using` that `StoreOrRefuseAsync` applies to a refusal before it checks `Acquired`. | T001 |
| **T005** | AS-4 — `The_default_limiter_bounds_writes_at_sixty_four`. Parameterless constructor (the one DI registers), take 64 leases, assert all granted and the 65th refused. Reference `IngestWriteLimiter.DefaultConcurrency` rather than a literal in the loop bound, but assert the number itself so a silent change to the constant is caught. | T001 |
| **T006** | Run `dotnet test tests/EventIngestion.Application.Tests/` and capture the **verbatim** output. Four tests green. Green is the expected colour here (spec §4) — a red test at this step means the test is wrong, not the code. | T002–T005 |

## US1 verification — the counterfactuals (phase 5, not phase 4)

| ID | Task | Depends on |
|---|---|---|
| **T007** | Apply **CF-A** to `src/EventIngestion/Application/Ingress/IngestWriteLimiter.cs:36-37` (drop the `slots.Wait(0)` gate), re-run, capture the verbatim failures, **revert with `git checkout -- src/`** and confirm `git diff --stat src/` is empty. Compare against the written prediction in spec §4; **report any mismatch rather than editing the prediction**. | T006 |
| **T008** | Apply **CF-B** to the same file, line 53 (empty `IngestWriteLease.Dispose`), re-run, capture verbatim, revert, confirm clean. The load-bearing observation is the **asymmetry**: AS-2 red, AS-1 green. If AS-1 also reddens, one of the two tests is not testing what it claims. | T007 |
| **T009** | Confirm `git status --short src/` is empty before committing. Test-only means test-only. | T008 |

---

## Out of scope, to be filed by the orchestrator (do not implement)

| # | Item | Why not here |
|---|---|---|
| **F1** | **An ADR for `BoundedIngestChannel`'s `FullMode = Wait`** — block the MQTT subscriber vs. shed. Case stated both ways in spec §7.1 / §7.2. | ADR-0144: the lane implements decisions, it does not make them. Half 1 does not depend on the outcome. |
| **F2** | **Make `IngestWriteLimiter`'s concurrency configurable** so an integration test can set it to 1 and provoke a deterministic 429 through the real endpoint (spec §5.2, §7.3). | Production change; this slice is test-only (ADR-0036). |
| **F3** | **`[T091]`'s task text is wrong in two places** (spec §0.1) — it attributes the 429 to the channel, and its `COUNT(*)` clause assumes a rejection outcome the MQTT path does not have. The issue text should be corrected when half 2 is filed. | Issue hygiene, not code. |

## Gate (phase 3)

The feature's issue is #625, already on Project #13. **No per-task issues** — the repo
stopped creating those after spec 028, and `tasks.md` is the artifact this work is tracked
against.

# Tasks 102 — A revoked credential stops working

**Spec:** `spec.md` · **Plan:** `plan.md` · **Issue:** #603
**Engineer:** `backend-engineer` (single engineer; see spec §6.1).
**Phase 4a colour:** behaviour-preserving → **characterisation, observed green**
(spec §6.3). There is no red to observe and none is to be manufactured; the
evidence is T003.

**Parallelism:** the slice is one file. Nothing here is `[P]` — T001→T002→T003→T004
is a strict chain, and the chain is short. No foundational task blocks anything
outside this slice; no ADR-0109 contention file is touched (plan §6), so this
slice may run concurrently with any other.

---

## US1 — A revoked webhook credential stops being accepted

### [T001] [US1] The test class, both `[Fact]`s

**File (new):**
`tests/Integration.Tests/EventIngestion/WebhookRevocationRefusesDeliveryIntegrationTests.cs`

Write exactly the two scenarios in spec §3, structured as plan §7.

Non-negotiables, each with its reason already recorded:

- `[Collection(AspireCollection.Name)]` and **no `[Trait("Category", …)]`** —
  `ci.yml:179` excludes `Measurement`, `Disruptive` and `Maintenance`; three
  files in this same folder carry `Disruptive`, and copying one delivers a test
  CI never runs.
- The pre-revoke delivery **asserts 201**. It is the control that makes the final
  401 attributable to the revocation (spec §3).
- **Same fab (`munich`) throughout.** A cross-fab delivery is refused at
  `Writes.cs:226` instead and would survive the counterfactual.
- The bearer is captured from the registration 201 body and held in a local — it
  cannot be recovered later (spec §2.3).
- No `Task.Delay`, no polling, no waiting on the integration event.
- Each `[Fact]` registers its own uniquely-named integration.
- Copy the helper idioms from the two named siblings; do **not** extract a shared
  base class (that is a separate refactor).
- Attach a `DiagnoseAsync`-style message to the 201, the revoke's 200 and the
  final 401 — CI has no other route to the service's stack trace.

**Done when:** the file compiles and both `[Fact]`s pass against the booted
fixture, twice (SC-004). ADR-0084 metrics clean; `dotnet format
--verify-no-changes` clean.

### [T002] [US1] Prove CI will actually run it

```sh
dotnet test tests/Integration.Tests/SmartSentinelEye.Integration.Tests.csproj \
  -c Release --no-build \
  --filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance" \
  --list-tests
```

**Done when (SC-003):** both `[Fact]` names appear in the listing. Quote the two
matching lines in the PR body. A test that CI silently skips is the failure mode
this task exists to rule out.

**Depends on:** T001.

### [T003] [US1] The counterfactual — mandatory, and reported against the prediction

Apply, in the working tree only:

```csharp
// src/EventIngestion/Api/EventsEndpoints.Writes.cs:211
if (!found.HasValue)          // was: if (!found.HasValue || found.Value.IsRevoked)
{
    return null;
}
```

Then run the **whole** integration suite under CI's filter, plus the Architecture
and EventIngestion unit suites.

**The prediction is written down in spec §8 and must be checked against, not
merely confirmed:**

- Expected: the new class's two `[Fact]`s are **the only** failures — the second
  delivery answers 201 instead of 401.
- If **anything else** reddens, the new test proves less than it appears to.
  **Say so in the PR body, name the extra files, and correct spec §8** — do not
  proceed as though the prediction held (SC-002).

Revert the production line afterwards. `git status` must show only the new test
file and `specs/102-*/`. A restored file keeps its old timestamp, so force the
rebuild rather than trusting an incremental one before re-running green.

**Done when:** both runs' verbatim output — red with the counterfactual, green
without — are captured for the PR (SC-001).

**Depends on:** T001.

### [T004] [US1] Final gates

- Release build clean (analyzers are `warning`-as-error on the collection-expression
  rule; S125 has been flaky before — a re-run on the same SHA is acceptable
  evidence, and say so if used).
- `dotnet format --verify-no-changes`.
- Full integration suite green under CI's filter, run twice.
- PR body carries: the T002 listing lines, the T003 red output, the T003 green
  output, and the §8 prediction outcome stated explicitly as held or corrected.

**Depends on:** T002, T003.

---

## Dependency graph

```
T001 ──┬── T002 ──┐
       └── T003 ──┴── T004
```

No task is `[P]`: T002 and T003 both need T001's file, and T004 needs both.

---

## Phase 3 gate (ADR-0037, as corrected in CLAUDE.md)

Per-task issues are **not** created (that practice stopped after spec 028). The
gate is that the **feature-level** issue is on Project #13:

```sh
gh project item-add 13 --owner smartsolutionslab --url https://github.com/smartsolutionslab/smart-sentinel-eye/issues/603
```

`item-add` prints nothing on success; verify with `item-list --limit 2000`, never
the default 30.

---

## Follow-ups to file separately (spec §7) — not this slice

1. **Open question / candidate ADR** — should a revoked webhook integration be
   distinguishable from an unknown one at
   `EventsEndpoints.Writes.cs:211`? Both arguments are written out in spec §7.1,
   with two middle grounds. **This lane may not decide it** (ADR-0144). Note the
   dependency: settling it toward "distinguishable" amends scenario 2's `[Fact]`
   and nothing else.
2. **A false coverage claim** — `AnonymousIngestIsRefusedTests.cs:14` states that
   specs 013/018/021 tested "whether a revoked webhook integration still works".
   They did not (spec §2.1). Doc-comment only, nothing red guards it.
3. **The stale half of #603** — the dedup/`eventId` half is already true in
   effect via `EventIdentifier.New()` at `Writes.cs:156`, and
   `IngestWebhookEventCommand` never existed. #603's title and body should be
   corrected or the issue closed against this spec, so the next reader does not
   go looking for the handler again.

# Plan — 124 A double that can produce the failure

## Phase 4a colour: **red**, for all seven

Every one of the seven assertions gains a *new* obligation, so this is
behaviour-changing test work, not characterisation. Ambiguity resolves to red
(ADR-0144), and the red here is not hypothetical — it is the evidence that
separates this change from a rewritten fake.

The red is reached **by counterfactual**: production is temporarily mutated to
embody the defect the test names, the test is observed, and production is
restored. This repository's standing practice for a guard whose subject is
correct (`KioskPrivilegeSweepSteadyStateTests` remarks; spec 092
§"Whether the existing tests can fail"). Each counterfactual is chosen so the
**current** assertion survives it — a mutant the current test already catches
would prove nothing.

## The counterfactuals

| Mutant | File | Change | Green-before for |
|---|---|---|---|
| M1 | `RuleEvaluator.cs:34` | `LookupActive(fab, …)` → `LookupActive(FabIdentifier.From("hamburg"), …)` | 1, 2, 3 |
| M2 | `FabEventIngestedV1Handler.cs:45-66` | both fab guards continue instead of returning | 4 |
| M3 | `HttpKeycloakAdminClient.cs:295-300` | drop the `sse.kind == "kiosk"` filter | 5 |
| M4 | `HttpKeycloakAdminClient.cs:346` | `assigned.Length == 0` → `== -1` (early exit never taken) | 7 (second half) |
| M5 | `KioskPrivilegeSweep.cs:63` | strip three times per kiosk | 7 (first half) |
| M6 | `SystemVariableValueRequestedV1Handler.cs:80` | log a constant instead of the rejected name | 6 |

**`"hamburg"` is not arbitrary.** M1 must miss every bucket the four tests seed
(so `munich` and `dresden` are out) *and* differ from the key each test expects
(so `berlin` is out, because test 2 asks for berlin and a mutant that asks for
it too would be invisible). Any fourth fab works; the point is that the
constraint exists and was solved rather than guessed.

## The design decision, and the one rejected

**Rejected: replace the test-side `InMemoryRuleCache` clone with the shipped
one** (`Automation.Application.Tests` → `Automation.Infrastructure`). It is the
strongest form of "make the double capable" — the assertion would bind to real
keying, and the clone that `InMemoryRuleCacheTests`' own remarks complain about
("two implementations kept in step by hand") would go.

It is rejected on layering. No `*.Application.Tests` project in this repository
references an Infrastructure project, and Identity's
`EnrolledKiosksKeycloakAdminClient` records the absence as *load-bearing* —
spec 092 leans on it when it calls test 5 vacuous. Buying falsifiability for
four tests by dissolving a boundary another spec's reasoning rests on is the
wrong trade, and it is not the smallest change.

**Taken instead:** the double records the key it was asked for. It keeps the
layering, it needs no production change, and it converts the seam from the
keyed kind (whose answer belongs to the double) into the recording kind (whose
log belongs to the caller) — which is the shape spec 087 measured as falsifiable
in 71 of 77 cases.

**And the guarantee the recorder cannot reach is already covered.**
`InMemoryRuleCacheTests` tests the shipped cache directly, including the fab
key. Tests 1–3 gain the caller's half; the cache's half was never unguarded.
This is the mitigating fact spec 087 recorded, and it is why FR-001 is a
sufficient remedy rather than a partial one.

## Sequence

1. Baseline: five projects green, unmutated.
2. Apply M1–M6; run the seven **unmodified** → all green. Quote verbatim.
3. Write the test changes (FR-001 … FR-005).
4. Re-run with M1–M6 still applied → the seven red. Quote verbatim.
5. `git checkout -- src/` and `touch` every reverted file — a restored file
   keeps its old timestamp and MSBuild will skip the rebuild, which reads
   exactly like the fix not working.
6. Re-run → all green. `dotnet build -c Release`.

M5 is applied on its own pass because it needs the pre-change
`KioskPrivilegeSweepTests` in the tree to show the green-before, and the
post-change one to show the red. The two runs are separated by a file swap, not
by a different mutant.

## Constitution / ADR check

- **§II value objects, ADR-0141 `Option<T>`, ADR-0105 `Ensure.That`,
  ADR-0049 `CancellationToken` last** — no signature in `src/` changes, so none
  is engaged. The new test-side members are a `List<string>` and a list of
  key tuples, in `tests/`, where §II does not bind.
- **ADR-0139 §Testing** — red first, failure quoted. Satisfied by step 4.
- **§IV latency** — N/A, test-only.
- **Coverage gates** — three tests added, none removed; no production line
  loses its cover.

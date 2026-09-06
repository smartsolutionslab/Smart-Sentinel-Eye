# Plan 082 — An audit row names its fab

**Spec:** `specs/082-an-audit-row-names-its-fab/spec.md`
**ADRs:** ADR-0102, ADR-0115, ADR-0036, ADR-0037, ADR-0139, ADR-0052, ADR-0103

## Bounded contexts and layers touched

| Context | Layer | Change |
|---|---|---|
| SystemVariables | Application | one argument in `VariableArchivedDomainEventHandler` |
| LayoutComposition | Application | one argument in each of two handlers |
| AuditObservability | Application | comment correction only (FR-006) — no behaviour |
| — | `tests/Architecture.Tests` | new guard |

**No Domain change. No Infrastructure change. No Api change. No contract change.**
`EventMetadata` and all three V1/V2 records are untouched: the `Fab` field already
exists and is already `string?`. Nothing is versioned, nothing is a breaking change
under ADR-0073.

## Boundary rules

The three handlers already live on the correct side of every boundary and this change
does not move them. Each reads a `FabIdentifier` from its **own** context's domain event
and writes the primitive `string` into `Shared.Contracts`' `EventMetadata` — exactly the
translation ADR-0040 prescribes at the wire boundary. No cross-context project reference
is added; `BoundaryTests` is unaffected.

The guard reads `src/**/*.cs` as **text** from the repository root (the
`HandlerDeconstructionTests` root-walk to `SmartSentinelEye.slnx`). It therefore takes no
project reference on anything it inspects, which is why an architecture test can span
nine contexts without breaking the rule it is enforcing.

## Entities, value objects, invariants

Nothing is introduced. The invariant being restored is not a domain invariant — it is a
**translation invariant**, and that is why it needs a guard rather than a value object:

> When a domain event carries a `FabIdentifier`, the integration event derived from it
> carries that fab in its `EventMetadata`.

A value object cannot express this. `EventMetadata.Fab` is `string?` because ADR-0102
requires the null case for genuinely fab-neutral events (overlay revisions, audit chunks),
so the type system permits `null` at every site by design. The obligation lives in the
*relation between two types*, not in either of them — which is the shape an architecture
test exists for, and the reason `PrimitiveBoundaryTests`-style reflection cannot help
either (a passed argument leaves no trace in metadata).

## Messaging — domain event → integration event

| Domain event (in-process) | Integration event (`Shared.Contracts`) | Fab today | Fab after |
|---|---|---|---|
| `VariableArchivedDomainEvent` | `SystemVariableArchivedV1` | `null` | `fab.Value` |
| `LayoutRevisionArchivedDomainEvent` | `LayoutRevisionArchivedV1` | `null` | `fab.Value` |
| `LayoutRevisionPublishedDomainEvent` | `LayoutRevisionPublishedV2` | `null` | `fab.Value` |

Downstream is unchanged and needs no coordination: `IntegrationEventAuditHandler.AuditAsync`
already reads `meta.Fab` uniformly and maps `null → Option.None`, non-null →
`Option.Some(FabIdentifier.From(...))`. Supplying a value simply exercises the branch that
already exists. The three events are delivered through the Wolverine outbox (ADR-0088) as
before; no queue, subscription or serialisation shape changes.

## The guard — design, and what it does not prove

**Name:** `EventMetadataFabDeclarationTests`, in `tests/Architecture.Tests`, beside
`HandlerDeconstructionTests` whose helpers (`ReadSources`, `Balanced`, `SplitTopLevel`,
`Names`, record-header lookup) it reuses rather than reimplements.

**The rule, stated as a derivation:**

> For every `Handle`/`HandleAsync` method in `src/` whose first parameter type resolves to
> a record declaring a component named `Fab`, no `EventMetadata` constructed within that
> method body may pass the literal `null` in the fab position (positional index 2).

**Why this rule and not the one the issue suggests.** "A handler that has a fab *in scope*"
is a syntactic accident — it depends on what the author happened to destructure, and it
would be silenced by binding the fab as `_`. "A handler whose *source event carries* a fab"
is a semantic derivation from the type, and it catches the defect however the author wrote
the destructure. It is also what makes the exemptions fall out for free.

**Why it is a derivation and not a register.** Both sides come from source. The obligation
side is the domain-event record's own header. The exemption side is the *absence* of a
`Fab` component on that header — so OverlayDesigner and `AuditChunk` are exempt because
ADR-0115 and Timescale chunking are already expressed in their types, not because anyone
typed their names into a list. **There is no exemption list to maintain, and nothing in
the guard needs updating when a handler is added.** This is the distinction from spec 075,
whose register had to be typed by hand because the fact it pinned did not exist in source.

**What a green run proves:**
- No handler translating a fab-carrying event writes a literal `null` in the fab position.
- The claim holds across all of `src/`, not just the three files this PR edits.
- It holds for a handler written next month by someone who never read this spec.

**What a green run does NOT prove — and the guard's doc comment must say so (FR-007):**
- **Not that the fab is the *right* one.** `fab.Value` and `someOtherFab.Value` are
  indistinguishable to a source scan. This is why FR-001–003 also need per-handler
  assertions; the guard is a floor, not a substitute.
- **Not that a fab reaches the audit row at runtime.** A nullable fab that is null at
  runtime (`StreamHealthChangedDomainEventHandler`'s `Fab?.Value`) passes cleanly. That
  is #2076, and the guard's greenness must not be read as covering it.
- **Not that the audit surface is scoped correctly.** That is US1's integration test.
- Nothing about publishers outside a handler — `AuditRetentionHostedService` publishes
  from a private `ArchiveAndDropAsync`, and its file declares no `Handle`/`HandleAsync`
  at all, so the scan never reaches it. It is correct today; it is not protected. Stated
  rather than left implicit.

  **Correction, made while implementing:** this list said
  `RotateWebhookClientCommandHandler` was outside the scan too. It is not. It publishes
  from `HandleAsync(RotateWebhookClientCommand command, …)`, and that command declares a
  `FabIdentifier Fab` component, so the guard reads it, checks it, and it passes. The
  only genuinely unscanned publisher is the retention service.

**Failure modes the guard must reject rather than skip (FR-005):** a first-parameter type
whose record declaration cannot be found; an `EventMetadata` construction using named
arguments (`Fab:`) — the positional scan cannot read it, so it must fail and demand the
positional form or an extension of the guard; an argument list whose arity is neither 4
nor 5. Plus `checkedCount.ShouldBeGreaterThan(0)` so a broken scan reports itself instead
of passing vacuously. Every one of these is the "silently passed what it did not
understand" failure this repo has already recorded.

## Sequencing and parallelism (ADR-0109)

The guard and the three handler fixes touch **disjoint files**, so they are `[P]` against
each other — but only after the guard has been *observed red*, because the red run against
unmodified source is the evidence that the population is exactly three. The guard is
therefore written first and fixed-against second.

The two contexts' handler fixes are `[P]` with each other: SystemVariables and
LayoutComposition share no file.

**No foundational task.** Nothing in `Shared.Kernel`, `Shared.Contracts`, `AppHost` or any
Aspire resource changes, so nothing blocks the fan-out beyond the guard's red run.

## Risks

- **The integration test needs the Aspire stack**, and the report notes C: is at ~12 GB.
  Phase 4 must check headroom before booting and stop the AppHost process directly.
- **Regex fragility.** Mitigated by FR-005's fail-loud posture and by reusing the
  balanced-delimiter helpers rather than matching parentheses with a regex.
- **The corrected comment (FR-006) drifting again.** It is a comment, not a guard, and
  this spec found it stale once already. Recorded as a known limit rather than solved.

# Plan — spec 115

## Declaration 1 — phase 4a colour: **RED**, behaviour-changing

The log message is the behaviour here. Two assertions must be observed failing:
a refusal for `"stream"` naming `stream`, and an absent field reading
differently from an unrecognised one. Both fail today because the value is
discarded at the endpoint and both cases share one `[LoggerMessage]`.

No characterisation control is declared beyond this: every pre-existing
assertion in `AuthorizeWhepCommandHandlerTests` must hold **unmodified**, and
that is the fail-closed evidence.

## Declaration 2 — §II says value object, and the reason is not §II

`PrimitiveBoundaryTests` walks outward from aggregate roots, so it does not
reach an Application command; `AuthorizeWhepCommand.BearerToken` is already a
`string` and is not a breach. Constitution §II binds *a domain model*, and a
command is not one. **So §II does not force the value object — checked, not
assumed.**

It is a value object anyway, and for a better reason: FR-004 is an invariant.
A bare `string` on the command puts "cap it, strip control characters" at the
log call site, where it is a rule someone has to remember; every future call
site gets it wrong once. `ReportedMediaMtxAction.From` makes *bounded* a
property of the type — there is no way to hold one that is not safe to log.

Home: `src/StreamDistribution/Domain/Stream/`, beside `MediaMtxAction`, on
`StringValueObject` (ADR-0046), mirroring `MediaMtxPath`.

## Declaration 3 — the cap is 64 characters, plus a truncation mark

Every action MediaMTX has ever posted is one lowercase word of at most eight
characters: `read`, `publish`, `playback`, `api`, `metrics`, `pprof`. 64 is
eight times the longest, with room for a namespaced or hyphenated successor
(`read-recording`, `whep.read`) — a cap that truncates the *honest* case
destroys the diagnosis it exists to provide, which is the failure mode to avoid
first. It is also bounded far below anything that could bloat a log record or
push a message past a sink's line limit.

Truncation appends `…`, so a capped value is never mistaken for a whole one:
64 characters plus one mark, 65 maximum.

Control characters are replaced with `U+FFFD`. The `action` field is
attacker-influenced only insofar as MediaMTX composes the body, but a `\r\n` in
a logged value forges a second line in any text sink, and the neutralisation is
three characters of code inside the factory that already exists.

## Declaration 4 — two log methods, not one with a branch

FR-003 asks for two diagnoses that cannot be confused. Two `[LoggerMessage]`
methods give two templates and two generated EventIds, so a log query can
separate them without reading prose. One method with a nullable parameter would
render `MediaMTX sent ''` for an absent field — exactly the confusion the issue
is about.

The absent-case message also drops spec 074's "the broker image tracks a
floating latest tag", which #2103 made false.

## Declaration 5 — the value never leaves the process

`AuthorizeWhepError.ActionUnknown` is untouched. MediaMTX gets the same
`WHEP_ACTION_UNKNOWN` / `403` / same detail string as before. Echoing what
arrived back to its sender would be a reflection with no diagnostic value and a
new trust-boundary question; the operator reads it from the log.

## Files

| File | Change |
|---|---|
| `src/StreamDistribution/Domain/Stream/ReportedMediaMtxAction.cs` | new |
| `src/StreamDistribution/Application/Commands/AuthorizeWhepCommand.cs` | 4th member |
| `src/StreamDistribution/Api/StreamEndpoints.cs` | translate, do not decide |
| `src/StreamDistribution/Application/Commands/Handlers/AuthorizeWhepCommandHandler.cs` | which of the two it logs |
| `src/StreamDistribution/Application/Log.cs` | two methods replace one |
| `tests/StreamDistribution.Domain.Tests/Stream/ReportedMediaMtxActionTests.cs` | new |
| `tests/StreamDistribution.Application.Tests/Commands/AuthorizeWhepCommandHandlerTests.cs` | 3 reds + mechanical arg additions |

No contention file is touched: not `Shared.Kernel`, not `Shared.Contracts`, not
`AppHost` (ADR-0109).

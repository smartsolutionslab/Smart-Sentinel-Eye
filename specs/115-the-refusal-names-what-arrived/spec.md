# Spec 115 — the refusal names what arrived

**Issue:** #2105
**Status:** Phase 4 complete (this branch), phases 5–7 with the orchestrator.
**ADRs:** 0050 (logging), 0047/0089 (errors), 0046/0066/0091 (value objects),
0141 (`Option<T>`), 0105 (guards), 0139/0140 (constitution §II).

## Problem

Spec 074 (#2094) made the MediaMTX WHEP external-auth hook **fail closed** on an
action it does not recognise. The refusal is correct. The *diagnosis* is not:

```
Refused a WHEP request on path cam-…: MediaMTX named no action this build
recognises — the field was absent, or held a value other than read, publish or
playback.
```

`AuthorizeWhepCommand.Action` is `Option<MediaMtxAction>`, and the raw text is
discarded at the endpoint by `MediaMtxAction.TryFrom(body.Action)`. By the time
the handler refuses, what arrived is gone, so the message can only describe two
cases in prose. An operator learns that *something* unrecognised arrived and that
every viewer is refused until the build learns the new vocabulary — but not
**which value to look up**.

Failing closed is defensible only because the outage is meant to be recoverable
in minutes. `MediaMTX sent 'stream', not 'read'` is the difference between a
one-line diagnosis and a release-note hunt.

### #2103 does not make this obsolete

The three MediaMTX containers now pin `1.21.0-ffmpeg`, guarded by
`ContainerImagePinTests`. That is the *version* becoming knowable, not the
*value*. A pinned image still gets bumped, and a bump is exactly when a
vocabulary changes; production is not this repo's fixture, and the hook has to
diagnose itself wherever it runs. With the pin, an operator now gets version and
value in one line — the pin makes the received value **more** useful, not less.

## Scope

**In:** carrying the received text from the endpoint to the handler, and two
distinct refusal log messages.

**Out — and this is the security boundary.** Nothing here may widen what the hook
accepts, change which actions are recognised, or alter the fail-closed outcome.
`MediaMtxAction.TryFrom` is untouched, both refusals remain
`AuthorizeWhepError.ActionUnknown` / `WHEP_ACTION_UNKNOWN` / `403`, and the
`ApiError` returned to MediaMTX is byte-for-byte unchanged — the received value
is a *log* field, never part of an answer sent back over the wire.

## Functional requirements

- **FR-001** The endpoint carries the `action` field's text to the command
  alongside the parsed action, without deciding anything.
- **FR-002** A refusal for a value that arrived but is not recognised names that
  value in a **structured log field** (ADR-0050) — never string-interpolated.
- **FR-003** A refusal for an absent field says so explicitly, in a **different**
  message from FR-002. The two diagnoses must not be confusable.
- **FR-004** The carried text is **bounded before it is logged**: capped in
  length, with truncation signalled, and control characters neutralised so a
  value cannot forge a second line in a text sink.
- **FR-005** Absent and present-but-empty stay distinguishable. `"action": ""` is
  a value that arrived; a missing field is not.
- **FR-006** Every existing refusal and admission outcome is unchanged.

## Acceptance scenarios

1. MediaMTX posts `{"action":"stream"}` → `403 WHEP_ACTION_UNKNOWN`, and the
   warning contains `stream`.
2. MediaMTX posts no `action` field → `403 WHEP_ACTION_UNKNOWN`, and the warning
   says the field was absent and does **not** read like scenario 1.
3. MediaMTX posts a 200-character `action` → the warning carries a 64-character
   prefix plus a truncation mark, and not the whole value.
4. `read`, `playback`, `publish`, missing token, bad scope, offline stream — all
   answer exactly as before.

## Non-functional

- ADR-0050: `[LoggerMessage]` source-gen in the per-layer `Log` catalog,
  `this ILogger` extension methods, structured fields only.
- §II: the carried text is a **value object**, not a bare `string` — see plan D2.
- ADR-0141: absence is `Option<T>`, not a nullable parameter.

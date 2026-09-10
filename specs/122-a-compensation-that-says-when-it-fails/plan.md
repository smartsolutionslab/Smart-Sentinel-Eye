# Plan 122 — a compensation that says when it fails

## What was surveyed before choosing

- `HttpKeycloakAdminClient.TryDeleteClientAsync` — the whole defect, 15 lines.
- `KioskPrivilegeSweep.SweepAsync` — to establish what the named backstop
  actually does to a residue. It strips realm roles. It removes nothing. The
  re-enrolment block is untouched by it (spec.md §"The premise that has changed").
- `Log.cs` (Identity Infrastructure) — the existing
  `CouldNotRemoveHalfEnrolledClient` entry, which is the *exception* arm and
  whose message text says "the startup sweep will strip its privileges",
  implying the case is handled. It is not: privileges yes, the block no.
- `SystemVariables/Infrastructure/Log.cs:11` — the precedent for logging a
  status as `HttpStatusCode`, not `int`. Followed.
- `tests/Identity.Infrastructure.Tests/Fakes/StubKeycloakHandler.cs` and
  `CapturingLogger.cs` — both already exist, both from the last two specs.
  Neither needed inventing; both needed one addition.

## The decision

**Three arms, because 404 is not the same news as 409.**

```
204 (or any 2xx)  → nothing. The compensation worked.
404               → HalfEnrolledClientWasAlreadyAbsent   (Warning)
any other non-2xx → HalfEnrolledClientSurvived           (Error, + status)
exception thrown  → CouldNotRemoveHalfEnrolledClient     (Error, + exception)
```

The issue itself reaches the same split — *"a 404 is arguably fine (already
gone); a 409 or 5xx is a residue that will persist"* — and the measured table
in spec.md confirms Keycloak means it: 404's body is `Could not find client`.
Collapsing 404 into one "a client was left behind" message would be a log line
that states something false, which is the failure mode this repo keeps having
to correct. It is still logged, because a client that the re-probe found
moments earlier answering 404 to a delete is worth a line — as an absence.

A 404 carrying `Realm not found.` is the one ambiguity (both are 404). Reading
the body to split them buys a parse and a second failure mode for a case in
which the realm has vanished mid-enrolment and the enrolment error already
says so. Noted in a comment at the branch instead.

**Level: Error for a surviving residue.** The existing exception arm is
`Warning`, and this raises both to `Error`. Argued rather than assumed: a
Warning is the right level for something the system will clear on its own,
and that was the original claim — *"caught by the startup sweep"*. Having
read the sweep, it is not true of the residue *client*. Nothing in the system
removes it; the kiosk stays un-enrollable until a human deletes it. A
condition whose only remedy is manual intervention is an Error. The two arms
describe one condition — the delete definitively did not happen — so they take
the same level.

**Nothing is propagated to the caller (FR-005).** Two reasons, and the second
is the one that decides it:

1. The caller's error is the strip failure, which is the accurate account of
   why enrolment failed. The residue is an operator concern, and the operator
   reads logs.
2. This is a security-relevant path. The enrolment response is the wrong place
   to disclose that a client bearing the requested `clientId` now exists in the
   realm with a service account. It makes the residue marginally *easier to
   discover from outside*, which is the opposite of the constraint. A log record
   reaches the operator without reaching the requester.

Changing the API contract would in any case be a decision, not an
implementation — so the lane stops at the log, by design and not by omission.

## Phase 4a — RED, behaviour-changing

New behaviour: a log record that does not exist today. Red.

The red must **discriminate**, not merely fail. Three separate risks:

- A test that passes because the stub never reached the DELETE at all. Guarded
  by asserting the client DELETE was actually sent, and by driving the failure
  through the real flow (`CreateClientAsync` → strip 500 → compensating delete)
  rather than calling the private method.
- A test that reads message text and would pass on a message naming the wrong
  client. Guarded by asserting the **structured field** `ClientId`, which
  requires extending `CapturingLogger` to keep the state's key/value pairs —
  today it keeps only the formatted string.
- A test that cannot tell 404 from 409. Guarded by asserting each arm names a
  *different* event and that the 404 case does **not** raise the residue line.

## Phase 5 — verification

The status table in spec.md is already the live measurement, taken before any
code was written. Phase 5 re-states it and records the suite.

## Out of scope, stated

Retrying the delete; a background reconciler that removes residue clients;
teaching the sweep to distinguish a residue from a healthy kiosk. Each is a
larger design question and none is needed to make the failure visible.

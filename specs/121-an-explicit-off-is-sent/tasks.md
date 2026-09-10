# Tasks 121 — an explicit off is sent

Issues #2165 and #2207 (one defect, two independent filings). No per-task issues
— feature-level tracking on Project #13, per CLAUDE.md's corrected Phase 3 gate.

## T001 — survey every use of the serialiser (done in plan.md)

Enumerate all `JsonOptions` uses in `HttpKeycloakAdminClient`, split write from
read, and list every nullable and value-type member of every serialised payload.
**Done when** the table in `plan.md` is complete and the choice between removing,
narrowing, or per-property annotation is decided *from* it.

## T002 — phase 4a, red: the wire carries the explicit off

Hand-written `StubKeycloakHandler` recording method, path and body; three tests
against the real `HttpKeycloakAdminClient`.

**Done when** all three fail, for the stated reason (a property absent from the
body), and the failure output is captured verbatim. A test that passes here is a
phase-4 failure, not a shortcut.

## T003 — phase 4b, green: narrow the ignore condition

`WhenWritingDefault` → `WhenWritingNull`, one line.

**Done when** the three tests pass unmodified, and `dotnet build -c Release` is
clean.

## T004 — confirm nothing else changed shape

Re-read the three write sites against the survey: no payload gains a field, and
none gains a `null`.

**Done when** FR-004 is argued from the payload types, not from the tests.

## T005 — phase 5 verification note

`verification.md` recording the red, the green, the §IV finding (not on the
event-to-overlay path), and — if a stack is available — the stored flags read back
from a live Keycloak.

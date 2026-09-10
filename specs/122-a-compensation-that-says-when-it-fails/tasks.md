# Tasks 122 — a compensation that says when it fails

Issue #2166. One slice; each task is a commit that builds on its own.

## T001 — establish what the sweep does to a residue (done, in spec.md)

Read `KioskPrivilegeSweep.SweepAsync` and `GetEnrolledKioskClientIdsAsync`.
Verdict: strips realm roles, removes nothing, cannot tell a residue from a
healthy kiosk. The re-enrolment block survives every sweep.

## T002 — measure the DELETE's answers against live Keycloak (done, in spec.md)

`keycloak-18bcf406`, 26.6, realm `smart-sentinel-eye`. 204 / 404 / 401 table,
including a create-and-delete round trip for the 204 and the second 404.

## T003 — phase 4a, red: a refused compensation names the client

`tests/Identity.Infrastructure.Tests/KeycloakAdmin/HalfEnrolledClientCompensationTests.cs`.
Drive the real `HttpKeycloakAdminClient` through create → strip 500 → DELETE
answering 409 / 500 / 404. Assert the structured `ClientId` field, and that
404 does not claim a residue. Requires:

- `StubKeycloakHandler` to answer a chosen status per request;
- `CapturingLogger` to keep the state's key/value pairs, not only the message.

Observed failing; output verbatim in `verification.md` and the PR body.

## T004 — phase 4b, green: inspect the response

Three catalog entries in `src/Identity/Infrastructure/Log.cs`, one branch in
`TryDeleteClientAsync`, the `clientId` threaded from `CreateClientAsync`.
No throw, no change to the propagated exception.

## T005 — phase 5 verification note

`verification.md`: red verbatim, green verbatim, the live status table,
`dotnet build -c Release`, the full suite count, and the §IV statement.

# Tasks — 052 a wall past its ceiling

**Spec**: [spec.md](./spec.md) · **Plan**: [plan.md](./plan.md) · **Research**: [research.md](./research.md) · **Data model**: [data-model.md](./data-model.md) · **Contract**: [contracts/wall-display-grant.md](./contracts/wall-display-grant.md)

Eighteen tasks across backend, realm configuration and the kiosk app.

---

## Do not

- **Do not grant any new admin authority.** The containment works with what the
  identity service already holds — measured. Needing more is a signal the design
  has drifted, not a step to take.
- **Do not apply the strip to any account enrolment did not create.** It removes
  *every* direct realm mapping; against an operator that is destructive.
- **Do not add `offline_access` to `kiosk-web`.** A wall screen uses a different
  client. Adding it here locks out every operator, which is what made the
  previous attempt unshippable.
- **Do not touch the provider's session-timing settings.** They are provider
  defaults this repository does not set; pinning them is a separate decision.
- **Do not reserialise the realm file.** Edit it line by line — a round-trip
  through a JSON writer expands its compact arrays and turns a ninety-line
  change into four hundred, burying it.
- **Do not touch** per-device identity (issues 1987, 1988) or `management-web`.
- **Do not throw for expected failures** in C#. `Result<T, Error>`, and
  `Ensure.That` for argument guards (ADR-0105).
- **Do not write bare `#NNNN` issue numbers** in committed docs.

---

## Phase 1 — The containment *(the gate)*

- [x] T001 Add the role-removal calls to `src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs`: read the account's **direct** realm mappings, then delete that same list. **The shape is load-bearing** — a role object obtained any other way returns 404, which reads like a permission failure and is not (research §R2).
- [x] T002 Strip the privilege during enrolment in `src/Identity/Application/Kiosks/`, immediately after the client exists. Return `Result<T, Error>`; do not throw.
- [x] T003 **Fail the enrolment when the strip fails**, in the same handler in `src/Identity/Application/Kiosks/`. Its own task because it is the whole point: an enrolment that reports success while leaving a privilege holder behind is the outcome the containment exists to prevent.
- [x] T004 [P] Test in `tests/Identity.Tests/` that a strip failure **fails the enrolment** — induce a failure of the removal call *after* the client is created, and assert the result is a failure. Assert on the reported outcome, not on an exception type.
- [~] T005 Add the startup sweep over accounts enrolment created, in `src/Identity/Application/Kiosks/`. Idempotent, so every start is safe and it doubles as reconciliation against drift.
  **Half of this shipped, and the tick claimed all of it.** `KioskPrivilegeSweep` was written — in `Application/KeycloakAdmin/`, not the `Application/Kiosks/` this line names — and wired to nothing. It implements neither `IHostedService` nor `BackgroundService`, and it had no DI registration and no caller of any kind, so **no start has ever run it**. Its five unit tests were green because every one of them constructs the class, which is precisely what nothing in production did. Spec 092 supplies the startup service and the registration; the correction is recorded here against what shipped, and **no other tick in this file is changed**.
- [x] T006 [P] **Test that the sweep does not touch a human account** in `tests/Identity.Tests/` — run it with an operator account present and assert **the operator still holds what it held**. Asserted on the operator, not on the absence of a crash: a sweep that matched everything would remove every role an operator has and still not throw.
- [x] T007 [P] Integration test against the Aspire fixture in `tests/Identity.IntegrationTests/`: enrol a kiosk, then **ask the running provider** what that account effectively holds, and assert the privilege is absent. **This is the US1 claim and only the provider can answer it.**

**Checkpoint — this is a security gate, not paperwork.** Spec 049 refused this
feature for widening who may mint a long-lived credential; spec 050 shipped the
widening with a containment that was true of a file and false of every running
system. **Nothing in Phase 2 or beyond may land until T001–T007 are green.**

---

## Phase 2 — The wall client and how a screen is told apart

- [x] T008 Declare the `kiosk-wall` client in `src/AppHost/Realms/smart-sentinel-eye-realm.json` — **edited line by line** — with the five read scopes, `sse-identity` and `sse-groups` as defaults, `offline_access` as **optional**, and **no `sse.events.write`**.
- [x] T009 Select client and scopes from one deployment flag in `apps/kiosk-web/src/app/auth.ts`: wall mode uses `kiosk-wall` and requests `openid offline_access`; anything else uses `kiosk-web` and requests `openid`. **One flag decides both**, so there is no half-configuration to get wrong.
- [x] T010 [P] Architecture test in `tests/Architecture.Tests/` for the realm's **declared** shape: the wall client omits the write scope, and `offline_access` is optional rather than default. **Label it as covering declaration only** — it is exactly the kind of check that passed for spec 050's whole feature while the claim it stood for was false, so it must not be offered as evidence for who *holds* the privilege.
- [x] T011 [P] Unit test in `apps/kiosk-web/src/app/auth.test.ts` that the two modes produce the two client/scope pairs, and that **no mode produces `kiosk-web` asking for `offline_access`** — that combination is the lockout.

---

## Phase 3 — US2: the wall outlives its ceiling *(P1)*

- [x] T012 [US2] End-to-end test in `e2e/kiosk-wall-outlives-its-session.spec.ts` that a wall screen's refresh token is an **offline grant carrying no expiry** — decoded, not counted. **This is the primary proof**: asserting a token exists passes today with the defect fully present.
- [x] T013 [US2] End-to-end test in `e2e/kiosk-wall-outlives-its-session.spec.ts`, **gated behind an explicit flag**, that a screen survives a ceiling shortened on a test realm. The test's own comment must say it **demonstrates the mechanism and not the production configuration**, and so must this task and the verification note — three places, and spec 050 did three and still needed correcting. **Note in the test: shortening the ceiling breaks the e2e seeds**, because they drive a long operator session that expires mid-run; spec 050's run worked only because the dev database already held published layouts.
- [x] T014 [US2] [P] End-to-end test in `e2e/kiosk-operator-unchanged.spec.ts` that an **operator's session is unchanged** and that every account which could sign in before still can. FR-007 and FR-008; "we did not touch them" is an argument, not evidence.

---

## Phase 4 — US3: what a wall display may do *(P1)*

- [x] T015 [US3] End-to-end test in `e2e/kiosk-wall-authority.spec.ts` that **enumerates the scopes out of the token the wall account actually receives**, asserts `sse.events.write` is absent, and exercises **every scope that is present**. Spec 050 asserted refusals on three endpoints somebody chose and never attempted the one the account held — which is how "the account can change nothing" was recorded while false.
- [x] T016 [US3] [P] End-to-end test in `e2e/kiosk-wall-authority.spec.ts` that a wall display **cannot read another fab**, with a control: the same request from that fab's own screen must return rows, or an empty result proves only that the query matched nothing.

---

## Phase 5 — The misconfigured screen

- [x] T017 Add `not_allowed` to the refused codes in `apps/kiosk-web/src/app/identityFailure.ts`, and test in `apps/kiosk-web/src/App.test.tsx` that such a screen shows the **terminal** state. A wall-mode screen signed in as an operator gets that code; unrecognised codes default to recoverable, so today it would retry forever behind "Reconnecting", telling whoever reads it that this will clear. **This feature makes the code reachable, so this feature fixes it.**

---

## Phase 6 — The record and verification

- [x] T018 Write the ADR — **superseding ADR-0132 properly** rather than leaving two records of the same idea — then `verification.md`, then update constitution §Availability. **State what could not be done**: twenty screens, a real power cut, ten hours in production, and an account created by hand in the provider's console, which this feature deliberately does not cover. Do not let the note imply §Availability is discharged.

---

## Mutations that must each kill a test

| # | Mutation | Must be killed by |
|---|---|---|
| 1 | Make `offline_access` a **default** scope on the wall client | T010, T014 (operators locked out) |
| 2 | Give the wall client `sse.events.write` | T010, T015 |
| 3 | Skip the strip when enrolment succeeds | T007 |
| 4 | Make the strip swallow its own failure | T004 |
| 5 | Let the sweep match **every** account, not kiosk accounts | T006 |
| 6 | Remove `not_allowed` from the refused set | T017 |
| 7 | Assert the wall grant by a token's **presence** rather than its type | T012 (must fail against an ordinary session) |
| 8 | Point the wall mode at `kiosk-web` | T011 |

**Mutation 3 is the one to run first.** It is the defect spec 050 shipped, and
it is invisible to every check that reads the realm file.

---

## Dependencies

```
T001 ─▶ T002 ─▶ T003 ─▶ T004
          └──▶ T005 ─▶ T006          Phase 1 (SECURITY GATE)
          └──▶ T007
                 │
                 ▼
T008 ─▶ T009 ─▶ T010, T011           Phase 2
                 │
      ┌──────────┼──────────┬─────────────┐
      ▼          ▼          ▼             ▼
T012 ─▶ T013   T014      T015 ─▶ T016   T017
   US2 (P1)              US3 (P1)       misconfig
      └──────────┴──────────┴─────────────┘
                     ▼
                   T018
```

**Phase 1 gates everything.** US2 and US3 are independent of each other once the
wall client exists.

---

## Parallel opportunities

- **T004, T006, T007 are parallel** — three separate claims about the
  containment, in different test files.
- **T010 and T011 are parallel** — one reads the realm, the other the app config.
- **US2 and US3 are parallel** once Phase 2 lands; they share no files.
- **T014 and T016 are parallel with everything in their phases.**
- **T001–T003 are strictly sequential** — one handler, one call chain.

---

## Implementation strategy

**Phase 1 first, and it is a gate rather than a phase.** The feature's whole cost
is that a credential may now outlive a session. That cost is acceptable only if
the privilege reaches accounts that show cameras and nothing else — and today it
reaches every account the system creates. Building Phase 2 first would ship the
cost without the containment, which is precisely what was withdrawn last time.

**Ask the provider, not the file.** Every US1 check queries a running provider.
The one file-reading test, T010, covers the realm's *declared* shape and says so
in its own comment, because that is the check that passed for spec 050's entire
feature while the claim was false.

**The feature issue is on Project #13** — Phase 3's gate is satisfied.

**Coverage gates apply here, and may be cited.** Identity's Application layer is
touched, so ADR-0065's **≥80% Application** threshold is live. The last two
specs correctly said no gate applied; this one does, and a coverage number is
legitimate evidence for once.

**The residual gap stays separate.** An account created by hand in the provider's
console still inherits the privilege. It is filed, it is named in the spec
(FR-002a), and it **must not be folded into this feature's claims** — SC-004 is
about accounts this system creates or declares, and nothing more.

---

## Three things most likely to go wrong

1. **A file-reading test is offered as proof of who holds the privilege.** It
   cannot be. The realm file describes what is *declared*; the defect lives
   entirely in what is *created afterwards*. Spec 050's guard was green for the
   whole feature while every enrolled kiosk held the privilege. T007 exists for
   this and mutation 3 is the first to run.

2. **The wall client is missing a scope the wall actually needs.** No call to
   event ingestion appears in the kiosk source, but "signs in" and "renders a
   wall" are different claims, and only the second is the product. T015 must open
   a wall, not merely authenticate.

3. **The sweep is written to match more than it should.** It removes every direct
   realm mapping, so a pattern matching an operator would strip that operator to
   nothing — and would not throw while doing it. T006 asserts on the operator
   rather than on the run completing.

---

## What the automated checks do and do not prove

| Claim | Proved by | Not proved by |
|---|---|---|
| An enrolled kiosk holds no privilege | **T007, asking the provider** | T010, which reads the file |
| The strip cannot fail silently | T004 | the happy path passing |
| The sweep is safe on humans | T006, asserting on the operator | the sweep not crashing |
| The wall grant outlives a session | T012, decoding the type | a token existing |
| A wall display cannot write | T015, the scope's **absence** from the issued token | three endpoints returning 403 |
| Operators gained nothing | T014, signing one in | not having edited them |
| A misconfigured screen says something true | T017 | the classification alone |
| **Ten hours in production** | **nothing** | a shortened ceiling shows the mechanism |
| **Twenty screens** | **nothing** | four, once, in spec 051 |
| **A real power cut** | **nothing** | a reload |
| **An account created by hand** | **nothing — it is not covered** | this feature, which excludes it |

The last four rows are the honest ones, and the last is the price of not taking
authority broader than the privilege it would contain.

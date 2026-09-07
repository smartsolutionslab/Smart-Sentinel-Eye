# Spec 092 — The kiosk privilege sweep is wired to the failure it was written to catch

**Issue:** #2132 — *KioskPrivilegeSweep is registered nowhere, and spec 052 ticks
it as done*
**Branch:** `fix/2132-the-sweep-is-registered-or-retired`
**Phase:** 1 (Specify) — ADR-0037

**ADRs:** ADR-0134 (a wall display holds a grant nobody else can; §Decision 1
names the sweep — the decision this spec makes true), ADR-0007 + ADR-0008
(Keycloak per fab, OIDC), ADR-0051 (per-context `Add<Context>Infrastructure`
registration — where the wiring goes and where it must *not* go), ADR-0103
(integration tests via the Aspire fixture; no Testcontainers), ADR-0105
(`Ensure.That` guards), ADR-0049 (`CancellationToken` last), ADR-0050
(`[LoggerMessage]` source-gen), ADR-0052 (xUnit + Shouldly), ADR-0036 (smallest
change), ADR-0084 (code metrics), ADR-0109 (parallel markers), ADR-0139 +
ADR-0144 (phase 4a has two colours and no exemption).

**Constitution:** §Testing — new behaviour is observed failing first. This spec
is entirely new behaviour (a service that has never run), so **red**, not
characterisation. §II is not engaged: nothing here touches a domain model.

**No new ADR is required, and none is written.** See §"The ADR verdict".

**Latency budget:** N/A. Nothing here is on the event→overlay path. The sweep
runs once at Identity API startup and touches no leg in constitution §IV.

---

## The verdict: register, and for a reason spec 052 never gave

The issue offers two outcomes and states no preference. The evidence picks
**register** — but it also disqualifies the justification spec 052 wrote down,
and supplies a different one that the production code already documents and that
nothing covers.

**The population spec 052 named is empty, and provably so.** The sweep's own
docstring says it exists for "every kiosk enrolled before this existed". There
are none, anywhere:

| Environment | Population | How established |
|---|---|---|
| The persistent run-mode realm | **0** | Booted a throwaway Keycloak 26.6 against a *copy* of `smartsentineleye.apphost-0d1b3f6bcf-keycloak-data` and asked the Admin API. 15 clients: the 9 declared in `src/AppHost/Realms/smart-sentinel-eye-realm.json` plus Keycloak's 6 built-ins. **Zero carry `sse.kind`.** |
| The three orphaned run-mode volumes | **0** | `strings` over each H2 store. Counterfactual first: `kiosk-web`, `kiosk-wall`, `management-web`, `identity-admin`, `offline_access` and `sse.cameras.read` all appear; `sse.kind` and `sse.fab` appear **0 times in all four**. The method has signal, so the absence means something. |
| CI and e2e | **0 by construction** | Fresh realm import per run, and no declared client carries `sse.kind` — only `EnrollKiosk`/`RegisterDevice`/`RotateWebhookClient` stamp it at runtime. |
| Production | **does not exist** | `deploy/` holds one `values.yaml` and a README for Mosquitto. The Aspire k8s publisher has never been run (ADR-0130). |

Confirming #1995 / spec 080 from the other direction: the realm's
`default-roles-smart-sentinel-eye` composite **does** include `offline_access`,
and **every declared service account holds zero direct realm roles**. A fresh
import grants declared accounts exactly what they name. So the backfill
population is empty and cannot refill by import.

**And it is empty going forward too.** `HttpKeycloakAdminClient.CreateClientAsync`
strips inside the create (spec 052 T001/T002), and a failed strip deletes the
client (T003). A kiosk cannot be *enrolled* holding the privilege.

**But one path leaves one behind, and the code says the sweep is what catches
it.** The best-effort comment in `TryDeleteClientAsync`
(`src/Identity/Infrastructure/KeycloakAdmin/HttpKeycloakAdminClient.cs:363`):

```csharp
catch (Exception exception) when (exception is not OperationCanceledException)
{
    // Best effort. The caller is already failing and will report that;
    // a client left behind is caught by the startup sweep, which is
    // exactly the backstop it exists to be.
    logger.CouldNotRemoveHalfEnrolledClient(clientUuid, exception);
}
```

When the strip throws *and* the compensating delete also fails, a client stamped
`sse.kind=kiosk` survives holding `offline_access`, with a service account and a
secret the caller never received. Enrolment reported failure, so nobody retries;
and `CreateClientAsync`'s existence probe will answer "already enrolled" for it
forever. **That is a live, ongoing, uncovered failure mode, and the production
comment delegates it by name to a service that has never run.**

So: the sweep's *stated* reason is dead, its *coded* reason is alive, and the
honest close is to wire it and correct the docstring to say which is which.

**This is a better outcome than either the issue anticipated**, and it is worth
naming why the "retire" reading was tempting: everything about the sweep's own
documentation points at an empty population. The justification that survives is
one paragraph in a different file, in a different layer.

### What would have changed the verdict

Had `TryDeleteClientAsync` propagated instead of swallowing, no residue could
exist and **retire** would have been correct. Recording that because it is a
single line away, and because a later change to that catch changes this spec's
conclusion.

---

## The ADR verdict

**Not an ADR. Phases 1–3 proceed. No ADR is written or amended.**

ADR-0134 §Decision 1 already decides this, in terms:

> Enrolment removes it as part of creating a kiosk, and a startup sweep covers
> the kiosks enrolled before this existed.

The sweep is a *named component of an Accepted decision*. Registering it **makes
the mechanism that sentence names exist**; it does not choose between
alternatives, move a boundary, change a technology, or alter a cross-context
contract. **Nothing is being decided; something recorded as decided is being
connected.**

> **Corrected at phase 6.** This paragraph originally said registering "makes
> that sentence true", and the sentence is *"a startup sweep covers the kiosks
> enrolled before this existed"*. **Registering cannot make that true**, because
> this spec's own §"The verdict" proves the population it names empty in every
> environment that exists. What registering makes true is the mechanism half —
> there is now a startup sweep, and it runs. The justification half of §Decision
> 1 stays false, and it is false in the way this repository has had to correct
> before: a stated reason nobody re-checked. **Correcting it wants its own
> issue**, because amending an ADR is a blocked outcome for the autonomous lane
> (ADR-0144) and is not attempted here.

The adjudication the issue asks for is worth stating in full, because it points
the other way and the difference is the whole shape of the change:

**Retiring the sweep *would* have been ADR-scale, and blocked.** Deleting it
falsifies ADR-0134 §Decision 1, which would then need amending — and amending an
ADR is a blocked outcome for the autonomous lane (ADR-0144), as is superseding
it with a new one. Retire had no unblocked path. Register does. **This is not
why register was chosen** — the `TryDeleteClientAsync` backstop is — but had the
evidence gone the other way, phases 1–3 would have ended here with a hand-back
rather than a `tasks.md`.

One consequence worth recording: **ADR-0134 §Decision 1 has been false since it
was accepted on 2026-08-31**, in the same way constitution §IV's leg table and
`specs/052/tasks.md:37` were false — a record nobody checked against what
happens. This spec builds the mechanism it names, which is the cheaper of the two
repairs and the only one the lane may perform. It does **not** make the whole
sentence true: the population clause stays false, and squaring that with the ADR
is a separate issue for a human (see the correction above).

---

## User stories

### US1 (P1) — A kiosk left behind by a failed enrolment loses the privilege at the next start

**The whole slice.** A residue client — `sse.kind=kiosk`, holding the realm's
inherited `offline_access`, produced when enrolment's rollback could not delete
it — is stripped when the Identity API next starts, and the run is visible in the
logs.

Independently shippable and independently observable: plant a residue client
against the running provider, start the Identity API, ask the provider what that
account holds.

### US2 (P2) — The record stops ticking a mechanism that never ran

`specs/052/tasks.md:37` marks T005 done. It shipped a class and no wiring.
Correcting that one tick, against what actually shipped.

**Deliberately not**: making the rest of `specs/052/tasks.md` agree with itself.
The issue forbids it and it is the wrong repair.

---

## Acceptance scenarios

### US1 — happy path

```gherkin
Given a Keycloak client stamped sse.kind=kiosk exists in the realm
  And its service account holds the realm's inherited offline_access privilege
When the Identity API starts
Then the sweep asks the provider for every client stamped sse.kind=kiosk
  And that account's direct realm role mappings are removed
  And asking the provider for the account's effective realm roles no longer
      returns offline_access
```

### US1 — the sweep is bounded (conflict case)

```gherkin
Given an account the enrolment path did not create is present in the realm
  And it holds realm roles it was deliberately given
When the sweep runs
Then that account is not read and not modified
  And it still holds every realm role it held before
```

*The removal takes away **every** directly-assigned realm role. Applied to an
operator this strips them of everything and does not throw while doing it — so
this must be asserted on the account, never on the run completing.*

### US1 — the provider is unreachable at start (bad-request / failure case)

```gherkin
Given Keycloak is not reachable when the Identity API starts
When the sweep's startup pass runs
Then the failure is logged
  And the Identity API host starts and serves requests
  And the next start tries again
```

*This is the case the sweep as written gets wrong: the enumeration that opens
`SweepAsync` (`KioskPrivilegeSweep.cs:56`) sits **outside** its try, so an
unreachable provider throws out of the pass. Registered as an `IHostedService`
that stops the host — the Identity API would fail to boot whenever Keycloak is
slow. Identity must not require Keycloak to be up in order to start.*

### US1 — one kiosk unreachable, the rest still swept

```gherkin
Given three kiosk accounts are present
  And the provider refuses the removal for exactly one of them
When the sweep runs
Then the other two are stripped
  And the refused one is named in the outcome and logged as still holding it
```

### US1 — auth / authority

```gherkin
Given the sweep runs as the identity-admin service account
When it enumerates clients and removes realm role mappings
Then it needs no authority beyond what enrolment already exercises
```

*ADR-0134: "It needs no new authority." Both calls are already made by the
enrolment path against the same account. **No new admin authority may be
requested** — needing it is a signal the design drifted, not a step to take.*

### US1 — the steady state says nothing

```gherkin
Given no client in the realm is stamped sse.kind=kiosk
When the Identity API starts
Then the sweep completes without logging that it did nothing
```

*This is the expected state on every start in every environment that exists
today. `StreamFabAttributionService` already made this call and wrote down why:
"A line per restart saying nothing happened trains an operator to skip the one
that matters." The sweep currently logs `SweptKioskPrivileges(0, 0)`
unconditionally at Information.*

### US2

```gherkin
Given specs/052/tasks.md:37 marks T005 complete
  And no registration for the sweep shipped with spec 052
When the record is corrected
Then T005 states what shipped and what did not, and cites this spec
  And no other tick in that file is changed
```

---

## Independent end-to-end test procedure

Run against the Aspire stack, by hand, without reading any source file:

1. Boot the AppHost. Note the Identity API is serving.
2. Using the `identity-admin` client, create a Keycloak client directly through
   the Admin API — `serviceAccountsEnabled: true`, and attributes
   `sse.kind=kiosk`, `sse.fab=munich`. Do **not** go through `POST /kiosks`;
   creating it directly is what makes it a residue rather than an enrolment.
3. `GET /admin/realms/smart-sentinel-eye/users/<sa>/role-mappings/realm/composite`
   for that account. **Confirm `offline_access` is present.** If it is not, the
   rest of this procedure proves nothing — stop and say so.
4. Create an operator-shaped client the same way, *without* the `sse.kind`
   attribute, and record the realm roles its service account holds.
5. Restart the Identity API resource.
6. Re-read step 3's endpoint. `offline_access` is **absent**.
7. Re-read step 4's account. Its realm roles are **unchanged**.
8. Read the Identity API's logs for the sweep's completion line. It names a
   non-zero count.
9. Restart the Identity API again. Step 6 still holds, and no new line is logged
   — there is nothing left to do.
10. Delete both probe clients.

**Step 4 and step 7 are the ones that matter.** A sweep whose idea of "a kiosk"
matched everything would pass steps 6 and 8 on the way past.

---

## Locked tech choices

- **.NET 10 + ASP.NET Core + Aspire** (ADR-0024). Aspire is the composition root.
- **`IHostedService`, not `BackgroundService`** — one pass at start, then done.
  `StreamFabAttributionService`, `MediaMtxReconciler`,
  `ReverseIndexSeederHostedService` and `RuleCacheSeederHostedService` are the
  four existing one-pass-at-start services and all four are `IHostedService`.
- **`IServiceScopeFactory`** — `IKeycloakAdminClient` is registered `AddScoped`
  at the end of `AddKeycloakAdminClient`
  (`IdentityInfrastructureModule.cs:161`); a hosted service is a singleton and
  cannot take it directly. `StreamFabAttributionService` is the pattern to
  mirror exactly.
- **Registration in `AddIdentityInfrastructure`** (ADR-0051), **not** in
  `AddKeycloakAdminClient` — `MigrationRunner/Program.cs:60` calls the latter
  too, and MigrationRunner must not sweep.
- **`[LoggerMessage]` source-gen** in the existing `Identity/Infrastructure/Log.cs`
  (ADR-0050). *Corrected at phase 6: this originally said
  `Identity/Application/Log.cs`. The new message reports that the **wrapper's**
  pass failed, and the wrapper is in Infrastructure — plan §VII already placed it
  next to `CouldNotRemoveHalfEnrolledClient`, which is where it went.
  `Identity/Application/Log.cs` is unchanged by this branch.*
- **xUnit + Shouldly + the Aspire fixture** (ADR-0052, ADR-0103). No
  Testcontainers.

---

## What the issue gets wrong

Checked because seven of nine issues this session carried a false premise.

**The central claim is true.** `KioskPrivilegeSweep` has no registration
anywhere. Verified independently: `grep -rn "KioskPrivilegeSweep"` across the
whole worktree returns its own file, its test file, and two references in
`specs/087-*`. Nothing else. `git log --all -S 'KioskPrivilegeSweep' -- src/`
returns exactly one commit, `214d91ff`, which touches no DI module. **It was
born unwired and has never been otherwise.**

Three corrections, none of which change the verdict:

1. **"No `AddHostedService` registration" is true but misleading.**
   `KioskPrivilegeSweep` is a plain class — it implements neither
   `IHostedService` nor `BackgroundService`. `AddHostedService<KioskPrivilegeSweep>`
   would not compile. It has **no DI registration of any kind**, and no caller.
   It is unreferenced production code, which is a wider defect than an unwired
   hosted service. This matters for phase 4: the fix is not a one-line
   `AddHostedService`, it is a wrapper plus a registration.

2. **"If the sweep is still needed, registering it is small" understates it.**
   Three things are needed, and two are load-bearing: an `IHostedService`
   wrapper, an `IServiceScopeFactory` (the admin client is Scoped), and a catch
   around the *whole* pass — because the enumeration that opens `SweepAsync`
   (`KioskPrivilegeSweep.cs:56`) is outside the existing try, and a hosted
   service that throws in `StartAsync` stops the host. Registering it as written
   makes the Identity API's boot depend on Keycloak being reachable.

3. **"The population needing a backfill may be smaller, or empty"** — it is
   empty, everywhere, and it can never refill from the direction spec 052 was
   worried about. The issue treats that as pointing toward retire. It does, and
   the `TryDeleteClientAsync` backstop outweighs it. The issue does not mention
   that path, and it is the only reason the sweep survives.

**Two related defects found, both out of scope here** (smallest change —
ADR-0036), both worth their own issue:

- `TryDeleteClientAsync` (`HttpKeycloakAdminClient.cs:353-368`) never inspects
  the response. A DELETE answering 404, 409 or 500 leaves the client behind
  **with no log line at all** — only a thrown exception is logged. So the residue
  population this spec covers can be created silently.
- Of `KioskPrivilegeSweepTests`' five tests, **two cannot fail from a defect in
  the class under test**. See §"Whether the existing tests can fail".

---

## Whether the existing tests can fail

Established by counterfactual, not by reading. Baseline: 5 passed.

**`Sweeping_twice_does_no_more_than_sweeping_once` is vacuous.** Mutating
`SweepAsync` to call `StripInheritedRealmRolesAsync` **three times per kiosk**
leaves all five tests green:

```
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5
```

`FakeKeycloakAdminClient.Stripped` is a `HashSet<string>` and `CallCount` is
never asserted, so no number of repeated calls is observable. The property the
test's name and docstring claim — that a second pass does no more than the first
— lives in `HttpKeycloakAdminClient`'s `if (assigned.Length == 0) return;` early
exit, in a project this test assembly does not reference. **The test duplicates
`Strips_every_kiosk_this_system_enrolled` under an idempotency label.**

**This finding is filed on issue #2151**, alongside
`Does_not_touch_an_account_this_system_did_not_enrol`, which that issue already
named. It was found here and it is recorded here, but a finding whose only home
is a spec.md about to merge is a finding nobody reads again — so it was added to
the issue that already owns the other one. #2151 argues against deleting either
test; nothing here changes that.

**`Does_not_touch_an_account_this_system_did_not_enrol` is vacuous in its
load-bearing half** — already found as F8 in `specs/087-*/spec.md:324`, marked
CONFIDENT, and left as a new issue. Confirmed here by construction, which is
stronger than the census's reading: `KioskPrivilegeSweep` contains **no filter**.
It strips whatever `GetEnrolledKioskClientIdsAsync` returns. The boundedness the
test asserts is implemented in `FakeKeycloakAdminClient` (test-side) and in
`HttpKeycloakAdminClient` (Infrastructure). `Identity.Application.Tests`
references neither Infrastructure nor anything that could reach it — its
`ProjectReference` set is Application, Domain, Shared.Kernel, Shared.CQRS,
Shared.Contracts. **No possible change to the production filter can make this
test red.** Its `ShouldContain("kiosk-a")` half is real; the `ShouldNotContain`
half asserts a property of the fake.

**The other three are real, and all five catch the grossest mutant.** Replacing
the strip call with `await Task.CompletedTask` fails four of five. So the suite
is not worthless — it is weaker than it reads, in two specific places.

**None of the five can fail on the defect this spec fixes.** All five construct
`KioskPrivilegeSweep` directly. The class working while wired to nothing is
exactly what they assert. That is the finding that generalises: a unit suite that
news up its subject cannot see that nothing news it up in production.

**Neither vacuous test is deleted or weakened by this spec** (ADR-0144 forbids
it, and it would be wrong regardless). US1's tests are additive.

---

## Any other unregistered hosted service

**No. The census is 11 of 11.** Every `IHostedService` / `BackgroundService`
implementation under `src/` has exactly one `AddHostedService` call site:

| Service | Registered in |
|---|---|
| `AuditRetentionHostedService` | `AuditObservabilityInfrastructureModule.cs:91` |
| `RuleCacheSeederHostedService` | `AutomationInfrastructureModule.cs:50` |
| `MqttSubscriberHostedService` | `EventIngestionInfrastructureModule.cs:131` |
| `PersistenceLoopHostedService` | `EventIngestionInfrastructureModule.cs:132` |
| `ScenarioSeeder` | `ScenarioSimulator/Program.cs:71` |
| `CameraSimReconciler` | `ScenarioSimulator/Program.cs:77` |
| `BilletTimelineHostedService` | `BilletTimelineExtensions.cs:24` |
| `StreamHealthWatcher` | `StreamDistributionInfrastructureModule.cs:106` |
| `MediaMtxReconciler` | `StreamDistributionInfrastructureModule.cs:107` |
| `StreamFabAttributionService` | `StreamDistributionInfrastructureModule.cs:108` |
| `ReverseIndexSeederHostedService` | `SystemVariablesInfrastructureModule.cs:102` |

**So this is one instance, not a class of defect** — and the reason it escaped
the shape of a census is precisely correction 1 above: the sweep is not a hosted
service, so a search for unregistered hosted services would never have found it.
The category it belongs to is *unreferenced production classes*, which nothing in
this repository currently measures.

---

## Phase 4a — what the red is

**Colour: red.** This is new behaviour — a service that has never executed. Not
characterisation. Constitution §Testing, ADR-0139, ADR-0144.

The issue asks the sharp question, and it deserves a direct answer:
**two tests, doing different jobs, and only one of them is load-bearing.**

**Red A — the behavioural one. This is the claim.** An integration test against
the Aspire fixture (ADR-0103) that plants a residue client through the Admin API
— `sse.kind=kiosk`, holding the inherited privilege — drives one sweep pass
through Identity's own registration, and then **asks the
provider** what that account holds. Plus a control: an account without the stamp,
asserted unchanged — **with a residue planted alongside it and asserted stripped
in the same test**, because with the steady-state line silenced a control-only
run over a residue-free realm is satisfied by a pass that did nothing at all. It
goes red today because there is no wired pass to drive.

> **Corrected at phase 4a.** This paragraph originally said the pass was
> "resolved from the running Identity API's own container". **It cannot be.**
> The Identity API is a separate process and its container is not addressable
> from the test process. What *is* addressable is `AddIdentityInfrastructure` —
> the one line `Identity/Api/Program.cs` calls, and the line that carries the
> whole defect. Red A composes that extension in the test process against the
> fixture's real Keycloak and starts only the startup services Identity's own
> assembly registers, so removing the registration still turns it red.
> **Phase 5 must not inherit the original belief:** nothing in this suite
> observes a boot, and the paragraph below already says so.
This mirrors `KioskInheritedPrivilegeIntegrationTests` exactly, which is spec
052's T007 and the only check that could answer its question.

*Driving one pass rather than restarting the host is the established pattern, not
a shortcut:* `StreamFabAttributionService` exposes `AttributeOnceAsync` publicly
for precisely this, and the `AspireFixture` boots once per collection so a
restart-based assertion would be both slow and shared-state-dependent.

**Red B — the wiring one, and it must be labelled.** An assertion that the
Identity API's service collection contains the hosted-service registration. This
is a **declaration-only** check and must say so in its own docstring, exactly as
spec 052's T010 labelled its realm-file guard. It goes red today.

**Why both, and why B alone would be the wrong red:** Red A can be satisfied by
resolving the sweep directly, without anything registering it — which is the
defect. Red B pins registration but reads the container, and this repository has
been burned twice by guards that read the artefact instead of the system (spec
050's realm-file guard stayed green for a whole feature while its claim was
false; and this very sweep's unit suite is green over a class wired to nothing).
**Red B is the weaker test and is included because Red A cannot see startup
wiring, not because container introspection is good evidence.**

**What neither proves, and phase 5 must:** that the pass actually ran *at boot*
in a real stack. The only evidence for that is the sweep's own log line, observed
in a running Identity API — step 8 of the end-to-end procedure. Do not let a
green Red A + Red B stand in for it. That substitution is the exact defect this
spec exists to repair.

**Red C — the unreachable-provider case.** A unit test that the pass does not
propagate when the *enumeration* fails, not only when a per-kiosk strip fails.
Red today: the enumeration that opens `SweepAsync`
(`KioskPrivilegeSweep.cs:56`) sits outside the try, so it throws.
`FakeKeycloakAdminClient.FailNextCall` already provides the seam.

**US2 discharges phase 4a differently and says so.** Correcting a tick in
`specs/052/tasks.md` is documentation. Under ADR-0037 that is a
skipped-phase case — `Phase 4a: skipped — documentation-only correction` in the
PR body. **Not a silent skip, and no test is manufactured for it.**

---

## Out of scope

- **Deleting or weakening `KioskPrivilegeSweepTests`.** The two vacuous tests are
  recorded here as findings and left green (ADR-0144: no gate may be weakened to
  reach green).
- **Fixing `TryDeleteClientAsync`'s unchecked response.** Separate defect,
  separate issue. Fixing it here would also change this spec's own premise.
- **Folding `RealmProbe` together with
  `KioskInheritedPrivilegeIntegrationTests`' private helpers.** Recorded at
  phase 4a, deliberately not repaired: Red A's `RealmProbe` duplicates that
  file's private admin-token, effective-realm-roles and delete helpers, so the
  same three shapes now exist twice in `tests/Integration.Tests/Identity/`.
  That file is spec 052's and is left untouched — extracting a shared helper
  would edit a passing test this spec has no claim on, for tidiness. It is a
  tidy-up for whoever next has a reason to open it, and the duplication is
  small, self-contained and named at both sites rather than left to be
  discovered.
- **Any other tick in `specs/052/tasks.md`.**
- **New admin authority.** ADR-0134 measured that none is needed.
- **The manual-console case.** ADR-0134 / spec 052 T018 declared an account
  created by hand in the provider's console explicitly out of scope, and this
  spec does not quietly bring it in — the sweep will happen to convert such an
  account if it carries `sse.kind=kiosk`, which by hand it would not.

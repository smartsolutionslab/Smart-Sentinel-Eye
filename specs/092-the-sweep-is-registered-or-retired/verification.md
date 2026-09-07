# Verification — 092 the kiosk privilege sweep is wired to the failure it was written to catch

**Feature:** 092 · **Issue:** #2132 · **Date:** 2026-09-07
**Branch:** `fix/2132-the-sweep-is-registered-or-retired`

Phase 5 (ADR-0037). This note exists because the branch's own artefacts say
five times that nothing in the suite proves the pass runs *at boot* —
`spec.md` §"Phase 4a" puts it hardest:

> **What neither proves, and phase 5 must:** that the pass actually ran *at
> boot* in a real stack. The only evidence for that is the sweep's own log
> line, observed in a running Identity API — step 8 of the end-to-end
> procedure. Do not let a green Red A + Red B stand in for it. That
> substitution is the exact defect this spec exists to repair.

So the question here is **not** "do the tests pass". Every test on this
branch composes `AddIdentityInfrastructure` in the test process and hands it
a configuration dictionary it wrote itself. The specific thing none of them
can see: in the real container the pass resolves `KioskPrivilegeSweep`, which
builds `HttpKeycloakAdminClient`, which forces
`IOptions<KeycloakAdminOptions>` — and that binder throws from
`IdentityInfrastructureModule.cs:144` if Aspire injects the Keycloak endpoint
under a key it does not read. The wrapper catches everything, logs one
Warning and returns. All three reds would stay green and the sweep would
never sweep, silently, for as long as the realm lives. That is #2132 again,
one layer up.

**It does not happen.** §2 is the evidence.

---

## 1. How this was observed

Windows 11, .NET 10, Release build, real Docker-backed `AspireFixture` boot
in `isE2ETests` mode (ephemeral containers; none were left behind — `docker
ps` counted 0 afterwards).

The procedure driven is `spec.md` §"Independent end-to-end test procedure",
steps 2–10. It was driven by a **scratch harness** in `Integration.Tests`
rather than typed at a console, because steps 5–9 need the `identity`
resource restarted and its console read, and `ResourceCommandService` +
`AspireFixture.RecentLogs` are how this repository already does both
(`LogTailDeliversIntegrationTests`, `RestartLosesNothingIntegrationTests`).
The harness was **removed before commit**; nothing in the tree references it.

What matters is what the harness did *not* do: it did not compose Identity.
The `identity` resource is the real project, launched by Aspire with
Aspire-injected configuration, restarted through Aspire's own restart
command. The harness only planted clients through the Admin API, read the
provider back, and sampled the resource's console.

**One correction to the method, recorded because the first attempt got it
wrong.** `RecentLogs` is a 400-line rolling queue that lags the boot. Read
once per restart, the first run showed no sweep line for boot 1 and one for
boot 2 — an artefact of the lag, not of the sweep. The harness was rewritten
to sample the tail every 150 ms and accumulate de-duplicated lines, and to
wait for *that boot's own* process-start marker plus five seconds before
reading a count. Every figure below comes from the second run. **A silence
that has not been waited for is not a silence.**

---

## 2. Step 8 — the log line, verbatim

Identity API console, boot 1, with the residue planted first:

```
2026-09-07T15:12:02.1830000Z info: SmartSentinelEye.Identity.Application.KeycloakAdmin.KioskPrivilegeSweep[1316351009]
2026-09-07T15:12:02.1830000Z       Stripped inherited realm privileges from 1 of 1 enrolled kiosk accounts.
```

**Non-zero, as the gate requires.** In context, that boot in order:

```
2026-09-07T15:11:58.1350000Z [sys] Starting process...: Cmd = C:\Program Files\dotnet\dotnet.exe, Args = ["run", "--project", "D:\\Github\\wt-2132\\src\\Identity\\Api\\SmartSentinelEye.Identity.Api.csproj", "--no-build", "--configuration", "Release", "--no-launch-profile"]
2026-09-07T15:12:01.6640000Z       Sending HTTP request POST https://localhost:57240/realms/smart-sentinel-eye/protocol/openid-connect/token
2026-09-07T15:12:02.0380000Z       Sending HTTP request GET https://localhost:57240/admin/realms/smart-sentinel-eye/clients
2026-09-07T15:12:02.1290000Z       Sending HTTP request GET https://localhost:57240/admin/realms/smart-sentinel-eye/clients/c778152d-7404-4d07-9063-b907fd8c6a21/service-account-user
2026-09-07T15:12:02.1530000Z       Sending HTTP request GET https://localhost:57240/admin/realms/smart-sentinel-eye/users/ed8f29cc-ddf1-40ea-a186-ef8bfa9d6600/role-mappings/realm
2026-09-07T15:12:02.1830000Z       Stripped inherited realm privileges from 1 of 1 enrolled kiosk accounts.
2026-09-07T15:12:02.1980000Z info: Wolverine.Runtime.WolverineRuntime[0]
2026-09-07T15:12:02.1980000Z       Starting Wolverine messaging for application assembly SmartSentinelEye.ServiceDefaults, ...
```

Three things are settled by those eight lines, and only the first is what the
gate asked for:

1. **The pass ran at boot**, in the Identity API's own process, mints its own
   `identity-admin` token, and needs no authority beyond what enrolment
   already exercises (spec §"US1 — auth / authority": no new admin authority
   was requested, and none was needed).
2. **The Aspire-injected configuration resolved.** The base URL Identity
   actually used is `https://localhost:57240` — the **https** endpoint.
   Every test on this branch hand-writes `ConnectionStrings:keycloak` as
   *http*, so the shape the tests model is not the shape the container gets;
   the module's three-way fallback (`GetConnectionString("keycloak")`, then
   `services:keycloak:http:0`, then `services:keycloak:https:0`) is what
   makes them agree. The `IdentityInfrastructureModule.cs:144` throw is real
   and reachable, and it is not reached here.
3. **The sweep is inline on the startup path.** Wolverine's own start follows
   it by 15 ms. See §5.

### Step 8's control — the steady state is genuinely silent

The fixture's own first boot of `identity` ran against a realm with no client
stamped `sse.kind`, and logged **no** completion line:

```
2026-09-07T15:11:13.2540000Z [sys] Starting process...: ...SmartSentinelEye.Identity.Api.csproj...
2026-09-07T15:11:35.4590000Z       Application started. Press Ctrl+C to shut down.
```

No `Stripped inherited realm privileges` between those two, over a full boot
observed to completion. That is `T008`'s change working in a real stack — and
it is why the residue had to be planted first: verified against an empty
realm this whole procedure produces no output and proves nothing.

---

## 3. Steps 3, 4, 6 and 7 — the provider's answers

Both probes created **directly** through the Admin API, never through
`POST /kiosks`, so nothing stripped them on the way in.

| | residue (`sse.kind=kiosk`, `sse.fab=munich`) | bystander (no `sse.kind`) |
|---|---|---|
| **before** (steps 3, 4) | `[default-roles-smart-sentinel-eye, offline_access, uma_authorization, user]` | `[default-roles-smart-sentinel-eye, offline_access, uma_authorization, user]` |
| **after** (steps 6, 7) | `[]` | `[default-roles-smart-sentinel-eye, offline_access, uma_authorization, user]` |

- **Step 3 held**: the residue did carry `offline_access` before the pass. If
  it had not, everything below would be satisfied by a sweep that removes
  nothing, and the procedure says to stop and say so.
- **Step 6**: `offline_access` is gone.
- **Step 7 is the one that matters.** The bystander is byte-identical before
  and after, and it was holding the *same four roles* as the residue — so a
  sweep whose idea of "a kiosk" matched everything would have emptied it too,
  passed step 6 on the way past, and not thrown while doing it.
- The residue came out holding **nothing at all**, not merely one role less.
  That is the documented behaviour — the removal takes away every
  directly-assigned realm role — and it is the reason step 7 exists.

---

## 4. Step 9 — **the procedure's own step 9 is wrong, and this is a finding**

`spec.md` step 9 says:

> Restart the Identity API again. Step 6 still holds, and no new line is
> logged — there is nothing left to do.

**The line is logged again**, from the next boot, with the same non-zero
count:

```
2026-09-07T15:12:12.8840000Z [sys] Starting process...: ...SmartSentinelEye.Identity.Api.csproj...
2026-09-07T15:12:17.1090000Z       Stripped inherited realm privileges from 1 of 1 enrolled kiosk accounts.
```

That boot was observed to its own process-start marker plus five seconds, so
this is not the tail lag of §1.

The reason is in the guard, and it is not a bug in the guard: the condition
is `kiosks.Count > 0`, and the residue client **still exists and is still
stamped** — the probes are not deleted until step 10. `SweptKioskPrivileges`
reports how many kiosks were *reached*, not how many lost something, and
`StripInheritedRealmRolesAsync` returns early on an account with no direct
mappings. So the second pass enumerated one kiosk, did nothing to it, and
reported "1 of 1".

**Why it is worth its own issue rather than a shrug.** Once any kiosk is
enrolled at all — the normal state of a running fab, not just this
procedure — every Identity start logs "stripped N of N" whether or not it
stripped anything. An operator cannot tell a fresh residue from a realm that
has been clean for a month, which is most of what the silence in T008 was
bought for. The honest count is the one the outcome record already carries:
`KioskSweepOutcome.StrippedCount` is `KioskCount - Unreachable.Count`, which
has the same defect.

**Not fixed here.** It is a behaviour change, it would need its own red, and
ADR-0036's smallest change forbids folding it in. Recorded, with the log
above, for whoever picks it up.

Step 6 *does* still hold after the second boot: the residue's roles were read
as `[]` before that restart and nothing re-granted them.

---

## 5. N4 — an unreachable Keycloak holds the host for 30 seconds, measured

`KioskPrivilegeSweepHostedService.StartAsync` awaits the pass **inline**, so
everything after it waits: `ApplicationStarted`, Aspire's `Running` state,
and every resource that `WaitFor(identity)`. `IdentityInfrastructureModule
.cs:183` sets `client.Timeout = Timeout.InfiniteTimeSpan` deliberately, so
the only bound is the standard resilience pipeline's total budget.

Measured, not reasoned: `AddIdentityInfrastructure` composed with
`ConnectionStrings:keycloak` pointing at a black-holed TEST-NET-3 address
(`http://203.0.113.7:8080`) and the same
`AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly)`
ServiceDefaults applies:

```
SCRATCH RESULT: StartAsync returned after 30154 ms.
```

**~30 s, once, then the host starts.** The host does start — that is Red C's
claim and it holds — but Red C cannot see the delay, because
`UnreachableKeycloakAdminClient` throws instantly and the real client spends
the budget. With Keycloak reachable the cost is small: on boot 1 above, the
sweep's first Keycloak call was at 15:12:01.664 and it finished at
15:12:02.183 — **519 ms** of the startup path.

Whether 30 s of delayed boot is acceptable is a judgement nobody has been
asked to make. It is not a regression this branch introduces relative to
*serving*, but it is a delay this branch introduces relative to *starting*,
and it did not exist before the registration. Recorded rather than decided.

---

## 6. Automated checks

| Check | Result |
|---|---|
| `dotnet build -c Release SmartSentinelEye.slnx` | succeeds, 0 warnings |
| `Architecture.Tests` | **336 pass** |
| `Identity.Infrastructure.Tests` | **3 pass** (1 → 3; the steady-state pair added at phase 6) |
| `Identity.Application.Tests` | **61 pass**, file untouched — `git diff` on that directory is empty |
| `Integration.Tests` — the two spec-092 tests | **2 pass** against the real fixture |

Coverage gates (ADR-0065) are Domain / Application / Shared. The only
production file this branch changes in a gated project is
`Identity/Application/KeycloakAdmin/KioskPrivilegeSweep.cs`, whose five
pre-existing tests are unmodified and whose new branch is covered by the
phase-6 pair. No threshold was changed.

---

## 7. What was **not** done

- **No production deployment exists** (ADR-0130), so nothing here says how
  this behaves outside a developer's machine.
- **No environment has ever contained a residue client** outside this
  procedure. `spec.md` §"The verdict" established that population empty in
  every realm this system has; the residue verified above was planted by hand
  for the purpose. The failure mode is real — `TryDeleteClientAsync` swallows
  its delete — but it has never been observed occurring.
- **The half-enrolment path itself was not provoked.** The residue was
  created directly, which is what makes it a residue rather than an
  enrolment; nobody made `CreateClientAsync`'s strip *and* its compensating
  delete both fail. Doing so needs the fault injection that issue #2166
  covers.
- **Only one kiosk.** The "one kiosk unreachable, the rest still swept"
  scenario is covered by unit tests and was not exercised against a provider.
- **The 30 s of §5 was measured at a composed host, not at a booted
  container.** Provoking it in the stack means stopping Keycloak while
  Identity restarts, and `identity` has `WaitFor(keycloak)` — it would wait
  for the resource rather than fail against it, which measures something
  else.

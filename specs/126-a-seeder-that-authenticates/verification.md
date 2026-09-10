# Verification — Spec 126, A seeder that authenticates

**Issue:** #2158
**Phase 5 (ADR-0037).** Observed against the dev Aspire stack on this
machine, 2026-09-10. Not a test run: the transcripts below are the running
system.

## Before — the defect, observed before any code changed

The issue recorded explicitly that the 401 was **not established at
runtime**; its audit was `git grep` and `git log` only. So this came first.

### The call the seeder makes

```
$ curl -s -o /dev/null -w "%{http_code}\n" "http://localhost:5288/overlays?state=Published"
401
$ curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOK" "http://localhost:5288/overlays?state=Published"
200
```

Unauthenticated: refused. With a bearer: fine. The endpoint is
`.RequireAuthorization(Scope.Sse.Overlays.Read)` and the seeder sent no
credential.

### What the service logged at cold start

```
warn: SmartSentinelEye.SystemVariables.Infrastructure.Resolution.ReverseIndexSeederHostedService[727974828]
      ReverseIndex seed: overlay-designer returned Unauthorized; starting with empty index. The index will populate as new OverlayRevisionPublishedV1 events arrive.
```

### That the index really was empty

An overlay `obs2158-overlay` was created with the label `Line: {{obs2158}}`
and published, and the variable `obs2158` defined. Publishing raises
`OverlayRevisionPublishedV1`, which warms the index, so a value change fanned
out:

```
Pushed ResolvedOverlayTextChanged to 1 overlays after 'obs2158' changed.
```

`system-variables` was then restarted and the **same** variable changed
again. The seed line said `Unauthorized`, and:

```
Set variable 01a08d01-e6c9-7d51-a21c-0014f35f2e6c 'obs2158' = 'after-restart' by f6fca112-…
```

— with **no** `Pushed ResolvedOverlayTextChanged` line at all. Same variable,
same overlay, same request: one overlay updated before the restart, nothing
after it. That is the defect, and it needs no republication story to see.

(`NoOverlaysReferenceVariable` is at `Debug` and so is not in the console at
the default level. The absence of the push line is the signal.)

## After

The realm client was added to the **running** Keycloak through the Admin API
rather than by re-importing the realm. A running Keycloak keeps its old realm
and only deleting its data volume forces a re-import — the volume holds
everything else in the dev realm, so it was left alone. The integration suite
is unaffected either way: `AppHost.cs` gates `.WithDataVolume()` on
`isRunMode && !isE2ETests`, and the fixture passes `E2ETests=true`, so its
Keycloak imports the realm JSON fresh.

### The credential, and only the credential

```
$ curl -sk -X POST ".../realms/smart-sentinel-eye/protocol/openid-connect/token" \
    -d grant_type=client_credentials -d client_id=system-variables-seeder \
    -d client_secret=dev-only-system-variables-seeder-secret
{"access_token":"<redacted>","expires_in":3600,"refresh_expires_in":0,"token_type":"Bearer","not-before-policy":0,"scope":"sse.overlays.read"}
```

`"scope":"sse.overlays.read"` — the token carries that and nothing else.
FR-003 read off the wire rather than off the realm file.

### The seed

`system-variables` restarted, with twelve overlays published (eleven from
earlier work plus the probe), none of them republished:

```
ReverseIndex seeded with 12 published overlays.
```

### The fan-out that was missing

The same `obs2158` value change that produced nothing before:

```
Pushed ResolvedOverlayTextChanged to 1 overlays after 'obs2158' changed.
```

## The refusal path, observed on the final build

The stack was rebuilt and rebooted with the seeder client **disabled** in
Keycloak (`enabled: false` through the Admin API - reversible, and the client
was re-enabled straight after). The mint is refused, and the seeder now says
so at `Error` - `fail:`, not `warn:` - with no promise of a repair:

```
fail: SmartSentinelEye.SystemVariables.Infrastructure.Resolution.ReverseIndexSeederHostedService[1256928116]
      ReverseIndex seed: overlay-designer refused the system-variables-seeder credential (Unauthorized). The index is empty and stays empty until the credential is fixed and the service restarted. Check the client's secret and its sse.overlays.read scope.
```

The client was then re-enabled and `system-variables` restarted, on the same
binary:

```
ReverseIndex seeded with 12 published overlays.
Pushed ResolvedOverlayTextChanged to 1 overlays after 'obs2158' changed.
```

So both branches were exercised against the code that ships, one after the
other, without rebuilding in between.

## What the first boot after the fix exposed

Worth recording, because it is the reason this note is longer than the fix.

The stack was booted **before** the realm client was added, so the seeder
had a credential to present and Keycloak refused it. It did not log the new
`SeedRefused`:

```
warn: SmartSentinelEye.SystemVariables.Infrastructure.Resolution.ReverseIndexSeederHostedService[549900927]
      ReverseIndex seed failed; starting with empty index. Self-heal will kick in as overlay V1 events arrive.
```

The mint failed, not the overlay-designer call: `EnsureSuccessStatusCode()`
inside `ClientCredentialsTokenProvider` threw, and the seeder's catch logged
`SeedFailed` at `Warning`. The retries are visible too, which is
`RetryEveryMethod` behaving as ADR-0143 describes:

```
Execution attempt. Source: '-standard//Standard-Retry', Result: 'Response status code does not indicate success: 401 (Unauthorized).', Handled: 'True', Attempt: '0'
… Attempt: '1' … Attempt: '2' … Attempt: '3'
```

So the **most likely real misconfiguration** — a missing, disabled or
wrong-secret service account — reached the operator as a quiet warning
promising a repair that cannot happen. That is the same defect #2158 is
about, on the path a broken deployment actually takes, and it is why the
mint refusal now takes the `SeedRefused` branch as well.

## Latency

No figure. Cold-start seeding is not one of constitution §IV's six legs, and
this change adds no work to leg 4 — see spec.md §Latency for the distinction
between *the index leg 4 reads* and *the leg itself*.

## Not fixed here, and deliberately

`SeedNonSuccessStatus` (a 503, say) and `SeedFailed` (an unreachable
overlay-designer) both still end with a self-heal promise, and that promise
is optimistic for the same reason it is wrong for a refusal: an overlay
published **before** this process started raises no event, so nothing
refills the index for it either. The difference is one of degree — an outage
ends, a wrong secret does not — and the covering test pins a 503 at
`Warning` by name. Rewording those two is a separate change to a separate
claim, and is recorded here rather than smuggled in.

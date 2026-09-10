# Plan — spec 119

## Declaration 1 — phase 4a colour: **RED**

Behaviour-changing. Today the hook throws; after this it answers. The natural
red is a handler-level test asserting a typed 401 while the exception still
propagates — it fails by *escaping*, which is the honest failure for this defect.

## D1 — the status is 401, and the reasoning is not "it is the only one left"

Three candidates, judged against spec 074's D3, which already decided this hook's
401/403 split ("`401` specifically tells the client to come back with
credentials", so a refusal that no credential can fix is terminal 403).

- **403** would be wrong. A discovery outage is *not* terminal. The credentials
  are very likely fine; the realm is not there to confirm them. A terminal
  refusal tells a client to stop trying at exactly the moment retrying is the
  correct behaviour.
- **503** is the semantically honest "we could not check", and is the one thing
  this issue exists to remove: MediaMTX's handling of 5xx is not its handling of
  401, and nobody here has observed which it does. Trading one unobserved 5xx
  for another, better-labelled 5xx delivers nothing.
- **401** is what the nine REST APIs *measurably* answer for this exact
  condition (spec.md, step one). Parity with the bearer pipeline is the
  strongest argument available: it makes the hook and the APIs agree about an
  unreachable realm, which is what the original claim assumed and nobody had
  checked.

**401.**

## D2 — a new `AuthorizeWhepError` member, not `Unauthorized`

`Unauthorized` reads "Bearer token is missing, malformed, or expired." When the
realm is down that sentence is **false**, and it is the sentence an operator
meets while every viewer on the wall drops. Spec 074's FR-011 settled the
precedent for the same shape of outage: a distinct code plus a warning turn
"video died" into "video died, and here is why".

`IdentityProviderUnavailable` → `WHEP_IDENTITY_PROVIDER_UNAVAILABLE`, 401.
Same status as `Unauthorized`, different code and message, so MediaMTX's
behaviour is unchanged and the diagnosis is not.

## D3 — the validator reports which of the two happened

`Option<WhepAuthSubject>` cannot carry the distinction — `None` is one fact and
we now have two. `IWhepAuthValidator.ValidateAsync` returns
`Result<WhepAuthSubject, WhepAuthFailure>` (ADR-0047), with

```csharp
public enum WhepAuthFailure { TokenRejected, IdentityProviderUnavailable }
```

An enum rather than a string value object: it never crosses the wire, is never
parsed and is never persisted, so `IValueObject<string>` would buy nothing that
`==` does not. Precedented in this repo — `BearerValidationMode`, `GridViolation`,
`IdempotencyOutcome`. §II bans primitive-typed *state*; a closed two-member
vocabulary is the opposite of one.

The mapping stays in the handler, where every other WHEP refusal is decided.
Infrastructure keeps reporting facts, not statuses.

## D4 — the catch is narrow, and the narrowness is what keeps cancellation

Only the `GetConfigurationAsync` await is wrapped, so an
`InvalidOperationException` from anywhere else — the token handler, the
`Result` struct's own guards — still propagates as the bug it would be.

**This plan first said the catch needed a `ThrowIfCancellationRequested()` ahead
of it**, on the reasoning that `ConfigurationManager` rewraps everything the
retriever threw as IDX20803, cancellations included. The counterfactual says
otherwise, and it was run: with the catch narrowed to
`InvalidOperationException`, a cancelled request surfaces as
`OperationCanceledException` in every shape that could be built — a retriever
throwing it directly, a fetch cancelled in flight, and a retriever wrapping it in
`IOException` exactly as `HttpDocumentRetriever` does. `ConfigurationManager`
raises the cancellation itself. The extra line was therefore dead code, and it is
not in the fix.

What the guard against FR-004 actually is: the catch stays narrow.
`A_cancelled_request_stays_cancelled` fails — measured, both ways — the moment
that catch is widened to `catch (Exception)`, which is the mistake worth having a
test for.

## D5 — logged in Infrastructure, where the exception is

`StreamDistribution.Infrastructure.Log` already exists; the message carries the
exception, so IDX20803's inner chain (DNS, refused, TLS) survives. Logging it in
the handler instead would keep the fact and lose the reason.

## Files

| File | Change |
|---|---|
| `src/StreamDistribution/Application/Auth/WhepAuthFailure.cs` | new — two-member enum |
| `src/StreamDistribution/Application/Auth/IWhepAuthValidator.cs` | returns `Result<WhepAuthSubject, WhepAuthFailure>` |
| `src/StreamDistribution/Application/Commands/AuthorizeWhepErrors.cs` | new member + factory |
| `src/StreamDistribution/Application/Commands/Handlers/AuthorizeWhepCommandHandler.cs` | maps the two failures |
| `src/StreamDistribution/Infrastructure/Auth/WhepAuthValidator.cs` | narrow catch, logger, new return type |
| `src/StreamDistribution/Infrastructure/Log.cs` | one `[LoggerMessage]` |
| `tests/StreamDistribution.Application.Tests/…` | the red, the fake |
| `tests/StreamDistribution.Infrastructure.Tests/Auth/…` | the validator's two cases |

No contention file is touched (`Shared.Kernel`, `Shared.Contracts`, `AppHost`).

## D6 — the warning is a transition, not a per-request line (phase 6)

One `int` flipped with `Interlocked.Exchange`: `1` on entering the catch, `0` on
the next successful fetch, and a line written only when the value **changed**.
The first exception is kept verbatim; the repeats say nothing it did not.

`Interlocked` rather than a plain field because the validator is a singleton and
every WHEP open runs through it at once — without it, "log the transition" means
one line per caller that raced into the catch, which is the thing being fixed.

**Not a throttling mechanism** (ADR-0036): no interval, no configuration surface,
no reusable abstraction. Two `if`s and a field.

**A transition and not a latch.** An outage that ends and returns is two outages,
and the second must speak — a latch would silence it precisely for the operator
who has already seen the recovery. Both halves are held by counterfactual:
logging unconditionally fails `An_outage_is_logged_once_however_many_viewers_are_refused`
(25 warnings against 1), and dropping the reset fails
`A_recovery_is_logged_and_a_second_outage_speaks_again`.

The recovery line is `Information`, not `Warning`: it closes the outage the
warning opened, and an operator scanning warnings should not find an all-clear
among them.

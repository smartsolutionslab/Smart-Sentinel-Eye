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

## D4 — the catch is narrow, and cancellation is separated inside it

Only the `GetConfigurationAsync` await is wrapped, so an
`InvalidOperationException` from anywhere else — the token handler, the
`Result` struct's own guards — still propagates as the bug it would be.

`ConfigurationManager.GetConfigurationNonBlockingAsync` catches *everything* the
retriever threw and rewraps it as IDX20803, so a cancelled fetch and a refused
socket arrive as the same type. The catch therefore calls
`cancellationToken.ThrowIfCancellationRequested()` **first**: a caller that went
away leaves as a cancellation, not as a refusal (FR-004).

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

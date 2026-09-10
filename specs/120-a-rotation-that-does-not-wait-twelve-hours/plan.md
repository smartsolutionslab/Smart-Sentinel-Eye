# Plan — spec 120

## Approach

Two commits, each building on its own (ADR-0087 rebase-merge lands them
individually).

1. **The red tests.** `WhepValidatorRotationTests`, in the
   `StreamDistribution.Infrastructure.Tests` suite, alongside the three files
   that already drive the real `ValidateAsync`.
2. **The fix.** One `is`-pattern and one `RequestRefresh()` call inside the
   existing `catch (SecurityTokenException)`.

## The seam, which is better than the issue said

The issue describes the harness as *"reflects a `ConfigurationManager` over a
stubbed `IDocumentRetriever`"*. **That reflection is gone.** Spec 112 (#2099)
replaced it with an `internal` constructor taking
`IConfigurationManager<OpenIdConnectConfiguration>`, which
`WhepValidatorIssuerTests` and `WhepValidatorAudienceTests` already construct
through. A document that *changes between calls* is therefore expressible by
supplying a manager over a retriever that serves a different document on the
second fetch — no reflection, no broker, no network, no clock.

The tests use a **real** `ConfigurationManager`, not a fake one. That matters:
a fake manager recording `RequestRefresh()` calls would prove the call was made;
a real one proves the call *has the effect claimed* — that a refresh actually
re-reads the document inside the 5-minute floor. With
`AutomaticRefreshInterval` untouched at 12 hours, nothing but a genuine
`RequestRefresh()` can make the second call see the rotated key.

`ConfigurationManager.RequestRefresh()` honours `RefreshInterval` **after** the
first request (`_isFirstRefreshRequest`), so one rotation per manager instance
is enough and the floor is never touched.

## The change

Inside `ValidateAsync`'s `catch (SecurityTokenException exception)`:

```csharp
if (exception is SecurityTokenSignatureKeyNotFoundException
              or SecurityTokenInvalidIssuerException)
{
    oidc.RequestRefresh();
}
```

The `catch (ArgumentException)` arm — the malformed-token path — gets nothing,
which is the discrimination the negative test pins.

## Composing with #2235 (spec 119, unmerged)

This branch is cut from `develop`, which does not carry #2235. That PR
restructures the same method: `IWhepAuthValidator` returns
`Result<WhepAuthSubject, WhepAuthFailure>`, a narrow `try` around
`GetConfigurationAsync` alone maps IDX20803 to `IdentityProviderUnavailable`,
and token validation keeps its **own** `try` retaining
`SecurityTokenException`/`ArgumentException`.

The refresh trigger belongs on the **token-validation** failure path — #2235's
second `try` — and that is where it is written here. The two changes are
complementary, not competing: #2235 discriminates *config-fetch* failure from
*token* failure; this one discriminates *within* token failure. Expect a textual
conflict in the `catch (SecurityTokenException)` arm, resolved by keeping
#2235's `Result` return and inserting these three lines above it.

## What is deliberately not done

- **No logging.** ADR-0050 would want a `[LoggerMessage]` extension, but
  `WhepAuthValidator` on `develop` has no `ILogger` and #2235 adds one through
  the constructor. Introducing a second, different logger constructor here turns
  a three-line conflict into an unresolvable one, and the issue asks for a
  refresh, not for a record of it. Left to whichever slice lands second.
- **No in-request retry**, no interval change, no widening of what is accepted.

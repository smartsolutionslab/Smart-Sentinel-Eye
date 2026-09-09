# Verification — spec 115 (issue #2105)

## Phase 4a — RED, observed

`dotnet test tests/StreamDistribution.Application.Tests -c Release --no-build
--filter "FullyQualifiedName~AuthorizeWhepCommandHandlerTests"` at
`211a24f1`:

```
Failed  Authorize_with_an_action_this_build_does_not_recognise_names_the_value_that_arrived
  Shouldly.ShouldAssertException : logger.Entries.ShouldHaveSingleItem().Message
    should contain (case insensitive comparison)
"stream"
    but was actually
"Refused a WHEP request on path cam-01a087fc-dbb5-72c6-b879-ce6e56496ec8: MediaMTX named no action th..."

Failed  An_absent_action_and_an_unrecognised_one_are_refused_with_different_messages
  Shouldly.ShouldAssertException : absent
    should not be
"Refused a WHEP request on path cam-01a087fc-dc2b-749a-90ad-5feb118ab2be: MediaMTX named no action this build recognises — the field was absent, or held a value other than read, publish or playback. Check for MediaMTX version skew first: the broker image tracks a floating latest tag, so an upgrade that renames, moves or drops the action field arrives here as unknown and every viewer is refused closed until this build learns the new vocabulary."
    but was

Failed  A_value_longer_than_the_cap_is_truncated_in_the_refusal
  Shouldly.ShouldAssertException : message
    should contain (case insensitive comparison)
"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa…"
    but was actually
"Refused a WHEP request on path cam-01a087fc-dc38-72b4-bff5-bbc93c8564d2: MediaMTX named no action th..."

Failed!  - Failed: 3, Passed: 13, Skipped: 0, Total: 16
```

Three assertion failures, not compile failures — the fourth command member and
`ReportedMediaMtxAction` ship in that same commit precisely so the assertions
compile and fail on their text. The 13 pre-existing assertions pass unchanged.

## Phase 4b — GREEN

Same command, after the two-message implementation:

```
Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

## The refusal still refuses

The security property is that nothing here widened the hook. Evidence, in
descending order of how much it would have hurt to get wrong:

- `git diff develop -- AuthorizeWhepErrors.cs Domain/Stream/MediaMtxAction.cs`
  is **empty**. The vocabulary and the error hierarchy are byte-for-byte
  unchanged; MediaMTX receives the same `WHEP_ACTION_UNKNOWN` / 403 / detail
  string. What arrived is a log field and never part of the answer.
- Every pre-existing assertion passed **unmodified** — none was edited,
  renamed, relaxed or skipped, and the diff on that file is argument additions
  only:

```
Passed  Authorize_with_no_action_is_refused
Passed  Authorize_with_an_unrecognised_action_is_refused
Passed  Authorize_a_publish_with_the_read_scope_is_refused
Passed  Authorize_a_publish_with_the_grandfathered_bundle_is_refused
Passed  Authorize_a_publish_with_no_token_is_refused_on_the_action_not_the_token
Passed  Authorize_with_an_empty_token_returns_Unauthorized
Passed  Authorize_with_an_invalid_token_returns_Unauthorized
Passed  Authorize_with_a_token_granting_neither_the_read_scope_nor_the_bundle_returns_Forbidden
Passed  Authorize_for_an_Offline_stream_returns_StreamUnavailable
Passed  Authorize_with_a_grandfathered_management_token_returns_success
Passed  Authorize_with_a_kiosk_token_returns_success
Passed  Authorize_a_read_with_the_read_scope_returns_success
Passed  Authorize_a_playback_with_the_read_scope_returns_success
```

- Both new refusal tests assert `ActionUnknown` **before** the message, so a
  regression in the decision fails them ahead of the diagnosis.

## Build and suite

`dotnet build -c Release` — **Build succeeded, 0 Warning(s), 0 Error(s)**.

All 29 non-Docker test projects: **2388 passed, 0 failed, 0 skipped**
(`Integration.Tests` needs the Aspire fixture and was not run here — phase 5).
`Architecture.Tests` 356/356, including `PrimitiveBoundaryTests`,
`HandlerDeconstructionTests` and `EndpointScopeDeclarationTests`.

## Not verified here

The message has not been read out of a live Aspire structured-log window. That
is phase 5: post `{"action":"stream"}` to `/streams/authorize` and read the
warning in the dashboard. The unit evidence covers the message text; it does not
cover the OTLP field surviving export.

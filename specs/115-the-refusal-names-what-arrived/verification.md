# Verification: The refusal names what arrived

**Issue #2105** · Branch `fix/2105-the-refusal-names-what-arrived`

| Commit | |
|---|---|
| `fde50e8f` | `docs(115): the refusal names what arrived` |
| `211a24f1` | `test(streams): a refusal cannot yet name what arrived` |
| `e2dba331` | `fix(streams): the WHEP refusal names the action that arrived` |

---

# Phase 4 (ADR-0037)

## 4a — RED, observed

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

## 4b — GREEN

Same command, after the two-message implementation:

```
Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

Every pre-existing assertion passed **unmodified** — none was edited, renamed,
relaxed or skipped, and the diff on that file is argument additions only. The
vocabulary and the error hierarchy are byte-for-byte unchanged:
`git diff develop -- AuthorizeWhepErrors.cs Domain/Stream/MediaMtxAction.cs` is
**empty**.

---

# Phase 5 — observed on a live stack

Phase 4b named its own gap: *"the message text is proven by unit tests only;
nobody has yet read `{ReportedAction}` out of a live OTLP structured-log
window."* That is what follows.

## 1. What was observed

A live Aspire stack was booted, the WHEP external-auth hook was POSTed the
actions MediaMTX would never send, and the refusals were read back out of the
dashboard's structured-log store. **`ReportedAction` arrives as its own OTLP log
attribute** — not merely as text inside the rendered message — carrying the
sanitised value.

How: `dotnet run --project src/AppHost -c Release --no-build --launch-profile
https` with `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`, then `curl`
against the `stream-distribution` project's own endpoint
(`http://localhost:5199/streams/authorize`), the way `WhepAuthIntegrationTests`
posts. Logs read through `mcp__aspire__list_structured_logs` immediately after
each POST — this machine's 10-second Wolverine poll floods the window, so the
events were **created and read**, not hunted for in history.

### The structured field, verbatim

The unrecognised-action case, exactly as the store returned it:

```json
{"logId":3500,"spanId":"78f2d772c2bef459","traceId":"940f8273d906ebf8763253e2d89e9eb8",
 "message":"Refused a WHEP request naming action stream on path cam-00000000-0000-7000-8000-000000000001: this build recognises only read, publish and playback. Check for MediaMTX version skew first — the broker image is pinned, so compare that pin against the release that renamed or replaced this action; every viewer stays refused closed until this build learns it.",
 "severity":"Warning","resourceName":"stream-distribution",
 "attributes":{"ReportedAction":"stream","Path":"cam-00000000-0000-7000-8000-000000000001",
               "ConnectionId":"0HNOEMUTVJ262","RequestId":"0HNOEMUTVJ262:00000001","RequestPath":"/streams/authorize"},
 "source":"SmartSentinelEye.StreamDistribution.Application.Commands.Handlers.AuthorizeWhepCommandHandler",
 "dashboardUrl":"https://localhost:17069/structuredlogs?logEntryId=3500"}
```

`"ReportedAction":"stream"` sits in `attributes`, beside `Path` — a key an
operator can filter on in the dashboard's structured-log view. That is what the
source-generated `[LoggerMessage]` template and the OTLP exporter had to
preserve, and what an assertion against a fake logger's rendered `Message`
cannot show.

**That it is a per-record attribute and not a rendering artefact is proved by the
absent-action case, which has no such key at all:**

```json
{"logId":3583,"spanId":"9f8fdfce2f56a5d6","traceId":"27d62b17a73042f9c1c67ccdbb1bce07",
 "message":"Refused a WHEP request on path cam-00000000-0000-7000-8000-000000000004: MediaMTX sent no action field at all — the field was absent, not merely unfamiliar. Check for MediaMTX version skew first — the broker image is pinned, so compare that pin against the release that moved or dropped the field; every viewer stays refused closed until this build reads it from wherever it went.",
 "severity":"Warning","resourceName":"stream-distribution",
 "attributes":{"Path":"cam-00000000-0000-7000-8000-000000000004","ConnectionId":"0HNOEMUTVJ265","RequestId":"0HNOEMUTVJ265:00000001","RequestPath":"/streams/authorize"}}
```

Two diagnoses, two messages, and the field present in exactly one of them. That
is the whole of #2105, observed rather than asserted.

## 2. Truncation, on the live path

`{"action": "<200 × 'x'>"}` — 200 characters sent, 64 logged plus U+2026:

```
"ReportedAction":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx…"
```

Counted: 64 `x` before the mark (`ReportedMediaMtxAction.MaximumLength`), then
`…`. The truncation mark survived UTF-8 export intact, so a capped value is still
legible as capped rather than as a plausible whole one.

## 3. Control characters, on the live path

`{"action": "read\r\nWARN forged second line"}` — the CR and the LF each became
U+FFFD:

```
"ReportedAction":"read��WARN forged second line"
```

And the reason the sanitisation exists, checked where it actually matters — the
resource's **console** output, a plain text sink. `mcp__aspire__list_console_logs`
for `stream-distribution`, searching `forged second line`:

```
Returned 1 console log.

Refused a WHEP request naming action read��WARN forged second line on path cam-00000000-0000-7000-8000-000000000003: this build recognises only read, publish and playback. Check for MediaMTX version skew first — the broker image is pinned, so compare that pin against the release that renamed or replaced this action; every viewer stays refused closed until this build learns it.
```

**One line, not two.** A surviving CRLF would have forged a second console line
reading `WARN forged second line`; it did not.

## 4. The refusal still refuses, and does not echo

Every unrecognised action — `stream`, the 200-character value, the CRLF value,
and a body with no `action` field at all — answered **403** with the same fixed
body:

```
HTTP 403
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"WHEP_ACTION_UNKNOWN","status":403,"detail":"The request named no recognised action.","traceId":"00-940f8273d906ebf8763253e2d89e9eb8-78f2d772c2bef459-01"}
```

**The reported action appears nowhere in the answer.** `grep -c "yyyy"` across all
four second-run response bodies returned `0`, `0`, `0`, `0` — the 200-character
value was logged and not reflected across the trust boundary. What arrived is a
log field only; that is the safety argument, and it holds on the wire rather than
only in a unit test.

The response's `traceId` is the log record's `traceId` (`940f8273…`), so the
refusal MediaMTX receives and the diagnosis an operator reads are one trace.

Nothing widened and nothing narrowed, checked in both directions with a real
Keycloak token (password grant, client `smart-sentinel-eye-web`, scope
`openid sse.management`, minted from Aspire's **proxied** endpoint
`https://localhost:10756`, not the container's mapped port):

| Request | Result |
|---|---|
| `action: "read"`, valid admin token | **HTTP 200** — the happy path is untouched |
| `action: "read"`, no token | **HTTP 401** `WHEP_UNAUTHORIZED` — the vocabulary still recognises `read` |
| `action: "stream"`, valid admin token | **HTTP 403** `WHEP_ACTION_UNKNOWN` — the broadest token this hook ever sees, still refused closed |

## 5. Run twice

Every case above was run a second time against the same live stack with a fresh
set of paths (`…0000000000b1`–`b4`) and `y` in place of `x`. Identical outcomes:
four 403s, the same three `ReportedAction` attributes with the same truncation and
the same two U+FFFD, the same absent key on the absent-action record. Both runs
are the reported result — the first run after machine churn is this repository's
known false regression, so neither figure was taken on its own.

## 6. Build and tests

`dotnet build -c Release`, whole solution, TreatWarningsAsErrors in force, with
no AppHost running:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:59.29
```

Re-run at the tip, `--no-build`:

```
Passed!  - Failed: 0, Passed:  60, Skipped: 0, Total:  60 - SmartSentinelEye.StreamDistribution.Application.Tests.dll
Passed!  - Failed: 0, Passed: 133, Skipped: 0, Total: 133 - SmartSentinelEye.StreamDistribution.Domain.Tests.dll
Passed!  - Failed: 0, Passed: 356, Skipped: 0, Total: 356 - SmartSentinelEye.Architecture.Tests.dll
```

No test was edited, renamed, relaxed or skipped by this phase.

## 7. The floating-tag claim, checked

Phase 4b flagged that spec 074's old single message asserted *"the broker image
tracks a floating latest tag"* — which #2103 falsified the same day by pinning
MediaMTX to `1.21.0-ffmpeg`.

- **Both new messages say the opposite**, and correctly: each reads *"the broker
  image is pinned, so compare that pin against the release that …"*
  (`src/StreamDistribution/Application/Log.cs`, `RefusedUnrecognisedWhepAction`
  and `RefusedAbsentWhepAction`). Both were read out of the live window in §1, so
  this is the **deployed** text, not the source text.
- **The false claim is gone**: `grep -rn "tracks a floating"` over the tree
  returns nothing.
- **No other message claims the tag floats.** Every surviving mention of a
  floating or `latest` tag is either the pin guard describing what it forbids
  (`tests/Architecture.Tests/ContainerImagePinTests.cs`), the AppHost comment
  recording the digest `latest-ffmpeg` resolved to when it was pinned
  (`src/AppHost/AppHost.cs:145`), or
  `tests/Integration.Tests/AppHostMediaMtxImageTests.cs:23` explaining why that
  assertion did not have to move. All three are accurate after the pin.

**One inaccuracy found and deliberately not fixed** — a *comment*, not a message:
`apps/shared/src/streaming/WhepClient.test.ts:399` still names *"a MediaMTX whose
SDP shape moves under a floating `latest` tag (#2103)"* as a live motivation for
#2108's fix. The pin removed that scenario. It is outside this slice, changes no
behaviour, and the fix it motivates remains right for the two other reasons the
same comment gives — so it is reported here rather than edited.

## 8. Stack conditions

One stack, booted for this phase; no other AppHost was running (checked before
boot). The MediaMTX containers left over from an earlier session still carried
`bluenviron/mediamtx:latest-ffmpeg` — a persistent container keeps the image it
was created with — so `mediamtx-18bcf406`, `camera-sim-18bcf406` and the tunnel
proxy were `docker rm`'d first. They carry no volumes; `postgres-data` and
`keycloak-data` were not touched. The stack this note observed therefore runs the
pin:

```
mediamtx-18bcf406       bluenviron/mediamtx:1.21.0-ffmpeg
camera-sim-18bcf406     bluenviron/mediamtx:1.21.0-ffmpeg
fixture-video-useuywrj  bluenviron/mediamtx:1.21.0-ffmpeg
```

## 9. Latency

**N/A** — an external-auth hook refusal is not on the event-to-overlay path. It
runs during the WHEP handshake, before any media flows, and only on the failing
branch; the succeeding branch is unchanged (200, same code path, same single
`GetByPathAsync`).

## 10. What was **not** verified

- **MediaMTX itself never sent these actions.** The hook was POSTed directly,
  because no MediaMTX release sends `stream`, a 200-character action or a CRLF
  one — that is precisely what makes them unrecognised. So this verifies *this
  service's* handling of an unrecognised action, not that a future MediaMTX's
  real unknown action would arrive looking like these. The gap is inherent to the
  issue and cannot be closed without that future release.
- **`Integration.Tests` was not run.** The AspireFixture boots its own stack and
  this machine runs one at a time; the run-mode stack above was in use for the
  whole phase. The six `WhepAuthIntegrationTests` last ran green on the pinned
  image during spec 113's phase 5, and none of them was changed here.
- **The dashboard's structured-log UI was not driven by hand.** The attribute was
  read through the Aspire MCP resource service — the same store the UI queries —
  and each record's `dashboardUrl` is recorded above, but nobody clicked a filter
  on `ReportedAction` in a browser.
- **No sink beyond the Aspire dashboard and the resource console was exercised.**
  There is no production OTLP sink to export to (ADR-0118), so what was shown is
  "survives export to *a* sink", not "survives export to whatever production
  eventually uses".
- **CI has not run this branch.** Everything above is local.

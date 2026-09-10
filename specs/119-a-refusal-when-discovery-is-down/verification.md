# Verification — spec 119

**Issue #2160** · Branch `fix/2160-a-refusal-when-discovery-is-down`

| Commit | |
|---|---|
| `2494e070` | `docs(119): a refusal when discovery is down — spec, plan, tasks` |
| `cea45b9d` | `test(streams): a WHEP open when the realm is unreachable` (red) |
| `b2d7d5ed` | `fix(streams): an unreachable realm refuses instead of throwing` |
| `96e907b2` | `docs(119): the measurements behind the refusal` (phase 4) |

Phase 4's evidence — the bearer-pipeline comparison, the red, the green and the
counterfactuals — is in `plan.md` D1–D4 and in this file's history at
`96e907b2`. **This file is now the phase 5 note** and answers the gap phase 4
named for itself: *"what a real MediaMTX does with the 401 … needs a stack with
Keycloak taken away under a live WHEP open."*

---

## 0. What was run

A run-mode Aspire stack, Release, no other AppHost on the machine (checked
before boot):

```
DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true \
  dotnet run --project src/AppHost -c Release --no-build --launch-profile https
```

- **MediaMTX** `bluenviron/mediamtx:1.21.0-ffmpeg` (the pin, `mediamtx-18bcf406`),
  WHEP on `localhost:10760`, its HTTP API on `localhost:10758`.
- **The stream**: `cam-01a084f6-0bbf-77ea-8a5e-eb4e5112cbe1` — `ready: true`,
  one H264 1280x720 track pulled over RTSP from `fixture-video`.
- **The token**: password grant, client `smart-sentinel-eye-web`, user `admin`,
  scope `openid sse.management`, minted from Aspire's **proxied** Keycloak
  endpoint `https://localhost:10756`.
- **stream-distribution** answers on `http://localhost:5199`; MediaMTX reaches it
  at `http://aspire.dev.internal:5199/streams/authorize`.

**Both routes were used, and they prove different things.**

- A **real WHEP open** — `POST http://localhost:10760/<path>/whep`,
  `Content-Type: application/sdp`, `Authorization: Bearer <token>`, a hand-built
  recvonly H264 offer. Authorized, this answers **`201 Created`** with an SDP
  answer and a session `Location`, so MediaMTX genuinely called the hook, read
  its status and decided on it. What it does *not* do is complete ICE/DTLS from
  `curl`, so **no media ever flowed on these sessions** — see §10.
- A **direct POST** to `/streams/authorize`, which proves what this service
  answers and nothing about MediaMTX.

## 1. Baseline — Keycloak up

```
WHEP open: HTTP 201 in 0.31s
WHEP open: HTTP 201 in 0.24s
```

## 2. What MediaMTX does with the 401, and how it compares with the 500

**They are indistinguishable, except in MediaMTX's own log text.** Said plainly,
because the issue's second justification — *"MediaMTX's retry behaviour on 5xx
differs from its behaviour on 401"* — **is not what MediaMTX 1.21.0 does**.

The status code was isolated as the only variable: MediaMTX's runtime config was
repointed (`PATCH /v3/config/global/patch`, `authHTTPAddress`) at a local stub
answering a chosen status, and the *same* offer, token and path were opened
against the *same* MediaMTX process.

| Hook answered | Client got | Time | Hook calls per open | Session |
|---|---|---|---|---|
| `500` + ProblemDetails | `401` `{"status":"error","error":"authentication error"}` | 3.41 s | **1** | closed |
| `401` + our ProblemDetails | `401` `{"status":"error","error":"authentication error"}` | 4.06 s | **1** | closed |

- **No retry.** One POST reached the stub per WHEP open, in both cases.
- **No back-off to discover**, because there is no second attempt.
- **The path is not torn down** — §3 shows `readyTime` unchanged across a
  refusal on the real path.
- Roughly 2 s of the elapsed time is MediaMTX's own pause between the failed
  authentication and closing the session (`WAR … failed to authenticate` at
  `10:54:09`, `INF … closed` at `10:54:13`), the same on both.

The one difference MediaMTX exposes is that it **logs the hook's status and body
verbatim**:

```
WAR [WebRTC] [session 7b4423a9] failed to authenticate: server replied with code 500: {"type":"…15.6.1","title":"An error occurred while processing your request.","status":500}
WAR [WebRTC] [session 5cfa0b1d] failed to authenticate: server replied with code 401: {"type":"…15.5.2","title":"WHEP_IDENTITY_PROVIDER_UNAVAILABLE","status":401,"detail":"The identity provider could not be reached; the bearer token was not checked."}
```

So the change buys a diagnosis in *another component's* log, and a status that
agrees with the nine REST APIs (ADR-0089's contract rule, plan D1) — **not** a
change in how MediaMTX retries. That is a narrower justification than the issue
claims, and it is still a good one.

A third case fell out of the same window and is worth recording: when the hook
is unreachable at the *transport* level, MediaMTX answers the client the same
`401 authentication error`:

```
WAR [WebRTC] [session b70a262a] failed to authenticate: HTTP request failed: Post "http://aspire.dev.internal:5199/streams/authorize": read: connection reset by peer
```

`authHTTPAddress` was patched back to the real hook afterwards and re-checked
(`"authHTTPAddress":"http://aspire.dev.internal:5199/streams/authorize"`, WHEP
open `201`).

## 3. The real thing — Keycloak taken away under a live WHEP open

Reproducing the defect needs a **cold** discovery cache, which is a scope
finding in its own right (§6). So: restart `stream-distribution` with Keycloak
up (process started `10:55:29Z`), then stop Keycloak before any WHEP open. The
container was stopped, never removed, so `keycloak-data` is untouched.

```
keycloak-18bcf406   Exited (143)
discovery from host: HTTP 000  (curl exit 7 — connection refused)
```

### Run 1, 10:56:29Z — direct POST

```
HTTP 401 in 4.29s
content-type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2",
 "title":"WHEP_IDENTITY_PROVIDER_UNAVAILABLE","status":401,
 "detail":"The identity provider could not be reached; the bearer token was not checked.",
 "traceId":"00-df7613e5dd98e98a8d490046992dda3a-dd12ecc03491720a-01"}
```

**No leak.** No discovery URL, no `localhost:10756`, no realm name, no IDX code,
no exception text, no stack frame — a fixed title, a fixed detail and a trace id.
The whole response header set was `Content-Type`, `Date`, `Server` and
`Transfer-Encoding`; there is not even a `WWW-Authenticate` challenge to read a
hostname out of.

### Run 1, 10:56:57Z — a real WHEP open through MediaMTX

```
path before: "ready":true "readyTime":"2026-09-10T10:54:55.213505344Z"
client got HTTP 401 in 6.93s
{"status":"error","error":"authentication error"}
path after:  "ready":true "readyTime":"2026-09-10T10:54:55.213505344Z"
```

MediaMTX's log, from the live window:

```
2026/09/10 10:56:57 INF [WebRTC] [session 37e8cf48] created by 172.18.0.1:44502
2026/09/10 10:57:01 WAR [WebRTC] [session 37e8cf48] failed to authenticate: server replied with code 401: {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"WHEP_IDENTITY_PROVIDER_UNAVAILABLE","status":401,"detail":"The identity provider could not be reached; the bearer token was not checked.","traceId":"00-5e5491fafa47358511dd9cdb21d4bf47-df661d0c0c9dd923-01"}
2026/09/10 10:57:04 INF [WebRTC] [session 37e8cf48] closed: failed to authenticate: …
```

**The viewer is refused; the stream is not disturbed.** `readyTime` is
byte-identical before and after, the RTSP pull keeps running, and a second
sample on a second path (`cam-01a084f7-19cb-…`, 10:57:38Z) behaved the same:
direct `401` in 4.13 s, WHEP `401` in 5.75 s, `readyTime` unchanged.

The refusal MediaMTX logs carries the trace id of the refusal, so the line an
SFU operator reads and the record in §4 are joinable.

## 4. The log record, read out of the live window

Created and read immediately — this machine's 10-second Wolverine poll floods
the window, so nothing was hunted for in history.
`mcp__aspire__list_structured_logs`, resource `stream-distribution`, returning
one entry:

```json
{"logId":14772,"spanId":"dd12ecc03491720a","traceId":"df7613e5dd98e98a8d490046992dda3a",
 "message":"The realm's OIDC discovery document could not be obtained; no WHEP bearer token can be checked until it can. Every viewer stays refused, and this is not a bad credential.",
 "severity":"Warning","resourceName":"stream-distribution",
 "attributes":{"ConnectionId":"0HNOF589ETCGJ","RequestId":"0HNOF589ETCGJ:00000001","RequestPath":"/streams/authorize"},
 "exception":"System.InvalidOperationException: IDX20803: Unable to obtain configuration from: 'https://localhost:10756/realms/smart-sentinel-eye/.well-known/openid-configuration'. … Exception: 'System.IO.IOException: IDX20804: Unable to retrieve document from: '…' ---> System.Net.Http.HttpRequestException: No connection could be made because the target machine actively refused it. (localhost:10756) ---> System.Net.Sockets.SocketException (10061): …",
 "source":"SmartSentinelEye.StreamDistribution.Infrastructure.Auth.WhepAuthValidator"}
```

`Log.WhepIdentityProviderUnreachable` carries the exception **as a structured
`exception` field**, not as text folded into the message, and the chain names
the address (IDX20803), the retrieval failure (IDX20804) and the transport cause
(`SocketException 10061`) — FR-005. Its `traceId` is the `traceId` in the
response body of §3: one trace, one story, and the detail lives only in the log.

## 5. Recovery, without a service restart

Keycloak started again; recovery measured from the moment its discovery document
answered.

| | Keycloak answering | First attempt | Result |
|---|---|---|---|
| Run 1 | 10:59:18Z | `t+5s`, direct POST | **HTTP 200** |
| Run 2 | 11:02:29Z | `t+5s`, **real WHEP open** | **HTTP 201** |

```
t+5s attempt 1 -> HTTP 200
RECOVERED after 5s with no service restart
…
t+5s WHEP open attempt 1 -> HTTP 201
RUN 2 RECOVERED after 5s, no service restart
```

The first attempt after the realm came back succeeded — there is no refresh
interval to sit out. **The process did not restart**: `Win32_Process` reports
`SmartSentinelEye.StreamDistribution.Api.exe` PID `21244`, created 13:00:21
local, both before run 2's outage and after its recovery (run 1's process, PID
`24756`, likewise spanned its own outage and recovery). Real WHEP opens answered
`201` in 0.31 s and 0.24 s immediately afterwards, and a third time on the later
boot of §6.

## 6. Both runs, and the scope finding they forced

Every observation above was made twice — run 1 at 10:56Z, run 2 at 11:01Z, each
a fresh cold-cache service start, each with its own outage and recovery:

```
RUN 2, Keycloak down at 11:01:03Z          (Exited (143))
DIRECT HTTP 401 in 4.31s   WHEP_IDENTITY_PROVIDER_UNAVAILABLE
WHEP   HTTP 401 in 5.61s   {"status":"error","error":"authentication error"}
path before/after: "ready":true "readyTime":"2026-09-10T10:54:55.213505344Z"  (identical)
```

Both runs are the reported result. This repository's known false regression is
the first run after machine churn, and it appeared here too — see §7.

**Restarting the service before each run is not incidental; it is the condition
the defect needs.** Measured on a third boot: warm the cache with one successful
authorize, *then* stop Keycloak.

```
keycloak: Exited (143)          discovery from host: HTTP 000 (curl exit 7)
direct hook: HTTP 200 in 0.07s
WHEP open:   HTTP 201 in 0.32s
```

With a discovery document already in hand, `ConfigurationManager` never goes to
the realm, so **a steady-state Keycloak outage does not reach this code path at
all** — viewers keep being admitted on cached signing keys. What #2160 fixes is
the window where the service has *no* cached document: a restart, a deployment
or a cold start that lands inside the outage. That is a narrower blast radius
than "Keycloak is down", and it is better said out loud than implied by a unit
test that constructs the cold case directly.

## 7. An environmental artifact, recorded so it is not read as a regression

The **first** WHEP open after the run-mode tunnel proxy has been idle is answered
`401`, and MediaMTX's log gives the reason:

```
WAR [WebRTC] [session 4a75333a] failed to authenticate: HTTP request failed:
Post "http://aspire.dev.internal:5199/streams/authorize": read tcp 172.18.0.11:42154->172.18.0.8:5199: read: connection reset by peer
```

Seen three times (first open of the session, first open after the config patch,
first open after the second boot); the immediately following attempts answered
`201` every time, and the same request from the host to `localhost:5199`
succeeded throughout. It is the DCP tunnel proxy in run mode, not this change —
but it is worth writing down, because it is a `401` on a WHEP open that looks
exactly like the failure this spec is about.

## 8. Latency

**N/A — not on the event-to-overlay path.** The external-auth hook runs during
the WHEP handshake, before any media flows, and the succeeding branch is
untouched (§1, §5: `201` in 0.24–0.32 s).

## 9. Suites, at the tip, Release

`dotnet build -c Release` (whole solution, TreatWarningsAsErrors, no AppHost
running): `Build succeeded. 0 Warning(s) 0 Error(s)`.

```
StreamDistribution.Application.Tests     Passed:  65, Failed: 0
StreamDistribution.Infrastructure.Tests  Passed:  26, Failed: 0
StreamDistribution.Domain.Tests          Passed: 140, Failed: 0
Architecture.Tests                       Passed: 360, Failed: 0
```

No test was edited, renamed, relaxed or skipped by this phase.

## 10. What is **not** observed

- **The pre-fix build's own 500 was never seen.** §2's 500 was synthesised at the
  hook boundary rather than produced by `develop`'s binary — reverting the tree
  to build it was refused by this session's permission classifier. The
  behavioural conclusion still holds, because MediaMTX's only input is the HTTP
  response and only the status was varied; what is genuinely unknown is the
  **body** the pre-fix service sent. In `Development` an unhandled exception is
  rendered by the developer exception page, so that body was plausibly a stack
  trace — and §2 shows MediaMTX copies the hook's body verbatim into its own log.
  Plausibly. Not observed, so not claimed.
- **No media ever flowed on these sessions.** `curl` cannot complete ICE/DTLS, so
  every WHEP open here proves the *authorization* leg and nothing past it.
  Nothing is therefore known about an **established, playing** session when the
  realm goes away — on the evidence of §2 MediaMTX asks the hook once per open
  and never again, but that is inference from call counts, not an observation of
  a playing tile surviving an outage.
- **No browser was driven.** How `apps/shared`'s `WhepClient` reacts to the
  `401` — retry, back off, show the refusal — is untested here, and the kiosk is
  the client that actually matters in a fab.
- **The nine REST APIs were not re-measured live under this outage.** Plan D1's
  measurement stands as phase 4 made it: a probe app configured with
  `AuthenticationDefaults`' options against a dead authority, answering `401`.
- **CI has not run this branch.** Everything above is local, on one machine.

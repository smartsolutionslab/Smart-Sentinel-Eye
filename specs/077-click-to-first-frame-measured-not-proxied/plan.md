# Plan — Spec 077, click-to-first-frame measured not proxied

**Phase:** 2 (Plan) · **Spec:** `spec.md` · **Issue:** #199 · **Date:** 2026-09-06

---

## Bounded context and layers

**None.** This spec touches no bounded context, no domain model, no application
handler, no contract, no endpoint, no migration, and no React component. It adds
one Playwright file to `e2e/` and reads two existing exports.

That is worth stating rather than leaving as an absence, because the usual
boundary questions all resolve to "not applicable" and a reviewer should be able
to confirm that quickly:

| Rule | Applies? |
|---|---|
| No cross-context project references (NetArchTest) | n/a — no `.csproj` changes |
| Domain → integration event | n/a — no events |
| §II primitives out of the domain | n/a — no domain model |
| `Ensure.That` guards (ADR-0105) | n/a — TypeScript |
| `Option<T>` over nullable params (ADR-0141) | n/a — TypeScript |
| Handler deconstruction | n/a — no handlers |
| Idempotency (ADR-0142/0143) | n/a — the harness issues one `POST` (register camera) through the UI, exactly as the existing seeds do |
| Coverage gates (ADR-0065) | n/a — e2e is not in the coverage measurement |

The rule that *does* apply is this suite's own **no-`fetch` rule**
(`camera-detail.spec.ts`): the harness arranges its state through the
application, not around it. Here that is not merely hygiene — the measured
quantity includes the auth hook, so a harness that minted its own token would
time a different journey than the one FR-013 budgets.

---

## What the harness is, mechanically

### Shape

```
e2e/click-to-first-frame.spec.ts        (new — the only file with behaviour)
```

One test in the `chromium` project (management-web, `:5173`). No new Playwright
project, no new `dependencies`, no new teardown: `chromium` already names
`cleanup` as its teardown, and `retire-e2e-cameras.teardown.ts` sweeps by the
`E2E ` name prefix.

### Sequence

1. `signInAsOperator(page)` — the real Keycloak login (`e2e/support/sign-in.ts`).
2. Register one camera named `E2E ClickToFrame ${Date.now()}` at
   `FIXTURE_VIDEO_RTSP_URL`, through the Register camera dialog, exactly as
   `seed-live-video-wall.setup.ts` does. Budgeted `FIRST_WRITE_TIMEOUT_MS`
   (90 s) because it is the run's first write of its kind.
3. **One discarded warm-up open.** Click the camera, wait up to 90 s for
   `totalVideoFrames > 0`. This absorbs MediaMTX path creation, the RTSP dial to
   `fixture-video`, and the health sweep — none of which recur, and none of
   which FR-013's warm-regime SLO covers. `WhepHandshakeLatencyTests` warms the
   OIDC cache for the same reason and says so.
   **Its elapsed time is printed and discarded, never asserted.** Printed
   because if it is not vastly larger than the samples that follow, assumption 1
   in `spec.md` is wrong and the whole figure is suspect.
4. **Twenty timed opens.** Each: navigate back to `/cameras`, mark `t0` and
   click, resolve on the first produced frame.
5. Sort, take p95 as the 19th of 20 (`ceil(20 × 0.95) - 1`, the same index
   arithmetic and the same comment as `WhepHandshakeLatencyTests:64`), print
   p50 / p95 / max / all samples, assert `p95 < 3000`.

### The clock — the part that is easy to get wrong

`t0` and the frame observation must both happen **in the page**, on one
`performance.now()` timebase. The shape:

```
// installed once per open, immediately before the click
await page.evaluate(() => {
  (window as ...).__ctff = { t0: performance.now(), elapsed: null };
  const tick = () => {
    const v = document.querySelector('video');
    const produced = v?.getVideoPlaybackQuality?.().totalVideoFrames ?? 0;
    if (produced > 0) { w.__ctff.elapsed = performance.now() - w.__ctff.t0; return; }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
});
```

then the Playwright click, then poll `__ctff.elapsed` for a value.

Three properties this buys, each of which the obvious alternative loses:

- **Resolution.** rAF samples every ~16 ms. `expect.poll` backs off to 250 ms
  and then 500 ms, so it would add up to half a second of error — always
  positive, so always flattering or damning by up to a sixth of the budget.
- **`t0` survives the navigation.** The route change is a client-side SPA
  transition (`react-router` `<Link>`), not a document load, so the `window`
  object and the running rAF loop persist across it. **If this stops being true
  — a full page load — the loop is destroyed and `elapsed` never appears; the
  harness must then fail loudly rather than silently fall back to the outer
  poll's timing.**
- **It survives the `<video>` element arriving late.** `CameraDetailPage` mounts
  the viewer after its RTK Query resolves, so the element does not exist at
  `t0`. The loop tolerates that by construction; an `addEventListener` on an
  element that is not there yet does not.

**Rejected: `video.onloadeddata`.** Meaning-wise it is nearly right, but it is
an event, so the listener must be attached before the element exists — needing
a `MutationObserver` or `addInitScript`, i.e. strictly more machinery for a
strictly weaker guarantee (it fires on data availability, not on a frame the
decoder produced).

**Rejected: `RTCPeerConnection.connectionState`.** Contradicts FR-013; #2111.

**Rejected: reusing `readDecode` from `kiosk-shows-a-label-over-video.spec.ts`.**
It returns a snapshot for the caller to poll, which is exactly the design that
puts the clock in the wrong process. Its *instrument* is reused (the same
`getVideoPlaybackQuality().totalVideoFrames` expression); its *sampling
strategy* deliberately is not, and the harness should carry a one-line comment
saying which of the two it took and why, so a later reader does not "unify" them.

### Per-open teardown between samples

Navigating back to `/cameras` unmounts `CameraViewer`, which closes the peer
connection. Each sample therefore measures a fresh WHEP open — SDP exchange,
ICE, DTLS, IDR wait, decode — against an already-pulled MediaMTX path. That is
the quantity FR-013 names.

`page.reload()` would also work and would additionally re-run OIDC restore,
adding a term FR-013 does not budget. **Use the in-app navigation.**

---

## Cost, and against which budget

Measured on the two most recent `develop` CI runs (`33995210155`,
`33993559761`):

| Job | Duration | Timeout |
|---|---|---|
| `e2e (Playwright, full stack)` | **13 m 01 s**, **10 m 59 s** | `timeout-minutes: 40` |
| `integration tests (Docker)` | 9 m 16 s, 9 m 13 s | — |

Estimated cost of this harness: sign-in ~10 s + a cold camera registration
(~40 s typical, 90 s budgeted) + the discarded warm-up open (~10–20 s) + 20 warm
opens at ~1.5–2.5 s each including navigation (~40 s) ≈ **2–3 minutes typical**.

**~20 % of the Playwright suite's own wall-clock; ~7 % of the job's 40-minute
ceiling.** With `retries: 2`, a genuinely failing measurement costs 3× ≈ 9 min,
still comfortably inside the ceiling.

### Recommendation: do not gate it

A measurement that only runs when someone remembers to ask for it is one nobody
runs, and the whole point of FR-013's *"CI emits the measured
click-to-first-frame"* is that it is emitted every time. Seven percent of the
job budget is not the kind of cost that justifies hiding the number.

**If a reviewer wants it gated anyway**, the mechanism is a Playwright **tag**,
not a project:

- Tag: `test('… @measurement', …)`, excluded with
  `pnpm test:e2e --grep-invert @measurement`, run with `--grep @measurement`.
- A **project** would be the wrong tool: it would need its own `use.baseURL`,
  its own `teardown: 'cleanup'` wiring, and `playwright.config.ts` already
  carries a scar comment about a project's files also running in `chromium`
  unless `testIgnore` is updated — one more file for that list, and one more
  place to get it wrong.

Do **not** reach for the xUnit trait mechanism (`Category!=Measurement` in
`ci.yml:179`). It filters `dotnet test` and has no effect on Playwright; the
suites are gated by entirely different machinery and conflating them is how a
"gated" test quietly keeps running.

---

## Flakiness: what a red means, and why the number will not drift

**It asserts.** Recording only would be the softer choice, and it is the wrong
one here: spec 002 `plan.md:707-708` already says a drift above 3 s p95 blocks
the PR, and an SLO nobody enforces is an SLO nobody keeps. The proxy that exists
today already asserts; replacing an assertion with a print would be a net loss.

Four things make that assertion defensible rather than a number waiting to be
raised:

1. **Repeat-before-believing is already wired.** `playwright.config.ts:15` sets
   `retries: isCI ? 2 : 0`. A red therefore means **three independent 20-open
   runs each measured a p95 above 3 s** — 60 samples across three browser
   sessions. That is exactly this repository's own rule that a measurement run
   must be repeated before it is believed, and it costs nothing on a green run.
   *State this in the harness's doc-comment*, so the retry count is understood
   as part of the measurement design rather than as incidental config that a
   later tidy-up might remove.
2. **The warm-up is discarded, so the cold path cannot cause a red.** The
   expensive, variable, once-per-run terms — path creation, RTSP dial, health
   sweep, OIDC discovery — are all in the discarded open.
3. **A breach is diagnosable, not just observable.** Spec 002 `plan.md:703`
   decomposes the 3 s into ≤ 200 ms lookup + ≤ 500 ms WHEP POST + ≤ 1 s
   ICE/DTLS + ≤ 1 s decoder warmup + 300 ms headroom, and the harness prints all
   twenty samples. A uniform spread across ~1 s is the IDR term behaving
   normally; a bimodal or uniformly-shifted spread is not, and points at a
   different sub-term. **A breach whose shape can be read is a breach someone
   fixes; one that is just a number is a breach someone re-baselines.**
4. **Raising it is forbidden.** ADR-0144 blocks the lane from weakening a gate
   to reach green, and #2119 is open precisely because a budget drifted by being
   adjusted to fit what was measured. If the first observed p95 exceeds 3 s,
   **that is a finding and a new issue** — the SLO is spec 002's, and changing
   it is a decision for a human, at spec level.

**The known headroom is thinner than it looks**, and the plan says so rather
than discovering it at phase 5: the fixture's 1.000 s GOP contributes up to
1000 ms (p95 ≈ 950 ms) that no product change can remove. Predicted p95 is
1.4–2.2 s against a 3000 ms threshold — real margin, but not the 60× the
existing proxy enjoys. That is the correct state for a threshold: close enough
to matter, far enough not to sing.

---

## What phase 4 must not do

- **Not create `tests/E2E.Tests/`.** The issue's wording invites it; `e2e/` is
  where Playwright tests live.
- **Not look for a `Watch` button.** It does not exist; the click is the camera
  name `<Link>` (`CamerasPage.tsx:85`).
- **Not use `connectionState`.** FR-013 and #2111.
- **Not `fetch` from the harness** to register the camera or mint a token.
- **Not touch `WhepHandshakeLatencyTests`** — US-2 is P2 and needs a human.
- **Not touch §IV, §VII, `CLAUDE.md`'s leg table, or ADR-0031.** No leg moves.
- **Not touch the board.** Projects v2 is separately rate-limited and the human
  handles it.

---

## Why this is not an ADR

The three things an ADR would settle are all already settled, in writing, and
the spec cites each by line:

| Would-be decision | Already decided | Where |
|---|---|---|
| The SLO (3 s p95) | yes | spec 002 FR-013 |
| The instrument (first **decoded frame**, not transport state) | yes | spec 002 FR-013, verbatim |
| That CI blocks on drift | yes | spec 002 `plan.md:707-708` |
| That this is *not* a §IV leg | yes | spec 002 `spec.md:284-286`, `plan.md:68` |

What is left is choosing *how* to observe a decided quantity — rAF versus
polling, in-page versus test-process, tag versus project. Those are
implementation, and they are argued above rather than voted on.

**The one place a human is nonetheless asked to speak is US-2**, and it is
flagged in the spec's review gate rather than smuggled into P1. Narrowing an
existing assertion is adjacent enough to ADR-0144's *"weaken a gate"* prohibition
that the lane should decline it, even though the motive is to replace a tripwire
that cannot trip with one that can.

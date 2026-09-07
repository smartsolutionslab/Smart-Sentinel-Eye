# Spec 093 — Tasks

**Phase:** 3 (Tasks) — ADR-0037
**Spec:** `spec.md` · **Plan:** `plan.md`
**Issue:** #2129
**Branch:** `fix/2129-a-retried-register-applies-once`
**Worktree:** `D:/Github/wt-2129`

---

## Engineer

**One `infra-engineer`.** The subject is the Aspire integration fixture's
HTTP-client composition and an architecture guard that CI reads — no bounded
context, no domain, no frontend, no migration. A `backend-engineer` would be
competent with the C#, but the thing being changed is *how the test host wires
its clients*, which is infra's subject; spec 087 was assigned on the same
reasoning.

No parallel engineers. See §"Parallelism".

---

## Phase 4a colour — both, on different tasks

The architect declares this, and ambiguity resolves to red. Here it is not
ambiguous; it is genuinely two obligations on two tasks, and running them in the
wrong order destroys the evidence.

- **T001 — behaviour-preserving → characterisation, observed green.** The
  extraction must not change what the fixture does. Its verification is that
  **T003's red reports 4 attempts** — the pre-existing number. A red reporting
  anything else means the extraction moved behaviour: stop, do not adjust.
- **T003 — behaviour-changing → RED.** Written first, observed failing, and the
  **verbatim** failure quoted in the PR body (ADR-0139, constitution §Testing).
  A test arriving green here is a phase-4 failure, not a shortcut.

**Neither is skippable.** ADR-0144 gives phase 4a two colours instead of an
exemption.

### What the red is, concretely — and why it is deterministic

`A_failing_POST_from_a_fixture_client_is_attempted_once` builds a
`ServiceCollection`, configures one named client through the **same**
`FixtureHttpClients.Configure` the fixture uses, plugs a counting handler that
answers 503, sends one `POST`, and asserts the handler saw **1** attempt. Before
T004 it sees **4**.

**No stack. No Docker. No burst. Milliseconds.** This is the deterministic
option the brief asks for, and it is available because T001 makes the fixture's
client defaults constructible without booting anything.

The load-dependent alternative — provoke the burst, then register a device, then
assert no 409 — is **rejected as the gate**:

- It needs a ~40-minute Docker run and a saturated machine to be meaningful.
- It is timing-shaped, so a green run proves nothing and a red run proves only
  that the machine was slow enough that day. This session already found a
  concurrency test that never reproduced its race on this hardware, and the
  deterministic assertion is what actually proved the defect.
- CI can never run it: `ci.yml:179` excludes `Category=Measurement`, so the
  burst never precedes NFR002 in the job NFR002 runs in. A gate CI cannot
  evaluate is not a gate.

The burst run survives as **T006**, a merge-blocking observation rather than the
red — and phase 5 must say which of the two it is citing.

---

## Parallelism (ADR-0109)

**Almost none, and that is honest rather than under-decomposed.** The tasks form
a chain because each one's output is the next one's precondition:

```
T001 (extract, no behaviour change)
  └─ T003 (the red — cannot compile before T001, cannot be red after T004)
       └─ T004 (the fix — turns it green)
            ├─ T005 [P]  the guard (tests/Architecture.Tests)
            ├─ T006 [P]  the Docker run (no files)
            └─ T007 [P]  file the orphan issue (no files)
```

**T001 is the foundational task and blocks everything.** Nothing else can start
until the fixture's client defaults are a nameable thing.

`[P]` is marked only on T005/T006/T007, which own disjoint files (one owns
`tests/Architecture.Tests/ResilienceRegistrationTests.cs`, the other two own no
files at all). Even there the saving is minutes; one engineer running them in
order is the expected shape.

---

## Commits (ADR-0030, ADR-0086, ADR-0087)

Conventional Commits. **No `Co-Authored-By` footer and no session trailer** —
ADR-0086 overrides the harness attribution reminder.

**Each commit must build on its own**, not merely at the tip. Rebase-merge lands
them individually on `develop` (ADR-0087), so a commit that compiles only with
its successor breaks `git bisect` forever. **Three specs broke this rule this
session** — verify per commit:

```sh
git rebase --exec "dotnet build -c Release --nologo" origin/develop
```

Planned sequence, one commit per task:

| # | Task | Message |
|---|---|---|
| 1 | T001 | `refactor(tests): the fixture's client defaults have a name` |
| 2 | T003 | `test(integration): a fixture client's POST is attempted once` |
| 3 | T004 | `fix(tests): the integration fixture retries only idempotent methods` |
| 4 | T005 | `test(architecture): a fixture resilience handler declares its predicate` |

**Do not push.** Phases 1–3 end at the phase-3 gate.

---

## Tasks

### Foundation — blocks everything

- [X] **T001** [US1] **Extract the fixture's client defaults into a named,
  constructible thing. Behaviour-preserving.**

  New file `tests/Integration.Tests/Fixtures/FixtureHttpClients.cs`:
  an `internal static class` with
  `internal static void Configure(IHttpClientBuilder http)`.
  Guard the parameter with `Ensure.That(http).IsNotNull()` (ADR-0105 — never
  `ArgumentNullException.ThrowIfNull`).

  **The body is the bare `http.AddStandardResilienceHandler()` it is today.** Do
  not narrow it in this task; T004 does that, and merging the two destroys the
  red.

  `tests/Integration.Tests/Fixtures/AspireFixture.cs:213` becomes
  `builder.Services.ConfigureHttpClientDefaults(FixtureHttpClients.Configure);`

  `void` is deliberate: all three `AddStandardResilienceHandler` overloads in
  `Microsoft.Extensions.Http.Resilience` 10.9.0 return *"the value of
  `builder`"*, so there is nothing worth handing back, and the signature then
  does not change between T001 and T004.

  **No `csproj` edit.** `ServiceDefaults` is already reachable transitively and
  `Microsoft.Extensions.Http.Resilience` already compiles in this project. If
  you find yourself adding a reference, re-read `plan.md` §"Boundary rules".

  *Done when:* the solution builds in Release, `dotnet format` and the analyzers
  are clean, and the diff is exactly one new file plus one changed line.

### US-1 (P1) — An integration test's `POST` is sent once

- [X] **T003** [US1] **Write the red, run it, capture the verbatim failure.**

  New file `tests/Integration.Tests/Fixtures/FixtureRetryPolicyTests.cs`.
  `[Trait("Category", "FixtureLogic")]` and **no**
  `[Collection(AspireCollection.Name)]` — it builds a bare `ServiceCollection`
  and must never touch the booted fixture. (`IntegrationTestSelectionTests`
  requires one declaration or the other; this is the one that puts the verdict
  in `ci.yml:72`'s Docker-free step.)

  Shape it on `tests/ServiceDefaults.Tests/Resilience/IdempotentRetryTests.cs` —
  a hand-written `CountingHandler : HttpMessageHandler` plugged in with
  `ConfigurePrimaryHttpMessageHandler` (ADR-0054: hand-written fakes, no
  AutoFixture; the neighbour uses no Moq either). Sentence-style names
  (ADR-0053).

  Call `FixtureHttpClients.Configure` on a **named** client builder (so
  `builder.Name` is a known string), then zero the backoff **after** it with the
  same named-options pattern `IdempotentRetry.RetryEveryMethod` uses:

  ```csharp
  services.Configure<HttpStandardResilienceOptions>(
      $"{builder.Name}-standard",
      options => { options.Retry.Delay = TimeSpan.Zero; options.Retry.UseJitter = false; });
  ```

  The later `Configure` wins. **Corrected at phase 4b:** `IdempotentRetryTests.Build`
  demonstrates the *ordering*, not the named-options key — it passes its delegate
  into the `AddStandardResilienceHandler` overload rather than calling
  `services.Configure` post-hoc. The post-hoc route is exercised only by
  `RetryEveryMethod`, hence by `A_client_that_opts_back_in_retries_its_POSTs_again`,
  which passes today. Neither half is assumed; the citation was one line off. Without this the two four-attempt facts cost ~14 s each on the real
  2/4/8 s backoff. If the key does not bind, **report it** — do not invent a
  second route.

  Four facts, one per `spec.md` scenario:

  | Fact | Expected after T004 | Observed now |
  |---|---|---|
  | `A_failing_POST_from_a_fixture_client_is_attempted_once` | 1 | **4** |
  | `A_thrown_POST_from_a_fixture_client_is_attempted_once` | 1 | **4** |
  | `A_failing_GET_from_a_fixture_client_is_still_retried` | 4 | 4 |
  | `A_failing_PUT_from_a_fixture_client_is_still_retried` | 4 | 4 |

  The two GET/PUT facts pass immediately, and that is correct: they are the
  half of the assertion that says this is a **narrowing and not a disabling**,
  and they must be present in the red run to prove the client was configured at
  all rather than left bare.

  *Run and capture:*

  ```sh
  dotnet test tests/Integration.Tests -c Release --filter "Category=FixtureLogic"
  ```

  *Done when:* the two POST facts fail, **reporting 4**, and the exact output is
  saved for the PR body. **If the red reports a number other than 4, stop** —
  T001 changed behaviour and the characterisation obligation has been broken.

- [X] **T004** [US1] **Apply ADR-0143 to the fixture. Turn the red green.**

  In `FixtureHttpClients.Configure`, the body becomes what
  `src/ServiceDefaults/Extensions.cs:52` already does:

  ```csharp
  http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);
  ```

  **Reuse the existing predicate verbatim.** Writing a second one here creates
  two spellings of one decision that drift apart silently — a review blocker.

  One comment at the call site, saying *why*: the fixture does not call
  `AddServiceDefaults`, so nothing else gives its clients ADR-0143's narrowing.
  No comment narrating *what* the call does.

  **Do not add `RetryEveryMethod()`.** It is the opt-back-in, and using it here
  would reinstate the exact defect with a justification comment attached.

  *Done when:* all four T003 facts pass, and `git diff --stat` shows
  `NFR002_MqttConnectAuthTests.cs` and
  `IdempotentRegistrationIntegrationTests.cs` **unchanged** (SC-003).

### US-2 (P2) — The fixture's registration cannot silently un-narrow

- [X] **T005** [P] [US2] **Guard the tree ADR-0143's own fix did not reach.**

  Extend `tests/Architecture.Tests/ResilienceRegistrationTests.cs` — it already
  owns this subject and already reads source from disk. Parameterise its
  `ReadSources()` on the root directory rather than copying it; today it is
  hard-coded to `src`, which is precisely why this defect was invisible to it.

  Two new facts:

  1. **Every file under `tests/Integration.Tests` containing
     `AddStandardResilienceHandler(` also contains
     `IdempotentRetry.RetryIdempotentMethodsOnly`.** Assert first that the scan
     found **at least one** registration — a source scan that matches an empty
     population passes, and a passing guard that checks nothing is
     indistinguishable from one that holds.
  2. **The counterfactual.** Run the same predicate over a synthetic source that
     registers the bare handler, and assert it is flagged. Without this the
     guard's claim rests on its own comment. (`IntegrationTestSelectionTests`
     carries five such discriminators for the same reason.)

  Normalise separators with `.Replace('\\', '/')` as the existing reader does —
  `Path.GetRelativePath` returns the platform separator, and a backslash literal
  is green on Windows and red on Linux CI, which is the worst direction for a
  guard to break.

  **Do not broaden `The_standard_resilience_handler_is_called_exactly_once_across_src`.**
  After this change there are legitimately two call sites, one per tree;
  broadening it would fail a correct state.

  Watch ADR-0084: the file is ~145 lines and gains ~45. The 300-line ceiling
  holds, with room.

  *Done when:* `dotnet test tests/Architecture.Tests -c Release` is green, and
  the counterfactual fact demonstrably fails when the narrowing is removed from
  `FixtureHttpClients.cs` (try it, then revert).

### Verification and follow-up

- [ ] **T006** [P] [US1] **Merge blocker — confirm no `POST` was relying on the
  retry.**

  This is the one risk in `plan.md`'s table that source reading cannot settle: a
  test somewhere may be surviving a warm-up blip on a retried `POST`.

  ```sh
  dotnet test tests/Integration.Tests -c Release \
    --filter "Category!=Measurement&Category!=Disruptive&Category!=Maintenance"
  ```

  **This needs the stack. Ask the operator for the token before running it** —
  three other tracks are live and there is one Aspire stack. Do not boot without
  asking.

  If a `POST` turns red, the answer is a **wait on the resource**, not a
  restored retry, and **never** `RetryEveryMethod()` on the fixture.

  *Done when:* the selection is green, or every failure is traced and fixed by
  waiting rather than retrying.

  **Not run at phase 4b — still open.** The engineer was instructed not to boot
  Aspire or Docker: another worktree holds the one stack. This remains the merge
  blocker it was declared to be, and nothing in phase 4b substitutes for it.

- [ ] **T007** [P] **File the orphan, do not fix it here.**

  `spec.md` §"The residue": an aborted registration leaves a **Keycloak client
  with no `RegisteredClient` row**, because `keycloak.CreateClientAsync` is not
  transactional with the Postgres save and does not roll back when
  `RequestAborted` fires. Nothing lists it, and a re-registration of the same
  device identifier collides with it.

  This slice prevents new orphans by stopping the second attempt. It does not
  clean up existing ones and does not make the handler transactional — a
  different failure with a different remedy (a compensating delete, or a
  reserve-then-create ordering).

  Open a `bug` issue against `Identity`, link it to #2129 and to this spec, and
  **add it to Project #13** — feature-level, by hand, as the phase-3 gate
  requires:

  ```sh
  gh project item-add 13 --owner smartsolutionslab --url <issue-url>
  ```

  Needs the `project` scope; `item-add` prints nothing on success, and
  `item-list` defaults to 30 items, so verify with `--limit 2000`.

- [ ] **T008** **Correct the reproduction line on #2129.**

  Comment on the issue: `--filter "Category=Measurement"` **cannot run**
  `NFR002_MqttConnectAuthTests`, which carries no `Category` trait, so as
  written the command runs the burst and not the test. Give the working filter
  (`spec.md` §"Independent end-to-end test procedure"), and record that
  `ci.yml:179` excludes the burst from the job NFR002 runs in — so CI has never
  seen this and cannot.

  Costs nothing and saves the next reader from concluding the defect is
  unreproducible.

---

## Phase-3 gate

- [ ] Tasks are atomic, ordered, and each commit builds on its own.
- [X] **#2129 is on Project #13.** Verified 2026-09-07, status *In Progress*, by
  `content.url` — the number filter returns zero — and with `--limit 2000`,
  because `item-list` defaults to 30 and a filled board looks empty. Nothing to
  add by hand.
- [ ] Phase 4a's colour is declared per task above; ambiguity resolved to red.
- [ ] No ADR written or amended; no gate weakened. Confirmed in `plan.md`
  §"Explicitly not done".

**Stop here.** Hand `spec.md`, `plan.md` and `tasks.md` back for review before
phase 4 (ADR-0037).

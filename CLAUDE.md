# Smart Sentinel Eye — Claude Code project guide

This file is the orienting context for Claude Code in this repo. Read it
once per session, then defer to the documents it points at.

## What this project is

Professional camera management / CCTV system for industrial production
fabs. 24/7 operation. 250-camera target. WebRTC, .NET Aspire, k3s.

The full picture lives in:

- **`.specify/memory/constitution.md`** — non-negotiable principles,
  locked tech stack, NFRs, governance.
- **`docs/adr/`** — every architectural decision, with reasoning.
  Start with `0000-initial-decisions.md`.
- **`specs/`** — per-feature specs produced by Spec-Kit. Each spec links
  to its `plan.md` and `tasks.md`.

## Branching — GitFlow (ADR-0028)

**`develop` is the default branch.** Every feature, doc, chore, and CI
branch is cut from `develop` and merges back to `develop`. **Never open
a PR against `main`.** `main` only ever receives merges from
`release/x.y.z` and `hotfix/<short>` branches.

**Still pass `--base develop` explicitly to `gh pr create`** — but as
insurance now, not as a workaround. The tracking that made it a
workaround is fixed (2026-08-28): GitHub's default branch is `develop`,
`origin/HEAD` points at `refs/remotes/origin/develop`, local `develop`
tracks `origin/develop`, and no branch sets `gh-merge-base`. So `gh pr
create` resolves the base to `develop` on its own.

It stays mandatory because the cost is asymmetric: one flag against a PR
opened on `main`, which ADR-0028 forbids outright and which the harness
actively invites — its git-status header says "Main branch (you will
usually use this for PRs)". **Trust this file over the header.**

`develop`'s protection rules require **linear history** with
**rebase-merge only** (no squash, no merge commits) — ADR-0087.

### Stacked PRs — retarget the child before merging the parent

Stack a PR only when it genuinely cannot build on `develop` alone —
a convention change whose call sites depend on it, a contract change
and its consumers. Otherwise cut from `develop` like everything else.

When you do stack, **retarget the child to `develop` first, then merge
the parent**:

```sh
gh pr edit <child> --base develop     # first
gh pr merge <parent> --rebase         # then
```

Backwards is unrecoverable. The repo has `delete_branch_on_merge`
enabled, so merging the parent deletes its branch; GitHub responds by
**closing** every PR based on that branch rather than reparenting it,
and a closed PR's base cannot be changed — so it cannot be reopened.
The branch and its commits survive, but the PR number, its review
thread and its CI history do not. PR #1378 was lost exactly this way.

Stacked PRs do get the full check set — `ci.yml`'s `pull_request`
trigger is deliberately unfiltered, because a stacked PR's base is
itself unreviewed code. Do not narrow it back to `[develop, main]`.

Each commit must build **on its own**, not merely at the tip of the
stack: rebase-merge lands them individually on `develop`, so a commit
that only compiles with its successor breaks `git bisect` forever.
Verify per commit, not per branch.

## Workflow — guided phased process (ADR-0037)

Seven phases, each with an artifact and an **explicit gate**. **Do not
autonomously advance past a gate.** Stop at every gate and ask the
user to confirm before continuing.

| # | Phase | Command(s) | Artifact | Gate |
|---|---|---|---|---|
| 1 | Specify | `/speckit-specify` (+ `/speckit-clarify`) | `specs/NNN-x/spec.md` | Spec reviewed; no `[NEEDS CLARIFICATION]` left. |
| 2 | Plan | `/speckit-plan` | `specs/NNN-x/plan.md` | Plan aligns with constitution + ADRs. |
| 3 | Tasks | `/speckit-tasks` | `tasks.md` | Tasks atomic; the feature's issue on Project #13. |
| 4 | Implement | `/speckit-implement` | Code + tests; format & analyzers clean | Tests green; **new-behaviour tests observed red first, failure quoted in the PR** (ADR-0139); commits follow ADR-0030. |
| 5 | Verify | `/verify` or explicit run/test | Verification note on the PR | Behaviour observed end-to-end. Latency cited if on SLO path. |
| 6 | QA | `/code-review`; `/security-review` if security-sensitive | Findings addressed | All findings resolved or accepted in writing. |
| 7 | PR | `gh pr create` with template | PR to `develop`, CI green | Reviewer approval + green CI. In the autonomous lane the *wait* is not a gate — see below. |

**Phase 3 stopped creating per-task issues after spec 028, and this file
said otherwise for sixteen specs.** The row above now records what the repo
does; what follows is the evidence, so the correction is not itself taken on
trust.

`[TNNN]` issues run to **#1845** (spec 028, 2026-08-24) and stop. Specs 029–044
created **none** — they carry a **feature-level** issue instead, and their
`tasks.md` is the artifact the work is tracked against. Project #13 is active
(≈450 items) and receives those feature and finding issues, not task issues.

So the gate is: **the feature's issue is on Project #13**, added by hand —
`/speckit-tasks` adds nothing to the board.

```sh
gh project item-add 13 --owner smartsolutionslab --url <issue-url>
```

That needs the `project` scope (`gh auth refresh -s project,read:project`).
`item-add` prints nothing on success, and **`item-list` defaults to 30 items** —
verify with `--limit 2000` or a query for a filled board will look empty.

`/speckit-taskstoissues` still exists and still works. Reach for it only when a
feature genuinely wants an issue per task; it is no longer the default, and
running it unthinkingly adds tens of items to a board that is used for tracking
in-flight work at feature granularity.

**The earlier exception this note used to describe still stands**: specs 018–021
missed the then-current gate and their task issues are not on the board.
Back-filling ~100 issues for merged work would bury the in-flight items the
board exists to show. The two feature-level issues from that period (#1605,
#1635) were added individually.

**Why this drifted, and why it is worth saying:** a documented gate that
sixteen specs quietly ignored is one the next spec either misses or follows by
surprise. It is the same defect as §IV recording a leg as unbuilt after it was
built — a record nobody checked against what was actually happening. Found while
running spec 045's Phase 3 (2026-08-28).

**Skipping a phase:** allowed only for trivial changes (typo, dep
bump, comment-only). Write `Phase X: skipped — <one line>` in the PR
body. Documentation-only PRs typically skip 5 and 6.

**Resumability:** every phase's artifact is the resumption point. If
interrupted, read the latest artifact and resume from its phase.

No code is written outside this loop. Every PR references at least one
task ID. Every spec references at least one ADR. Other meta-commands
exist for amendments — `/speckit-constitution` (rare; requires ADR).

### The autonomous lane (ADR-0144)

**There are two lanes, and the difference is a label.** Everything above
is the **supervised lane**: the default, all seven gates, unchanged. It
applies to anything a human starts in a session.

The **autonomous lane** runs phases 1–6 without stopping and merges at
phase 7 on green CI. An issue enters it **only** by carrying
`agent:ready` — on Project #13, status Todo, without `agent:blocked`.
Absence of the label is a hold, so nothing is picked up by accident.

**The lane does not wait for CI** (2026-09-05). Phase 7 opens the PR,
starts a background watcher and takes the next issue; the PR is *parked*
until its checks conclude. Green merges immediately, mid-phase if need
be. Red waits for the issue in hand to reach its own ending first — this
workflow resumes worst from a phase abandoned halfway — then gets one
retry with the CI log, then `agent:blocked`.

The green gate itself is unchanged. **Nothing merges that CI has not
passed**; the loop simply stops standing still for the twenty-plus
minutes the Docker integration and full-stack e2e jobs take.

Two constraints make it safe rather than merely fast:

- **At most three PRs parked at once.** At three, settle one before
  taking a fourth. The cap exists so a branch that cannot rebase is
  found in hours, not at the end of a run.
- **Rebase every parked branch after every merge**, not once at the end
  — `git fetch origin && git rebase origin/develop && git push
  --force-with-lease`. Rebase-merge renames the SHAs (ADR-0087), so a
  parked branch still carries the **old copies** of whatever just
  landed, and they replay as conflicts against themselves.

A run that ends with PRs still parked has ended normally. Report their
numbers; never report a parked PR as delivered.

- `/next-issue` — deliver one eligible issue end to end.
- `/deliver-board` — repeat until none remain.
- `/verify` — phase 5, callable in either lane. It existed only as a
  reference in this file and in ADR-0037 from 2026-05-25 until
  2026-09-03; if you reached for it before then, it was not there.

**Each phase runs in its own subagent**, so phase 6 reviews the code
rather than a summary of it and issue N+1 starts clean. The phase↔role
binding is the table in ADR-0144; `.claude/agents/` holds the briefs.
Those briefs are load-bearing when no human is watching — two of them
said "NRT disabled" for the eleven days after ADR-0141 enabled it.

**Phase 4 is two agents in order, and that split is the point.**
`test-writer` writes tests only, runs them, and returns the **verbatim
output**; the engineer receives that output as its brief and may not edit
the tests to pass. The output is quoted in the PR body — the only form of
the evidence a later reader can check.

**Phase 4a has two colours, because §Testing has two obligations.** The
architect declares at phase 3 which applies, and ambiguity resolves to
red — that path fails loudly, the other passes quietly.

- **Behaviour-changing → red.** A test arriving green is a phase-4
  failure, not a shortcut.
- **Behaviour-preserving → characterisation, observed green.** The
  covering tests are captured passing *before* the change (written first
  if they don't exist — a refactor with no covering test is a rewrite),
  and must pass **unmodified** after. An assertion that has to be edited
  is evidence the behaviour moved: block, don't adjust. A refactor that
  is also a bug fix is two issues, because characterisation would
  otherwise encode the bug as the safety net.

**Three things the lane may not do**, each a blocked outcome rather than
a judgement call: write an ADR or amend the constitution (it implements
decisions, it does not make them); weaken a gate to reach green (a
deleted test, a lowered threshold, a new suppression, a narrowed
analyzer); or skip phase 4a — every other phase has the skip mechanism
above, 4a has two colours instead of an exemption.

**On failure: retry once, then park.** A phase that fails twice gets a
comment with the verbatim failure, the `agent:blocked` label, and its
card back in Todo — and the loop **continues to the next issue**. One
bad issue must not end an overnight run.

## Coding behavior — Karpathy guidelines (ADR-0036)

The `andrej-karpathy-skills:karpathy-guidelines` skill is **baseline
coding behaviour** in this repo. It is invoked automatically during
phases 4–6. Internalize the operational rules:

- **Smallest possible change.** A bug fix changes the bug, nothing
  else. A refactor changes shape, not behaviour. Don't mix them.
- **Define "done" up front.** State the verifiable success criterion
  (a passing test, a measurement, an observable behaviour) before
  writing code. No "done when it compiles".
- **Surface assumptions; don't bury them.** When the task is
  ambiguous, ask one or two clarifying questions before guessing.
  Mark unavoidable guesses explicitly in the PR.
- **No speculative generality.** No frameworks, abstractions, or
  config knobs for needs that don't exist yet — except the
  explicitly-scoped forward-compat interfaces in constitution §IX.
- **No drive-by error handling.** Validate at trust boundaries only.
  Swallowed exceptions are review blockers. Argument guards use
  `Ensure.That(x).IsNotNull()` (ADR-0105), not
  `ArgumentNullException.ThrowIfNull`.
- **No drive-by comments.** Code says what; comments say *why*, only
  when the why is non-obvious. References to issues/tasks belong in
  the PR body, not the code.
- **Read before write.** Read the surrounding code and tests; mirror
  existing patterns rather than introducing new ones unjustified.

## What lives where

```
src/
  AppHost/              Aspire composition root (dev + prod)
  ServiceDefaults/      Aspire defaults: telemetry, resilience, health
  Shared.Kernel/        Value-object base types, Result<T,E>, no domain
  Shared.Contracts/     RabbitMQ messages + HTTP DTOs (versioned)
  <Context>/
    Domain/             Pure domain. No I/O, no framework refs.
    Application/        Use cases, command/query handlers.
    Infrastructure/     Persistence, RabbitMQ, external adapters.
    Api/                HTTP + RabbitMQ entry points.

apps/kiosk-web/         React + TypeScript + Vite — the wall. Aspire JS resource.
apps/management-web/    React + TypeScript + Vite — the operator console.
apps/shared/            Composites, API clients, observability. Used by both.
tests/                  xUnit. Includes NetArchTest boundary rules.
deploy/helm/            One hand-written Mosquitto chart. The Aspire k8s
                        publisher has never been run, and no k8s package is
                        referenced (ADR-0130, issue 1015).
```

## House rules

- **DDD with value objects.** Primitives do not appear on a domain
  model. **The authority is constitution §II** (ADR-0139, as amended by
  ADR-0140), which bans a *category* — any type with a C# keyword
  spelling, plus a named list of BCL types — with a stated scope and
  four exemptions. Do not restate it here, and do not restate it as a
  count: a partial summary is how §II drifted twice, first illustrated
  by three examples while 35 primitive properties accumulated, then
  enumerated as nine types that omitted `short`. If you find yourself
  passing one, introduce a value object (e.g. `CameraId`,
  `Percentage`). **`PrimitiveBoundaryTests` now fails the build** when a
  domain model exposes primitive-typed state, so this is no longer a rule
  a reviewer has to remember (issue #2028).
- **Refactors stay green; new behaviour starts red.** Two obligations,
  not one rule with an exception — constitution §Testing. A red test
  during a behaviour-preserving change is a regression. A new-behaviour
  test that was never seen failing does not satisfy the Phase 4 gate.
- **No cross-context project references.** Communication between
  bounded contexts only via `Shared.Contracts`. NetArchTest enforces
  this; PRs that break the rule cannot merge.
- **CQRS / event sourcing only where it earns its keep.** Overlays
  and Automation are first candidates (via Marten, **not yet used anywhere**). Other contexts
  default to plain CRUD against Postgres.
- **Latency budget is sacred** (constitution §IV). Any change on the
  event-to-overlay path cites which leg it affects.
- **Aspire is the composition root.** New runtime resources go in
  `AppHost`. Don't wire connection strings by hand.
- **A retried `POST` must not apply twice.** Two mechanisms, and they
  cover different populations — neither replaces the other.

  **The default protects everything** (ADR-0143): the standard resilience
  handler retries only methods RFC 9110 calls idempotent, so `POST` and
  `PATCH` get one attempt. A client whose non-idempotent calls are
  idempotent *in fact* opts back in with `RetryEveryMethod()` **and says
  why at the call site** — five do today: the four token mints, where a
  second token supersedes the first, and the MediaMTX gateway, whose
  `add/` answers 4xx for an existing path and whose `patch/` sets a fixed
  value. Retrying is the thing that needs justifying.

  **A caller may ask for more** (ADR-0142): an `Idempotency-Key` header
  makes the operation apply at most once and the same key return the same
  answer. Opt-in — no key, no change, including the 409 a genuine
  duplicate has always earned. Wire it with
  `IdempotencyHeaders.TryRead` plus `IdempotentRequest.ExecuteCreateAsync`
  (or `ExecuteAsync` where the answer is more than an identifier), an
  `IdempotencyStore<TDbContext>` registration, and
  `IdempotencyKeyTable.Create` in a migration.

  **Nine of the ten creates and rotations have a key. The tenth cannot**:
  `POST /webhook-integrations` returns a bearer token persisted only as a
  hash, so nothing on the server can rebuild it — unlike Identity's
  secrets, which Keycloak still holds and a replay reads back. Recorded
  because "all of them" is the kind of summary this file has had to
  correct before, and because the next person to reach for the pattern
  should meet the exception rather than discover it.

  **The scope includes the authenticated caller.** Keys are strings
  callers invent, so `"1"` and `"retry"` will collide; keyed on the string
  alone the second caller is handed the first caller's resource.
- **Prefer `Option<T>` to a nullable parameter, in Domain and
  Application** (ADR-0141). A nullable parameter says a value may be
  absent; `Option<T>` says what absent *means* and forces the caller to
  handle it. **Advisory, not enforced** — no analyzer, no architecture
  test, and existing signatures are not a defect.

  Scope is deliberate. Infrastructure and Api keep nullable parameters,
  because they translate wire and framework shapes where `null` is the
  native vocabulary — the same reasoning that exempts `Shared.Contracts`
  from §II. Nullable **properties** are untouched: persisted absences use
  nullable value-object references, because EF maps those and does not map
  `Option<T>`.

  **Baseline, 2026-09-02: 70 nullable reference parameters in Domain and
  Application, against 7 `Option<T>` parameters.** The count is recorded
  because this rule has no test, and an advisory rule nobody measures is
  the exact shape of every rule this repository has had to correct — §II
  twice, the Phase 3 board gate, §IV's leg table, ADR-0048. Re-measure
  before claiming it holds:

  ```sh
  grep -rnE '\([^)]*[A-Za-z]+\? [a-z][A-Za-z]*[,)]' --include=*.cs src/*/Domain src/*/Application | grep -v obj/ | wc -l
  ```

- **Handlers destructure their input first.** A message, command or
  query handler that reads **two or more** fields deconstructs the
  incoming record into locals as the first statement after the guard,
  then works with those locals. A handler that reads a single field
  keeps member access — a line of discards reads worse than the one
  `command.Name` it replaces.

  ```csharp
  public async Task Handle(SystemVariableValueRequestedV1 message, CancellationToken cancellationToken)
  {
      Ensure.That(message).IsNotNull();

      var (name, value, _, causingEventIdentifier, _) = message;
      ...
  }
  ```

  Discard (`_`) the fields the handler does not use.

  Deconstruction binds by **position**, so transposing two same-typed
  fields compiles cleanly and changes behaviour silently. Value objects
  make most such swaps a type error; contracts in `Shared.Contracts`
  carry primitives and do not. `HandlerDeconstructionTests` guards the
  gap: a local named after a *different* field of the same record fails
  the build. Renaming a local is fine — `Camera` → `cameraId` — as long
  as the new name isn't another field's.
- **`var` is allowed anywhere.** Both spellings are legal; pick
  whichever reads better at the call site. Neither IDE0007 nor
  IDE0008 fires — the `csharp_style_var_*` keys are `true:silent`.
  Prefer it where the right-hand side already names the type:
  `var items = rows.Select(Map).ToArray();`.
- **Private fields carry no leading underscore** — `gate`, not
  `_gate`. Most types use primary constructors, so an explicit field
  is already the exception. Where a constructor parameter shares the
  field's name, assign with `this.value = value;` (`this.` is
  permitted for exactly this, not mandated elsewhere).
- **Collections are declared with an explicit type and a collection
  expression** — `List<Camera> cameras = [];`, not `= new()`. `var`
  cannot express this at all: a collection expression has no natural
  type, so `var x = []` does not compile, which is exactly why the
  type is written out. Enforced by
  `dotnet_style_prefer_collection_expression` at `warning`, so it
  fails the Release build. A collection needing constructor
  arguments (`new(StringComparer.Ordinal)`) keeps `new` — it cannot
  be a collection expression, and the analyzer does not flag it.
  Rewriting a materialising `.ToList()` / `.ToArray()` into a spread
  (`[.. query]`) is a *different* transform and only advises
  (IDE0305 at `suggestion`).
- **Tests:** TDD for domain; integration against the real Aspire stack
  (booted via the `AspireFixture`; no Testcontainers — ADR-0103);
  NetArchTest for boundaries.

## Latency budget (do not erode)

`event arrival → overlay rendered ≤ 800 ms` — **not frame-synced**
(ADR-0129); a label is aged to match its picture rather than paired with
a frame. Broken
down as:

| Leg | Budget |
|---|---|
| Camera → SFU | ≤ 80 ms |
| SFU → kiosk decode | ≤ 120 ms |
| Presentation buffer (playout alignment) | ≤ 200 ms |
| Event → overlay state | ≤ 200 ms |
| Composite + render | ≤ 50 ms |
| Headroom | ≤ 150 ms |

**Every leg here is now built** (spec 045). The presentation buffer was
the last, and it turned out not to be a PTP problem: ADR-0128 found
ADR-014 could not be built as written (nothing we own is in the media
path; no browser exposes a PTP time API) and replaced it for the
intra-wall case with receiver playout alignment against the SFU's RTCP
clock. **Inter-display sync still needs PTP, and remains unbuilt and out
of scope** — it is not one of these six rows.

**Built is not the same as holding.** No one has yet watched a wall align
or re-measured the whole path with alignment active, so #1714 stays open
and §IV records this leg as *recorded, not yet observed* rather than
measured. Alignment is bought with latency out of this very budget —
measured at roughly double the absolute lag — so a wall that is aligned
but late has traded one breach for another.

Decode and composite-and-render were built long before that, and stood
recorded as unbuilt until spec 040 on the strength of a search scoped to
`apps/kiosk-web` when the capability lives in `apps/shared`.

§VII's dashboard rule binds **implemented** legs only (ADR-0117) — an
unbuilt leg is not exempt, it is *not yet subject*, and the obligation
attaches to whichever spec builds it. With nothing unbuilt, **every leg
is now subject**. **The authority is the table in constitution §IV**,
which distinguishes four states across the six legs; this summary must
not compete with it. Keep it current: a leg recorded as unbuilt after it
is built exempts itself by clerical error — not hypothetical, and §IV
says so — and a leg recorded as measured before anyone read its figure
claims a discharge nobody earned.

## Stack at a glance

| Concern | Choice | ADR |
|---|---|---|
| Frontend | React + TypeScript + Vite, **two apps** (`management-web` + `kiosk-web`) | 0074 |
| Frontend state | Redux Toolkit + RTK Query | 0075 |
| Real-time push | **Replaceable transport** (WebSocket v1, SSE v2 candidate) | 0076 |
| UI primitives | Radix UI headless components + custom design system | 0077 |
| Styling | Tailwind CSS with design tokens via CSS custom properties | 0078 |
| Frontend forms | React Hook Form + Zod | 0079 |
| Browser auth | `react-oidc-context` + custom kiosk flow | 0080 |
| Backend | .NET 10 + ASP.NET Core + .NET Aspire | 0024 |
| API style | Minimal APIs only | 0070 |
| Mediator | Hand-rolled `ICommandHandler<T,R>` / `IQueryHandler<T,R>` + Wolverine as dispatcher | 0042, 0057 |
| Domain events | Separate domain (in-process) and integration (`Shared.Contracts`, `V<N>` suffix) | 0040, 0073 |
| Value objects | **Maximalist hand-written**, `IValueObject<T>` marker, `.From(...)` + `Ensure.That(...)` | 0038, 0046, 0066 |
| IDs | **Guid v7** in strongly-typed records with **`Identifier` suffix** (`CameraIdentifier`, `LayoutIdentifier`) | 0039, 0090 |
| Naming | **No shortcuts or aliases** (`Identifier` not `Id`, `Repository` not `Repo`, …); identifier-typed properties named after the noun (`Owner` not `OwnerIdentifier`) | 0091, 0094 |
| Domain layout | Per-aggregate folder containing aggregate + VOs + repository + `Events/` subfolder | 0092 |
| Application layout | Per-message-kind: `Commands/`, `Queries/`, `EventHandlers/`, `DTOs/`, each with `Handlers/` subfolder and paired `*Errors.cs` | 0093 |
| Errors | `Result<T, Error>` with `ApiError(Code, Message, HttpStatusCode)` base | 0047, 0089 |
| Argument guards | **`Ensure.That(x).IsNotNull()`** — never `ArgumentNullException.ThrowIfNull` or bare `throw new ArgumentException` for argument preconditions (AppHost + generated migrations + parse/format errors excepted) | 0059, 0105 |
| Nulls | **NRT enabled** solution-wide, no exceptions (ADR-0141); `Option<T>` for domain absences and repository lookups, nullable references for persisted state | 0048, **0141** |
| Async | `CancellationToken` mandatory last param; no `ConfigureAwait` | 0049 |
| Persistence | PostgreSQL. **Marten is permitted and unused** — no context has justified it (ADR-0130) | 0009, 0071, **0130** |
| Concurrency | Two-layer optimistic: `If-Match` expected version (cross-request) + EF token (in-transaction); no retry-on-conflict | 0043, **0113** |
| Retry safety | `POST`/`PATCH` not retried by default; five clients opt back in with a stated reason. Caller-supplied `Idempotency-Key` replays the original answer — opt-in, on 9 of the 10 creates and rotations | **0142**, **0143** |
| Object store | MinIO (future) | 0009 |
| Messaging | RabbitMQ (via Wolverine) | 0010, 0042 |
| Sagas | Wolverine state machines + compensating actions | 0072 |
| Identity | Keycloak (OIDC) per fab | 0007, 0008 |
| Streaming | WebRTC SFU; passthrough + GPU transcode fallback | 0011, 0012 |
| Time | PTP (IEEE 1588) per fab — for fab-wide correlation and inter-display sync, **not** for the presentation-buffer leg | 0014, 0021, **0128** |
| Logging | `ILogger<T>` + OpenTelemetry OTLP (MEL-native, **no Serilog**); `[LoggerMessage]` source-gen; structured fields | 0050 |
| DI | Per-context `Add<Context>{Infrastructure,Api}` extension methods | 0051 |
| Migrations | Dedicated `MigrationRunner` worker | 0067 |
| Test framework | xUnit + **Shouldly** (free) + **Moq** + hand-written fakes; integration via the **Aspire fixture** (no Testcontainers) | 0052, 0103 |
| Test naming | Sentence-style with underscores | 0053 |
| Test data | Hand-written fluent builders, no AutoFixture | 0054 |
| Coverage gates | Domain ≥ 90%, Application ≥ 80%, Shared ≥ 90% (CI-enforced) | 0065 |
| Code metrics | Max 300 LOC/file, 30 LOC/method, 4 params, complexity ≤ 10, depth ≤ 3 (SonarAnalyzer) | 0084 |
| Wolverine defaults | Per-module queue isolation + eager transactions + Postgres outbox | 0088 |
| Git: commits | Conventional Commits, **no `Co-Authored-By` footer** | 0030, 0086 |
| Git: merge | **Rebase-only** (no squash, no merge commits) | 0029, 0087 |
| Observability | OpenTelemetry → **one sink per environment**: Aspire dashboard in dev/CI; production sink deferred until there is a production deployment. The dual-sink comparison ADR-0026 planned never started and is abandoned. | 0026, 0118 |
| Orchestration | Aspire AppHost (dev) → k3s + Helm (prod) | 0024, 0025 |

**Diverges from Yumney on:** NRT (we: disabled; Yumney: enabled), `Result<T, Error>` shape, Shouldly vs FluentAssertions, Moq vs NSubstitute, sentence-style vs `Method_Scenario_Expected` test naming, initial test layout (minimal vs full per-layer), **Marten** for event-sourced contexts (Yumney: EF Core), narrower Architecture.Tests scope, no story-ref in commits. See ADRs 0056–0063, 0082, 0083, 0085 for the reasoning per divergence.

**Aligns with Yumney on:** Hand-written VOs, Guid v7 typed IDs, `Identifier` suffix, no shortcuts, per-aggregate Domain folders, per-message-kind Application folders, identifier-noun property naming, custom `Deconstruct(...)`, plural variable names for repository injections, `IValueObject<T>` marker, `MigrationRunner` pattern, AspireFixture pattern (deferred), 90/80/90 coverage gates, hand-rolled `ICommandHandler<T,R>` interfaces with Wolverine dispatcher, `ApiError` with HTTP status, per-module Wolverine queue isolation + eager transactions, no `Co-Authored-By`, rebase-only merge, SonarAnalyzer code-metric limits.

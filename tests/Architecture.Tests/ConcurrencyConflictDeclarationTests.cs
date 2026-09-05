using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the contract half of ADR-0113's Layer 2, widened to the question the
/// document actually answers: <b>a mutating endpoint that can answer
/// <c>409</c> declares it, and one that cannot does not</b> (issue #2096,
/// spec 075).
///
/// <para>
/// <b>The first version of this guard partitioned on
/// <c>DbUpdateConcurrencyException</c>, and that was too narrow to be true.</b>
/// OpenAPI has one <c>409</c> slot per operation and does not record a cause,
/// so the only question a <c>Produces</c> chain can be judged against is
/// whether the route can answer the status <em>at all</em>. This repository has
/// <b>four</b> producers of it, and a register naming one of them classified
/// eight routes by a mechanism it never mentioned and one route wrongly:
/// </para>
/// <list type="number">
/// <item>
/// <b>Handler refusal.</b> A command handler returns a <c>Result</c> failure
/// whose <c>ApiError.Status</c> is <c>HttpStatusCode.Conflict</c>;
/// <c>ApiErrorResults.ToProblem</c> renders that status onto the wire. No race
/// is involved — a name already taken, a stale <c>If-Match</c> version
/// (ADR-0113 Layer 1), a terminal state. <b>This is the mechanism on 25 of the
/// 33 mappings</b>, and it is the one the earlier register did not name.
/// <b>Re-measured on 2026-09-05; this figure read 26 until then</b>, and so does
/// the body of the commit that claimed to have corrected the class doc's false
/// figures. Counted three ways, because a number in a document about wrong
/// numbers should not rest on one: the word <c>refusal</c> occurs 25 times in
/// <see cref="CanAnswerConflict"/>; the four rows that lack it are exactly the
/// four whose mechanism string says <c>ONLY</c>, and 29 − 4 = 25; and 25 is the
/// number of <c>.ProducesProblem(StatusCodes.Status409Conflict)</c> call sites
/// that stood under <c>src/*/Api</c> before this branch — a set that coincides
/// with the refusal rows exactly, because the four declarations this branch adds
/// are those same four <c>ONLY</c> routes.
/// </item>
/// <item>
/// <b>Lost update.</b> <c>ConcurrencyConflictExceptionHandler</c> turns EF
/// Core's <c>DbUpdateConcurrencyException</c> into
/// <c>409 AGGREGATE_VERSION_STALE</c>. EF raises it from its affected-row check
/// on an <c>UPDATE</c> or a <c>DELETE</c>, so an insert cannot reach it.
/// </item>
/// <item>
/// <b>Unique-index race.</b> <c>UniqueConstraintExceptionHandler</c> answers
/// <c>409 RESOURCE_ALREADY_EXISTS</c> on any unique violation. It is registered
/// after the concurrency handler and matches the SQLSTATE rather than the
/// exception type, so the two cannot swallow each other.
/// </item>
/// <item>
/// <b>Idempotency in progress.</b> <c>IdempotentRequest</c> <em>returns</em>
/// <c>Results.Problem(… Status409Conflict)</c> with
/// <c>IDEMPOTENT_REQUEST_IN_PROGRESS</c> when an earlier request carrying the
/// same <c>Idempotency-Key</c> outlives the five-second poll window (ADR-0142).
/// It is not an exception, so nothing catches it and no exception-handler
/// survey finds it.
/// </item>
/// </list>
///
/// <para>
/// All four are registered or reached unconditionally — the exception handlers
/// in <c>AddBearerAuthentication</c>, which all nine Api <c>Program.cs</c> files
/// call — so the limit on their reach is the write path, not registration.
/// </para>
///
/// <para>
/// <b>This is a register, and the two sets below are typed in, not derived.</b>
/// That is the first thing to know about it, because every other guard in this
/// directory derives its claim and this one cannot. Whether a route can answer
/// the status is settled three or four hops away — endpoint, command handler,
/// repository, EF, plus the endpoint's own idempotency wiring — across the
/// Application boundary, and the discriminating facts (an <c>ApiError</c>
/// carrying <c>HttpStatusCode.Conflict</c>, <c>camera.Retire(…)</c> before a
/// <c>SaveAsync</c>, an <c>.IsUnique()</c> index, an
/// <c>IdempotentRequest.Execute…</c> call) are not all visible at the Api layer.
/// No scan of <c>src/*/Api</c> can decide it, so a human decided it on
/// 2026-09-05, at every handler named below, and wrote the answer here.
/// </para>
///
/// <para>
/// <b>The question a new endpoint's author answers</b> to place it in one set or
/// the other: <em>can any of the four mechanisms above produce a <c>409</c> on
/// this route?</em> If yes, its route joins <see cref="CanAnswerConflict"/> with
/// the mechanism, and its chain declares <c>StatusCodes.Status409Conflict</c>.
/// If no, its route joins <see cref="CannotAnswerConflict"/> with the reason,
/// and its chain must not declare one. Declaring 409 on an endpoint that cannot
/// answer it is the same defect as omitting it from one that can, pointing the
/// other way, and this guard fails on both with different messages.
/// </para>
///
/// <para>
/// <b>What it asserts.</b> Every <c>Map(Post|Put|Patch|Delete)</c> mapping under
/// <c>src/*/Api</c> resolves to a route this reader can name; the census is
/// pinned at <see cref="MutatingMappingCount"/> mappings in
/// <see cref="MutatingMappingFileCount"/> files across
/// <see cref="MutatingMappingContextCount"/> contexts and cross-checked against
/// an independent flat sweep; every mapped route sits in exactly one of the two
/// pinned sets and every pinned route is still mapped; each of
/// <see cref="CanAnswerConflict"/> declares the conflict in its own fluent
/// chain; and none of <see cref="CannotAnswerConflict"/> does.
/// </para>
///
/// <para>
/// <b>Route identity is lexical, and carries its context.</b> A route is the
/// bounded context from <c>src/&lt;Context&gt;/Api</c>, the verb, the prefix of
/// the nearest preceding <c>MapGroup</c> literal in the same file, and the
/// mapping's own route literal, concatenated exactly as written — so a mapping
/// on <c>"/"</c> reads with a trailing slash (<c>CameraCatalog POST
/// /cameras/</c>), and the <b>five</b> files that map two groups
/// (<c>RulesEndpoints</c>, <c>CameraEndpoints</c>, <c>EventsEndpoints</c>,
/// <c>DevicesEndpoints</c>, <c>KiosksEndpoints</c>) bind by lexical position
/// rather than by the variable the mapping is written on. The context is part
/// of the identity because the prefix alone is not unique: EventIngestion and
/// Identity both map <c>/webhook-integrations</c> today. Without it, two rows
/// that collided would silently merge into one and the only failure would be
/// the census arithmetic — <c>"32 is not 33"</c>, the one message that names no
/// route.
/// </para>
///
/// <para>
/// <b>What a green run does not prove.</b> This repository has a recorded
/// failure mode — a guard that reads the design artefact proves the design was
/// written down, not that it holds. This guard is one of those, and the list
/// below is here so that nobody has to discover it.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>It cannot classify a new endpoint.</b> It is a register, not a
/// derivation: it records a judgement about thirty-three routes and cannot make
/// the judgement about a thirty-fourth. What it buys is that the judgement
/// cannot be <em>skipped</em> — a new mutating mapping fails the pinned census
/// and lands its author in this file, at the question above.
/// </item>
/// <item>
/// <b>It cannot tell which mechanism a declared 409 was written for, and does
/// not read the problem code.</b> OpenAPI has one slot; most rows below carry
/// two or three mechanisms, and the guard checks only that the slot is present.
/// A route whose declaration was added for a name collision is green whether or
/// not anyone ever considered the lost update, and a route that answers
/// <c>IDEMPOTENT_REQUEST_IN_PROGRESS</c> where this register records
/// <c>AGGREGATE_VERSION_STALE</c> is green too. <b>A green run is not evidence
/// that the rule was applied</b> — only that nobody removed a line or added a
/// mutating endpoint unclassified.
/// </item>
/// <item>
/// <b>It does not prove reachability.</b> That a lost update or a unique-index
/// race can actually be provoked on a route is argued in spec 075 from the
/// handler bodies and the EF configurations. No test asserts it: a true
/// database race needs two overlapping transactions against real Postgres,
/// which is Docker, CI-only and a race to arrange. Not attempted, and not
/// claimed. The two deterministic mechanisms — a handler refusal and an
/// idempotency replay — are reachable by a single request each, and are still
/// only argued here, not exercised.
/// </item>
/// <item>
/// <b>It reads the fluent chain, not the generated document.</b> Safe today
/// because no <c>MapGroup</c> chain in these directories declares a response —
/// asserted below, so it stops being an assumption. If one ever does, the guard
/// under-reads. It reads <em>masked</em> source: comments are blanked before
/// anything is matched, so a <c>.ProducesProblem(StatusCodes.Status409Conflict)</c>
/// commented out inside a chain is no longer credited, and string <em>and char</em>
/// literals are stepped over when the chain's end is found, so neither an
/// unbalanced bracket inside a <c>WithSummary</c> nor a bracket written as
/// <c>'('</c> can run one chain into the next. Those are the same defect reached
/// two ways, and the second was found only after the first was believed to have
/// closed it — so both are held by constructed counterexamples below rather than
/// by the census, which would have had to ban a legal form to notice. The masker
/// handles only the string and comment forms these files use — no verbatim, raw
/// or interpolated-with-escape literals — and <em>that</em> is asserted below
/// too, rather than assumed.
/// </item>
/// <item>
/// <b>It is rooted at <c>src/*/Api</c>.</b> A mapping that leaves those
/// directories is invisible to it; the pinned census, not the sweep, is what
/// turns that into a failure — two numbers derived from one glob shrink together
/// and stay green.
/// </item>
/// </list>
/// </summary>
public class ConcurrencyConflictDeclarationTests
{
    /// <summary>
    /// The shape a declaration is written in. Matched by shape rather than by
    /// the bare status name so that the sweep and the walk count declarations
    /// rather than every mention of the constant.
    /// </summary>
    private static readonly Regex ConflictDeclaration = new(
        @"\.ProducesProblem\(\s*StatusCodes\.Status409Conflict\s*\)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex MutatingMappingCall = new(
        @"\.Map(?<verb>Post|Put|Patch|Delete)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex GroupPrefix = new(
        @"\.MapGroup\s*\(\s*""(?<prefix>[^""]*)""",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A plain string literal as the first argument of the mapping call.
    /// Anything else is unreadable and fails; nothing resolves to a pass by
    /// default.
    /// </summary>
    private static readonly Regex PlainRouteLiteral = new(
        @"^\s*""(?<route>[^""\\]*)""\s*,",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The literal forms this reader's masker does not handle. Their absence is
    /// asserted rather than assumed: masking is what makes a commented-out
    /// declaration uncreditable and an unbalanced bracket inside a summary
    /// harmless, and a masker that silently mis-reads a shape it was never
    /// taught is worse than no masking at all.
    /// </summary>
    private static readonly (string Form, string Why)[] UnmaskableLiteralForms =
    [
        ("@\"", "a verbatim string, in which a doubled quote closes nothing"),
        ("\"\"\"", "a raw string literal, whose delimiter is longer than one quote"),
        ("\\\"", "an escaped quote, which this reader steps over but which changes where a literal ends"),
        ("'\"'", "a quote as a char literal, which would open a string that never closes"),
    ];

    /// <summary>
    /// Thirty-three mutating mappings, in eleven files, across eight contexts.
    /// Pinned rather than merely compared: every other count in this file is
    /// derived from one glob, so a file leaving <c>src/*/Api</c> shrinks both
    /// sides of every comparison at once and nothing goes red. Adding, moving or
    /// removing a mutating endpoint edits one of these numbers in the same diff.
    /// </summary>
    private const int MutatingMappingCount = 33;

    private const int MutatingMappingFileCount = 11;

    private const int MutatingMappingContextCount = 8;

    /// <summary>
    /// The twenty-nine routes that can answer <c>409</c>, each with the
    /// mechanism — or mechanisms — that produce it. Each must declare
    /// <c>Status409Conflict</c> in its own chain.
    ///
    /// <para>
    /// The mechanism is written out per row rather than left to the class doc so
    /// that a wrong row is <em>visible</em> rather than merely plausible: a
    /// reader can open the named errors file, index or handler and disagree.
    /// Every row below was read at its handler on 2026-09-05.
    /// </para>
    ///
    /// <para>
    /// <b>A row names mechanisms sufficient to reach the status, not every
    /// mechanism that reaches it.</b> Read it as <em>at least these</em>. Review
    /// found the unique-race column incomplete — the two <c>draft</c> rows race
    /// on <c>ux_layout_revisions_number</c> / <c>ux_overlay_revisions_number</c>,
    /// and no row cited it — and that gap is now closed, but nothing here can
    /// promise there is not another: a route's mechanisms are settled three or
    /// four hops away, and completeness across four mechanisms and twenty-nine
    /// routes is not something a reader can check. <b>No assertion in this file
    /// depends on the strings.</b> They are carried into the two partition
    /// failure messages by <see cref="MechanismFor"/> and
    /// <see cref="ReasonFor"/>, so an incomplete row makes a failure less
    /// informative and can never make a passing route wrong — the classification
    /// turns on <em>whether any</em> mechanism reaches the status, and one
    /// sufficient mechanism settles that. A <see cref="CannotAnswerConflict"/>
    /// reason is held to the opposite and stricter standard: it has to clear all
    /// four.
    /// </para>
    ///
    /// <para>
    /// <b>Three rows carry one mechanism only, and they are the spec's defect.</b>
    /// <c>POST /cameras/{camera:guid}/retire</c>, <c>DELETE /devices/{clientId}</c>
    /// and <c>DELETE /kiosks/{clientId}</c> reach nothing but the lost update —
    /// none of <c>RetireCameraErrors</c>, <c>DisableDeviceError</c> or
    /// <c>DisableKioskError</c> carries <c>HttpStatusCode.Conflict</c>. The
    /// absence of a <c>Conflict</c> is the evidence, not the absence of a file:
    /// the two Disable error hierarchies do exist, inside
    /// <c>DisableDeviceCommand.cs</c> and <c>DisableKioskCommand.cs</c> rather
    /// than in files of their own, and an earlier draft of this register argued
    /// from a filename that would have gone stale the moment someone moved them.
    /// That is <em>why</em> those three were the omissions: every other mutating
    /// route had a second, deterministic reason to declare the status, and these
    /// had only the race nobody was thinking about.
    /// </para>
    ///
    /// <para>
    /// <b>A fourth row carries one mechanism only, and it is the review's
    /// finding.</b> <c>POST /events/manual</c> refuses nothing with a
    /// <c>Conflict</c> and inserts rather than updates; <c>events</c> does carry
    /// a unique constraint in its composite key <c>(Fab, Id, IngestedAt)</c>, but
    /// <c>Id</c> is a fresh Guid v7 and <c>StoreOrRefuseAsync</c> answers 503 to
    /// every non-cancel exception anyway, so no database exception on that route
    /// survives to be rendered as a 409. It is wired to
    /// <c>IdempotentRequest.ExecuteCreateAsync</c> against a registered
    /// <c>IdempotencyStore&lt;EventIngestionDbContext&gt;</c>, and that 409 is
    /// <em>returned</em> rather than thrown — outside the catch — so two
    /// concurrent calls sharing a key and a caller make the second answer
    /// <c>409 IDEMPOTENT_REQUEST_IN_PROGRESS</c>. It is the only one of the nine
    /// keyed creates and rotations whose chain does not say so.
    /// </para>
    /// </summary>
    private static readonly ConflictCapableRoute[] CanAnswerConflict =
    [
        new(
            "Automation POST /rules/",
            "refusal (CreateRuleErrors.NameAlreadyTaken); unique race (ux_rules_fab_name_active); "
            + "idempotency"),
        new(
            "Automation POST /rules/{name}/publish",
            "refusal (PublishRuleFailures.RuleStale, RuleAlreadyArchived); lost update "
            + "(rule.Publish(clock) then SaveAsync)"),
        new(
            "Automation POST /rules/{name}/archive",
            "refusal (ArchiveRuleFailures.RuleStale); lost update (rule.Archive(clock) then SaveAsync)"),
        new(
            "CameraCatalog POST /cameras/",
            "refusal (RegisterCameraFailures.NameAlreadyTaken); unique race "
            + "(ux_cameras_fab_name_normalized_active); idempotency"),
        new(
            "CameraCatalog POST /cameras/{camera:guid}/retire",
            "lost update ONLY — RetireCameraErrors declares no Conflict, so the race is the whole of "
            + "this route's 409 and the omission spec 075 fixes"),
        new(
            "CameraCatalog PATCH /cameras/{camera:guid}",
            "refusal (RenameCameraErrors, ChangeCameraAddressErrors); lost update; unique race on a "
            + "rename (ux_cameras_fab_name_normalized_active)"),
        new(
            "EventIngestion POST /events/manual",
            "idempotency ONLY — IngestEventCommandHandler calls events.Add(@event) then SaveAsync and "
            + "refuses nothing with a Conflict. The events table does carry a unique constraint, its "
            + "composite key (Fab, Id, IngestedAt) at EventConfiguration.cs:30, but it is unreachable "
            + "(Id is a fresh Guid v7) and moot either way: StoreOrRefuseAsync catches every non-cancel "
            + "exception and answers 503, so no database exception on this path ever reaches the "
            + "handlers that render 409. The 409 is IdempotentRequest.ExecuteCreateAsync answering "
            + "IDEMPOTENT_REQUEST_IN_PROGRESS, which is returned rather than thrown and so is outside "
            + "that catch"),
        new(
            "EventIngestion POST /webhook-integrations/",
            "refusal (RegisterWebhookIntegrationErrors); unique race (ux_webhook_integrations_name); no "
            + "idempotency — ADR-0142's one create whose answer cannot be replayed"),
        new(
            "EventIngestion DELETE /webhook-integrations/{name}",
            "refusal (RevokeWebhookIntegrationErrors); lost update (integration.Revoke then SaveAsync)"),
        new(
            "Identity POST /devices/register",
            "refusal (RegisterDeviceErrors); unique race (ux_registered_clients_clientid_active); "
            + "idempotency"),
        new(
            "Identity DELETE /devices/{clientId}",
            "lost update ONLY — DisableDeviceError declares DeviceNotFound (404) and KeycloakUnavailable "
            + "(502) and nothing carrying HttpStatusCode.Conflict (DisableDeviceCommand.cs), so "
            + "DisableDeviceCommandHandler's load, client.Disable(clock) and SaveAsync is the whole of "
            + "this route's 409"),
        new(
            "Identity POST /kiosks/enroll",
            "refusal (EnrollKioskErrors); unique race (ux_registered_clients_clientid_active); "
            + "idempotency"),
        new(
            "Identity DELETE /kiosks/{clientId}",
            "lost update ONLY — DisableKioskError declares KioskNotFound (404) and KeycloakUnavailable "
            + "(502) and nothing carrying HttpStatusCode.Conflict (DisableKioskCommand.cs), so "
            + "DisableKioskCommandHandler's load, client.Disable(clock) and SaveAsync is the whole of "
            + "this route's 409"),
        new(
            "Identity POST /webhook-integrations/{name}/rotate",
            "refusal (RotateWebhookClientCommand's stale check); lost update on the branch that rotates "
            + "an existing client; idempotency"),
        new(
            "LayoutComposition POST /layouts/",
            "refusal (CreateLayoutDraftErrors.NameAlreadyTaken); idempotency. NOT a unique race: "
            + "ix_layouts_fab_name is not unique, so a concurrent create is not refused by the index"),
        new(
            "LayoutComposition POST /layouts/{layoutIdentifier:guid}/draft",
            "refusal (BranchDraftRevisionErrors); lost update; unique race "
            + "(ux_layout_revisions_number — Layout.BranchDraft adds a revision numbered "
            + "MaxRevisionNumber().Next(), so two concurrent branches of one layout compute the same "
            + "number and the second violates the index)"),
        new(
            "LayoutComposition POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/publish",
            "refusal (PublishRevisionErrors); lost update; unique race "
            + "(ux_layout_revisions_one_published)"),
        new(
            "LayoutComposition POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/archive",
            "refusal (ArchiveRevisionErrors); lost update"),
        new(
            "LayoutComposition POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/revert",
            "refusal (RevertRevisionErrors); lost update"),
        new(
            "LayoutComposition PATCH /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}",
            "refusal (EditDraftRevisionErrors); lost update"),
        new(
            "OverlayDesigner POST /overlays/",
            "refusal (CreateOverlayDraftErrors.NameAlreadyTaken); idempotency. NOT a unique race: "
            + "ix_overlays_name is not unique"),
        new(
            "OverlayDesigner POST /overlays/{overlayIdentifier:guid}/draft",
            "refusal (BranchDraftRevisionErrors); lost update; unique race "
            + "(ux_overlay_revisions_number — Overlay.BranchDraft adds a revision numbered "
            + "MaxRevisionNumber().Next(), so two concurrent branches of one overlay compute the same "
            + "number and the second violates the index)"),
        new(
            "OverlayDesigner POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/publish",
            "refusal (PublishRevisionErrors); lost update; unique race "
            + "(ux_overlay_revisions_one_published)"),
        new(
            "OverlayDesigner POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/archive",
            "refusal (ArchiveRevisionErrors); lost update"),
        new(
            "OverlayDesigner POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/revert",
            "refusal (RevertRevisionErrors); lost update"),
        new(
            "OverlayDesigner PATCH /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}",
            "refusal (EditDraftRevisionErrors); lost update"),
        new(
            "SystemVariables POST /system-variables/",
            "refusal (DefineVariableErrors); unique race (ux_system_variables_fab_name_active); "
            + "idempotency"),
        new(
            "SystemVariables PUT /system-variables/{name}/value",
            "refusal (SetVariableValueErrors); lost update"),
        new(
            "SystemVariables POST /system-variables/{name}/archive",
            "refusal (ArchiveVariableErrors); lost update"),
    ];

    /// <summary>
    /// The four routes no mechanism can make answer <c>409</c>, each with the
    /// reason. Adding the status to any of them would be a new false claim of
    /// exactly the kind #2096 was filed about — the same defect, in the opposite
    /// direction.
    ///
    /// <para>
    /// Each reason has to clear all four mechanisms, not just the lost update.
    /// That is what the earlier register got wrong about
    /// <c>POST /events/manual</c>: its write path really is an insert, and the
    /// conclusion drawn from that was still false, because the 409 came from
    /// somewhere the register was not looking.
    /// </para>
    /// </summary>
    private static readonly ConflictFreeRoute[] CannotAnswerConflict =
    [
        new(
            "Automation POST /rules/{name}/dry-run",
            "a POST because it carries a sample-event body, but a read: mapped on the read group, "
            + "nothing is persisted, and DryRunRuleErrors declares only 404 and 400"),
        new(
            "EventIngestion POST /events/webhook/{integrationName}",
            "it reads the integration to authenticate the delivery and never writes it back, then "
            + "inserts an event; no error on that path carries HttpStatusCode.Conflict, this route reads "
            + "no Idempotency-Key, and the only unique constraint on events — the composite key "
            + "(Fab, Id, IngestedAt) — is both unreachable on a fresh Guid v7 and moot, because "
            + "StoreOrRefuseAsync catches every non-cancel exception and answers 503 before any "
            + "exception handler could render a 409"),
        new(
            "StreamDistribution POST /streams/authorize",
            "AuthorizeWhepCommandHandler validates a forwarded token against a read-only stream lookup "
            + "and calls no SaveAsync; AuthorizeWhepErrors declares only 401 and 403"),
        new(
            "StreamDistribution POST /streams/kiosk-latency",
            "it records a meter value; nothing enters a domain model and no DbContext is reached"),
    ];

    private static readonly Lazy<List<MutatingMapping>> TheMappings = new(Read);

    // ---- nothing resolves to a pass by default ------------------------------

    /// <summary>
    /// <b>FR-005 — every mutating mapping resolves to a route this guard can
    /// name.</b> A guard that quietly skips what it cannot parse is the guard
    /// that was not there, so a route literal that is not a plain string, or a
    /// chain with no terminating semicolon, is a failure naming the file, line
    /// and verb — never a skip, never a compliant row.
    /// </summary>
    [Fact]
    public void Every_mutating_mapping_under_the_api_directories_resolves_to_a_route_this_guard_can_name()
    {
        IReadOnlyList<MutatingMapping> mappings = TheMappings.Value;

        mappings.Count.ShouldBeGreaterThan(
            0,
            "no mutating mapping was found under src/*/Api. That is the reader failing, not the product: "
            + "every later assertion in this file would then pass over an empty set.");

        string[] unreadable = mappings
            .Where(mapping => mapping.Failure is not null)
            .Select(mapping => $"{mapping.File}:{mapping.Line} {mapping.Verb} — {mapping.Failure}")
            .ToArray();

        unreadable.ShouldBeEmpty(
            "these mutating mappings cannot be named as routes:"
            + Environment.NewLine + string.Join(Environment.NewLine, unreadable) + Environment.NewLine
            + "This guard classifies an endpoint by its route identity, so a mapping it cannot name is a "
            + "mapping it cannot place in either pinned set — and it fails rather than passing. The shape "
            + "it reads is a plain string literal as the first argument of the Map call, inside a method "
            + "whose nearest preceding MapGroup literal supplies the prefix.");
    }

    // ---- FR-004: the census, pinned and cross-checked -----------------------

    /// <summary>
    /// <b>FR-004 — the census.</b> Three numbers rather than one, so the failure
    /// says which moved: a mapping added or removed, a file that stopped mapping
    /// or started, a context that gained or lost a write surface.
    /// </summary>
    [Fact]
    public void The_mutating_surface_is_thirty_three_mappings_in_eleven_files_across_eight_contexts()
    {
        IReadOnlyList<MutatingMapping> mappings = TheMappings.Value;

        string[] files = mappings.Select(m => m.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] contexts = mappings.Select(m => m.Context).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        mappings.Count.ShouldBe(
            MutatingMappingCount,
            $"the walk found {mappings.Count} mutating mappings under src/*/Api, not {MutatingMappingCount}. "
            + "The population moved. Whichever endpoint was added or removed, it is classified in the same "
            + "diff: can a handler refusal, a lost update, a unique-index race or an idempotency replay "
            + "answer 409 on it? Add its route to CanAnswerConflict with the mechanism and declare the "
            + "status, or to CannotAnswerConflict with the reason all four are out of reach. Routes found: "
            + string.Join(", ", mappings.Select(m => m.Identity).Order(StringComparer.Ordinal)));

        files.Length.ShouldBe(
            MutatingMappingFileCount,
            $"the walk found mutating mappings in {files.Length} files, not {MutatingMappingFileCount}: "
            + string.Join(", ", files));

        contexts.Length.ShouldBe(
            MutatingMappingContextCount,
            $"the walk found mutating mappings in {contexts.Length} bounded contexts, not "
            + $"{MutatingMappingContextCount}: " + string.Join(", ", contexts));
    }

    /// <summary>
    /// <b>FR-004 — the walk and a flat sweep count the same thing two ways.</b>
    /// The walk reads a mapping only when it can also read its route literal and
    /// its chain; the sweep counts the call token alone. Two numbers that can
    /// disagree, not one number checked twice — which is what keeps a lexical
    /// reader honest when a mapping is written in a shape it does not parse.
    /// </summary>
    [Fact]
    public void A_flat_sweep_of_the_api_directories_counts_the_same_mutating_mappings_as_the_walk()
    {
        DirectoryInfo root = RepositoryRoot();
        int swept = ApiSourceFiles(root)
            .Sum(file => MutatingMappingCall.Count(Source(root, file)));

        swept.ShouldBe(
            MutatingMappingCount,
            $"a flat sweep of src/*/Api found {swept} .Map(Post|Put|Patch|Delete)( call sites, not "
            + $"{MutatingMappingCount}. Re-measure and edit this number in the same diff as the endpoint — "
            + "it is what stops the walk and the sweep shrinking together and staying green.");

        TheMappings.Value.Count.ShouldBe(
            swept,
            $"the walk read {TheMappings.Value.Count} mutating mappings; the flat sweep found {swept} call "
            + "sites. A call site the walk cannot read is an endpoint this guard does not classify at all.");
    }

    // ---- FR-003: the partition, in both directions --------------------------

    /// <summary>
    /// <b>FR-003 — the omission direction.</b> Every route that can answer the
    /// status declares it. This is the assertion spec 075 expects to be red
    /// before the fix — on the three lost-update omissions when the guard was
    /// first written, and on <c>POST /events/manual</c> after the review widened
    /// the predicate to the mechanism that route actually reaches.
    /// </summary>
    [Fact]
    public void Every_endpoint_that_can_answer_a_conflict_declares_it()
    {
        string[] undeclared = TheMappings.Value
            .Where(mapping => CanAnswerConflict.Any(capable => Same(capable.Route, mapping.Identity)))
            .Where(mapping => !ConflictDeclaration.IsMatch(mapping.Chain))
            .OrderBy(mapping => mapping.File, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Line)
            .Select(mapping => $"{Describe(mapping)} — {MechanismFor(mapping.Identity)}")
            .ToArray();

        undeclared.ShouldBeEmpty(
            $"{undeclared.Length} mutating endpoint(s) do not declare the 409 their write path can "
            + "answer:" + Environment.NewLine
            + string.Join(Environment.NewLine, undeclared) + Environment.NewLine
            + "The mechanism recorded against each route above is the one that produces the status — a "
            + "handler refusal rendered by ApiErrorResults.ToProblem, a lost update answered by "
            + "ConcurrencyConflictExceptionHandler, a unique-index race answered by "
            + "UniqueConstraintExceptionHandler, or an in-progress replay returned by IdempotentRequest "
            + "(ADR-0113 Layer 2, ADR-0119, ADR-0142). The generated OpenAPI currently asserts that "
            + "status cannot happen on this route, so a client generated from it has no branch for it. "
            + "Add .ProducesProblem(StatusCodes.Status409Conflict) to the mapping's own chain.");
    }

    /// <summary>
    /// <b>FR-003, FR-005 — the mirror.</b> A declared conflict that no mechanism
    /// can produce is the same defect as an undeclared one, pointing the other
    /// way, and it arrives from a different cause: someone reading #2096 as filed
    /// and adding the status to all thirty-three. The message shares no sentence
    /// with the one above, on purpose.
    /// </summary>
    [Fact]
    public void No_endpoint_that_cannot_answer_a_conflict_declares_one()
    {
        string[] surplus = TheMappings.Value
            .Where(mapping => CannotAnswerConflict.Any(free => Same(free.Route, mapping.Identity)))
            .Where(mapping => ConflictDeclaration.IsMatch(mapping.Chain))
            .OrderBy(mapping => mapping.File, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Line)
            .Select(mapping => $"{Describe(mapping)} — {ReasonFor(mapping.Identity)}")
            .ToArray();

        surplus.ShouldBeEmpty(
            $"{surplus.Length} endpoint(s) advertise a 409 that nothing on their path can answer:"
            + Environment.NewLine + string.Join(Environment.NewLine, surplus) + Environment.NewLine
            + "Four mechanisms can produce the status here and the reason above clears all four: no "
            + "error on the path carries HttpStatusCode.Conflict, EF is not made to UPDATE or DELETE an "
            + "existing row, no unique index is written through, and no Idempotency-Key is read. So this "
            + "line publishes an answer the route will never give. Remove it, or — if the path has "
            + "genuinely gained one of the four — move the route to CanAnswerConflict, naming which, in "
            + "the same diff as the change that made it true.");
    }

    /// <summary>
    /// <b>FR-005, FR-007 — no route is unclassified, and no pinned route is a
    /// ghost.</b> Read in both directions: a mapped route in neither pinned set
    /// fails naming the question its author must answer, and a pinned route that
    /// no longer exists fails too, because a register nobody prunes is a
    /// register that stops describing the product.
    /// </summary>
    [Fact]
    public void Every_mutating_route_sits_in_exactly_one_of_the_two_pinned_sets()
    {
        IReadOnlyList<MutatingMapping> mappings = TheMappings.Value;
        string[] pinned = [.. CanAnswerConflict.Select(c => c.Route), .. CannotAnswerConflict.Select(f => f.Route)];

        string[] unclassified = mappings
            .Where(mapping => !pinned.Contains(mapping.Identity, StringComparer.Ordinal))
            .Select(Describe)
            .ToArray();

        unclassified.ShouldBeEmpty(
            $"{unclassified.Length} mutating endpoint(s) appear in neither pinned set:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified) + Environment.NewLine
            + "Classify each one here, in "
            + GuardSource
            + ", by answering: can any of the four mechanisms answer 409 on this route — a command "
            + "handler returning an ApiError whose Status is Conflict, EF's affected-row check on an "
            + "UPDATE or DELETE, a unique index written through, or IdempotentRequest finding an earlier "
            + "call with the same key still running? If any can, add the route to CanAnswerConflict with "
            + "the mechanism and declare StatusCodes.Status409Conflict on its chain. If none can, add it "
            + "to CannotAnswerConflict with the reason that clears all four. This guard is a register and "
            + "cannot answer that question for you — but it will not let it go unanswered.");

        string[] ghosts = pinned
            .Where(route => !mappings.Any(mapping => Same(mapping.Identity, route)))
            .ToArray();

        ghosts.ShouldBeEmpty(
            $"{ghosts.Length} pinned route(s) are no longer mapped under src/*/Api: "
            + string.Join(", ", ghosts) + ". A register that outlives the routes it describes stops being "
            + "a record of the product, and its rows would then be checked against nothing. Delete the row "
            + "in the same diff as the endpoint, and adjust the census.");

        // Last, because the two assertions above name the routes and this one
        // only names a number: an author who added an endpoint should read
        // "classify this route" rather than "32 is not 33".
        pinned.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            pinned.Length,
            "a route appears twice across the two pinned sets. The sets are a partition: an endpoint "
            + "either can answer the conflict or it cannot, and it is recorded once.");

        pinned.Length.ShouldBe(
            MutatingMappingCount,
            $"the two pinned sets hold {pinned.Length} routes between them, and the census is pinned at "
            + $"{MutatingMappingCount}. They are the same population read two ways and must agree.");
    }

    // ---- the assumptions the chain read rests on ----------------------------

    /// <summary>
    /// <b>Every conflict declaration sits inside a mapping's own chain.</b> This
    /// guard credits a declaration only where the chain reader can see it, so a
    /// declaration that moved into a shared convention, an endpoint filter or a
    /// metadata helper would make the omission assertion report its endpoint as
    /// declaring nothing. The walk and the sweep count the same token two ways;
    /// neither number is pinned, because both move legitimately with the fix.
    /// </summary>
    [Fact]
    public void Every_conflict_declaration_under_the_api_directories_sits_in_a_mapping_chain()
    {
        DirectoryInfo root = RepositoryRoot();
        int swept = ApiSourceFiles(root).Sum(file => ConflictDeclaration.Count(Source(root, file)));
        int walked = TheMappings.Value.Sum(mapping => ConflictDeclaration.Count(mapping.Chain));

        swept.ShouldBeGreaterThan(
            0,
            "no 409 declaration was found anywhere under src/*/Api. Every route in CanAnswerConflict is "
            + "supposed to carry one, so zero means the sweep is reading nothing — most likely the "
            + "declaration has been spelled some other way than "
            + ".ProducesProblem(StatusCodes.Status409Conflict), or the comment masker is blanking live "
            + "source.");

        walked.ShouldBe(
            swept,
            $"the mapping walk found {walked} 409 declarations inside mutating mapping chains; a flat "
            + $"sweep of src/*/Api found {swept}. The two directions have different causes and this "
            + "message used to give only the first, which is how a real defect was once diagnosed as its "
            + "opposite. Fewer walked than swept: a declaration sits somewhere this reader does not "
            + "look — outside a fluent chain, or on a read mapping; put it in the mapping's own chain, or "
            + "teach the reader the shape. More walked than swept: a chain ran past its own semicolon "
            + "and counted its neighbour's declaration a second time, so some endpoint is passing the "
            + "partition on a 409 that belongs to the mapping below it; look for whatever leaves "
            + "StatementEnd's bracket depth positive — an unbalanced bracket inside a string, a bracket "
            + "written as a char literal, a literal form the masker was never taught.");
    }

    /// <summary>
    /// <b>No route group declares a response.</b> The whole of this guard's
    /// reading rests on it: if a mapping's own chain is its entire response
    /// metadata, then reading the chain is reading the document. Zero
    /// <c>MapGroup</c> chains declare one today, and this keeps it that way
    /// rather than leaving it an assumption recorded in a spec.
    /// </summary>
    [Fact]
    public void No_route_group_declares_a_response_so_a_mappings_own_chain_is_its_whole_metadata()
    {
        DirectoryInfo root = RepositoryRoot();
        List<string> offenders = [];

        foreach (string file in ApiSourceFiles(root))
        {
            string text = Source(root, file);
            foreach (Match group in GroupPrefix.Matches(text))
            {
                int end = StatementEnd(text, group.Index);
                string chain = end < 0 ? text[group.Index..] : text[group.Index..end];
                if (chain.Contains(".Produces", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}:{LineOf(text, group.Index)} MapGroup(\"{group.Groups["prefix"].Value}\")");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"{offenders.Count} route group(s) declare a response on the group chain:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "This guard reads each mapping's own chain and treats it as the whole of that endpoint's "
            + "response metadata. A declaration on the group is inherited by every mapping in it and is "
            + "invisible here, so the partition above would be judged against a partial document. Either "
            + "move the declaration onto the mappings, or replace this reader with one that composes group "
            + "and mapping metadata.");
    }

    /// <summary>
    /// <b>The masker's assumptions hold.</b> Masking comments is what stops a
    /// commented-out declaration being credited, and stepping over string
    /// literals is what stops an unbalanced bracket inside a <c>WithSummary</c>
    /// running one chain into the next and borrowing its 409. Both rest on the
    /// literal forms in these files being simple ones. That is true today and is
    /// asserted here rather than assumed, because a masker meeting a shape it
    /// was never taught mis-reads silently and in the passing direction.
    /// </summary>
    [Fact]
    public void The_api_sources_use_only_the_string_and_comment_forms_this_reader_can_mask()
    {
        DirectoryInfo root = RepositoryRoot();
        List<string> offenders = [];

        foreach (string file in ApiSourceFiles(root))
        {
            string text = Text(root, file);
            foreach ((string form, string why) in UnmaskableLiteralForms)
            {
                int at = text.IndexOf(form, StringComparison.Ordinal);
                if (at >= 0)
                {
                    offenders.Add($"{file}:{LineOf(text, at)} contains {form} — {why}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"{offenders.Count} source file(s) use a literal form this reader's masker does not handle:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "The masker is a single pass that treats an unescaped double quote as opening a literal "
            + "that ends at the next one on the same line, and everything from // to end of line as a "
            + "comment. Each form above breaks that, and it breaks it silently: the mask would blank live "
            + "source or expose a comment, and the partition above would be judged against text that is "
            + "not the source. Teach MaskComments and EndOfStringLiteral the new form, or keep it out of "
            + "src/*/Api.");
    }

    // ---- the reader's two defects, constructed rather than argued -----------

    /// <summary>
    /// <b>A commented-out declaration is not credited.</b> Catching exactly this
    /// omission is the guard's only job, and before the mask it was the one
    /// thing the guard could not see: commenting the line out <em>inside</em> a
    /// chain left every assertion green while the generated document lost the
    /// declaration, and the flat-sweep cross-check could not catch it either,
    /// because the sweep counted the commented line too.
    ///
    /// <para>
    /// Constructed rather than argued. This repository has a recorded habit of
    /// guards whose claim about themselves is false, and the cheap way to find
    /// out is to build the thing the guard says it catches and watch it get
    /// caught. Synthetic source, so no <c>src/</c> file has to be broken to run
    /// it — and permanent, so the proof does not evaporate after one run.
    /// </para>
    /// </summary>
    [Fact]
    public void A_declaration_commented_out_inside_a_chain_is_not_credited()
    {
        const string live =
            "group.MapPost(\"/retire\", Retire)\n"
            + "    .ProducesProblem(StatusCodes.Status409Conflict);\n";
        const string commented =
            "group.MapPost(\"/retire\", Retire)\n"
            + "    // .ProducesProblem(StatusCodes.Status409Conflict);\n";

        ConflictDeclaration.IsMatch(MaskComments(live)).ShouldBeTrue(
            "a live declaration must still be read after masking. If this fails the mask is blanking "
            + "source, and every route would report as declaring nothing.");

        ConflictDeclaration.IsMatch(MaskComments(commented)).ShouldBeFalse(
            "a declaration commented out inside a chain must not be credited. It is absent from the "
            + "generated document, so crediting it is the guard reporting compliance on the exact "
            + "omission it exists to catch.");
    }

    /// <summary>
    /// <b>An unbalanced bracket inside a summary cannot run one chain into the
    /// next.</b> <c>StatementEnd</c> counts brackets to survive a lambda, and an
    /// unbalanced <c>(</c> inside a <c>WithSummary("…")</c> used to leave the
    /// depth permanently positive — so the chain ran past its own <c>;</c> and
    /// inherited the next mapping's declarations. A route could be made to look
    /// compliant by borrowing its neighbour's 409, and the only assertion that
    /// noticed was the walked-versus-swept arithmetic, whose message points the
    /// author at the wrong thing entirely.
    /// </summary>
    [Fact]
    public void An_unbalanced_bracket_inside_a_summary_does_not_run_one_chain_into_the_next()
    {
        const string source =
            "group.MapPost(\"/retire\", Retire)\n"
            + "    .WithSummary(\"Retire a camera (terminal\")\n"
            + "    .ProducesProblem(StatusCodes.Status404NotFound);\n"
            + "group.MapPost(\"/rename\", Rename)\n"
            + "    .ProducesProblem(StatusCodes.Status409Conflict);\n";

        string masked = MaskComments(source);
        int end = StatementEnd(masked, 0);

        end.ShouldBeGreaterThan(
            0,
            "the first chain's terminating semicolon was not found at all, so every mapping in a file "
            + "like this would be reported as unreadable.");

        string chain = masked[..end];

        chain.ShouldNotContain(
            "MapPost(\"/rename\"",
            Case.Sensitive,
            "the first chain swallowed the mapping after it. An unbalanced bracket inside a string is "
            + "the way a route borrows its neighbour's declarations and passes while its own document "
            + "says nothing of the kind.");

        ConflictDeclaration.IsMatch(chain).ShouldBeFalse(
            "the first chain declares only 404, and must not be credited with the 409 that belongs to "
            + "the mapping below it.");
    }

    /// <summary>
    /// <b>A bracket written as a char literal cannot run one chain into the next
    /// either.</b> The same defect as the test above, reached through a form the
    /// masker's assumption census does not check: <c>'('</c> is one bracket with
    /// no partner, so <c>StatementEnd</c>'s depth stayed positive, the chain ran
    /// past its own <c>;</c>, and the mapping was credited with the next
    /// mapping's 409 — found in review on 2026-09-05, after the string fix was
    /// believed to have closed the hole.
    ///
    /// <para>
    /// Two forms, because the escape is its own trap: <c>'\''</c> closes on its
    /// escaped quote and reopens on the real one unless the backslash is
    /// honoured, and then runs to the next quote anywhere in the file. Both are
    /// legal C# that could arrive in an endpoint file tomorrow — <c>' '</c>,
    /// <c>','</c> and <c>'\t'</c> are already there — which is why this is a
    /// step-over in the reader rather than a ban in
    /// <see cref="UnmaskableLiteralForms"/>: a ban detects the shape and still
    /// leaves the count wrong.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("'('", "an unpartnered opening bracket")]
    [InlineData("'\\''", "an escaped quote, which reopens the literal if the backslash is ignored")]
    public void A_char_literal_does_not_run_one_chain_into_the_next(string literal, string why)
    {
        string source =
            "writes.MapPost(\"/{camera:guid}/retire\", Retire)\n"
            + $"    .WithName(NameOf({literal}))\n"
            + "    .ProducesProblem(StatusCodes.Status404NotFound);\n"
            + "writes.MapPost(\"/\", Register)\n"
            + "    .ProducesProblem(StatusCodes.Status409Conflict);\n";

        string masked = MaskComments(source);
        int end = StatementEnd(masked, 0);

        end.ShouldBeGreaterThan(
            0,
            $"the retire chain's terminating semicolon was not found at all past {literal} — {why}. "
            + "Every mapping in a file like this would be reported as unreadable.");

        string chain = masked[..end];

        chain.ShouldNotContain(
            "MapPost(\"/\"",
            Case.Sensitive,
            $"the retire chain swallowed the mapping after it past {literal} — {why}. A char literal is "
            + "the second way a route borrows its neighbour's declarations while its own document says "
            + "nothing of the kind. The walked-versus-swept arithmetic is only a backstop for it: it "
            + "reports a number rather than the route, which is why the shape is constructed here.");

        ConflictDeclaration.IsMatch(chain).ShouldBeFalse(
            "the retire chain declares only 404, and must not be credited with the 409 that belongs to "
            + "the register mapping below it.");
    }

    // ---- reading the surface ------------------------------------------------

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string Describe(MutatingMapping mapping) =>
        $"{mapping.File}:{mapping.Line} {mapping.Identity}";

    private static string MechanismFor(string route) =>
        CanAnswerConflict.First(capable => Same(capable.Route, route)).Mechanism;

    private static string ReasonFor(string route) =>
        CannotAnswerConflict.First(free => Same(free.Route, route)).Reason;

    /// <summary>
    /// Every mutating mapping under <c>src/*/Api</c>, with the prefix of the
    /// nearest preceding <c>MapGroup</c> literal in the same file and the fluent
    /// chain to its terminating semicolon.
    /// </summary>
    private static List<MutatingMapping> Read()
    {
        DirectoryInfo root = RepositoryRoot();
        List<MutatingMapping> mappings = [];

        foreach (string file in ApiSourceFiles(root))
        {
            string text = Source(root, file);
            foreach (Match call in MutatingMappingCall.Matches(text))
            {
                mappings.Add(Mapping(file, text, call));
            }
        }

        return mappings;
    }

    private static MutatingMapping Mapping(string file, string text, Match call)
    {
        int line = LineOf(text, call.Index);
        string verb = call.Groups["verb"].Value.ToUpperInvariant();
        string prefix = PrecedingGroupPrefix(text, call.Index);
        int open = call.Index + call.Length;

        Match route = PlainRouteLiteral.Match(text[open..Math.Min(text.Length, open + 400)]);
        if (!route.Success)
        {
            return new MutatingMapping(
                file,
                line,
                verb,
                prefix,
                string.Empty,
                string.Empty,
                "its first argument is not a plain string literal, so the route cannot be read");
        }

        int end = StatementEnd(text, call.Index);
        if (end < 0)
        {
            return new MutatingMapping(
                file,
                line,
                verb,
                prefix,
                route.Groups["route"].Value,
                string.Empty,
                "its fluent chain has no terminating semicolon, so the chain cannot be read");
        }

        return new MutatingMapping(
            file,
            line,
            verb,
            prefix,
            route.Groups["route"].Value,
            text[call.Index..end],
            null);
    }

    /// <summary>
    /// The literal of the nearest <c>MapGroup</c> before this mapping in the
    /// same file. Lexical by design — see the class doc — and empty when a file
    /// maps outside any group.
    /// </summary>
    private static string PrecedingGroupPrefix(string text, int index)
    {
        string prefix = string.Empty;
        foreach (Match group in GroupPrefix.Matches(text))
        {
            if (group.Index >= index)
            {
                break;
            }

            prefix = group.Groups["prefix"].Value;
        }

        return prefix;
    }

    /// <summary>
    /// The index of the semicolon that ends the statement starting at
    /// <paramref name="from"/>, ignoring semicolons nested inside brackets — a
    /// chain may carry a lambda — and stepping over string literals whole.
    ///
    /// <para>
    /// The literals matter. Without this, an unbalanced <c>(</c> inside a
    /// <c>WithSummary("…")</c> leaves the depth counter permanently positive, so
    /// the chain runs past its own <c>;</c> into the next mapping and inherits
    /// whatever that one declares — a route can be made to look compliant by
    /// borrowing its neighbour's 409. Comments are already blank by the time
    /// this runs, so strings and <b>char literals</b> are what is left to step
    /// over.
    /// </para>
    ///
    /// <para>
    /// <b>Char literals were the same hole reached a second way, and the string
    /// fix did not close it</b> (found in review, 2026-09-05). A <c>'('</c>
    /// written as a char literal — in a <c>Split</c>, a <c>Trim</c>, an
    /// <c>IndexOf</c> — is one bracket with no partner, so the depth stayed
    /// positive exactly as an unbalanced summary had, and the chain swallowed the
    /// mapping below it along with its 409. Banning the form would only have
    /// detected it; stepping over it is what makes the depth count right. The
    /// escape is honoured because <c>'\''</c> otherwise closes on its own escaped
    /// quote and reopens on the real one, running away to the next quote in the
    /// file.
    /// </para>
    /// </summary>
    private static int StatementEnd(string text, int from)
    {
        int depth = 0;
        int i = from;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                i = EndOfStringLiteral(text, i);
            }
            else if (c == '\'')
            {
                i = EndOfCharLiteral(text, i);
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ';' && depth <= 0)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The index of the quote closing the literal opened at
    /// <paramref name="open"/>. A newline ends the search: these files hold no
    /// verbatim or raw literals — asserted by
    /// <see cref="The_api_sources_use_only_the_string_and_comment_forms_this_reader_can_mask"/>
    /// — so a quote with no partner on its own line is a reader error, and
    /// stopping at the line end contains it instead of swallowing the rest of
    /// the file.
    /// </summary>
    private static int EndOfStringLiteral(string text, int open)
    {
        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] is '"' or '\n')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// The index of the quote closing the char literal opened at
    /// <paramref name="open"/>. A backslash consumes the character after it, so
    /// <c>'\''</c> and <c>'\\'</c> close where they really close rather than on
    /// their own escape; a newline ends the search for the same reason it ends
    /// <see cref="EndOfStringLiteral"/>'s.
    /// </summary>
    private static int EndOfCharLiteral(string text, int open)
    {
        int i = open + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] is '\'' or '\n')
            {
                return i;
            }

            i++;
        }

        return text.Length - 1;
    }

    /// <summary>
    /// The source with every comment blanked to spaces, length and line breaks
    /// preserved so offsets and line numbers still refer to the real file.
    ///
    /// <para>
    /// This is the fix for a hole the earlier reader had: commenting out the
    /// <c>.ProducesProblem(StatusCodes.Status409Conflict)</c> <em>inside</em> a
    /// chain left all its assertions green while the generated document lost the
    /// declaration — and the flat-sweep cross-check could not catch it, because
    /// the sweep counted the commented line too. Catching exactly that omission
    /// is the guard's only job.
    /// </para>
    ///
    /// <para>
    /// One pass, not two: 42 lines under <c>src/*/Api</c> carry a quote inside a
    /// comment, so a comment pass that did not know about strings and a string
    /// pass that did not know about comments would each corrupt what the other
    /// relies on.
    /// </para>
    /// </summary>
    private static string MaskComments(string text)
    {
        char[] masked = text.ToCharArray();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                i = EndOfStringLiteral(text, i) + 1;
            }
            else if (Starts(text, i, "//"))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    masked[i] = ' ';
                    i++;
                }
            }
            else if (Starts(text, i, "/*"))
            {
                i = BlankBlockComment(text, masked, i) + 1;
            }
            else
            {
                i++;
            }
        }

        return new string(masked);
    }

    /// <summary>
    /// Blanks a <c>/* … *&#47;</c> comment and returns the index of its last
    /// character, leaving newlines in place so line numbers survive.
    /// </summary>
    private static int BlankBlockComment(string text, char[] masked, int start)
    {
        int close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
        int end = close < 0 ? text.Length : close + 2;
        for (int i = start; i < end; i++)
        {
            if (text[i] != '\n')
            {
                masked[i] = ' ';
            }
        }

        return end - 1;
    }

    private static bool Starts(string text, int index, string token) =>
        index + token.Length <= text.Length
        && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// The file's text with <c>\r</c> stripped, so a pattern anchored to a line
    /// end behaves the same on both platforms. Raw — only the masker assumption
    /// test reads this; everything else reads <see cref="Source"/>.
    /// </summary>
    private static string Text(DirectoryInfo root, string file) =>
        File.ReadAllText(Path.Combine(root.FullName, file)).Replace("\r", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// What every reader in this file matches against: the source with comments
    /// blanked. Memoised because nine tests walk the same forty-odd files.
    /// </summary>
    private static string Source(DirectoryInfo root, string file) =>
        MaskedSources.GetOrAdd(file, key => MaskComments(Text(root, key)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> MaskedSources =
        new(StringComparer.Ordinal);

    private static List<string> ApiSourceFiles(DirectoryInfo root)
    {
        string src = Path.Combine(root.FullName, "src");
        return Directory.EnumerateDirectories(src)
            .Select(context => Path.Combine(context, "Api"))
            .Where(Directory.Exists)
            .SelectMany(api => Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories))
            .Select(file => Relative(root, file))
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal)
                && !file.Contains("/bin/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reported with <c>/</c> throughout. <see cref="Path.GetRelativePath"/>
    /// returns the platform separator, so a backslash in an expected string is
    /// green on Windows and red on Linux CI — this repository has been bitten by
    /// exactly that.
    /// </summary>
    private static string Relative(DirectoryInfo root, string file) =>
        Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/');

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "SmartSentinelEye.slnx")))
        {
            candidate = candidate.Parent;
        }

        return candidate
            ?? throw new InvalidOperationException(
                $"could not locate the repository root above {AppContext.BaseDirectory}");
    }

    private const string GuardSource = "tests/Architecture.Tests/ConcurrencyConflictDeclarationTests.cs";

    /// <summary>
    /// A route that can answer <c>409</c>, and by which of the four mechanisms.
    /// The mechanism is carried into the omission failure, so someone who
    /// deletes a declaration is told what the route actually does rather than
    /// merely that a line is missing.
    /// </summary>
    private sealed record ConflictCapableRoute(string Route, string Mechanism);

    /// <summary>
    /// A route that cannot answer <c>409</c>, and why — a reason that has to
    /// clear all four mechanisms, not just the lost update.
    /// </summary>
    private sealed record ConflictFreeRoute(string Route, string Reason);

    private sealed record MutatingMapping(
        string File,
        int Line,
        string Verb,
        string Prefix,
        string Route,
        string Chain,
        string? Failure)
    {
        /// <summary>
        /// Context-qualified, because the prefix alone is not unique: two
        /// contexts map <c>/webhook-integrations</c> today. See the class doc.
        /// </summary>
        public string Identity => $"{Context} {Verb} {Prefix}{Route}";

        /// <summary>The bounded context, from <c>src/&lt;Context&gt;/Api</c>.</summary>
        public string Context => File.Split('/') is [_, string context, ..] ? context : File;
    }
}

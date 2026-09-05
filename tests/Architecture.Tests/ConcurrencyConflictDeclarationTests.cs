using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the contract half of ADR-0113's Layer 2: <b>a mutating endpoint whose
/// write path makes EF update or delete a row that already exists declares the
/// <c>409</c> the shared handler answers, and the ones whose write path cannot
/// do not</b> (issue #2096, spec 075).
///
/// <para>
/// <c>ConcurrencyConflictExceptionHandler</c> turns EF Core's
/// <c>DbUpdateConcurrencyException</c> into <c>409 AGGREGATE_VERSION_STALE</c>.
/// It is registered once, in <c>AddBearerAuthentication</c>, which all nine Api
/// <c>Program.cs</c> files call, and it answers unconditionally — so the limit
/// on its reach is EF, not registration. The limit is this: EF raises that
/// exception from its affected-row check on an <c>UPDATE</c> or a
/// <c>DELETE</c>. An insert has no prior row to disagree with, and an endpoint
/// that never reaches a <c>DbContext</c> has none either.
/// </para>
///
/// <para>
/// <b>This is a register, and the two sets below are typed in, not derived.</b>
/// That is the first thing to know about it, because every other guard in this
/// directory derives its claim and this one cannot. Whether a route can produce
/// the conflict is settled three or four hops away — endpoint, command handler,
/// repository, EF — across the Application boundary, and the discriminating
/// fact (<c>events.Add(@event)</c> versus <c>camera.Retire(…)</c> before the
/// same <c>SaveAsync</c>) is not visible at the Api layer at all. No scan of
/// <c>src/*/Api</c> can decide it, so a human decided it on 2026-09-05 and wrote
/// the answer here.
/// </para>
///
/// <para>
/// <b>The question a new endpoint's author answers</b> to place it in one set or
/// the other: <em>does this endpoint's command handler mutate or delete an
/// aggregate that already exists?</em> If yes, its route joins
/// <see cref="ConflictProducing"/> and its chain declares
/// <c>StatusCodes.Status409Conflict</c>. If no — it only inserts, or it touches
/// no database — its route joins <see cref="ConflictFree"/> with the reason, and
/// its chain must not declare one. Declaring 409 on an endpoint that cannot
/// produce it is the same defect as omitting it from one that can, pointing the
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
/// <see cref="ConflictProducing"/> declares the conflict in its own fluent
/// chain; and none of <see cref="ConflictFree"/> does.
/// </para>
///
/// <para>
/// <b>Route identity is lexical.</b> A route is the verb, the prefix of the
/// nearest preceding <c>MapGroup</c> literal in the same file, and the mapping's
/// own route literal, concatenated exactly as written — so a mapping on
/// <c>"/"</c> reads with a trailing slash (<c>POST /cameras/</c>), and the four
/// files that map two groups bind by lexical position rather than by the
/// variable the mapping is written on. It is the one place a reader could bind
/// the wrong prefix, which is why it is stated rather than left to be inferred.
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
/// <b>It cannot tell why a 409 is declared.</b> OpenAPI has one 409 slot per
/// operation. On twenty-five of these routes the declaration was already there
/// for a name collision, a stale version or a terminal state, and this guard is
/// green on them whether or not anyone ever considered the lost update. A green
/// run is not evidence that the rule was applied — only that nobody removed a
/// line or added a mutating endpoint unclassified.
/// </item>
/// <item>
/// <b>It does not prove reachability.</b> That
/// <c>DbUpdateConcurrencyException</c> can actually be raised on a route in
/// <see cref="ConflictProducing"/> is argued in spec 075 from the handler bodies
/// and the EF configurations. No test asserts it: provoking a true database race
/// needs two overlapping transactions against real Postgres, which is Docker,
/// CI-only and a race to arrange. Not attempted, and not claimed.
/// </item>
/// <item>
/// <b>It reads the fluent chain, not the generated document.</b> Safe today
/// because no <c>MapGroup</c> chain in these directories declares a response —
/// asserted below, so it stops being an assumption. If one ever does, the guard
/// under-reads. It also reads the source text unmasked: a
/// <c>.ProducesProblem(StatusCodes.Status409Conflict)</c> commented out inside a
/// chain would still be credited. There are none today — every one of the
/// twenty-five occurrences of the token in these directories is a live
/// declaration — and the cross-check below cannot see that case, because the
/// sweep would count it too.
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
    /// The twenty-eight routes whose command handler loads an aggregate that
    /// already exists, mutates or deletes it, and saves — so EF's affected-row
    /// check can disagree and the shared handler can answer <c>409</c>. Each
    /// must declare <c>Status409Conflict</c> in its own chain.
    ///
    /// <para>
    /// Twenty-five of them declared it before this guard existed. Three did not,
    /// and are the defect spec 075 fixes: <c>POST /cameras/{camera:guid}/retire</c>,
    /// <c>DELETE /devices/{clientId}</c> and <c>DELETE /kiosks/{clientId}</c>.
    /// They are written here from the classification, not from the source, which
    /// is why this set is red before that fix and green after it.
    /// </para>
    /// </summary>
    private static readonly string[] ConflictProducing =
    [
        "POST /rules/",
        "POST /rules/{name}/publish",
        "POST /rules/{name}/archive",
        "POST /cameras/",
        "POST /cameras/{camera:guid}/retire",
        "PATCH /cameras/{camera:guid}",
        "POST /webhook-integrations/",
        "DELETE /webhook-integrations/{name}",
        "POST /webhook-integrations/{name}/rotate",
        "POST /devices/register",
        "DELETE /devices/{clientId}",
        "POST /kiosks/enroll",
        "DELETE /kiosks/{clientId}",
        "POST /layouts/",
        "POST /layouts/{layoutIdentifier:guid}/draft",
        "POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/publish",
        "POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/archive",
        "POST /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/revert",
        "PATCH /layouts/{layoutIdentifier:guid}/revisions/{revisionNumber:int}",
        "POST /overlays/",
        "POST /overlays/{overlayIdentifier:guid}/draft",
        "POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/publish",
        "POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/archive",
        "POST /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/revert",
        "PATCH /overlays/{overlayIdentifier:guid}/revisions/{revisionNumber:int}",
        "POST /system-variables/",
        "PUT /system-variables/{name}/value",
        "POST /system-variables/{name}/archive",
    ];

    /// <summary>
    /// The five routes whose write path performs no EF update or delete, each
    /// with the reason. Adding <c>409</c> to any of them would be a new false
    /// claim of exactly the kind #2096 was filed about — the same defect, in the
    /// opposite direction.
    /// </summary>
    private static readonly ConflictFreeRoute[] ConflictFree =
    [
        new(
            "POST /rules/{name}/dry-run",
            "a POST because it carries a sample-event body, but a read: it is mapped on the read group "
            + "and nothing is persisted"),
        new(
            "POST /events/manual",
            "IngestEventCommandHandler calls events.Add(@event) and then SaveAsync — an insert, and an "
            + "insert has no prior row for EF's affected-row check to disagree with"),
        new(
            "POST /events/webhook/{integrationName}",
            "it reads the integration to authenticate the delivery and never writes it back, then takes "
            + "the same insert-only path as POST /events/manual"),
        new(
            "POST /streams/authorize",
            "AuthorizeWhepCommandHandler validates a forwarded token against a read-only stream lookup "
            + "and calls no SaveAsync"),
        new(
            "POST /streams/kiosk-latency",
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
            + "diff: does its command handler mutate or delete an aggregate that already exists? Add its "
            + "route to ConflictProducing and declare the 409, or to ConflictFree with the reason it cannot "
            + "produce one. Routes found: "
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
            .Sum(file => MutatingMappingCall.Count(Text(root, file)));

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
    /// <b>FR-003 — the omission direction.</b> Every route whose write path can
    /// lose the race declares the status the shared handler answers when it
    /// does. This is the assertion spec 075 expects to be red on three routes
    /// before the fix.
    /// </summary>
    [Fact]
    public void Every_endpoint_whose_write_path_can_lose_the_race_declares_the_conflict()
    {
        string[] undeclared = TheMappings.Value
            .Where(mapping => ConflictProducing.Contains(mapping.Identity, StringComparer.Ordinal))
            .Where(mapping => !ConflictDeclaration.IsMatch(mapping.Chain))
            .OrderBy(mapping => mapping.File, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Line)
            .Select(Describe)
            .ToArray();

        undeclared.ShouldBeEmpty(
            $"{undeclared.Length} mutating endpoint(s) do not declare the conflict their write path can "
            + "produce:" + Environment.NewLine
            + string.Join(Environment.NewLine, undeclared) + Environment.NewLine
            + "Each of these loads an aggregate that already exists, mutates or deletes it and saves, so "
            + "EF's affected-row check can disagree and ConcurrencyConflictExceptionHandler answers 409 "
            + "AGGREGATE_VERSION_STALE (ADR-0113 Layer 2, ADR-0119). The generated OpenAPI currently "
            + "asserts that status cannot happen on this route, so a client generated from it has no "
            + "branch for the lost update. Add .ProducesProblem(StatusCodes.Status409Conflict) to the "
            + "mapping's own chain.");
    }

    /// <summary>
    /// <b>FR-003, FR-005 — the mirror.</b> A declared conflict that no write
    /// path can produce is the same defect as an undeclared one, pointing the
    /// other way, and it arrives from a different cause: someone reading #2096
    /// as filed and adding the status to all thirty-three. The message shares no
    /// sentence with the one above, on purpose.
    /// </summary>
    [Fact]
    public void No_endpoint_whose_write_path_cannot_lose_the_race_declares_the_conflict()
    {
        string[] surplus = TheMappings.Value
            .Where(mapping => ConflictFree.Any(free => string.Equals(free.Route, mapping.Identity, StringComparison.Ordinal)))
            .Where(mapping => ConflictDeclaration.IsMatch(mapping.Chain))
            .OrderBy(mapping => mapping.File, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Line)
            .Select(mapping => $"{Describe(mapping)} — {ReasonFor(mapping.Identity)}")
            .ToArray();

        surplus.ShouldBeEmpty(
            $"{surplus.Length} endpoint(s) advertise a conflict nothing in their write path can raise:"
            + Environment.NewLine + string.Join(Environment.NewLine, surplus) + Environment.NewLine
            + "EF raises DbUpdateConcurrencyException from its affected-row check on an UPDATE or a "
            + "DELETE. An insert has nothing to compare against and an endpoint that reaches no DbContext "
            + "has nothing at all, so this line publishes an answer the route will never give. Remove it, "
            + "or — if the handler has genuinely started mutating an existing aggregate — move the route "
            + "to ConflictProducing in the same diff as the change that made it true.");
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
        string[] pinned = [.. ConflictProducing, .. ConflictFree.Select(free => free.Route)];

        string[] unclassified = mappings
            .Where(mapping => !pinned.Contains(mapping.Identity, StringComparer.Ordinal))
            .Select(Describe)
            .ToArray();

        unclassified.ShouldBeEmpty(
            $"{unclassified.Length} mutating endpoint(s) appear in neither pinned set:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified) + Environment.NewLine
            + "Classify each one here, in "
            + GuardSource
            + ", by answering: does this endpoint's command handler mutate or delete an aggregate that "
            + "already exists? If it does, add the route to ConflictProducing and declare "
            + "StatusCodes.Status409Conflict on its chain. If it only inserts, or reaches no DbContext at "
            + "all, add it to ConflictFree with the reason. This guard is a register and cannot answer "
            + "that question for you — but it will not let it go unanswered.");

        string[] ghosts = pinned
            .Where(route => !mappings.Any(mapping => string.Equals(mapping.Identity, route, StringComparison.Ordinal)))
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
            + "either can produce the conflict or it cannot, and it is recorded once.");

        pinned.Length.ShouldBe(
            MutatingMappingCount,
            $"the two pinned sets hold {pinned.Length} routes between them, and the census is pinned at "
            + $"{MutatingMappingCount}. They are the same population read two ways and must agree.");
    }

    // ---- the two assumptions the chain read rests on ------------------------

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
        int swept = ApiSourceFiles(root).Sum(file => ConflictDeclaration.Count(Text(root, file)));
        int walked = TheMappings.Value.Sum(mapping => ConflictDeclaration.Count(mapping.Chain));

        swept.ShouldBeGreaterThan(
            0,
            "no 409 declaration was found anywhere under src/*/Api. Twenty-five chains declared one when "
            + "this guard was written, so zero means the sweep is reading nothing — most likely the "
            + "declaration has been spelled some other way than "
            + ".ProducesProblem(StatusCodes.Status409Conflict).");

        walked.ShouldBe(
            swept,
            $"the mapping walk found {walked} 409 declarations inside mutating mapping chains; a flat "
            + $"sweep of src/*/Api found {swept}. The difference sits somewhere this reader does not "
            + "look — outside a fluent chain, or on a read mapping. Put it in the mapping's own chain, or "
            + "teach the reader the shape.");
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
            string text = Text(root, file);
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

    // ---- reading the surface ------------------------------------------------

    private static string Describe(MutatingMapping mapping) =>
        $"{mapping.File}:{mapping.Line} {mapping.Identity}";

    private static string ReasonFor(string route) =>
        ConflictFree.First(free => string.Equals(free.Route, route, StringComparison.Ordinal)).Reason;

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
            string text = Text(root, file);
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
    /// chain may carry a lambda.
    /// </summary>
    private static int StatementEnd(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
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
        }

        return -1;
    }

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
    /// end behaves the same on both platforms.
    /// </summary>
    private static string Text(DirectoryInfo root, string file) =>
        File.ReadAllText(Path.Combine(root.FullName, file)).Replace("\r", string.Empty, StringComparison.Ordinal);

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
    /// A route that cannot produce the conflict, and why. The reason is carried
    /// into the mirror failure, so someone who adds a 409 to one of these is
    /// told what the write path actually does rather than merely that the line
    /// is unwelcome.
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
        public string Identity => $"{Verb} {Prefix}{Route}";

        /// <summary>The bounded context, from <c>src/&lt;Context&gt;/Api</c>.</summary>
        public string Context => File.Split('/') is [_, string context, ..] ? context : File;
    }
}

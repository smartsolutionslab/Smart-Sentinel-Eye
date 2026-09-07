using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the half of ADR-0113 that lives in the contract rather than in the
/// handler: <b>an endpoint whose handler reads <c>If-Match</c> declares both
/// answers that handler can give to the header itself</b> — the <c>428</c> when
/// the header is absent (issue #2088, spec 072) and the <c>400</c> when it is
/// present but malformed (issue #2101).
///
/// <para>
/// <c>ConcurrencyHeaders.Missing()</c> and <c>MissingUpsert()</c> answer
/// <c>428 IF_MATCH_REQUIRED</c>; <c>ConcurrencyHeaders.Malformed()</c> answers
/// <c>400 IF_MATCH_MALFORMED</c> for a wildcard, a weak tag, a multi-valued or
/// an unparseable header. All three live in one place in ServiceDefaults, and
/// every one of the seventeen endpoints that reads the header returns their
/// result unchanged — verified call site by call site, not assumed. So both
/// statuses are reachable on all seventeen, and neither is a matter of taste.
/// </para>
///
/// <para>
/// Nine of the seventeen did not declare the 428, across three contexts, from
/// the day ADR-0113 landed until this guard. Five of the seventeen —
/// OverlayDesigner's entire write surface — did not declare the 400, and were
/// still missing it after spec 072 added the 428 beside it, because the
/// instruction was to place the new line next to the 400 that in that one file
/// was not there. In both cases the generated OpenAPI asserted that a status
/// those routes routinely return cannot happen, so a client generated from it
/// has no branch for it. Nothing noticed either time, because the only thing
/// that could have noticed was a reviewer remembering a convention.
/// </para>
///
/// <para>
/// <b>Why the 400 is mechanically checkable and the stale 409 is not.</b> The
/// malformed answer comes off the <em>same call site</em> as the missing one:
/// the handler that reads the header is the handler that can answer either, so
/// one body scan settles both and the reachability argument is identical. The
/// stale answer is produced four hops away, in a command handler across the
/// Application boundary, at a status ADR-0119 leaves legally variable — which is
/// why it stays out, and stays named as a residual below.
/// </para>
///
/// <para>
/// <b>What it asserts.</b> Every route-handler mapping under <c>src/*/Api</c>
/// resolves to exactly one handler body; a mapping whose handler body calls
/// <c>ConcurrencyHeaders.TryReadExpectedVersion</c> or
/// <c>TryReadUpsertPrecondition</c> declares <em>both</em>
/// <c>Status428PreconditionRequired</c> and <c>Status400BadRequest</c> in its
/// own fluent chain; and — for the 428 only — the mirror, a chain declaring 428
/// whose handler reads neither helper, fails with a different message. The
/// mirror is zero today and exists so it stays zero: a declared precondition no
/// handler enforces tells a caller to send a header that will be ignored.
/// </para>
///
/// <para>
/// <b>There is deliberately no 400 mirror.</b> 428 is answered from exactly one
/// place in the product, so declaring it without reading the header is always
/// wrong. 400 is not: fifty-five chains declare one, at most one declaration
/// each, and only seventeen of those also read <c>If-Match</c> — a malformed
/// body, an unparseable route value and a rejected filter all answer 400 too. A
/// mirror there would fail on the other thirty-eight, every one of them correct,
/// so the rule runs one way only.
/// </para>
///
/// <para>
/// <b>Every denominator is checked against an independent sweep.</b> The
/// mapping walk reports how many helper calls, how many 428 declarations and how
/// many 400 declarations it found; a flat file sweep over the same directories
/// reports the same three numbers, and each pair must agree. That is what keeps
/// the lexical body scan honest — hoist a helper call out of a mapped handler
/// into a private method it calls and the walk finds one fewer than the sweep,
/// so the build fails instead of the reader quietly answering "requires
/// nothing". Without it the guard's sharpest edge would be silent, which is the
/// property <c>PaginatedConsumerTests</c> established and this file borrows.
/// </para>
///
/// <para>
/// The 400 sweep matches the declaration shape —
/// <c>.ProducesProblem(...)</c> or <c>.ProducesValidationProblem(...)</c> — and
/// not the bare status name, which is the one place the two rules cannot share
/// machinery. <c>Status428PreconditionRequired</c> appears in these directories
/// only in chains, because nothing here writes a 428 by hand;
/// <c>Status400BadRequest</c> appears fifty-seven further times inside handler
/// bodies, in the <c>Results.Problem(statusCode: ...)</c> calls that return one.
/// Sweeping the bare name would compare 112 against 55 and fail on the first
/// run.
/// </para>
///
/// <para>
/// <b>The corpus is pinned, because the denominator alone does not pin it.</b>
/// Seventeen precondition endpoints across seven files, and twelve endpoint
/// files each asserted non-empty individually. Comparing two numbers both
/// derived from the same glob stays green when a file leaves that glob: spec
/// 070 watched exactly that report <c>Failed: 0, Passed: 59</c> with eight
/// endpoints no longer checked. Adding an endpoint therefore edits a number
/// here in the same diff, which is the point rather than the cost.
/// </para>
///
/// <para>
/// <b>There is no way to excuse an endpoint, and a self-scan keeps it that
/// way.</b> Unlike spec 070's register, nothing here has a claim to one: an
/// endpoint that can answer 428 can declare it in one line, and the only two
/// roads to green are declaring the status or not requiring the header.
/// </para>
///
/// <para>
/// <b>What it provably cannot do, stated up front.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// It reads source, not a running application. A handler that delegated the
/// header read to a shared helper in another type would read as "does not
/// require If-Match" — the cross-check above turns that from a wrong answer
/// into a failure, but it cannot repair the reading.
/// </item>
/// <item>
/// It resolves the handler by method-group name <em>within the declaring
/// class</em>, across every file declaring that class in the same Api project —
/// partial endpoint classes are the normal case, not the exception. A lambda, a
/// name qualified by another type, a name that resolves to no method or to two
/// is <em>unreadable</em> and fails; nothing resolves to a pass by default.
/// </item>
/// <item>
/// It checks that a status is declared, not that it is reachable at run time. A
/// future filter short-circuiting ahead of the handler would leave the chain
/// judged correct. It reads presence and not order, and blesses neither: the
/// fab check deliberately precedes the header read on
/// <c>PATCH /cameras/{camera}</c>, because answering 428 for another fab's
/// camera would confirm it exists.
/// </item>
/// <item>
/// <b>It reads the status, not the schema.</b> A chain that declared its 400
/// only as <c>.ProducesValidationProblem(...)</c> would pass, though
/// <c>Malformed()</c> returns a plain <c>ProblemDetails</c> rather than a
/// validation one. No precondition endpoint is in that shape today — all twelve
/// that declare the 400 use <c>.ProducesProblem(...)</c> — so tightening this
/// would police a case that does not exist, and the looser reading is the one
/// that matches the defect being fixed: the OpenAPI claiming 400 cannot happen.
/// </item>
/// <item>
/// <b>It says nothing about the stale half of the pair.</b> ADR-0119 leaves
/// <c>409</c> and <c>412</c> both legal and keys a lost update off the
/// <c>_STALE</c> code suffix instead, so the status varies legally by context;
/// linking a mapping to the status its command handler returns is a four-hop
/// inference across the Application boundary. Spec 072 fixes three missing
/// <c>409</c>s by hand and leaves them unguarded, and issue #2101 leaves that
/// exactly as it found it. That is the honest residual: if they regress, nothing
/// here catches it.
/// </item>
/// <item>
/// The route it reports is the literal written at the mapping, not the path a
/// caller uses: the group prefix lives on the <c>MapGroup</c> call and is not
/// resolved here, so <c>POST /rules/{name}/publish</c> is reported as
/// <c>POST /{name}/publish</c>. The file, line and handler name make the row
/// unambiguous; the route alone does not.
/// </item>
/// <item>
/// It is rooted at <c>src/*/Api</c>, and its masker understands line comments,
/// block comments, character literals, and regular and verbatim string
/// literals. A raw string literal (three quotes), of which there are none in
/// these directories, would be masked wrongly.
/// </item>
/// </list>
/// </summary>
public class PreconditionDeclarationTests
{
    private const string GuardSource = "tests/Architecture.Tests/PreconditionDeclarationTests.cs";
    private const string EndpointFileSuffix = "Endpoints.cs";
    private const string DeclarationToken = "Status428PreconditionRequired";

    /// <summary>
    /// Seventeen mappings whose handler reads <c>If-Match</c>, spread over seven
    /// files. Pinned, not merely compared — see the class doc.
    /// </summary>
    private const int PreconditionEndpointCount = 17;

    private const int PreconditionFileCount = 7;

    /// <summary>
    /// Every file under <c>src/*/Api</c> whose name ends <c>Endpoints.cs</c>.
    /// Each is asserted to contribute at least one mapping, individually: a
    /// single total lets one file stop being read while the others carry it.
    /// </summary>
    private const int EndpointFileCount = 12;

    private static readonly Regex MappingCall = new(
        @"\.Map(?<verb>Get|Post|Put|Patch|Delete)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex MethodGroupName = new(
        @"^[A-Za-z_]\w*$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The two readers of <c>If-Match</c> in the product. Nothing else reads the
    /// header by hand; if something ever does, the sweep below does not see it,
    /// and that assumption is recorded in the spec rather than hidden here.
    /// </summary>
    private static readonly Regex HelperCall = new(
        @"ConcurrencyHeaders\.TryRead(?<helper>ExpectedVersion|UpsertPrecondition)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A declaration of <c>400</c> on a fluent chain, in either spelling. Unlike
    /// the 428 this is matched by shape rather than by the bare status name:
    /// <c>StatusCodes.Status400BadRequest</c> also appears in the handler bodies
    /// that return one, so the name alone counts 101 things of which 57 are not
    /// declarations at all.
    /// </summary>
    private static readonly Regex MalformedDeclaration = new(
        @"\.Produces(?:Validation)?Problem\(\s*StatusCodes\.Status400BadRequest\s*\)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The content of a string literal is data, not mechanism. The self-scan
    /// reads code, not the prose the code prints.
    /// </summary>
    private static readonly Regex StringLiteral = new(
        @"""(?:[^""\\\r\n]|\\.)*""",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Lazy<Surface> TheSurface = new(Read);

    /// <summary>
    /// The endpoint files, found by glob and never named, one theory case each.
    /// </summary>
    public static TheoryData<string> EndpointFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in TheSurface.Value.EndpointSources)
        {
            data.Add(file);
        }

        return data;
    }

    // ---- A1, A2: nothing resolves to a pass by default ---------------------

    /// <summary>
    /// <b>A1 / FR-009 — every mapping resolves to exactly one handler body.</b>
    /// A guard that quietly skips what it cannot parse is the guard that was not
    /// there, so an unreadable argument, no match and two matches are each a
    /// failure naming the mapping and the shape.
    /// </summary>
    [Fact]
    public void Every_route_mapping_resolves_to_one_handler_body_the_guard_can_read()
    {
        IReadOnlyList<ResolvedMapping> mappings = TheSurface.Value.Routes;

        mappings.Count.ShouldBeGreaterThan(
            0,
            "no route mappings were found under src/*/Api. That is the reader failing, not the product: "
            + "every later assertion in this file would then pass over an empty set.");

        string[] unresolved = mappings
            .Where(m => m.Failure is not null)
            .Select(m => $"{Describe(m.Mapping)}: {m.Failure}")
            .ToArray();

        unresolved.ShouldBeEmpty(
            "these mappings do not resolve to exactly one handler body:"
            + Environment.NewLine + string.Join(Environment.NewLine, unresolved) + Environment.NewLine
            + "This guard reads the handler's body to decide whether the endpoint requires If-Match, so a "
            + "mapping it cannot resolve is a mapping it cannot judge — and it fails rather than passing. "
            + "The shape it reads is a bare method-group name as the second argument of the Map call, "
            + "declared once in the mapping's own class, in any file of the same Api project that declares "
            + "that class. An inline lambda, a name qualified by another type, or a name declared twice in "
            + "the class is not that shape.");
    }

    /// <summary>
    /// <b>A2 / FR-002 — a partial endpoint class is indexed across every file
    /// that declares it.</b> Three of the twelve endpoint classes map in one
    /// file and handle in another; resolving within a single file would report
    /// every one of their mappings as unresolvable, and resolving by bare name
    /// across the project would bind Identity's three <c>List</c> handlers to
    /// each other.
    /// </summary>
    [Theory]
    [InlineData("LayoutEndpoints", 3)]
    [InlineData("OverlayEndpoints", 3)]
    [InlineData("EventsEndpoints", 3)]
    public void A_partial_endpoint_class_is_indexed_across_every_file_that_declares_it(
        string className,
        int expected)
    {
        string[] files = TheSurface.Value.Classes
            .Where(c => string.Equals(c.Name, className, StringComparison.Ordinal))
            .Select(c => c.File)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        files.Length.ShouldBe(
            expected,
            $"'{className}' is declared in {files.Length} file(s): {string.Join(", ", files)}. "
            + "A partial endpoint class split across files is the normal case here, and an index that "
            + "sees only one of them resolves the other files' handlers to nothing — which would make "
            + "every assertion below it green and meaningless.");
    }

    /// <summary>
    /// <b>The control that A1 discriminates.</b> LayoutComposition maps in
    /// <c>LayoutEndpoints.cs</c> and handles in
    /// <c>LayoutEndpoints.Commands.cs</c>, and its five write endpoints are the
    /// same shape as the nine this spec fixes and are already correct. If
    /// cross-file resolution failed, they would be reported as offenders and the
    /// guard's central claim would be unproven.
    /// </summary>
    [Fact]
    public void The_layout_write_mappings_bind_to_the_handlers_in_their_partial_file()
    {
        ResolvedMapping[] bound = TheSurface.Value.Routes
            .Where(m => m.Mapping.File.EndsWith("LayoutComposition/Api/LayoutEndpoints.cs", StringComparison.Ordinal))
            .Where(RequiresPrecondition)
            .ToArray();

        bound.Length.ShouldBe(
            5,
            "LayoutComposition's five If-Match write endpoints are mapped in LayoutEndpoints.cs and "
            + "handled in LayoutEndpoints.Commands.cs. Finding a different number means the mapping is not "
            + "being bound across the partial's files, and the omission assertion below is then measuring "
            + "the reader rather than the product.");

        string[] elsewhere = bound
            .Select(m => m.Handler!.File)
            .Where(file => !file.EndsWith("LayoutEndpoints.Commands.cs", StringComparison.Ordinal))
            .ToArray();

        elsewhere.ShouldBeEmpty(
            "all five resolve into LayoutEndpoints.Commands.cs; these did not: "
            + string.Join(", ", elsewhere));
    }

    // ---- A3: the claim -----------------------------------------------------

    /// <summary>
    /// <b>A3 / FR-004 — an endpoint that requires <c>If-Match</c> declares the
    /// 428 it answers.</b>
    /// </summary>
    [Fact]
    public void Every_endpoint_that_requires_If_Match_declares_the_428_it_answers()
    {
        ResolvedMapping[] requiring = TheSurface.Value.Routes.Where(RequiresPrecondition).ToArray();

        requiring.Length.ShouldBeGreaterThan(
            0,
            "no endpoint under src/*/Api was read as requiring If-Match, which cannot be true while "
            + "ConcurrencyHeaders exists. The body reader has stopped finding the helper calls.");

        string[] offenders = requiring
            .Where(m => !Declares428(m.Mapping))
            .Select(m => $"{Describe(m.Mapping)} -> {m.Mapping.ContainingClass}.{m.Mapping.HandlerArgument} "
                + $"[{string.Join(", ", m.Calls.Select(c => $"{c.File}:{c.Line} ConcurrencyHeaders.TryRead{c.Helper}"))}]")
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{offenders.Length} endpoint(s) require If-Match and do not declare 428:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "ConcurrencyHeaders.Missing()/MissingUpsert() answers 428 IF_MATCH_REQUIRED, and each of "
            + "these handlers returns that result unchanged when the header is absent — the commonest way "
            + "the endpoint is called wrongly. Without the declaration the generated OpenAPI asserts that a "
            + "status the endpoint routinely returns cannot happen, so a client generated from it has no "
            + "branch for it. Add '.ProducesProblem(StatusCodes.Status428PreconditionRequired)' to the "
            + "chain above, beside the 400 the malformed-header branch already declares (ADR-0113 Layer 1: "
            + "428 for a missing precondition, 409 or 412 for a failed one).");
    }

    /// <summary>
    /// <b>B1 / issue #2101 — an endpoint that reads <c>If-Match</c> declares the
    /// 400 it answers for a malformed one.</b>
    ///
    /// <para>
    /// Same corpus, same handler resolution, same reachability argument as A3:
    /// <c>ConcurrencyHeaders.Malformed()</c> is returned from the very call site
    /// A3 already finds. Twelve of the seventeen declare it; the five in
    /// OverlayDesigner do not, which is what this assertion is here to stop being
    /// discovered by accident a fourth time.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_endpoint_that_reads_If_Match_declares_the_400_it_answers_for_a_malformed_one()
    {
        ResolvedMapping[] requiring = TheSurface.Value.Routes.Where(RequiresPrecondition).ToArray();

        requiring.Length.ShouldBeGreaterThan(
            0,
            "no endpoint under src/*/Api was read as requiring If-Match, which cannot be true while "
            + "ConcurrencyHeaders exists. The body reader has stopped finding the helper calls.");

        string[] offenders = requiring
            .Where(m => !DeclaresMalformed(m.Mapping))
            .Select(m => $"{Describe(m.Mapping)} -> {m.Mapping.ContainingClass}.{m.Mapping.HandlerArgument} "
                + $"[{string.Join(", ", m.Calls.Select(c => $"{c.File}:{c.Line} ConcurrencyHeaders.TryRead{c.Helper}"))}]")
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{offenders.Length} endpoint(s) read If-Match and do not declare 400:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "ConcurrencyHeaders.Malformed() answers 400 IF_MATCH_MALFORMED for a wildcard, a weak tag, a "
            + "multi-valued or an unparseable If-Match, and each of these handlers returns that result "
            + "unchanged — off the same call site as the 428 the chain already declares. Without the "
            + "declaration the generated OpenAPI asserts that a status the endpoint routinely returns "
            + "cannot happen, so a client generated from it has no branch for it. Add "
            + "'.ProducesProblem(StatusCodes.Status400BadRequest)' to the chain above (ADR-0113 Layer 1: "
            + "400 for an unreadable precondition, 428 for a missing one, 409 or 412 for a failed one).");
    }

    // ---- A4: the mirror ----------------------------------------------------

    /// <summary>
    /// <b>A4 / FR-005 — the mirror, and the control that resolution is not
    /// failing open.</b> Zero offenders today. A reader that returned "requires
    /// nothing" for every mapping would turn all eight correct declarations into
    /// mirror violations, so a red here on an unmodified tree is a broken guard
    /// rather than a broken product.
    /// </summary>
    [Fact]
    public void No_endpoint_declares_428_without_requiring_If_Match()
    {
        IReadOnlyList<ResolvedMapping> mappings = TheSurface.Value.Routes;

        mappings.Count(m => Declares428(m.Mapping)).ShouldBeGreaterThan(
            0,
            "no chain under src/*/Api declares 428, so this assertion is passing over an empty set. "
            + "Either the chain reader is broken or the declarations have been removed wholesale.");

        string[] offenders = mappings
            .Where(m => Declares428(m.Mapping) && !RequiresPrecondition(m))
            .Select(m => $"{Describe(m.Mapping)} -> {m.Mapping.ContainingClass}.{m.Mapping.HandlerArgument}")
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{offenders.Length} endpoint(s) declare 428 and read neither If-Match helper:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "A declared precondition that no handler enforces tells a caller to compute and send a "
            + "header the endpoint ignores, and it will keep telling them after the enforcement is gone. "
            + "Either the handler should read the header — ConcurrencyHeaders.TryReadExpectedVersion, or "
            + "TryReadUpsertPrecondition where absence is legal — or the declaration should go. This is "
            + "the opposite direction from the missing-declaration failure and has its own cause.");
    }

    // ---- A5, A6: the two independent sweeps --------------------------------

    /// <summary>
    /// <b>A5 / FR-006 — every If-Match call site in these directories is inside
    /// a mapped handler.</b> The walk and a flat file sweep count the same thing
    /// two ways. Hoisting a call into a private helper the handler calls makes
    /// them disagree, which is the whole point: the lexical body scan is this
    /// guard's sharpest edge and this is what stops it failing quietly.
    /// </summary>
    [Fact]
    public void Every_If_Match_call_site_under_the_api_directories_sits_in_a_mapped_handler()
    {
        Surface surface = TheSurface.Value;
        int swept = surface.Files.Sum(file => HelperCall.Count(surface.Masked[file]));
        int walked = surface.Routes
            .Where(m => m.Handler is not null)
            .DistinctBy(m => (m.Handler!.File, m.Handler.BodyStart))
            .Sum(m => m.Calls.Count);

        swept.ShouldBe(
            PreconditionEndpointCount,
            $"a flat sweep of src/*/Api found {swept} ConcurrencyHeaders.TryRead* call sites, not "
            + $"{PreconditionEndpointCount}. The population moved: an endpoint started or stopped "
            + "requiring If-Match, or an endpoint file left these directories. Re-measure and edit this "
            + "number in the same diff — it is what stops both sides of the comparison below shrinking "
            + "together and staying green.");

        walked.ShouldBe(
            swept,
            $"the mapping walk found {walked} If-Match call sites inside mapped handler bodies; the file "
            + $"sweep found {swept}. This guard decides whether an endpoint requires If-Match by reading "
            + "the mapped method's own body, so a call that has moved out of one — into a private helper, "
            + "a local function, an extension — is invisible to the walk, and the endpoint is then judged "
            + "as requiring nothing. A source scan cannot repair that reading, so it reports it instead: "
            + "bring the call back into the mapped handler, or replace this guard with one that follows "
            + "calls.");
    }

    /// <summary>
    /// <b>A6 / FR-006 — every 428 declaration in these directories is inside a
    /// mapping's own chain.</b> Eight before spec 072's twelve declarations,
    /// seventeen after; the assertion is the agreement of the two counts, not
    /// either number, because both move together and legitimately.
    /// </summary>
    [Fact]
    public void Every_428_declaration_under_the_api_directories_sits_in_a_mapping_chain()
    {
        Surface surface = TheSurface.Value;
        int swept = surface.Files.Sum(file => Occurrences(surface.Masked[file], DeclarationToken));
        int walked = surface.Routes.Sum(m => Occurrences(m.Mapping.Chain, DeclarationToken));

        swept.ShouldBeGreaterThan(
            0,
            "no 428 declaration was found anywhere under src/*/Api. Eight endpoints declared it before "
            + "spec 072 and seventeen after, so zero means the sweep is reading nothing.");

        walked.ShouldBe(
            swept,
            $"the mapping walk found {walked} Status428PreconditionRequired declarations inside mapping "
            + $"chains; a flat sweep of src/*/Api found {swept}. A declaration the walk cannot see is a "
            + "declaration this guard does not credit: it sits outside the fluent chain the reader "
            + "captures — in a shared convention, an endpoint filter, a metadata helper — and the mirror "
            + "assertion above would report its endpoint as declaring nothing. Put it in the mapping's own "
            + "chain, or teach the reader the shape.");
    }

    /// <summary>
    /// <b>B2 / issue #2101 — every 400 declaration under the api directories
    /// sits in a mapping's own chain.</b> The same cross-check A6 makes for the
    /// 428, for the same reason: B1 credits a declaration only where the chain
    /// reader can see it, so a declaration that has moved into a shared
    /// convention or an endpoint filter would make B1 report its endpoint as
    /// declaring nothing. Neither number is pinned, because both move with any
    /// endpoint's validation surface and legitimately — the precondition
    /// population is pinned instead, in A5 and A7.
    /// </summary>
    [Fact]
    public void Every_400_declaration_under_the_api_directories_sits_in_a_mapping_chain()
    {
        Surface surface = TheSurface.Value;
        int swept = surface.Files.Sum(file => MalformedDeclaration.Count(surface.Masked[file]));
        int walked = surface.Routes.Sum(m => MalformedDeclaration.Count(m.Mapping.Chain));

        swept.ShouldBeGreaterThan(
            0,
            "no 400 declaration was found anywhere under src/*/Api. Fifty-five chains declared one when "
            + "this figure was last measured, so zero means the sweep is reading nothing — most likely the "
            + "declaration shape it matches has been spelled some other way.");

        walked.ShouldBe(
            swept,
            $"the mapping walk found {walked} 400 declarations inside mapping chains; a flat sweep of "
            + $"src/*/Api found {swept}. A declaration the walk cannot see is a declaration this guard does "
            + "not credit: it sits outside the fluent chain the reader captures — in a shared convention, "
            + "an endpoint filter, a metadata helper — and the assertion above would report its endpoint as "
            + "declaring nothing. Put it in the mapping's own chain, or teach the reader the shape.");
    }

    // ---- A7: the pinned corpus ---------------------------------------------

    /// <summary>
    /// <b>A7 / FR-008 — the corpus, pinned.</b> The counts above are all derived
    /// from one glob, so a file leaving <c>src/*/Api</c> shrinks both sides of
    /// every comparison at once and nothing goes red. These numbers are what
    /// turn that into a failure.
    /// </summary>
    [Fact]
    public void The_precondition_corpus_is_seventeen_endpoints_across_seven_files()
    {
        ResolvedMapping[] requiring = TheSurface.Value.Routes.Where(RequiresPrecondition).ToArray();
        string[] files = requiring
            .Select(m => m.Mapping.File)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        requiring.Length.ShouldBe(
            PreconditionEndpointCount,
            $"{requiring.Length} mappings were read as requiring If-Match, not {PreconditionEndpointCount}:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, requiring.Select(m => Describe(m.Mapping)))
            + Environment.NewLine
            + "If an endpoint genuinely started or stopped requiring the header, edit this number and the "
            + "one in the sweep above in the same diff. If it did not, the reader has lost sight of a "
            + "mapping.");

        files.Length.ShouldBe(
            PreconditionFileCount,
            $"the {PreconditionEndpointCount} precondition endpoints live in {files.Length} file(s), not "
            + $"{PreconditionFileCount}: {string.Join(", ", files)}.");
    }

    /// <summary>
    /// <b>The corpus, per item.</b> One case per endpoint file, because a single
    /// total lets one file stop being read while the others carry the count —
    /// which is how a guard reports a clean run over a shrinking population.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_endpoint_file_contributes_at_least_one_mapping(string file)
    {
        ResolvedMapping[] mappings = TheSurface.Value.Routes
            .Where(m => string.Equals(m.Mapping.File, file, StringComparison.Ordinal))
            .ToArray();

        mappings.Length.ShouldBeGreaterThan(
            0,
            $"'{file}' is named like an endpoint file and yielded no route mapping this guard can read. "
            + "Either it stopped mapping routes — rename it — or it maps them in a shape the reader does "
            + "not parse, in which case none of its endpoints are being checked at all.");
    }

    /// <summary>
    /// The number of endpoint files, pinned for the same reason as the corpus:
    /// the theory above proves each file it is given is non-empty, and says
    /// nothing about a file it is never given.
    /// </summary>
    [Fact]
    public void The_endpoint_file_glob_still_finds_twelve_files()
    {
        IReadOnlyList<string> files = TheSurface.Value.EndpointSources;

        files.Count.ShouldBe(
            EndpointFileCount,
            $"the glob found {files.Count} *Endpoints.cs files under src/*/Api, not {EndpointFileCount}: "
            + string.Join(", ", files));
    }

    // ---- FR-010: no soft edge ----------------------------------------------

    /// <summary>
    /// <b>FR-010 — the gate has no soft edge.</b> Spec 070's guard needed a
    /// register, read in both directions, for work that was genuinely open. This
    /// one needs nothing: an endpoint that can answer 428 can declare it in one
    /// line, so a skip list here would only ever record a decision not to.
    ///
    /// <para>
    /// It reads code, not prose: comment lines, attribute lines and the content
    /// of string literals are outside the scan, because prose about this rule
    /// necessarily uses this rule's vocabulary. It polices a vocabulary rather
    /// than a mechanism — someone who names the same thing differently walks
    /// past it — which is a fair price for making the obvious move loud, and is
    /// not a proof.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("allowlist")]
    [InlineData("whitelist")]
    [InlineData("skiplist")]
    [InlineData("baseline")]
    [InlineData("exempt")]
    [InlineData("waiver")]
    [InlineData("waived")]
    [InlineData("knownViolation")]
    [InlineData("suppress")]
    [InlineData("#pragma warning disable")]
    public void The_guard_offers_no_way_to_excuse_an_endpoint(string mechanism)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, GuardSource));

        string[] offenders = source.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith('['))
            .Select(line => StringLiteral.Replace(line, "\"\""))
            .Where(line => line.Contains(mechanism, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"the guard's own code names '{mechanism}': {string.Join(" | ", offenders)}. That reads as a "
            + "way to excuse an endpoint from the rule, and a rule with a soft edge is a review convention "
            + "wearing a build failure's clothes. There are two roads to green here and no third: declare "
            + "the status, or stop requiring the header.");
    }

    // ---- reading the surface -----------------------------------------------

    private static bool RequiresPrecondition(ResolvedMapping mapping) => mapping.Calls.Count > 0;

    private static bool Declares428(RouteMapping mapping) =>
        mapping.Chain.Contains(DeclarationToken, StringComparison.Ordinal);

    private static bool DeclaresMalformed(RouteMapping mapping) =>
        MalformedDeclaration.IsMatch(mapping.Chain);

    private static string Describe(RouteMapping mapping) =>
        $"{mapping.File}:{mapping.Line} {mapping.Verb} {mapping.Route}";

    private static int Occurrences(string text, string token)
    {
        int count = 0;
        int index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static Surface Read()
    {
        DirectoryInfo root = RepositoryRoot();
        List<string> files = ApiSourceFiles(root);

        Dictionary<string, string> masked = new(StringComparer.Ordinal);
        Dictionary<string, string> text = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string body = File.ReadAllText(Path.Combine(root.FullName, file))
                .Replace("\r", string.Empty, StringComparison.Ordinal);
            text[file] = body;
            masked[file] = Mask(body);
        }

        List<ClassSpan> classes = files.SelectMany(file => ClassSpans(file, masked[file])).ToList();
        List<ResolvedMapping> mappings = files
            .SelectMany(file => Mappings(file, text[file], masked[file], classes))
            .Select(mapping => Resolve(mapping, classes, masked))
            .ToList();

        return new Surface(
            files,
            files.Where(f => f.EndsWith(EndpointFileSuffix, StringComparison.Ordinal)).ToList(),
            masked,
            classes,
            mappings);
    }

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
    /// Every class declaration in one file, with the extent of its body. A
    /// nested class works out because the innermost containing span is chosen.
    /// </summary>
    private static IEnumerable<ClassSpan> ClassSpans(string file, string masked)
    {
        foreach (Match declaration in ClassDeclaration.Matches(masked))
        {
            int open = masked.IndexOf('{', declaration.Index + declaration.Length);
            if (open < 0)
            {
                continue;
            }

            int close = Balanced(masked, open, '{', '}');
            if (close > 0)
            {
                yield return new ClassSpan(file, declaration.Groups["name"].Value, open, close);
            }
        }
    }

    /// <summary>
    /// Every <c>Map*</c> call in one file: the route literal, the handler
    /// argument as written, and the whole fluent chain to its terminating
    /// semicolon. The chain is captured from the masked text, so a status name
    /// mentioned inside a summary is prose rather than a declaration.
    /// </summary>
    private static IEnumerable<RouteMapping> Mappings(
        string file,
        string text,
        string masked,
        IReadOnlyList<ClassSpan> classes)
    {
        foreach (Match call in MappingCall.Matches(masked))
        {
            int open = call.Index + call.Length - 1;
            int close = Balanced(masked, open, '(', ')');
            if (close < 0)
            {
                continue;
            }

            int end = StatementEnd(masked, close + 1);
            string chain = end < 0 ? masked[call.Index..] : masked[call.Index..end];

            List<(int Start, int End)> arguments = SplitArguments(masked, open + 1, close);
            string route = arguments.Count > 0 ? text[arguments[0].Start..arguments[0].End].Trim() : string.Empty;
            string handler = arguments.Count > 1 ? text[arguments[1].Start..arguments[1].End].Trim() : string.Empty;

            yield return new RouteMapping(
                file,
                LineOf(masked, call.Index),
                call.Groups["verb"].Value.ToUpperInvariant(),
                route.Length > 1 && route[0] == '"' && route[^1] == '"' ? route[1..^1] : route,
                DeclaringClass(classes, file, call.Index),
                handler,
                chain);
        }
    }

    private static string DeclaringClass(IReadOnlyList<ClassSpan> classes, string file, int index) =>
        classes
            .Where(c => string.Equals(c.File, file, StringComparison.Ordinal) && c.Start < index && index < c.End)
            .OrderBy(c => c.End - c.Start)
            .Select(c => c.Name)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Binds a mapping to the one method its handler argument names, searching
    /// every file of the same Api project that declares the mapping's own class.
    /// Anything other than exactly one match is a failure carried on the
    /// mapping, never a silent skip.
    /// </summary>
    private static ResolvedMapping Resolve(
        RouteMapping mapping,
        IReadOnlyList<ClassSpan> classes,
        Dictionary<string, string> masked)
    {
        if (mapping.ContainingClass.Length == 0)
        {
            return Unreadable(mapping, "the mapping is not inside a class declaration this reader can find");
        }

        if (!MethodGroupName.IsMatch(mapping.HandlerArgument))
        {
            return Unreadable(
                mapping,
                $"the handler argument '{Ellipsis(mapping.HandlerArgument)}' is not a bare method-group name");
        }

        string project = ProjectOf(mapping.File);
        List<HandlerBody> candidates = classes
            .Where(c => string.Equals(c.Name, mapping.ContainingClass, StringComparison.Ordinal)
                && string.Equals(ProjectOf(c.File), project, StringComparison.Ordinal))
            .SelectMany(c => MethodBodies(c, masked[c.File], mapping.HandlerArgument))
            .ToList();

        if (candidates.Count == 0)
        {
            return Unreadable(
                mapping,
                $"'{mapping.ContainingClass}.{mapping.HandlerArgument}' resolves to no method declaration in {project}");
        }

        if (candidates.Count > 1)
        {
            return Unreadable(
                mapping,
                $"'{mapping.ContainingClass}.{mapping.HandlerArgument}' resolves to {candidates.Count} method "
                + $"declarations ({string.Join(", ", candidates.Select(c => $"{c.File}:{c.Line}"))})");
        }

        HandlerBody body = candidates[0];
        List<PreconditionCall> calls = HelperCall.Matches(body.Body)
            .Select(match => new PreconditionCall(
                match.Groups["helper"].Value,
                body.File,
                LineOf(masked[body.File], body.BodyStart + match.Index)))
            .ToList();

        return new ResolvedMapping(mapping, body, calls, null);
    }

    private static ResolvedMapping Unreadable(RouteMapping mapping, string failure) =>
        new(mapping, null, [], failure);

    /// <summary>
    /// The Api project a file belongs to — <c>src/&lt;Context&gt;/Api</c>. Two
    /// contexts may each declare a class of the same name, and Identity declares
    /// three <c>List</c> handlers in three classes of its own.
    /// </summary>
    private static string ProjectOf(string file)
    {
        int marker = file.IndexOf("/Api/", StringComparison.Ordinal);
        return marker < 0 ? file : file[..(marker + 4)];
    }

    /// <summary>
    /// Every method of the given name declared directly in one class body. The
    /// leading accessibility keyword is what separates a declaration from a call
    /// site: a call has no modifier between it and the punctuation before it.
    /// </summary>
    private static IEnumerable<HandlerBody> MethodBodies(ClassSpan span, string masked, string name)
    {
        Regex declaration = new(
            @"(?<!\w)(?:private|public|internal|protected)[^;{}()\n]*?\b" + Regex.Escape(name) + @"\s*\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        foreach (Match match in declaration.Matches(masked))
        {
            if (match.Index <= span.Start || match.Index >= span.End)
            {
                continue;
            }

            int close = Balanced(masked, match.Index + match.Length - 1, '(', ')');
            if (close < 0)
            {
                continue;
            }

            HandlerBody? body = BodyAfter(span.File, masked, match.Index, close);
            if (body is not null)
            {
                yield return body;
            }
        }
    }

    /// <summary>
    /// The body of a method whose parameter list ends at
    /// <paramref name="close"/> — block or expression-bodied. A declaration with
    /// neither has no body to read and is not a candidate.
    /// </summary>
    private static HandlerBody? BodyAfter(string file, string masked, int declaration, int close)
    {
        int brace = masked.IndexOf('{', close + 1);
        int semicolon = masked.IndexOf(';', close + 1);
        int arrow = masked.IndexOf("=>", close + 1, StringComparison.Ordinal);
        int line = LineOf(masked, declaration);

        if (brace >= 0 && (semicolon < 0 || brace < semicolon) && (arrow < 0 || brace < arrow))
        {
            int end = Balanced(masked, brace, '{', '}');
            return end < 0 ? null : new HandlerBody(file, line, brace + 1, masked[(brace + 1)..end]);
        }

        if (arrow >= 0 && (semicolon < 0 || arrow < semicolon))
        {
            int end = StatementEnd(masked, arrow + 2);
            return end < 0 ? null : new HandlerBody(file, line, arrow + 2, masked[(arrow + 2)..end]);
        }

        return null;
    }

    /// <summary>
    /// The index of the semicolon that ends the statement starting at
    /// <paramref name="from"/>, ignoring semicolons nested inside brackets — a
    /// chain may carry a lambda.
    /// </summary>
    private static int StatementEnd(string masked, int from)
    {
        int depth = 0;
        for (int i = from; i < masked.Length; i++)
        {
            char c = masked[i];
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

    /// <summary>
    /// The half-open spans of the top-level arguments between
    /// <paramref name="from"/> and <paramref name="close"/>.
    /// </summary>
    private static List<(int Start, int End)> SplitArguments(string masked, int from, int close)
    {
        List<(int Start, int End)> arguments = [];
        int depth = 0;
        int start = from;
        for (int i = from; i < close; i++)
        {
            char c = masked[i];
            if (c is '(' or '[' or '{' or '<')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add((start, i));
                start = i + 1;
            }
        }

        if (close > start)
        {
            arguments.Add((start, close));
        }

        return arguments;
    }

    /// <summary>
    /// The index of the delimiter matching the one at
    /// <paramref name="openIndex"/>, or -1.
    /// </summary>
    private static int Balanced(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The source with comment and literal <em>content</em> replaced by spaces,
    /// the same length throughout so every index still points at the same
    /// character of the original. Delimiters are kept, so a route literal is
    /// still recognisable as one.
    /// </summary>
    private static string Mask(string text)
    {
        char[] masked = text.ToCharArray();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && Next(text, i) == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    masked[i++] = ' ';
                }
            }
            else if (text[i] == '/' && Next(text, i) == '*')
            {
                i = MaskBlockComment(text, masked, i);
            }
            else if (text[i] == '@' && Next(text, i) == '"')
            {
                i = MaskVerbatim(text, masked, i);
            }
            else if (text[i] is '"' or '\'')
            {
                i = MaskLiteral(text, masked, i);
            }
            else
            {
                i++;
            }
        }

        return new string(masked);
    }

    private static int MaskBlockComment(string text, char[] masked, int from)
    {
        int i = from;
        while (i < text.Length && !(text[i] == '*' && Next(text, i) == '/'))
        {
            masked[i] = text[i] == '\n' ? '\n' : ' ';
            i++;
        }

        return Blank(masked, i, 2);
    }

    private static int MaskVerbatim(string text, char[] masked, int from)
    {
        int i = from + 2;
        while (i < text.Length)
        {
            if (text[i] == '"' && Next(text, i) == '"')
            {
                masked[i] = ' ';
                masked[i + 1] = ' ';
                i += 2;
                continue;
            }

            if (text[i] == '"')
            {
                return i + 1;
            }

            masked[i] = text[i] == '\n' ? '\n' : ' ';
            i++;
        }

        return i;
    }

    private static int MaskLiteral(string text, char[] masked, int from)
    {
        char quote = text[from];
        int i = from + 1;
        while (i < text.Length && text[i] != quote && text[i] != '\n')
        {
            masked[i] = ' ';
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                masked[i + 1] = ' ';
                i++;
            }

            i++;
        }

        return i + 1;
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private static int Blank(char[] masked, int from, int count)
    {
        for (int i = from; i < from + count && i < masked.Length; i++)
        {
            masked[i] = ' ';
        }

        return from + count;
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

    private static string Ellipsis(string value) =>
        value.Length <= 60 ? value : value[..57] + "...";

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

    private sealed record Surface(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> EndpointSources,
        IReadOnlyDictionary<string, string> Masked,
        IReadOnlyList<ClassSpan> Classes,
        IReadOnlyList<ResolvedMapping> Routes);

    private sealed record ClassSpan(string File, string Name, int Start, int End);

    private sealed record RouteMapping(
        string File,
        int Line,
        string Verb,
        string Route,
        string ContainingClass,
        string HandlerArgument,
        string Chain);

    private sealed record HandlerBody(string File, int Line, int BodyStart, string Body);

    private sealed record PreconditionCall(string Helper, string File, int Line);

    private sealed record ResolvedMapping(
        RouteMapping Mapping,
        HandlerBody? Handler,
        IReadOnlyList<PreconditionCall> Calls,
        string? Failure);
}

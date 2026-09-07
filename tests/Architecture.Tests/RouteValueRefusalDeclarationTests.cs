using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the contract half of a refusal a handler already performs: <b>a route
/// mapping whose own handler body can answer <c>400</c> declares that <c>400</c>
/// on its own fluent chain</b> (issue #2114, spec 091).
///
/// <para>
/// A status produced in the handler body does not reach the generated OpenAPI
/// document by itself. Six routes across five bounded contexts refuse their
/// route value with a <c>400</c> in the handler's first statement block and
/// declare 200/403/404/409 and no 400, so the document asserts that a status
/// those routes certainly return cannot happen — and a client generated from it
/// has no branch for the one answer a typo earns. The six were found one at a
/// time, in five separate reviews of unrelated issues, which is the case for a
/// rule that fails the build rather than a convention a reviewer remembers
/// (ADR-0139).
/// </para>
///
/// <para>
/// <b>The antecedent, stated exactly.</b> Within the mapping's resolved handler
/// body — comments blanked, string and char literals masked — the token
/// <c>StatusCodes.Status400BadRequest</c>, or a call to <c>Results.BadRequest(</c>
/// or <c>.ValidationProblem(</c>. <b>The consequent:</b> within the mapping's own
/// chain span, from the <c>.MapX(</c> call to the statement's terminating
/// semicolon, a call matching
/// <c>\.Produces(?:Validation)?Problem\(\s*StatusCodes\.Status400BadRequest\s*\)</c>.
/// </para>
///
/// <para>
/// <b>The population, measured on this branch.</b> 56 route-handler mappings in
/// 12 <c>*Endpoints.cs</c> files under <c>src/*/Api</c>; 43 of them have a
/// handler body matching the antecedent: 37 declared the 400 and <b>6 did
/// not</b> before this change, 43 and 0 after, across six files in five
/// contexts. Independently: 49 of the 56 chains declared a 400 before this
/// change, 55 after — the remaining one is <c>POST /streams/authorize</c>,
/// whose handler produces no 400 at all and is therefore correct.
/// </para>
///
/// <para>
/// <b>Two producer shapes, evenly split, and a guard keyed on either finds
/// three.</b> Shape A is <c>try { X.From(routeValue) } catch (ArgumentException)
/// { … 400 }</c>, where the value object's own <c>Ensure.That</c> guard throws
/// (ADR-0105) — AuditObservability, and Identity's two. Shape B is
/// <c>if (routeValue == Guid.Empty) { … 400 }</c>, a hand-written check ahead of
/// a <c>.From</c> that would throw — LayoutComposition, OverlayDesigner,
/// StreamDistribution. A guard written from the issue's own sentence
/// ("<c>ClientId.From</c> throws") matches shape A and reports three; one written
/// from <c>catch (ArgumentException)</c> reports the same three. The antecedent
/// that matches all six is neither: it is the status the body returns, not the
/// reason it returns it.
/// </para>
///
/// <para>
/// <b>Both declaration spellings count.</b> Forty-eight chains declare their 400 as
/// <c>.ProducesProblem(...)</c> and seven as <c>.ProducesValidationProblem(...)</c>.
/// A guard matching only the first reports 13 offenders, seven of them correct
/// code. <c>PreconditionDeclarationTests</c> learned this in spec 072 and says so
/// in its own doc; this file inherits the regex rather than rediscovering it.
/// </para>
///
/// <para>
/// <b>The rule runs one way only.</b> <c>GET /audit</c> declares a 400 and its
/// handler body produces none — correctly: its signature binds
/// <c>[FromQuery] Guid? actor</c>, <c>DateTimeOffset?</c> and <c>int?</c>, and
/// ASP.NET's own model binding answers 400 for a malformed one, inside the
/// framework where no scan of <c>src/*/Api</c> can see it. A mirror rule
/// (<em>declares 400 ⇒ must produce one</em>) would fail correct code on its
/// first run. There is deliberately none. That framework producer is also this
/// guard's largest residual: a route with <em>only</em> a framework producer and
/// no declaration passes here unnoticed, and <c>GET /audit</c> is the one route
/// that would have exposed it — it already declares.
/// </para>
///
/// <para>
/// <b>The <c>:guid</c> constraint is not a defence.</b> Four of the six carry
/// one, and it is tempting to read that as "a malformed value never reaches the
/// handler". <c>00000000-0000-0000-0000-000000000000</c> satisfies the constraint
/// and fails the domain check, so it reaches the handler and earns the 400. The
/// constraint filters non-Guid text into a routing 404; it does not filter the
/// empty Guid. Typing the two untyped parameters would therefore not fix the
/// contract — it would change four callers' answers from 400 to 404 to make the
/// contract true, which spec 091 refuses.
/// </para>
///
/// <para>
/// <b>Why a new file rather than an addition to
/// <c>PreconditionDeclarationTests</c>.</b> That file is the nearest neighbour by
/// mechanism — it resolves a mapping's handler body by method-group name across
/// an Api project's partial classes, and it already owns the both-spellings 400
/// declaration regex — and the reader below is copied from it deliberately rather
/// than reinvented (ADR-0036). But its <em>subject</em> is the <c>If-Match</c>
/// precondition, and the populations are disjoint: none of these six touches
/// <c>ConcurrencyHeaders</c>. A rule whose subject its filename denies is a rule
/// the next author does not find, and this defect class has now been found five
/// separate times by people who could not find the previous instance. The named
/// cost is a fifth copy of <c>RepositoryRoot()</c>, the masker and the chain
/// reader; extracting them is a behaviour-preserving refactor of test
/// infrastructure, deferred with spec 085's reasoning and owned by #2142.
/// </para>
///
/// <para>
/// <b>What it asserts</b>, and why each of the last three exists so the first
/// cannot pass while checking nothing.
/// </para>
/// <list type="number">
/// <item>
/// <b>G1</b> — per endpoint file: every mapping whose handler body matches the
/// antecedent declares the 400 on its own chain, and the file contributes at
/// least one mapping.
/// </item>
/// <item>
/// <b>G2</b> — no route group declares a 400. OpenAPI <em>inherits</em>
/// <c>MapGroup</c> metadata, so a group-level declaration would make G1's
/// per-chain demand unsound and fail correct code. Zero of the seventeen groups
/// declare any status today; this says so out loud, so the day someone adds one
/// the guard fails instead of lying.
/// </item>
/// <item>
/// <b>G3</b> — the walk agrees with an independent flat sweep of the same
/// directories. A declaration hoisted somewhere the chain reader cannot see — a
/// shared convention, an endpoint filter, a metadata helper — would read as
/// "declares nothing", and G1 would report a correct endpoint. The property comes
/// from <c>PaginatedConsumerTests</c> and is already borrowed by two other
/// guards.
/// </item>
/// <item>
/// <b>G4</b> — every mapping resolves to exactly one handler body, and the corpus
/// is 56 mappings in 12 files. A mapping the reader silently drops leaves the
/// population and G1 goes green over it; and every count here is derived from one
/// glob, so a file leaving that glob shrinks both sides of G3 at once. Spec 070
/// watched exactly that report a clean run over eight endpoints no longer
/// checked. Adding an endpoint therefore edits a number here in the same diff,
/// which is the point rather than the cost.
/// </item>
/// </list>
///
/// <para>
/// <b>What it provably cannot do, stated up front.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// It reads source, not a running application, and it reads <em>source</em>
/// rather than the emitted <c>openapi/v1.json</c>. No member of this guard family
/// reads the generated document; that gap is shared by all five and is #2142's.
/// </item>
/// <item>
/// It reads the mapping's <em>own</em> handler body and follows no calls. On
/// today's corpus that costs nothing — following calls within the same Api
/// project to depth three adds zero routes to the population, measured on this
/// branch — but a producer moved one hop out would leave the population silently.
/// G3 catches a hoisted <em>declaration</em>; nothing here catches a hoisted
/// producer.
/// </item>
/// <item>
/// It checks that a status is declared, not that it is reachable at run time, and
/// it reads presence rather than order.
/// </item>
/// <item>
/// It resolves the handler by method-group name within the declaring class,
/// across every file of the same Api project declaring that class — partial
/// endpoint classes are the normal case here. A lambda, a name qualified by
/// another type, or a name resolving to none or two is <em>unreadable</em> and
/// fails G4; nothing resolves to a pass by default.
/// </item>
/// <item>
/// Its masker understands line comments, block comments, character literals and
/// regular and verbatim string literals. A raw string literal (three quotes), of
/// which there are none in these directories, would be masked wrongly.
/// </item>
/// <item>
/// <c>RefusalDeclaration</c> has no <c>\s*</c> before its opening paren, unlike
/// the antecedent's <c>\.BadRequest\s*\(</c>: <c>.ProducesProblem (...)</c> with
/// a space would read as undeclared and fail correct code. This is a known
/// choice, not an oversight — the regex is byte-identical to
/// <c>PreconditionDeclarationTests.MalformedDeclaration</c>, which this file
/// inherits verbatim rather than rediscovering (ADR-0036), and no code in these
/// directories is written with that space.
/// </item>
/// </list>
/// </summary>
public class RouteValueRefusalDeclarationTests
{
    private const string EndpointFileSuffix = "Endpoints.cs";

    /// <summary>
    /// Every <c>.Map(Get|Post|Put|Patch|Delete)("route", Handler)</c> site under
    /// <c>src/*/Api</c>. Pinned, not merely compared — see G4.
    /// </summary>
    private const int RouteHandlerMappingCount = 56;

    /// <summary>
    /// Every file under <c>src/*/Api</c> whose name ends <c>Endpoints.cs</c>.
    /// Each is asserted to contribute at least one mapping, individually.
    /// </summary>
    private const int EndpointFileCount = 12;

    private static readonly Regex MappingCall = new(
        @"(?<receiver>[A-Za-z_]\w*)\.Map(?<verb>Get|Post|Put|Patch|Delete)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A route group, with the variable the builder is assigned to. All
    /// seventeen are written this way; G2 asserts that every <c>.MapGroup(</c>
    /// site in these directories was read, so a group created in some other
    /// shape fails rather than escaping the check.
    /// </summary>
    private static readonly Regex GroupDeclaration = new(
        @"(?<variable>[A-Za-z_]\w*)\s*=\s*[A-Za-z_]\w*\s*\.MapGroup\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex GroupCall = new(
        @"\.MapGroup\s*\(",
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
    /// The antecedent: the handler body can answer 400. The status token covers
    /// every <c>Results.Problem(statusCode: ...)</c> refusal; the two call shapes
    /// cover the helpers that carry the status implicitly and never spell it.
    /// </summary>
    private static readonly Regex RefusalProducer = new(
        @"StatusCodes\.Status400BadRequest|\.BadRequest\s*\(|\.ValidationProblem\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The consequent, in both spellings. Matched by shape rather than by the
    /// bare status name, because <c>StatusCodes.Status400BadRequest</c> also
    /// appears in the handler bodies that return one.
    /// </summary>
    private static readonly Regex RefusalDeclaration = new(
        @"\.Produces(?:Validation)?Problem\(\s*StatusCodes\.Status400BadRequest\s*\)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Any status named on a chain, used only to tell the reader of a failure
    /// what the chain declares today.
    /// </summary>
    private static readonly Regex DeclaredStatus = new(
        @"StatusCodes\.Status(?<code>\d{3})[A-Za-z]*",
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

    // ---- G1: the claim -----------------------------------------------------

    /// <summary>
    /// <b>G1 — a route that refuses its own route value declares the refusal.</b>
    /// One case per endpoint file, so a failure names the file rather than a
    /// total: a single aggregate lets one file stop being read while the others
    /// carry the count, which is how a guard reports a clean run over a shrinking
    /// population.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_route_whose_handler_can_answer_400_declares_it_on_its_own_chain(string file)
    {
        ResolvedMapping[] mappings = TheSurface.Value.Routes
            .Where(m => string.Equals(m.Mapping.File, file, StringComparison.Ordinal))
            .ToArray();

        mappings.Length.ShouldBeGreaterThan(
            0,
            $"'{file}' is named like an endpoint file and yielded no route mapping this guard can read. "
            + "Either it stopped mapping routes — rename it — or it maps them in a shape the reader does "
            + "not parse, in which case none of its endpoints are being checked at all and this assertion "
            + "would pass over an empty set.");

        string[] offenders = mappings
            .Where(CanRefuse)
            .Where(m => !RefusalDeclaration.IsMatch(m.Mapping.Chain))
            .Select(Describe)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{offenders.Length} route(s) in '{file}' can answer 400 from their own handler body and do "
            + "not declare it:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "The handler refuses the value it was given and returns 400 with a problem title the caller "
            + "needs; the chain says 400 cannot happen, so the generated OpenAPI says so too and a client "
            + "generated from it has no branch for the one answer a typo earns. Add "
            + "'.ProducesProblem(StatusCodes.Status400BadRequest)' to the chain above, among its existing "
            + "Produces calls. Do not instead type or constrain the route parameter: that turns the 400 "
            + "into a routing 404 and changes the answer callers already receive, which is fixing the "
            + "contract by changing the product.");
    }

    // ---- G2: the soundness prerequisite ------------------------------------

    /// <summary>
    /// <b>G2 — no route group declares a 400.</b> OpenAPI inherits group
    /// metadata, so a group-level declaration would satisfy the document while
    /// leaving G1's per-chain demand failing correct code. Zero of seventeen
    /// today.
    /// </summary>
    [Fact]
    public void No_route_group_declares_a_400_that_its_endpoints_would_inherit()
    {
        Surface surface = TheSurface.Value;

        int sites = surface.Files.Sum(file => GroupCall.Count(surface.Masked[file]));
        surface.Groups.Count.ShouldBe(
            sites,
            $"the reader bound {surface.Groups.Count} route groups to a builder variable; a flat sweep of "
            + $"src/*/Api found {sites} '.MapGroup(' call sites. A group the reader cannot bind is a group "
            + "whose metadata it cannot check, and its endpoints' prefixes are then reported wrongly too. "
            + "The shape it reads is '<variable> = <builder>.MapGroup(\"/prefix\")'.");

        sites.ShouldBeGreaterThan(
            0,
            "no route group was found under src/*/Api, which cannot be true while every endpoint file "
            + "opens with one. This assertion is passing over an empty set.");

        string[] offenders = surface.Groups
            .Where(g => RefusalDeclaration.IsMatch(g.Chain))
            .Select(g => $"{g.File}:{g.Line} MapGroup(\"{g.Prefix}\")")
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{offenders.Length} route group(s) declare a 400:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "This is refused for the guard's sake, not the endpoint's: OpenAPI inherits group metadata, "
            + "so every route in the group would carry a 400 in the document whether its handler can "
            + "answer one or not — and the per-route rule above would then demand a declaration that is "
            + "already there invisibly, or bless a route that answers no 400 at all. Declare the status on "
            + "the chains that answer it.");
    }

    // ---- G3: the independent sweep -----------------------------------------

    /// <summary>
    /// <b>G3 — every 400 declaration in these directories sits in a chain the
    /// walk can see.</b> Both sides read 49 before spec 091's six declarations
    /// and 55 after; the assertion is the agreement, not either number, because
    /// both move with any endpoint's validation surface and legitimately.
    /// </summary>
    [Fact]
    public void Every_400_declaration_under_the_api_directories_sits_in_a_chain_the_walk_reads()
    {
        Surface surface = TheSurface.Value;
        int swept = surface.Files.Sum(file => RefusalDeclaration.Count(surface.Masked[file]));
        int walked = surface.Routes.Sum(m => RefusalDeclaration.Count(m.Mapping.Chain))
            + surface.Groups.Sum(g => RefusalDeclaration.Count(g.Chain));

        swept.ShouldBeGreaterThan(
            0,
            "no 400 declaration was found anywhere under src/*/Api. Fifty-five chains declared one when "
            + "this figure was last measured, so zero means the sweep is reading nothing — most likely the "
            + "declaration shape it matches is now spelled some other way.");

        walked.ShouldBe(
            swept,
            $"the walk found {walked} 400 declarations inside mapping and group chains; a flat sweep of "
            + $"src/*/Api found {swept}. A declaration the walk cannot see is a declaration this guard "
            + "does not credit: it sits outside the chain span the reader captures — in a shared "
            + "convention, an endpoint filter, a metadata helper — and the rule above would report its "
            + "route as an omission. Put it in the mapping's own chain, or teach the reader the shape.");
    }

    // ---- G4: nothing resolves to a pass by default -------------------------

    /// <summary>
    /// <b>G4 — every mapping is read rather than skipped, over a pinned
    /// corpus.</b> An unreadable argument, no match and two matches are each a
    /// failure naming the mapping and the shape; and the corpus is pinned because
    /// every other count here comes off the same glob, so a file leaving it would
    /// shrink both sides of G3 together and stay green. It also closes the gap
    /// between the two populations G1 and this method each read: G1 is driven by
    /// <c>EndpointFiles()</c>, named only from files ending <c>Endpoints.cs</c>,
    /// while every mapping here is read from every <c>*.cs</c> file under
    /// <c>src/*/Api</c> — so a mapping declared anywhere else would resolve here
    /// and never reach G1's per-file check. Asserting every mapping's file is one
    /// of the named endpoint files keeps that add-elsewhere-remove-here move from
    /// passing at an unchanged 56.
    /// </summary>
    [Fact]
    public void Every_route_mapping_resolves_to_one_handler_body_the_guard_can_read()
    {
        IReadOnlyList<ResolvedMapping> mappings = TheSurface.Value.Routes;

        string[] unresolved = mappings
            .Where(m => m.Failure is not null)
            .Select(m => $"{Describe(m)}: {m.Failure}")
            .ToArray();

        unresolved.ShouldBeEmpty(
            "these mappings do not resolve to exactly one handler body:" + Environment.NewLine
            + string.Join(Environment.NewLine, unresolved) + Environment.NewLine
            + "This guard reads the handler's body to decide whether the route can answer 400, so a "
            + "mapping it cannot resolve is a mapping it cannot judge — and it fails rather than passing "
            + "over it. The shape it reads is a bare method-group name as the second argument of the Map "
            + "call, declared once in the mapping's own class, in any file of the same Api project that "
            + "declares that class. An inline lambda, a name qualified by another type, or a name declared "
            + "twice in the class is not that shape.");

        mappings.Count.ShouldBe(
            RouteHandlerMappingCount,
            $"the reader found {mappings.Count} route-handler mappings under src/*/Api, not "
            + $"{RouteHandlerMappingCount}. Every other count in this file is derived from the same glob, "
            + "so a file that leaves it shrinks both sides of the sweep comparison at once and nothing "
            + "goes red. If a route was genuinely added or removed, edit this number in the same diff.");

        TheSurface.Value.EndpointSources.Count.ShouldBe(
            EndpointFileCount,
            $"the glob found {TheSurface.Value.EndpointSources.Count} *Endpoints.cs files under "
            + $"src/*/Api, not {EndpointFileCount}: "
            + string.Join(", ", TheSurface.Value.EndpointSources) + ". The per-file theory above proves "
            + "each file it is given is non-empty and says nothing about a file it is never given.");

        string[] outsideEndpointFiles = mappings
            .Select(m => m.Mapping.File)
            .Distinct(StringComparer.Ordinal)
            .Where(file => !TheSurface.Value.EndpointSources.Contains(file, StringComparer.Ordinal))
            .ToArray();

        outsideEndpointFiles.ShouldBeEmpty(
            "these files declare at least one route mapping but do not end 'Endpoints.cs':" + Environment.NewLine
            + string.Join(Environment.NewLine, outsideEndpointFiles) + Environment.NewLine
            + "G1 above is driven by EndpointFiles(), named only from files ending 'Endpoints.cs'; a mapping "
            + "declared anywhere else resolves here but is never given to G1's per-file check, so it would "
            + "pass over it unnoticed. Move the mapping into an *Endpoints.cs file, or rename the file so "
            + "G1 covers it.");
    }

    // ---- reading the surface -----------------------------------------------

    private static bool CanRefuse(ResolvedMapping mapping) =>
        mapping.Handler is not null && RefusalProducer.IsMatch(mapping.Handler.Body);

    private static string Describe(ResolvedMapping mapping)
    {
        RouteMapping route = mapping.Mapping;
        string[] declared = DeclaredStatus.Matches(route.Chain)
            .Select(match => match.Groups["code"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string states = declared.Length == 0 ? "no status at all" : string.Join(", ", declared);

        return $"{route.File}:{route.Line} {route.Verb} {route.FullRoute} -> "
            + $"{route.ContainingClass}.{route.HandlerArgument} declares {states}";
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
        List<RouteGroup> groups = files.SelectMany(file => RouteGroups(file, text[file], masked[file])).ToList();
        List<ResolvedMapping> mappings = files
            .SelectMany(file => Mappings(file, text[file], masked[file], classes, groups))
            .Select(mapping => Resolve(mapping, classes, masked))
            .ToList();

        return new Surface(
            files,
            files.Where(f => f.EndsWith(EndpointFileSuffix, StringComparison.Ordinal)).ToList(),
            masked,
            groups,
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
    /// Every route group in one file: the variable it is bound to, its prefix
    /// literal, and the whole statement as its chain. The prefix is read from the
    /// unmasked text, the structure from the masked.
    /// </summary>
    private static IEnumerable<RouteGroup> RouteGroups(string file, string text, string masked)
    {
        foreach (Match declaration in GroupDeclaration.Matches(masked))
        {
            int open = declaration.Index + declaration.Length - 1;
            int close = Balanced(masked, open, '(', ')');
            if (close < 0)
            {
                continue;
            }

            int end = StatementEnd(masked, close + 1);
            List<(int Start, int End)> arguments = SplitArguments(masked, open + 1, close);
            string prefix = arguments.Count > 0 ? Unquote(text[arguments[0].Start..arguments[0].End].Trim()) : string.Empty;

            yield return new RouteGroup(
                file,
                LineOf(masked, declaration.Index),
                declaration.Groups["variable"].Value,
                prefix,
                end < 0 ? masked[declaration.Index..] : masked[declaration.Index..end]);
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
        IReadOnlyList<ClassSpan> classes,
        IReadOnlyList<RouteGroup> groups)
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
            string route = arguments.Count > 0 ? Unquote(text[arguments[0].Start..arguments[0].End].Trim()) : string.Empty;
            string handler = arguments.Count > 1 ? text[arguments[1].Start..arguments[1].End].Trim() : string.Empty;

            yield return new RouteMapping(
                file,
                LineOf(masked, call.Index),
                call.Groups["verb"].Value.ToUpperInvariant(),
                route,
                PrefixOf(groups, file, call.Groups["receiver"].Value),
                DeclaringClass(classes, file, call.Index),
                handler,
                chain);
        }
    }

    /// <summary>
    /// The group prefix a mapping's receiver carries, so a failure names the path
    /// a caller uses rather than the literal written at the mapping. A receiver
    /// that is not a bound group builder — the application itself — has none.
    /// </summary>
    private static string PrefixOf(IReadOnlyList<RouteGroup> groups, string file, string receiver) =>
        groups
            .Where(g => string.Equals(g.File, file, StringComparison.Ordinal)
                && string.Equals(g.Variable, receiver, StringComparison.Ordinal))
            .Select(g => g.Prefix)
            .FirstOrDefault() ?? string.Empty;

    private static string DeclaringClass(IReadOnlyList<ClassSpan> classes, string file, int index) =>
        classes
            .Where(c => string.Equals(c.File, file, StringComparison.Ordinal) && c.Start < index && index < c.End)
            .OrderBy(c => c.End - c.Start)
            .Select(c => c.Name)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Binds a mapping to the one method its handler argument names, searching
    /// every file of the same Api project that declares the mapping's own class.
    /// Anything other than exactly one match is a failure carried on the mapping,
    /// never a silent skip.
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

        return new ResolvedMapping(mapping, candidates[0], null);
    }

    private static ResolvedMapping Unreadable(RouteMapping mapping, string failure) =>
        new(mapping, null, failure);

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
    /// chain may carry a lambda or a collection initialiser.
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

    private static string Unquote(string value) =>
        value.Length > 1 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

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
        IReadOnlyList<RouteGroup> Groups,
        IReadOnlyList<ResolvedMapping> Routes);

    private sealed record ClassSpan(string File, string Name, int Start, int End);

    private sealed record RouteGroup(string File, int Line, string Variable, string Prefix, string Chain);

    private sealed record RouteMapping(
        string File,
        int Line,
        string Verb,
        string Route,
        string GroupPrefix,
        string ContainingClass,
        string HandlerArgument,
        string Chain)
    {
        public string FullRoute => (GroupPrefix + Route).Replace("//", "/", StringComparison.Ordinal);
    }

    private sealed record HandlerBody(string File, int Line, int BodyStart, string Body);

    private sealed record ResolvedMapping(RouteMapping Mapping, HandlerBody? Handler, string? Failure);
}

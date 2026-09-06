using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SmartSentinelEye.EventIngestion.Domain.WebhookIntegration;
using SmartSentinelEye.LayoutComposition.Infrastructure.Broadcasting;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.StreamDistribution.Application.Commands.Handlers;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the rule spec 070 was written for: <b>an endpoint names the scope it
/// needs</b> (issue 2087; #850 was closed as delivered for its Identity label).
///
/// <para>
/// Eighteen endpoints already spell it — <c>.WithSummary("… Required scope:
/// sse.x.y")</c> — and thirty-three do not. Nothing enforced it, and the price
/// of that is issue <b>2070</b>: <c>sse.variables.read</c> exists in the
/// catalogue, is provisioned in the realm and granted to three clients, and is
/// required by nothing. Two of the three endpoints that should require it carry
/// a summary describing fab filtering and archived rows in careful detail and
/// simply never mention a scope. The prose was written; the omission was
/// invisible, because there was no place the omission had to show up. A phase-6
/// review of an unrelated PR found it by accident.
/// </para>
///
/// <para>
/// <b>What it asserts.</b> Every route-handler mapping under <c>src/*/Api</c>
/// resolves to an effective authorization — a scope, explicit anonymity, or a
/// bare <c>.RequireAuthorization()</c> naming nothing — and the summary must
/// agree with it: a scoped endpoint contains <c>Required scope: </c> followed by
/// the literal the constant it cites resolves to, and an anonymous one contains
/// <c>No OIDC scope:</c> followed by what authenticates it instead.
/// </para>
///
/// <para>
/// <b>The second rule, spec 085 (issue 2113): an endpoint that needs a scope
/// declares the refusal that scope produces.</b> Every mapping whose effective
/// authorization is a scope must also carry
/// <c>.ProducesProblem(StatusCodes.Status403Forbidden)</c> in its own chain, so
/// the generated document has a branch for the answer a wrong-scope token
/// actually gets instead of an assertion that it cannot happen. The two rules
/// share this file because they share one antecedent — the effective
/// authorization of a mapping, resolved chain-then-group — and that resolution
/// exists here and nowhere else. <c>PreconditionDeclarationTests</c> holds its
/// 428 and its 400 together for the same reason: the organising principle is the
/// antecedent, not the status.
/// </para>
///
/// <para>
/// <b>Measured on this branch, 2026-09-07.</b> 56 mappings in 12 files: 54
/// resolve to a scope, 2 are anonymous. <b>28 of the 54 declare no authorization
/// in their own chain at all</b> and are in the population only through their
/// <c>MapGroup</c> — which is why a rule written as a per-chain grep finds 26 and
/// calls the other 28 unscoped. 38 chains declared a 403 before spec 085 (37
/// scoped, 1 anonymous), leaving <b>17 undeclared across 5 files in 3
/// contexts</b>: Identity 8, OverlayDesigner 8, AuditObservability 1. Zero of the
/// 17 groups declared one.
/// </para>
///
/// <para>
/// <b>Three things produce a 403 in this system, and only the first is this
/// rule's antecedent.</b> (1) The scope policy — <c>AddScopePolicies</c> builds
/// every <c>sse.*</c> policy as <c>RequireAuthenticatedUser()</c> plus a claim
/// assertion, so an authenticated caller who fails the assertion is forbidden
/// rather than challenged; this reaches all 54. (2) The fab guard —
/// <c>IFabAuthorizationGuard</c> throws and
/// <c>FabAuthorizationExceptionHandler</c> writes
/// <c>403 RESOURCE_FAB_NOT_AUTHORIZED</c>. (3) A handler result whose
/// <c>ApiError</c> carries <c>HttpStatusCode.Forbidden</c> (ADR-0089) — one route
/// today, <c>POST /streams/authorize</c>.
/// </para>
///
/// <para>
/// <b>So the refusal rule runs one way, and has no mirror.</b> Nothing here says
/// an unscoped mapping must <em>not</em> declare a 403.
/// <c>POST /streams/authorize</c> is <c>AllowAnonymous</c> and declares one
/// correctly, from producer 3; a mirror would fail on correct code, and a mirror
/// carved around that one route would be a register where none is needed. The
/// same call <c>PreconditionDeclarationTests</c> records for its 400.
/// </para>
///
/// <para>
/// <b>A rule written to <c>RequireScope</c>'s name would have passed
/// vacuously.</b> The extension exists —
/// <c>src/ServiceDefaults/Authorization/RequireScopeExtensions.cs</c> — and
/// forwards to <c>RequireAuthorization</c>, and <em>no endpoint under
/// <c>src/</c> calls it</em>: all 56 mappings spell it
/// <c>.RequireAuthorization(Scope.…)</c>. A guard matching that name would match
/// nothing and stay green for ever. <see cref="FirstAuthorizationCall"/> reads
/// both spellings, which is why the population read here is the real one.
/// </para>
///
/// <para>
/// <b>What the refusal rule provably cannot do</b>, stated so a green run is not
/// read as more than it is. <i>It never observes a 403</i> — it compares source
/// against source. The only observation in this repository is
/// <c>tests/Integration.Tests/SystemVariables/VariableReadScopeIntegrationTests.cs</c>,
/// on three <c>/system-variables</c> reads; the other 51 are derived from the one
/// <c>AddScopePolicies(Scope.All)</c> registration all nine APIs share.
/// <i>It never reads the emitted OpenAPI document</i> — and neither does
/// <c>PreconditionDeclarationTests</c> nor
/// <c>ConcurrencyConflictDeclarationTests</c>, so if ASP.NET stopped honouring
/// <c>ProducesProblem</c> all three would stay green. <i>It sees only the
/// population it can parse</i> — the 56 chain mappings plus the one registered
/// hub. <i>And it is single-producer</i>: a future anonymous route that refuses
/// through producer 3 and declares nothing is invisible to it, which is the
/// shape of #91 one producer along.
/// </para>
///
/// <para>
/// <b>Both registers are derived, neither is typed in.</b> The scope literals
/// come from reflection over <see cref="Scope"/>'s nested constants, not from
/// <c>Scope.All</c> — <c>All</c> is a hand-maintained list in the same file, and
/// a guard checked against it would agree with a list rather than with the
/// constants the endpoints actually cite. The endpoints come from a glob, never
/// from a list of names, and the count is checked against a sweep of every
/// <c>.cs</c> file under <c>src/*/Api</c>, so a mapping in a file <em>within
/// those directories</em> that the glob does not match is red rather than
/// silent.
/// </para>
///
/// <para>
/// <b>The size of the corpus is pinned, because the denominator alone does not
/// pin it.</b> <see cref="EndpointFileCount"/> and
/// <see cref="RouteHandlerMappingCount"/> are asserted exactly. The denominator
/// compares two numbers that are both computed from
/// <see cref="ContextApiDirectories"/>, so moving an endpoint file <em>out</em>
/// of <c>src/*/Api</c> shrinks both sides equally and stays green: a reviewer
/// moved <c>OverlayEndpoints.cs</c> to <c>src/OverlayDesigner/Web/</c> and the
/// suite reported <c>Failed: 0, Passed: 59</c> with eight endpoints no longer
/// checked. The two counts are the thing that turns that red. Adding an endpoint
/// therefore edits a number here, in the same diff — which is the point, not the
/// cost.
/// </para>
///
/// <para>
/// <b><see cref="UnenforcedByDesign"/> is a register checked both ways, which is
/// the opposite of an allow-list — do not simplify it into one.</b> An
/// allow-list is read in one direction: a row silences a failure and then sits
/// there forever, and the day the code is fixed the row is the only thing that
/// remembers a problem nobody has any more. This register is read in both. A
/// bare-authorization endpoint missing from it fails
/// (<see cref="Every_endpoint_that_enforces_no_scope_is_registered_against_an_open_issue"/>),
/// <em>and</em> a row that no longer matches a bare-authorization endpoint also
/// fails
/// (<see cref="Every_registered_route_still_enforces_no_scope"/>).
/// So the three rows it ships with — the #2070 routes, which have no scope to
/// name and therefore cannot satisfy the main rule — turn red the moment #2070
/// is fixed, and stay red until they are deleted. That is how this guard makes
/// #2070 build-visible without touching it. Adding a row is a reviewable line
/// in a diff that cites an open issue, not a suppression.
/// </para>
///
/// <para>
/// <b>Under-recognition is the failure mode, so it is reported by name.</b> A
/// mapping whose chain the guard cannot read is a mapping it does not check, and
/// a guard that quietly skips what it cannot parse is the guard that was not
/// there. An unreadable receiver, an unreadable authorization argument and an
/// unreadable summary argument each resolve to a <em>failure</em> naming the
/// mapping, never to a pass. Every endpoint file is asserted non-empty
/// individually rather than through one total, because a total lets one file
/// stop being read while the others carry the count.
/// </para>
///
/// <para>
/// <b>What this guard provably cannot catch.</b>
/// </para>
///
/// <para>
/// <i>It is a source scan, not a running application.</i> It reads the fluent
/// chain in the file. An endpoint mapped from a helper in another file, or a
/// scope chosen at run time from configuration, would be judged on the wrong
/// text or not at all. No such indirection exists today.
/// </para>
///
/// <para>
/// <i>The sweep is rooted at <c>src/*/Api</c>, so a route mapped anywhere else
/// is not seen at all.</i> This is a limit of where it looks, not of what it
/// reads, and the denominator assertion does not cover it — both of that
/// assertion's numbers come from the same roots. What covers it is the pinned
/// <see cref="EndpointFileCount"/> and <see cref="RouteHandlerMappingCount"/>:
/// a file that leaves the sweep makes the corpus smaller than the number
/// written here, which is red. A route mapped from a project that has no
/// <c>Api</c> directory at all is outside both, and nothing here would notice
/// it.
/// </para>
///
/// <para>
/// <i>Only <c>MapGet</c>, <c>MapPost</c>, <c>MapPut</c>, <c>MapPatch</c> and
/// <c>MapDelete</c> are read as endpoints.</i> <c>MapHub</c>,
/// <c>MapMethods</c>, <c>MapFallback</c> and the bare <c>Map</c> overload
/// register live routes and carry no chain this reader understands — a reviewer
/// inserted <c>reads.MapMethods("/sneaky", ["GET"], List)</c> into
/// <c>CameraEndpoints</c> and got <c>Failed: 0, Passed: 63</c>, because the
/// denominator counts the same regex on both sides and so can only catch "right
/// shape, wrong file", never "wrong shape, any file". They are covered instead
/// by <see cref="MappedOutsideTheChain"/>, a second register read in both
/// directions: an unregistered one fails
/// (<see cref="Every_route_mapped_outside_the_readable_shapes_is_registered"/>)
/// and a row matching nothing fails
/// (<see cref="Every_route_mapped_outside_the_readable_shapes_still_exists"/>).
/// The register can only describe a route whose authorization is an attribute on
/// a handler type, so any other shape is red until someone teaches the reader
/// it — which is the intended answer, not a gap.
/// </para>
///
/// <para>
/// <i>It cannot see policy composition, and today that is not hypothetical.</i>
/// <c>RequireAuthorization(scope)</c> is taken at face value as "requires that
/// scope". <c>AddScopePolicies</c> <em>does</em> map every <c>sse.*</c> policy
/// onto two acceptable claims — the scope itself <em>or</em>
/// <see cref="RequireScopeExtensions.LegacyManagementBundle"/>, the sole
/// exception being <see cref="Scope.Sse.Events.Publish"/>. So a token carrying
/// the bundle alone satisfies every scoped endpoint this guard checks, and every
/// <c>Required scope:</c> sentence it demands is true but not the whole truth.
/// Only <c>POST /streams/authorize</c> says so, because only there is the check
/// hand-rolled in the handler rather than delegated to the policy. That is
/// <c>KioskScopeParityTests</c>' territory, not this one.
/// </para>
///
/// <para>
/// <i>It checks that the sentence is present and consistent, not that the prose
/// around it is true.</i> A summary reading <c>Required scope:
/// sse.cameras.read</c> on an endpoint enforcing <c>sse.cameras.read</c> passes
/// however wrong the rest of the sentence is. Nothing here reads the
/// description.
/// </para>
///
/// <para>
/// <i>It says nothing about whether the scope is the</i> right <i>scope.</i> An
/// endpoint enforcing <c>sse.cameras.read</c> for a write is perfectly
/// consistent and perfectly wrong; only a human or a security review catches
/// that.
/// </para>
///
/// <para>
/// <i>It reads the group a mapping's receiver was assigned from, in the same
/// file, by variable name.</i> Two files declare two groups on the same route
/// prefix with different scopes — <c>RulesEndpoints</c> and
/// <c>CameraEndpoints</c> — so binding by prefix would be green and wrong. A
/// group assigned across files is not read and reports as unreadable.
/// </para>
///
/// <para>
/// <i>Binding is by name across the whole file, not per method body, so a name
/// declared twice is refused outright.</i> A bare reassignment was always
/// caught; a second <em>declaration</em> of the same name in a second method —
/// legal C#, and the obvious shape the day a file splits into <c>MapReads</c>
/// and <c>MapWrites</c> — was not, and the last declaration won for every
/// mapping in the file including those written above it. A reviewer added a
/// second <c>reads</c> group requiring <c>sse.events.write</c> and the three
/// real <c>/events</c> reads, which enforce <c>sse.events.read</c>, passed while
/// documented as writes: <c>Failed: 0, Passed: 63</c>. A duplicate declaration
/// is now an unreadable mapping rather than a silent rebind, so the file is red
/// until the second group is given its own name.
/// </para>
///
/// <para>
/// The residual is deliberate. This removes the failure mode that actually
/// occurred — a scope silently absent from both the chain and the prose — and
/// claims nothing more.
/// </para>
/// </summary>
public class EndpointScopeDeclarationTests
{
    private const string GuardSource = "tests/Architecture.Tests/EndpointScopeDeclarationTests.cs";
    private const string EndpointGlob = "src/*/Api/**/*Endpoints.cs";
    private const string ApiGlob = "src/*/Api/**/*.cs";

    /// <summary>
    /// The label, spelled exactly as the eighteen conformant endpoints spell it.
    /// No document declares it; the code is unanimous, which is the only
    /// authority available and a better one than a document nobody checks.
    /// </summary>
    private const string ScopeLabel = "Required scope: ";

    /// <summary>
    /// What an endpoint outside OIDC must say instead. Silent anonymity is the
    /// failure this prevents: both of today's anonymous endpoints are
    /// token-authenticated by other means and neither says so in metadata.
    /// </summary>
    private const string AnonymousLabel = "No OIDC scope:";

    private const string NoSummary = "(no .WithSummary in the chain)";

    /// <summary>
    /// How many files match <see cref="EndpointGlob"/>, and how many route
    /// handlers they register between them. Pinned rather than merely compared,
    /// because <see cref="Every_route_handler_mapping_under_the_api_projects_is_enumerated"/>
    /// computes both of its numbers from <see cref="ContextApiDirectories"/> and
    /// so cannot see a file that leaves those directories: moving
    /// <c>OverlayEndpoints.cs</c> out of <c>src/OverlayDesigner/Api</c> shrinks
    /// both sides together and stays green while eight endpoints go unchecked.
    /// Adding or removing an endpoint edits one of these numbers in the same
    /// diff as the endpoint.
    /// </summary>
    private const int EndpointFileCount = 12;

    private const int RouteHandlerMappingCount = 56;

    /// <summary>
    /// The routes that enforce no scope at all, each against the open issue that
    /// will fix it. <b>Read in both directions</b> — see the class doc: an
    /// unregistered scopeless route fails, and a row naming a route that now
    /// enforces a scope fails too.
    ///
    /// <para>
    /// <b>Empty today</b>, which is the honest record that nothing is currently
    /// deferred rather than a dormant mechanism. A row is an excuse checked
    /// against an open issue, not a permanent exemption: the day the scope lands
    /// the completeness half turns the row red, so the row is deleted in the same
    /// diff as the fix. That is what happened to the three <c>#2070</c> rows —
    /// <c>sse.variables.read</c> is now required by the three
    /// <c>/system-variables</c> reads (spec 073).
    /// </para>
    /// </summary>
    private static readonly UnenforcedRoute[] UnenforcedByDesign = [];

    /// <summary>
    /// The routes registered by a <c>Map*</c> call this reader does not parse as
    /// a chain, each with the handler type whose attribute declares its scope.
    /// <b>Read in both directions</b>, like <see cref="UnenforcedByDesign"/>: an
    /// unregistered one fails, and a row matching no call fails.
    ///
    /// <para>
    /// One row today. <c>app.MapHub&lt;LayoutLifecycleHub&gt;(…)</c> in
    /// <c>src/LayoutComposition/Api/Program.cs</c> registers <c>/hubs/layouts</c>
    /// — a live, authorized route whose scope is enforced by
    /// <c>[Authorize(Policy = …)]</c> on the hub class rather than by a fluent
    /// chain, and which appears in neither the 56 nor the OpenAPI surface. It is
    /// the #2070 shape this guard exists to make visible, and until this register
    /// existed the guard walked past it. It is registered rather than fixed
    /// because giving a SignalR hub a <c>.WithSummary</c> is not possible and
    /// changing how it authorizes is not behaviour-preserving; what this buys is
    /// that the route, its handler and its scope are now asserted rather than
    /// unmentioned.
    /// </para>
    ///
    /// <para>
    /// <b>It is outside the refusal rule as well, and for a reason rather than
    /// by accident of the glob.</b> A SignalR hub has no OpenAPI operation, so
    /// there is nothing for <c>ProducesProblem</c> to write into — the same
    /// reason it cannot carry a <c>.WithSummary</c>. It is named here rather
    /// than left to fall outside a pattern, because a route walked past because
    /// nobody parsed its shape is the failure this register was created to stop.
    /// The two directional checks above are unchanged, so a <em>second</em>
    /// route mapped outside the chain fails the build instead of inheriting this
    /// row's reasoning.
    /// </para>
    /// </summary>
    private static readonly RouteOutsideTheChain[] MappedOutsideTheChain =
    [
        new(
            "src/LayoutComposition/Api/Program.cs",
            "MapHub",
            "/hubs/layouts",
            typeof(LayoutLifecycleHub)),
    ];

    /// <summary>
    /// Every scope constant, by its full path and by its <c>Sse.</c>-prefixed
    /// tail. Built by reflection, so no scope string is typed into this file.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ScopeLiterals = BuildScopeCatalogue();

    /// <summary>
    /// A route-handler registration and the variable it was registered on. The
    /// receiver is captured because it, not the route prefix, is what binds a
    /// mapping to its group.
    /// </summary>
    private static readonly Regex MappingSite = new(
        @"\b(?<receiver>\w+)\s*\.Map(?<verb>Get|Post|Put|Patch|Delete)\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A route registration written in a shape <see cref="MappingSite"/> does
    /// not read: the ASP.NET routing primitives that are not verb methods, and
    /// the bare <c>Map</c> overload. Named exhaustively rather than matched
    /// loosely, because <c>Map*</c> is also how a bounded context's own
    /// registration extension is spelled — <c>app.MapCameraCatalogEndpoints()</c>
    /// maps nothing itself, and flagging ten of those would bury the one call
    /// that does.
    ///
    /// <para>
    /// The bare overload needs a receiver test as well: <c>Option&lt;T&gt;.Map</c>
    /// is spelled identically and is used nine times in these same files.
    /// <see cref="RouteBuilderNames"/> decides, so <c>key.Map(supplied =&gt; …)</c>
    /// is not a route and <c>reads.Map("/sneaky", List)</c> is.
    /// </para>
    /// </summary>
    private static readonly Regex OutsideTheChainSite = new(
        @"\b(?<receiver>\w+)\s*\.Map(?<kind>FallbackToFile|Fallback|Hub|Methods|GrpcService|HealthChecks|StaticAssets|RazorPages|RazorComponents)?\s*(?:<[^<>;]*>)?\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A local or parameter whose declared type makes it a thing routes can be
    /// mapped on. Group locals declared with <c>var</c> are added separately,
    /// from the declarations <see cref="LocalDeclaration"/> already finds.
    /// </summary>
    private static readonly Regex RouteBuilderDeclaration = new(
        @"\b(?:IEndpointRouteBuilder|WebApplication|RouteGroupBuilder)\s+(?<name>\w+)\b",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The declaration of a local that a group may have been assigned to. The
    /// statement is only treated as a group once it is seen to contain a
    /// <c>MapGroup</c> call.
    /// </summary>
    private static readonly Regex LocalDeclaration = new(
        @"\b(?:RouteGroupBuilder|var)\s+(?<name>\w+)\s*=",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex GroupSite = new(
        @"\.MapGroup\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The declaration a scoped endpoint owes its callers. Matched on the masked
    /// text, so a commented-out line is not credited and the words appearing
    /// inside a summary are not either.
    ///
    /// <para>
    /// The closing parenthesis is required, so an overload carrying a content
    /// type would not match. That is deliberate and safe in one direction only:
    /// the mapping walk and the flat sweep run the same pattern, so an
    /// unrecognised spelling is invisible to <em>both</em> and the endpoint reads
    /// as undeclared — red, and never a silent pass.
    /// </para>
    /// </summary>
    private static readonly Regex ForbiddenDeclaration = new(
        @"\.ProducesProblem\s*\(\s*StatusCodes\s*\.\s*Status403Forbidden\s*\)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A dotted constant path such as <c>Scope.Sse.Cameras.Write</c>. Anything
    /// else in the authorization argument is unreadable rather than guessed at.
    /// </summary>
    private static readonly Regex ConstantPath = new(
        @"^[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*$",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The scope a summary claims. Trailing punctuation is trimmed so a sentence
    /// that ends in a full stop is read as the scope it names, not as a mismatch.
    /// </summary>
    private static readonly Regex ClaimedScope = new(
        @"Required scope:\s*(?<claimed>[A-Za-z0-9._\-]+)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The content of a string literal, which is data rather than mechanism. The
    /// self-scan reads code, not the prose the code prints.
    /// </summary>
    private static readonly Regex StringLiteral = new(
        @"""(?:[^""\\\r\n]|\\.)*""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The endpoint files, found by glob and never named, one theory case each.
    /// A per-file case reports every offender in that file at once, which is
    /// what someone fixing a file wants; per-endpoint cases would produce
    /// thirty-five separate results and bury the shape of the problem.
    /// </summary>
    public static TheoryData<string> EndpointFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in EndpointSourceFiles())
        {
            data.Add(file);
        }

        return data;
    }

    /// <summary>
    /// <b>A1 — the denominator is not chosen by the guard, and the corpus does
    /// not shrink quietly.</b>
    ///
    /// <para>
    /// The mappings enumerated from <see cref="EndpointGlob"/> must account for
    /// every <c>Map*</c> registration in every <c>.cs</c> file under
    /// <c>src/*/Api</c>. An endpoint moved into a file the glob does not match
    /// is then red rather than quietly outside the sweep.
    /// </para>
    ///
    /// <para>
    /// That comparison alone is not enough, and this was demonstrated rather
    /// than reasoned about: both of its numbers are computed from
    /// <see cref="ContextApiDirectories"/>, so a file moved <em>out</em> of
    /// <c>src/*/Api</c> shrinks both together. <c>OverlayEndpoints.cs</c> moved
    /// to <c>src/OverlayDesigner/Web/</c> gave <c>Failed: 0, Passed: 59</c> —
    /// eight endpoints silently unguarded, four theory cases silently gone.
    /// <see cref="EndpointFileCount"/> and <see cref="RouteHandlerMappingCount"/>
    /// are the assertions that see it.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_route_handler_mapping_under_the_api_projects_is_enumerated()
    {
        string[] endpointFiles = EndpointSourceFiles();
        int swept = ApiSourceFiles().Sum(file => MappingSite.Count(Masked(ReadRepositoryFile(file))));
        int enumerated = endpointFiles.Sum(file => Read(file).Mappings.Length);

        endpointFiles.Length.ShouldBe(
            EndpointFileCount,
            $"{EndpointGlob} matched {endpointFiles.Length} files, not the {EndpointFileCount} this guard "
            + $"is pinned to:{Environment.NewLine}{string.Join(Environment.NewLine, endpointFiles)}"
            + $"{Environment.NewLine}Fewer means a file left the sweep — moved out of src/*/Api, or "
            + "renamed away from *Endpoints.cs — and every endpoint in it is now unchecked on a green "
            + "build, because the denominator below counts both of its numbers from the same directories "
            + "and shrinks with it. More means a new endpoint file the register and the counts have not "
            + "been told about. Either way, edit the number here in the same diff as the file.");

        swept.ShouldBe(
            RouteHandlerMappingCount,
            $"the sweep over {ApiGlob} found {swept} route handlers, not the {RouteHandlerMappingCount} "
            + "this guard is pinned to. An endpoint was added, removed or moved out of the swept "
            + "directories; the last of those is invisible to every other assertion here. Confirm the "
            + "corpus is what you meant it to be, then edit the number.");

        swept.ShouldBeGreaterThan(
            0,
            $"the sweep over {ApiGlob} found no route-handler mapping at all. Every assertion below is "
            + "then green because it read nothing, not because the endpoints are correct. The API "
            + "projects moved, or the sweep stopped matching — fix the sweep before trusting any result.");

        enumerated.ShouldBe(
            swept,
            $"{ApiGlob} registers {swept} route handlers, but the {EndpointGlob} sweep enumerated "
            + $"{enumerated}. A mapping this guard never enumerates is a mapping it never checks, and it "
            + "fails nothing on its way past — the endpoint would ship with no summary, no scope sentence "
            + "and a green build. Teach the sweep where the mapping now lives.");
    }

    /// <summary>
    /// <b>A1b — the scope catalogue is a real catalogue.</b>
    ///
    /// <para>
    /// The companion to A1. Reflection that stops finding constants would leave
    /// every scoped endpoint with an unresolvable scope, and while A3 reports
    /// that, it reports fifty-one endpoints when the fault is one — this says
    /// which of the two it is.
    /// </para>
    /// </summary>
    [Fact]
    public void The_scope_catalogue_is_read_from_the_constants_the_endpoints_cite()
    {
        ScopeLiterals.Keys.ShouldContain(
            $"{nameof(Scope)}.{nameof(Scope.Sse)}.{nameof(Scope.Sse.Cameras)}.{nameof(Scope.Sse.Cameras.Read)}",
            "reflection over ServiceDefaults.Authorization.Scope did not yield the constant path the "
            + "endpoints are written against. The catalogue moved or changed shape, and every scoped "
            + "endpoint would then resolve to nothing — which reads as fifty-one broken endpoints rather "
            + "than one broken reader.");

        ScopeLiterals.Values.Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThan(
            1,
            "the scope catalogue resolved to fewer than two distinct literals, so it cannot distinguish "
            + "the scope an endpoint enforces from any other. Walk the nested constants, not Scope.All.");
    }

    /// <summary>
    /// <b>A2 — nothing is dropped for being unreadable, and every file is read.</b>
    ///
    /// <para>
    /// The non-emptiness claim is per file rather than in aggregate: a single
    /// total lets one file stop being parsed while the eleven others carry the
    /// count, and the endpoints in it are then unguarded on a green build.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_mapping_in_an_endpoint_file_is_read_rather_than_skipped(string file)
    {
        EndpointFileReading reading = Read(file);

        reading.Mappings.ShouldNotBeEmpty(
            $"{file} matches {EndpointGlob} but yielded no route-handler mapping. Either it registers none "
            + "— in which case it is misnamed — or the chain reader stopped recognising the shape it is "
            + "written in, and every endpoint in it is now unchecked while the suite stays green.");

        reading.Unread.ShouldBeEmpty(
            $"the chain reader could not resolve these registrations in {file}: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, reading.Unread)}"
            + $"{Environment.NewLine}A registration it cannot read is one it does not check — it is not "
            + "treated as anonymous, and it is not treated as scoped, because guessing either way is how a "
            + "guard passes an endpoint it never looked at. Teach the reader the shape, or write the "
            + "registration in one of the shapes it reads.");
    }

    /// <summary>
    /// <b>A3 — every scope an endpoint cites exists in the catalogue.</b>
    ///
    /// <para>
    /// Resolved by reflection over <see cref="Scope"/>. A path that resolves to
    /// nothing is a mapping whose required sentence cannot even be computed, so
    /// it is reported here rather than silently excused from A4.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_scope_an_endpoint_enforces_resolves_against_the_catalogue(string file)
    {
        string[] unresolved = Read(file).Mappings
            .Where(mapping => mapping.Kind == AuthorizationKind.Scoped)
            .Where(mapping => !ScopeLiterals.ContainsKey(mapping.ScopeConstant))
            .Select(mapping => $"{Where(mapping)} cites {mapping.ScopeConstant}")
            .ToArray();

        unresolved.ShouldBeEmpty(
            "these endpoints require an authorization argument that is not a constant of "
            + $"ServiceDefaults.Authorization.Scope: {Environment.NewLine}"
            + $"{string.Join(Environment.NewLine, unresolved)}{Environment.NewLine}"
            + "The catalogue is read by reflection over the nested constants, not from Scope.All, so a "
            + "scope that is granted in the realm but never declared as a constant does not resolve here. "
            + "Declare the constant and cite it, so the scope the endpoint enforces and the scope the "
            + "summary names are the same string by construction.");
    }

    /// <summary>
    /// <b>A4 — a scoped endpoint names its scope.</b>
    ///
    /// <para>
    /// Omission only: a summary that names some <em>other</em> scope is A5's,
    /// and the two never fire on the same endpoint. A reader who gets one
    /// message for both learns less than the guard knows.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_scoped_endpoint_names_the_scope_it_enforces_in_its_summary(string file)
    {
        string[] silent = ScopedWithLiteral(file)
            .Where(pair => ClaimedScopes(pair.Mapping.Summary).Length == 0)
            .Select(pair => Omission(pair.Mapping, pair.Literal))
            .ToArray();

        silent.ShouldBeEmpty(
            "these endpoints enforce a scope their summary never names: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, silent)}{Environment.NewLine}"
            + "The required-scope catalogue is only legible at the surface if the surface says it. "
            + "Eighteen endpoints already spell the sentence exactly this way; nothing enforced it, and "
            + "the gap that opened is issue 2070 — a scope granted in the realm, absent from the chain, "
            + "and absent from prose that described everything else about the endpoint in detail. Append "
            + "the sentence; change no route, handler, policy or Produces declaration.");
    }

    /// <summary>
    /// <b>A5 — a summary that names the wrong scope is worse than one that names
    /// none.</b>
    ///
    /// <para>
    /// An omission leaves a reader to read the chain. A mismatch tells them not
    /// to bother, and is believed. Its own message, as the spec requires.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void No_summary_names_a_scope_other_than_the_one_the_endpoint_enforces(string file)
    {
        string[] misinforming = ScopedWithLiteral(file)
            .SelectMany(pair => ClaimedScopes(pair.Mapping.Summary)
                .Where(claimed => !string.Equals(claimed, pair.Literal, StringComparison.Ordinal))
                .Select(claimed => Mismatch(pair.Mapping, pair.Literal, claimed)))
            .ToArray();

        misinforming.ShouldBeEmpty(
            "these summaries name a scope the endpoint does not enforce: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, misinforming)}{Environment.NewLine}"
            + "This is strictly worse than saying nothing. A missing sentence sends a reader to the "
            + "authorization chain; a wrong one persuades them they have already read it, and they act on "
            + "a scope the endpoint will refuse — or, worse, grant a client a scope it did not need. "
            + "Correct the sentence, or correct the chain, but do not leave them disagreeing.");
    }

    /// <summary>
    /// <b>A6 — an endpoint that enforces no scope is registered against an open
    /// issue.</b>
    ///
    /// <para>
    /// A bare <c>.RequireAuthorization()</c> has no scope to name, so it cannot
    /// satisfy A4. It must therefore be visible some other way, and the register
    /// is that way: a reviewable line citing an issue, in a diff, rather than a
    /// silence.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_endpoint_that_enforces_no_scope_is_registered_against_an_open_issue()
    {
        string[] unregistered = AllMappings()
            .Where(mapping => mapping.Kind == AuthorizationKind.Bare)
            .Where(mapping => !UnenforcedByDesign.Any(row => Matches(row, mapping)))
            .Select(mapping => $"{Where(mapping)} — .RequireAuthorization() names no scope")
            .ToArray();

        unregistered.ShouldBeEmpty(
            "these endpoints require authentication and nothing else: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, unregistered)}{Environment.NewLine}"
            + "Any authenticated caller reaches them, whatever they were granted. Enforce a scope from the "
            + "catalogue, or add the route to the register in this file against the issue that will — the "
            + "register is read in both directions, so the row fails the build again the day the scope "
            + "lands and must be deleted with it.");
    }

    /// <summary>
    /// <b>A7 — the register is read the other way too.</b>
    ///
    /// <para>
    /// The half that makes the register not an allow-list. A row whose endpoint
    /// now enforces a scope, or no longer exists, fails here — so fixing #2070
    /// cannot leave a stale row behind, quietly recording a problem nobody has.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_registered_route_still_enforces_no_scope()
    {
        EndpointMapping[] mappings = AllMappings();

        string[] stale = UnenforcedByDesign
            .Where(row => !mappings.Any(mapping => mapping.Kind == AuthorizationKind.Bare && Matches(row, mapping)))
            .Select(row => $"{row.Verb} {row.Route} (issue {row.Issue})")
            .ToArray();

        stale.ShouldBeEmpty(
            "these register rows no longer match an endpoint that enforces no scope: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, stale)}{Environment.NewLine}"
            + "Either the route was fixed — in which case delete the row, and let the scope sentence be "
            + "demanded of it like every other scoped endpoint — or it moved, in which case the row is "
            + "pointing at nothing and the endpoint it was meant to cover is being checked by neither "
            + "half. A register that is only read in one direction is an allow-list with better manners.");
    }

    /// <summary>
    /// <b>A8 — an anonymous endpoint says what authenticates it instead.</b>
    ///
    /// <para>
    /// Both of today's two are reached with a bearer the handler validates
    /// itself, and neither says so. <c>AllowAnonymous</c> in a chain reads as
    /// "no authentication" to anyone who does not open the handler.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_anonymous_endpoint_says_what_authenticates_it_instead()
    {
        string[] silent = AllMappings()
            .Where(mapping => mapping.Kind == AuthorizationKind.Anonymous)
            .Where(mapping => mapping.Summary is null
                || !mapping.Summary.Contains(AnonymousLabel, StringComparison.Ordinal))
            .Select(mapping => Anonymity(mapping))
            .ToArray();

        silent.ShouldBeEmpty(
            "these endpoints are explicitly anonymous and do not say why: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, silent)}{Environment.NewLine}"
            + "AllowAnonymous in a chain reads as unauthenticated to every reader who does not open the "
            + "handler, and both of these are in fact reached with a bearer the handler validates itself. "
            + "Say so in the summary, so the surface no longer looks like a hole.");
    }

    /// <summary>
    /// <b>A8b — the WHEP hook's sentence is pinned to the constants it is about.</b>
    ///
    /// <para>
    /// <see cref="Every_anonymous_endpoint_says_what_authenticates_it_instead"/>
    /// asks only whether the label is present, so <c>"No OIDC scope: trust me"</c>
    /// would satisfy it. This endpoint's sentence names two scopes the handler
    /// hand-rolls, and both are read here as constants rather than typed, so the
    /// day either moves the sentence is red instead of quietly false.
    /// </para>
    /// </summary>
    [Fact]
    public void The_whep_hook_summary_names_the_two_scopes_its_handler_accepts()
    {
        string required = PrivateConstant(typeof(AuthorizeWhepCommandHandler), "RequiredScope");
        string bundle = RequireScopeExtensions.LegacyManagementBundle;
        EndpointMapping hook = AnonymousMapping("POST", "/streams/authorize");
        string summary = SummaryOf(hook);

        summary.ShouldContain(
            required,
            Case.Sensitive,
            $"AuthorizeWhepCommandHandler requires '{required}', and the summary that tells a reader what "
            + $"admits them does not name it: {Quoted(summary)}. The endpoint is anonymous, so this "
            + "sentence is the only place the surface says what the call is checked against.");

        summary.ShouldContain(
            bundle,
            Case.Sensitive,
            $"the handler also accepts '{bundle}' and the summary no longer names it: "
            + $"{Quoted(summary)}. If the bundle was withdrawn, delete the clause with it; "
            + "AuthenticationDefaults.AdminPolicy is already marked obsolete, so this sentence is on a "
            + "path to becoming false without anything failing.");
    }

    /// <summary>
    /// <b>A8c — the webhook sentence's "by default" is pinned to the default.</b>
    ///
    /// <para>
    /// The summary tells a reader that an unrotated integration is validated by
    /// SHA-256 against a stored hash. That is true only while
    /// <see cref="BearerValidationMode.StaticHash"/> is the enum's zero value and
    /// so the persisted default. Flip the enum and the sentence is false with
    /// nothing red, which is the rot this pins.
    /// </para>
    /// </summary>
    [Fact]
    public void The_webhook_ingest_summary_describes_the_validation_mode_that_is_actually_the_default()
    {
        EndpointMapping ingest = AnonymousMapping("POST", "/events/webhook/{integrationName}");
        string summary = SummaryOf(ingest);

        default(BearerValidationMode).ShouldBe(
            BearerValidationMode.StaticHash,
            "the webhook ingest summary says the token is validated 'by default' as a SHA-256 match "
            + "against the stored hash. That is the summary describing BearerValidationMode's zero "
            + "value, which is what an integration has until it is rotated. It is no longer the zero "
            + "value, so the sentence now describes the wrong branch — amend it with the enum.");

        summary.ShouldContain(
            "by default",
            Case.Sensitive,
            "the summary no longer distinguishes the default validation mode from the rotated one: "
            + $"{Quoted(summary)}. Both branches are live and they check entirely different things; a "
            + "reader who cannot tell which one applies to their integration has been told nothing "
            + "useful.");

        summary.ShouldContain(
            ScopeLiterals[$"{nameof(Scope)}.{nameof(Scope.Sse)}.{nameof(Scope.Sse.Events)}."
                + nameof(Scope.Sse.Events.Write)],
            Case.Sensitive,
            "the summary no longer names the scope the rotated, JWT branch demands: "
            + $"{Quoted(summary)}. The literal is read from the catalogue here, so this fails when the "
            + "constant moves rather than when someone remembers to check.");
    }

    /// <summary>
    /// <b>A10 — a route mapped in a shape the reader does not parse is registered.</b>
    ///
    /// <para>
    /// The other half of the denominator, and the half that was missing. A1
    /// compares <see cref="MappingSite"/> against itself, so it catches "right
    /// shape, wrong file" and never "wrong shape, any file" — a reviewer added a
    /// live <c>MapMethods</c> endpoint to <c>CameraEndpoints</c> and the suite
    /// stayed green at 63. Every <c>MapHub</c>, <c>MapMethods</c>,
    /// <c>MapFallback</c> and bare <c>Map</c> under <c>src/*/Api</c> must now
    /// appear in <see cref="MappedOutsideTheChain"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_route_mapped_outside_the_readable_shapes_is_registered()
    {
        string[] unregistered = OutsideTheChainSites()
            .Where(site => !MappedOutsideTheChain.Any(row => Matches(row, site)))
            .Select(site => $"{site.File}:{site.Line}  .{site.Call}(…)")
            .ToArray();

        unregistered.ShouldBeEmpty(
            "these calls register a route this guard's chain reader does not parse: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, unregistered)}{Environment.NewLine}"
            + "They are live routes with live authorization, and A1's denominator cannot see them — it "
            + "counts the verb-method regex on both sides, so a shape that regex does not know is absent "
            + "from both. Write the endpoint as one of the five verb methods so the rest of this guard "
            + "reads it, or add a row to the register above naming the handler type whose attribute "
            + "declares its scope. The register is read in both directions and cannot describe a route "
            + "whose authorization is not an attribute on a type, which is deliberate: any other shape "
            + "stays red until someone teaches this reader it.");
    }

    /// <summary>
    /// <b>A11 — that register is read the other way too.</b>
    /// </summary>
    [Fact]
    public void Every_route_mapped_outside_the_readable_shapes_still_exists()
    {
        OutsideTheChainMapping[] sites = OutsideTheChainSites();

        string[] stale = MappedOutsideTheChain
            .Where(row => !sites.Any(site => Matches(row, site)))
            .Select(row => $"{row.File}  .{row.Call}(…)  {row.Route}")
            .ToArray();

        stale.ShouldBeEmpty(
            "these register rows no longer match a route mapped in this file: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, stale)}{Environment.NewLine}"
            + "Either the route was deleted — delete the row with it — or it moved, and the row is now "
            + "pointing at nothing while the route it described is covered by neither half.");

        sites.Length.ShouldBe(
            MappedOutsideTheChain.Length,
            $"{sites.Length} routes are mapped outside the readable shapes and "
            + $"{MappedOutsideTheChain.Length} are registered. The two lists agree file by file, so the "
            + "difference is a second call in a file that already has a row — which the row would "
            + "silently cover. Give it its own row.");
    }

    /// <summary>
    /// <b>A12 — a registered route still enforces a scope from the catalogue.</b>
    ///
    /// <para>
    /// A row is a claim that the route's scope is declared somewhere this guard
    /// can check, not a place to record that it is not. The policy is read off
    /// the handler type by reflection and looked up in the same catalogue every
    /// scoped endpoint is measured against, so a hub that loses its attribute —
    /// or gains a policy name that is not a scope — is red.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_route_mapped_outside_the_readable_shapes_declares_a_catalogue_scope()
    {
        foreach (RouteOutsideTheChain row in MappedOutsideTheChain)
        {
            string policy = DeclaredPolicy(row.Handler);

            policy.ShouldNotBeNullOrEmpty(
                $"{row.Route} is mapped by {row.Handler.Name}, and that type carries no "
                + "[Authorize(Policy = …)]. The route has no chain to read, so the attribute is the "
                + "only place its scope is declared — without it the route is reachable by any "
                + "authenticated caller and says so nowhere.");

            ScopeLiterals.Values.ShouldContain(
                policy,
                $"{row.Handler.Name} requires policy '{policy}', which is not a constant of "
                + "ServiceDefaults.Authorization.Scope. A policy name that is not a scope in the "
                + "catalogue is the #2070 shape: granted somewhere, required here, and reconciled "
                + "nowhere.");

            DeclaredPath(row.Handler).ShouldBe(
                row.Route,
                $"the register says {row.Handler.Name} is mapped at {row.Route}, and the constant on the "
                + "type says otherwise. The route in the register is the only part of the row a reader "
                + "can use without opening the file, so it is checked against the type rather than "
                + "trusted.");
        }
    }

    /// <summary>
    /// <b>A13 — a scoped endpoint declares the refusal its scope produces.</b>
    ///
    /// <para>
    /// The antecedent is not typed in and neither is the population: it is
    /// <see cref="ScopedWithLiteral"/>, the same resolution
    /// <see cref="Every_scoped_endpoint_names_the_scope_it_enforces_in_its_summary"/>
    /// uses — the chain if it declares authorization, otherwise the
    /// <c>MapGroup</c> it was written on, bound by receiver name. Twenty-eight of
    /// the fifty-four are in this population only through that inheritance, so a
    /// rule that read the chain alone would check twenty-six routes and call the
    /// rest unscoped. Delete a route's <c>RequireAuthorization</c> and it leaves
    /// this population in the same edit — which is what makes this a derivation
    /// rather than a list of routes wearing a build failure's clothes.
    /// </para>
    ///
    /// <para>
    /// A constant that resolves to nothing is A3's to report and is left out
    /// here, so one cause does not fail twice.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointFiles))]
    public void Every_scoped_endpoint_declares_the_refusal_its_scope_produces(string file)
    {
        string[] undeclared = ScopedWithLiteral(file)
            .Where(pair => pair.Mapping.ForbiddenDeclarations == 0)
            .Select(pair => Undeclared(pair.Mapping, pair.Literal))
            .ToArray();

        undeclared.ShouldBeEmpty(
            "these endpoints sit behind a scope and never declare the refusal that scope produces: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, undeclared)}{Environment.NewLine}"
            + "AddScopePolicies builds every sse.* policy as RequireAuthenticatedUser() plus a claim "
            + "assertion, so a caller who authenticates and does not hold the scope is forbidden, not "
            + "challenged. The document currently tells a client author that answer cannot happen, and a "
            + "generated client has no branch for it. Add the declaration to the mapping's own chain; "
            + "change no route, scope, summary or handler.");
    }

    /// <summary>
    /// <b>A14 — no route group declares the refusal its members must declare.</b>
    ///
    /// <para>
    /// This is not a style rule, it is what keeps A13 sound. OpenAPI
    /// <em>inherits</em> group metadata, so a 403 on a <c>MapGroup</c> would make
    /// every operation under it correct while A13 still demanded a per-chain line
    /// from each — a guard failing correct code. Failing on the group instead
    /// says the shape is refused and why, rather than accusing eight endpoints of
    /// an omission they do not have.
    /// </para>
    ///
    /// <para>
    /// Zero of the seventeen groups declare one today, so this begins green and
    /// stays green unless someone reaches for the eight lines it would save.
    /// </para>
    /// </summary>
    [Fact]
    public void No_route_group_declares_the_refusal_its_members_must_declare()
    {
        string[] declaring = EndpointSourceFiles()
            .SelectMany(file => Read(file).Groups)
            .Where(group => group.ForbiddenDeclarations > 0)
            .Select(group => $"{group.File}:{group.Line}  MapGroup(\"{group.Prefix}\") on '{group.Name}'")
            .ToArray();

        declaring.ShouldBeEmpty(
            "these route groups declare the 403 their members are required to declare: "
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, declaring)}{Environment.NewLine}"
            + "The resulting document would be correct — OpenAPI inherits group metadata — and that is "
            + "the problem: the rule above reads each mapping's own chain, so every endpoint under this "
            + "group is then reported as omitting a declaration it effectively has, and every endpoint "
            + "added under it later inherits a declaration nobody wrote for it. One shape or the other, "
            + "not both. Move the declaration back into each chain, or change both assertions together "
            + "and say why.");
    }

    /// <summary>
    /// <b>A15 — every refusal declaration under these directories is one the
    /// walk can see.</b>
    ///
    /// <para>
    /// The mapping walk plus the group walk must account for every
    /// <c>Status403Forbidden</c> declaration a flat sweep of <c>src/*/Api</c>
    /// finds. Both read 38 before spec 085 and 55 after, and the assertion is the
    /// agreement rather than either number — both move legitimately with any
    /// endpoint.
    /// </para>
    ///
    /// <para>
    /// Without it A13's sharpest edge is silent: a declaration hoisted into a
    /// shared convention, an endpoint filter or a metadata helper reads to the
    /// walk as no declaration at all, and A13 would then be reporting an
    /// omission that is not there. The property is <c>PaginatedConsumerTests</c>',
    /// borrowed by <c>PreconditionDeclarationTests</c>, borrowed again here. The
    /// sweep runs over the wider <c>src/*/Api/**/*.cs</c> glob deliberately: a
    /// declaration in an Api file that is not an endpoint file should make the
    /// two disagree.
    /// </para>
    /// </summary>
    [Fact]
    public void The_refusal_declarations_the_walk_finds_are_all_the_ones_there_are()
    {
        int swept = ApiSourceFiles()
            .Sum(file => ForbiddenDeclaration.Count(Masked(ReadRepositoryFile(file))));
        EndpointFileReading[] readings = EndpointSourceFiles().Select(Read).ToArray();
        int walked = readings.Sum(reading =>
            reading.Mappings.Sum(mapping => mapping.ForbiddenDeclarations)
            + reading.Groups.Sum(group => group.ForbiddenDeclarations));

        swept.ShouldBeGreaterThan(
            0,
            $"no Status403Forbidden declaration was found anywhere under {ApiGlob}. Thirty-eight chains "
            + "declared one before spec 085 and fifty-five after, so zero means the sweep is reading "
            + "nothing — most likely the declaration is now spelled some way this pattern does not "
            + "match, in which case the rule above is green because it checked no text at all.");

        walked.ShouldBe(
            swept,
            $"the walk found {walked} Status403Forbidden declarations inside mapping and group chains; a "
            + $"flat sweep of {ApiGlob} found {swept}. A declaration the walk cannot see is a declaration "
            + "this guard does not credit: it sits outside the fluent chain the reader captures — in a "
            + "shared convention, an endpoint filter, a metadata helper — and the rule above would report "
            + "its endpoint as declaring nothing while the document is in fact correct. Put it in the "
            + "mapping's own chain, or teach the reader the shape.");
    }

    /// <summary>
    /// <b>A9 — the gate has no soft edge.</b>
    ///
    /// <para>
    /// <see cref="UnenforcedByDesign"/> is the one escape, and it is checked in
    /// both directions, cites an issue, and shows up in a diff. A second one
    /// added here — a list of files to skip, a rule to suppress — would be
    /// silent in every direction, and ADR-0144 names reaching green by weakening
    /// a gate as a blocked outcome rather than a judgement call.
    /// </para>
    ///
    /// <para>
    /// It reads code, not prose: comment lines, attribute lines and the content
    /// of string literals are outside the scan, because prose about this rule
    /// necessarily uses this rule's vocabulary and a scan that read it would
    /// turn a wording change red. It polices a vocabulary, not a mechanism —
    /// someone who names the same thing differently walks past it. That is a
    /// fair price for making the obvious move loud, and it is not a proof.
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
        string[] offenders = ExecutableLines(ReadRepositoryFile(GuardSource))
            .Where(line => line.Contains(mechanism, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"the guard's own code names '{mechanism}': {string.Join(" | ", offenders)}. That reads as a "
            + "way to excuse an endpoint from the rule, and a rule with a soft edge is a review convention "
            + "wearing a build failure's clothes. The one escape is the register above, which is read in "
            + "both directions and cites an open issue; a second one that is read in neither is how a "
            + "convention goes quiet again.");
    }

    // ---- reading the chain -------------------------------------------------

    /// <summary>
    /// Every mapping in every endpoint file, in file order.
    /// </summary>
    private static EndpointMapping[] AllMappings() =>
        EndpointSourceFiles().SelectMany(file => Read(file).Mappings).ToArray();

    /// <summary>
    /// The scoped mappings of one file paired with the literal their constant
    /// resolves to. A constant that resolves to nothing is A3's to report, so it
    /// is left out here rather than failing twice for one cause.
    /// </summary>
    private static ScopedEndpoint[] ScopedWithLiteral(string file) =>
        Read(file).Mappings
            .Where(mapping => mapping.Kind == AuthorizationKind.Scoped)
            .Where(mapping => ScopeLiterals.ContainsKey(mapping.ScopeConstant))
            .Select(mapping => new ScopedEndpoint(mapping, ScopeLiterals[mapping.ScopeConstant]))
            .ToArray();

    /// <summary>
    /// Parses one endpoint file into its groups, its mappings and whatever it
    /// could not resolve. Comments are blanked before anything else, so a
    /// semicolon or a <c>Map*</c> sample inside one changes no result.
    /// </summary>
    private static EndpointFileReading Read(string file)
    {
        string text = WithoutComments(ReadRepositoryFile(file));
        string masked = MaskLiterals(text);

        Dictionary<string, RouteGroup> groups = new(StringComparer.Ordinal);
        List<GroupDeclaration> declared = [];
        List<(int From, int To)> declarations = [];
        List<string> unread = [];

        foreach (Match declaration in LocalDeclaration.Matches(masked))
        {
            int end = StatementEnd(masked, declaration.Index);
            string statement = masked[declaration.Index..end];
            if (!GroupSite.IsMatch(statement))
            {
                continue;
            }

            string name = declaration.Groups["name"].Value;
            declarations.Add((declaration.Index, end));
            int forbidden = ForbiddenDeclarationsIn(masked, declaration.Index, end);
            string prefix = FirstLiteral(text, masked, declaration.Index, end);
            declared.Add(new GroupDeclaration(
                file,
                LineAt(text, declaration.Index),
                name,
                prefix,
                forbidden));

            // Binding is by name across the whole file, so a name declared a
            // second time — in a second method, which is legal C# — would rebind
            // every mapping written on it, including the ones above. Reached in
            // review: three /events reads enforcing sse.events.read passed while
            // documented as sse.events.write. Refused rather than resolved.
            if (groups.ContainsKey(name))
            {
                unread.Add($"{file}:{LineAt(text, declaration.Index)} — '{name}' is declared as a route "
                    + "group a second time in this file. A mapping is bound to its group by name across "
                    + "the whole file, not per method, so this declaration would rebind every mapping "
                    + "written on that name — including the ones above it, whose scope would then be "
                    + "read from a group they never touched. Give this group its own name.");
                groups[name] = new RouteGroup(
                    name,
                    string.Empty,
                    new EnforcedAuthorization(
                        AuthorizationKind.Unreadable,
                        $"its receiver '{name}' names more than one route group in this file, so which "
                            + "group's authorization it inherits cannot be established"));
                continue;
            }

            groups[name] = new RouteGroup(
                name,
                prefix,
                DeclaredAuthorization(text, masked, declaration.Index, end));
        }

        foreach (Match site in GroupSite.Matches(masked))
        {
            if (!declarations.Any(span => span.From < site.Index && site.Index < span.To))
            {
                unread.Add($"{file}:{LineAt(text, site.Index)} — a MapGroup call outside a "
                    + "RouteGroupBuilder or var declaration this reader recognises, so no mapping can "
                    + "inherit the authorization it declares");
            }
        }

        List<EndpointMapping> mappings = [];
        foreach (Match site in MappingSite.Matches(masked))
        {
            EndpointMapping mapping = ReadMapping(file, text, masked, site, groups);
            mappings.Add(mapping);
            if (mapping.Problem is not null)
            {
                unread.Add($"{Where(mapping)} — {mapping.Problem}");
            }
        }

        return new EndpointFileReading(file, [.. mappings], [.. declared], [.. unread]);
    }

    private static EndpointMapping ReadMapping(
        string file,
        string text,
        string masked,
        Match site,
        Dictionary<string, RouteGroup> groups)
    {
        int start = site.Index;
        int end = StatementEnd(masked, start);
        string receiver = site.Groups["receiver"].Value;
        string verb = site.Groups["verb"].Value.ToUpperInvariant();

        int open = masked.IndexOf('(', site.Index + site.Length - 1);
        string route = FirstLiteralIn(text, masked, open, end);

        groups.TryGetValue(receiver, out RouteGroup? group);
        string fullRoute = JoinRoute(group?.Prefix ?? string.Empty, route);
        int line = LineAt(text, start);

        (string? summary, bool summaryUnreadable) = DeclaredSummary(text, masked, start, end);
        EnforcedAuthorization? own = DeclaredAuthorization(text, masked, start, end);
        EnforcedAuthorization effective = own ?? group?.Authorization
            ?? new EnforcedAuthorization(AuthorizationKind.Unreadable, ReceiverProblem(receiver, group));

        string? unreadableSummary = summaryUnreadable
            ? "its .WithSummary argument is not a plain string literal or a concatenation of them, so "
                + "the sentence it declares cannot be read"
            : null;

        string? problem = effective.Kind == AuthorizationKind.Unreadable
            ? effective.Detail
            : unreadableSummary;

        return new EndpointMapping(
            file,
            line,
            verb,
            fullRoute,
            effective.Kind,
            effective.Kind == AuthorizationKind.Scoped ? effective.Detail : string.Empty,
            summary,
            ForbiddenDeclarationsIn(masked, start, end),
            problem);
    }

    private static string ReceiverProblem(string receiver, RouteGroup? group) =>
        group is null
            ? $"its receiver '{receiver}' is not a route group declared in this file, so no group "
                + "authorization can be inherited and the chain declares none of its own"
            : "neither the chain nor its group declares any authorization, so the endpoint is "
                + "reachable without a token and says nothing about it";

    /// <summary>
    /// The authorization a chain declares, or <c>null</c> when it declares none
    /// and must inherit. An endpoint-level <c>RequireAuthorization</c> wins over
    /// the group's, which is how <c>SystemVariableEndpoints</c>' three writes
    /// override a group that names no scope.
    ///
    /// <para>
    /// <c>RequireScope</c> is read as the same call, because it is one — the
    /// extension in <c>RequireScopeExtensions</c> forwards to
    /// <c>RequireAuthorization</c>. Nothing in <c>src/</c> uses it today, and a
    /// reader that did not know it would take the group's scope for the
    /// endpoint's.
    /// </para>
    ///
    /// <para>
    /// More than one such call in a chain is unreadable rather than resolved to
    /// the first. ASP.NET requires <em>all</em> of them, so a chain carrying both
    /// a bare call and a scoped one enforces the scope, while reading the first
    /// alone would report it as naming none and demand a register row it must
    /// not have.
    /// </para>
    /// </summary>
    private static EnforcedAuthorization? DeclaredAuthorization(string text, string masked, int start, int end)
    {
        int require = FirstAuthorizationCall(masked, start, end);
        if (require >= 0)
        {
            if (FirstAuthorizationCall(masked, require + 1, end) >= 0)
            {
                return new EnforcedAuthorization(
                    AuthorizationKind.Unreadable,
                    "its chain declares authorization more than once and ASP.NET applies all of them, "
                        + "so no single call names what the endpoint enforces");
            }

            int open = masked.IndexOf('(', require);
            (int from, int to) = ArgumentSpan(masked, open);
            string argument = text[from..to].Trim();

            if (argument.Length == 0)
            {
                return new EnforcedAuthorization(AuthorizationKind.Bare, string.Empty);
            }

            return ConstantPath.IsMatch(argument)
                ? new EnforcedAuthorization(AuthorizationKind.Scoped, Compact(argument))
                : new EnforcedAuthorization(
                    AuthorizationKind.Unreadable,
                    $"its authorization argument '{argument}' is not a constant path, so the "
                        + "scope it enforces cannot be resolved");
        }

        return IndexOfCall(masked, ".AllowAnonymous", start, end) >= 0
            ? new EnforcedAuthorization(AuthorizationKind.Anonymous, string.Empty)
            : null;
    }

    /// <summary>
    /// The first call in a span that declares authorization, whichever of the
    /// two spellings it uses.
    /// </summary>
    private static int FirstAuthorizationCall(string masked, int start, int end)
    {
        int authorization = IndexOfCall(masked, ".RequireAuthorization", start, end);
        int scope = IndexOfCall(masked, ".RequireScope", start, end);

        if (authorization < 0)
        {
            return scope;
        }

        return scope < 0 ? authorization : Math.Min(authorization, scope);
    }

    /// <summary>
    /// The summary text a chain declares, and whether the declaration was
    /// readable. A missing <c>WithSummary</c> is <c>null</c> and readable; an
    /// interpolated one is unreadable rather than guessed at.
    /// </summary>
    private static (string? Summary, bool Unreadable) DeclaredSummary(
        string text,
        string masked,
        int start,
        int end)
    {
        int call = IndexOfCall(masked, ".WithSummary", start, end);
        if (call < 0)
        {
            return (null, false);
        }

        int open = masked.IndexOf('(', call);
        (int from, int to) = ArgumentSpan(masked, open);

        return TryJoinLiterals(text[from..to], out string joined) ? (joined, false) : (null, true);
    }

    /// <summary>
    /// The first string literal in a statement — a group's route prefix.
    /// </summary>
    private static string FirstLiteral(string text, string masked, int start, int end)
    {
        int call = IndexOfCall(masked, ".MapGroup", start, end);
        return call < 0 ? string.Empty : FirstLiteralIn(text, masked, masked.IndexOf('(', call), end);
    }

    private static string FirstLiteralIn(string text, string masked, int open, int end)
    {
        if (open < 0 || open >= end)
        {
            return string.Empty;
        }

        (int from, int to) = ArgumentSpan(masked, open);
        int quote = masked.IndexOf('"', from);

        if (quote < 0 || quote >= to)
        {
            return string.Empty;
        }

        int close = EndOfLiteral(masked, quote, verbatim: false);
        return Unescape(text[(quote + 1)..(close - 1)]);
    }

    /// <summary>
    /// How many refusal declarations a span carries. A count rather than a flag,
    /// so <see cref="The_refusal_declarations_the_walk_finds_are_all_the_ones_there_are"/>
    /// compares like with like: a chain that declares the same thing twice is
    /// odd but legal, and a flag would make the walk read one where the sweep
    /// reads two and fail on the wrong thing.
    /// </summary>
    private static int ForbiddenDeclarationsIn(string masked, int start, int end) =>
        ForbiddenDeclaration.Count(masked[start..end]);

    private static string[] ClaimedScopes(string? summary) =>
        summary is null
            ? []
            : ClaimedScope.Matches(summary)
                .Select(match => match.Groups["claimed"].Value.TrimEnd('.'))
                .ToArray();

    private static bool Matches(UnenforcedRoute row, EndpointMapping mapping) =>
        string.Equals(row.Verb, mapping.Verb, StringComparison.Ordinal)
        && string.Equals(row.Route, mapping.Route, StringComparison.Ordinal);

    private static bool Matches(RouteOutsideTheChain row, OutsideTheChainMapping site) =>
        string.Equals(row.File, site.File, StringComparison.Ordinal)
        && string.Equals(row.Call, site.Call, StringComparison.Ordinal);

    /// <summary>
    /// The one anonymous mapping at a verb and route, asserted to be exactly one
    /// so a sentence is never checked against a mapping that has moved.
    /// </summary>
    private static EndpointMapping AnonymousMapping(string verb, string route)
    {
        EndpointMapping[] found = AllMappings()
            .Where(mapping => mapping.Kind == AuthorizationKind.Anonymous)
            .Where(mapping => string.Equals(mapping.Verb, verb, StringComparison.Ordinal)
                && string.Equals(mapping.Route, route, StringComparison.Ordinal))
            .ToArray();

        found.Length.ShouldBe(
            1,
            $"expected exactly one .AllowAnonymous() mapping at {verb} {route}, found {found.Length}. "
            + "The endpoint moved, changed route, or stopped being anonymous — and the sentence this "
            + "test pins to its handler's constants is now describing something else.");

        return found[0];
    }

    private static string SummaryOf(EndpointMapping mapping)
    {
        string? summary = mapping.Summary;

        summary.ShouldNotBeNull(
            $"{Where(mapping)} declares no .WithSummary, so the sentence this asserts about does not "
            + "exist at all.");

        return summary ?? string.Empty;
    }

    /// <summary>
    /// Every route registered by a call <see cref="MappingSite"/> does not read.
    /// The bare <c>Map</c> overload is only a route when its receiver is
    /// something routes can be mapped on — <c>Option&lt;T&gt;.Map</c> is spelled
    /// the same and appears nine times in these files.
    /// </summary>
    private static OutsideTheChainMapping[] OutsideTheChainSites()
    {
        List<OutsideTheChainMapping> found = [];

        foreach (string file in ApiSourceFiles())
        {
            string text = WithoutComments(ReadRepositoryFile(file));
            string masked = MaskLiterals(text);
            HashSet<string> builders = RouteBuilderNames(masked);

            foreach (Match site in OutsideTheChainSite.Matches(masked))
            {
                string kind = site.Groups["kind"].Value;
                if (kind.Length == 0 && !builders.Contains(site.Groups["receiver"].Value))
                {
                    continue;
                }

                found.Add(new OutsideTheChainMapping(file, LineAt(text, site.Index), $"Map{kind}"));
            }
        }

        return [.. found];
    }

    private static HashSet<string> RouteBuilderNames(string masked)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Match declaration in RouteBuilderDeclaration.Matches(masked))
        {
            names.Add(declaration.Groups["name"].Value);
        }

        foreach (Match declaration in LocalDeclaration.Matches(masked))
        {
            int end = StatementEnd(masked, declaration.Index);
            if (GroupSite.IsMatch(masked[declaration.Index..end]))
            {
                names.Add(declaration.Groups["name"].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// The policy an <c>[Authorize]</c> attribute names, read as attribute data
    /// so no ASP.NET type needs to be referenced here. Both spellings are read:
    /// the named <c>Policy</c> property and the constructor argument.
    /// </summary>
    private static string DeclaredPolicy(Type handler)
    {
        CustomAttributeData[] authorize = handler.GetCustomAttributesData()
            .Where(attribute => string.Equals(
                attribute.AttributeType.Name,
                "AuthorizeAttribute",
                StringComparison.Ordinal))
            .ToArray();

        string named = authorize
            .SelectMany(attribute => attribute.NamedArguments)
            .Where(argument => string.Equals(argument.MemberName, "Policy", StringComparison.Ordinal))
            .Select(argument => argument.TypedValue.Value as string ?? string.Empty)
            .FirstOrDefault(string.Empty);

        return named.Length > 0
            ? named
            : authorize
                .SelectMany(attribute => attribute.ConstructorArguments)
                .Select(argument => argument.Value as string ?? string.Empty)
                .FirstOrDefault(string.Empty);
    }

    /// <summary>
    /// The route constant a registered handler type publishes, so the register's
    /// route is checked against the type rather than taken on trust.
    /// </summary>
    private static string DeclaredPath(Type handler)
    {
        FieldInfo? path = handler.GetField("Path", BindingFlags.Public | BindingFlags.Static);

        return path is { IsLiteral: true }
            ? (string?)path.GetRawConstantValue() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// A private constant's value, so a sentence about what a handler enforces
    /// is pinned to the handler rather than retyped beside it.
    /// </summary>
    private static string PrivateConstant(Type type, string name)
    {
        FieldInfo? constant = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

        constant.ShouldNotBeNull(
            $"{type.Name} no longer declares a constant named {name}, so what this endpoint requires "
            + "cannot be read from the handler and the summary is pinned to nothing.");

        return (string?)constant?.GetRawConstantValue() ?? string.Empty;
    }

    // ---- reporting ---------------------------------------------------------

    private static string Where(EndpointMapping mapping) =>
        $"{mapping.File}:{mapping.Line}  {mapping.Verb} {mapping.Route}";

    private static string Omission(EndpointMapping mapping, string literal) =>
        Where(mapping)
        + $"{Environment.NewLine}    enforces : {literal}  (via {mapping.ScopeConstant})"
        + $"{Environment.NewLine}    summary  : {Quoted(mapping.Summary)}"
        + $"{Environment.NewLine}    expected : the summary to contain {Quoted(ScopeLabel + literal)}";

    private static string Mismatch(EndpointMapping mapping, string literal, string claimed) =>
        Where(mapping)
        + $"{Environment.NewLine}    enforces : {literal}  (via {mapping.ScopeConstant})"
        + $"{Environment.NewLine}    claims   : {claimed}"
        + $"{Environment.NewLine}    summary  : {Quoted(mapping.Summary)}"
        + $"{Environment.NewLine}    expected : the summary to name {literal}, the scope the chain enforces";

    private static string Undeclared(EndpointMapping mapping, string literal) =>
        Where(mapping)
        + $"{Environment.NewLine}    enforces : {literal}  (via {mapping.ScopeConstant})"
        + $"{Environment.NewLine}    declares : no .ProducesProblem(StatusCodes.Status403Forbidden)"
        + $"{Environment.NewLine}    expected : the chain to declare it — a caller who authenticates "
        + $"without {literal} is refused 403 by the policy this mapping selects";

    private static string Anonymity(EndpointMapping mapping) =>
        Where(mapping)
        + $"{Environment.NewLine}    enforces : nothing — .AllowAnonymous()"
        + $"{Environment.NewLine}    summary  : {Quoted(mapping.Summary)}"
        + $"{Environment.NewLine}    expected : the summary to contain {Quoted(AnonymousLabel)} and what "
        + "authenticates the call instead";

    private static string Quoted(string? value) =>
        value is null ? NoSummary : $"\"{value}\"";

    // ---- the scope catalogue, by reflection --------------------------------

    /// <summary>
    /// Every <c>public const string</c> on <see cref="Scope"/> and its nested
    /// classes, indexed by full constant path and by every tail of it that names
    /// exactly one literal. <c>Scope.All</c> is a property and is not walked —
    /// deliberately: it is a hand-maintained list, and a guard checked against
    /// it would agree with the list rather than with the constants the endpoints
    /// cite.
    /// </summary>
    private static Dictionary<string, string> BuildScopeCatalogue()
    {
        Dictionary<string, List<string>> candidates = new(StringComparer.Ordinal);
        Collect(typeof(Scope), nameof(Scope), candidates);

        return candidates
            .Where(entry => entry.Value.Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(entry => entry.Key, entry => entry.Value[0], StringComparer.Ordinal);
    }

    private static void Collect(Type type, string path, Dictionary<string, List<string>> candidates)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string))
            {
                continue;
            }

            string literal = (string)field.GetRawConstantValue()!;
            foreach (string key in CatalogueKeys($"{path}.{field.Name}"))
            {
                if (!candidates.TryGetValue(key, out List<string>? found))
                {
                    found = [];
                    candidates[key] = found;
                }

                found.Add(literal);
            }
        }

        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public))
        {
            Collect(nested, $"{path}.{nested.Name}", candidates);
        }
    }

    /// <summary>
    /// A constant path, and the one shortening a <c>using static</c> can produce
    /// — <c>Sse.Cameras.Read</c> for <c>Scope.Sse.Cameras.Read</c>.
    ///
    /// <para>
    /// It used to index <em>every</em> tail, which no call site needs and which
    /// made a false green reachable: <c>.RequireAuthorization(Overlays.Read)</c>
    /// against some other catalogue's <c>Overlays</c> would resolve by name to
    /// <c>sse.overlays.read</c> while the constant held something else, and the
    /// summary naming the wrong scope would pass. Nothing used the generality
    /// (ADR-0036), and it was the only thing making that reachable.
    /// </para>
    /// </summary>
    private static IEnumerable<string> CatalogueKeys(string path)
    {
        yield return path;

        int shortened = path.IndexOf($"{nameof(Scope.Sse)}.", StringComparison.Ordinal);
        if (shortened > 0)
        {
            yield return path[shortened..];
        }
    }

    // ---- source mechanics --------------------------------------------------

    /// <summary>
    /// Every <c>.cs</c> file under <c>src/*/Api</c>, build output excluded. The
    /// API Gateway is not among them: it lives at <c>src/ApiGateway</c>, is pure
    /// YARP, and maps no route handler of its own.
    /// </summary>
    private static string[] ApiSourceFiles()
    {
        DirectoryInfo root = RepositoryRoot();

        return ContextApiDirectories()
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Select(file => RelativePath(root, file))
            .Where(IsSource)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] EndpointSourceFiles()
    {
        string[] files = ApiSourceFiles()
            .Where(path => path.EndsWith("Endpoints.cs", StringComparison.Ordinal))
            .ToArray();

        files.ShouldNotBeEmpty(
            $"no file matching {EndpointGlob} was found, so every assertion in this guard would pass "
            + "against an empty corpus. The endpoint files moved — point the sweep at where they now "
            + "live rather than leaving it reading nothing.");

        return files;
    }

    private static string[] ContextApiDirectories() =>
        Directory.EnumerateDirectories(Path.Combine(RepositoryRoot().FullName, "src"))
            .Select(context => Path.Combine(context, "Api"))
            .Where(Directory.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool IsSource(string relative) =>
        !relative.Contains("/obj/", StringComparison.Ordinal)
        && !relative.Contains("/bin/", StringComparison.Ordinal);

    /// <summary>
    /// Forward slashes throughout, and every path comparison and every report in
    /// this file is written against them. <see cref="Path.GetRelativePath"/>
    /// returns the <em>platform</em> separator, so a filter or an expected string
    /// written with a backslash is green on a Windows developer machine and red
    /// on Linux CI — the worst direction for a guard to break, because it passes
    /// exactly where nobody looks. This repository has been bitten by it.
    /// </summary>
    private static string RelativePath(DirectoryInfo root, string file) =>
        Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Line endings are stripped of <c>\r</c> on the way in, so every offset,
    /// line number and literal is the same on both platforms.
    /// </summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        string path = Path.Combine(RepositoryRoot().FullName, relativePath);
        File.Exists(path).ShouldBeTrue(
            $"expected {relativePath} at {path} — if it moved, update this guard rather than deleting it.");
        return File.ReadAllText(path).Replace("\r", string.Empty, StringComparison.Ordinal);
    }

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

    private static string Masked(string source) => MaskLiterals(WithoutComments(source));

    /// <summary>
    /// The source with every comment replaced by spaces, newlines kept, length
    /// preserved — so every offset still names the same line. A semicolon in a
    /// comment would otherwise end a chain early, and three sit inside
    /// <c>StreamEndpoints.MapStreamEndpoints</c> between the chains they explain
    /// — lines 31, 58 and 76. An earlier draft of this sentence cited the
    /// <c>Map*</c> sample in <c>RequireScopeExtensions</c>' XML doc instead,
    /// which is in <c>src/ServiceDefaults</c>: outside <c>src/*/Api</c>, and so
    /// never read by this guard at all.
    /// </summary>
    private static string WithoutComments(string source)
    {
        char[] result = source.ToCharArray();
        int index = 0;

        while (index < source.Length)
        {
            char current = source[index];

            if (current == '/' && Next(source, index) == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    result[index++] = ' ';
                }

                continue;
            }

            if (current == '/' && Next(source, index) == '*')
            {
                while (index < source.Length && !(source[index] == '*' && Next(source, index) == '/'))
                {
                    if (source[index] != '\n')
                    {
                        result[index] = ' ';
                    }

                    index++;
                }

                for (int blank = 0; blank < 2 && index < source.Length; blank++)
                {
                    result[index++] = ' ';
                }

                continue;
            }

            if (current == '@' && Next(source, index) == '"')
            {
                index = EndOfLiteral(source, index + 1, verbatim: true);
                continue;
            }

            if (current is '"' or '\'')
            {
                index = EndOfLiteral(source, index, verbatim: false);
                continue;
            }

            index++;
        }

        return new string(result);
    }

    /// <summary>
    /// The same text with the <em>interior</em> of every literal blanked, quotes
    /// and length kept. Structural searches run on this; literal contents are
    /// then taken from the unmasked text at the very same offsets. It is what
    /// lets a summary contain a semicolon — several do — without ending the
    /// statement that carries it.
    /// </summary>
    private static string MaskLiterals(string text)
    {
        char[] result = text.ToCharArray();
        int index = 0;

        while (index < text.Length)
        {
            int start;
            int end;

            if (text[index] == '@' && Next(text, index) == '"')
            {
                start = index + 1;
                end = EndOfLiteral(text, index + 1, verbatim: true);
            }
            else if (text[index] is '"' or '\'')
            {
                start = index;
                end = EndOfLiteral(text, index, verbatim: false);
            }
            else
            {
                index++;
                continue;
            }

            for (int inner = start + 1; inner < end - 1 && inner < text.Length; inner++)
            {
                if (result[inner] != '\n')
                {
                    result[inner] = ' ';
                }
            }

            index = Math.Max(end, index + 1);
        }

        return new string(result);
    }

    private static char Next(string text, int index) =>
        index + 1 < text.Length ? text[index + 1] : '\0';

    private static int EndOfLiteral(string text, int start, bool verbatim)
    {
        char quote = text[start];
        int index = start + 1;

        while (index < text.Length)
        {
            char current = text[index];

            if (verbatim)
            {
                if (current == quote)
                {
                    if (Next(text, index) == quote)
                    {
                        index += 2;
                        continue;
                    }

                    return index + 1;
                }

                index++;
                continue;
            }

            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == quote)
            {
                return index + 1;
            }

            if (current == '\n')
            {
                return index;
            }

            index++;
        }

        return index;
    }

    /// <summary>
    /// A chain runs from its <c>Map*</c> call to the terminating semicolon at
    /// statement level. Searched on the masked text, so a semicolon inside a
    /// summary does not end it early.
    /// </summary>
    private static int StatementEnd(string masked, int start)
    {
        int semicolon = masked.IndexOf(';', start);
        return semicolon < 0 ? masked.Length : semicolon;
    }

    private static int IndexOfCall(string masked, string call, int start, int end)
    {
        int found = masked.IndexOf(call, start, StringComparison.Ordinal);
        return found >= 0 && found < end ? found : -1;
    }

    /// <summary>
    /// The span between an opening parenthesis and its match, parentheses inside
    /// literals excluded because the literals are already blanked.
    /// </summary>
    private static (int From, int To) ArgumentSpan(string masked, int open)
    {
        int depth = 0;

        for (int index = open; index < masked.Length; index++)
        {
            if (masked[index] == '(')
            {
                depth++;
            }
            else if (masked[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return (open + 1, index);
                }
            }
        }

        return (open + 1, masked.Length);
    }

    /// <summary>
    /// Joins adjacent string literals and <c>+</c> concatenations into the one
    /// string the compiler would produce. Anything else in the argument —
    /// notably a <c>$</c> interpolation — leaves residue and the argument is
    /// reported unreadable rather than half-read. No summary interpolates today.
    /// </summary>
    private static bool TryJoinLiterals(string argument, out string joined)
    {
        StringBuilder value = new();
        StringBuilder residue = new();
        int index = 0;

        while (index < argument.Length)
        {
            if (argument[index] == '@' && Next(argument, index) == '"')
            {
                int end = EndOfLiteral(argument, index + 1, verbatim: true);
                value.Append(argument[(index + 2)..(end - 1)].Replace("\"\"", "\"", StringComparison.Ordinal));
                index = end;
                continue;
            }

            if (argument[index] == '"')
            {
                int end = EndOfLiteral(argument, index, verbatim: false);
                value.Append(Unescape(argument[(index + 1)..(end - 1)]));
                index = end;
                continue;
            }

            residue.Append(argument[index]);
            index++;
        }

        joined = value.ToString();

        return value.Length > 0
            && residue.ToString().All(character => char.IsWhiteSpace(character) || character == '+');
    }

    private static string Unescape(string literal) =>
        literal
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>
    /// A group prefix and a mapping route as one route, with the doubled and
    /// trailing slashes a naive join would leave behind removed.
    /// </summary>
    private static string JoinRoute(string prefix, string route)
    {
        string joined = $"{prefix.TrimEnd('/')}/{route.TrimStart('/')}";
        return joined.Length > 1 ? joined.TrimEnd('/') : joined;
    }

    private static string Compact(string path) =>
        new(path.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static int LineAt(string text, int index) =>
        text[..index].Count(character => character == '\n') + 1;

    /// <summary>
    /// Lines that are neither commentary nor attribute metadata, with the
    /// content of every string literal removed — what is left is the code that
    /// could carry a mechanism, rather than the prose it prints.
    /// </summary>
    private static IEnumerable<string> ExecutableLines(string source) =>
        source.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith('['))
            .Select(line => StringLiteral.Replace(line, "\"\""));

    private enum AuthorizationKind
    {
        Scoped,
        Anonymous,
        Bare,
        Unreadable,
    }

    /// <summary>
    /// A route that enforces no scope, and the open issue that will give it one.
    /// </summary>
    private sealed record UnenforcedRoute(string Verb, string Route, int Issue);

    /// <summary>
    /// A route registered by a call this reader does not parse, and the type
    /// whose <c>[Authorize]</c> attribute declares the scope it enforces.
    /// </summary>
    private sealed record RouteOutsideTheChain(string File, string Call, string Route, Type Handler);

    private sealed record OutsideTheChainMapping(string File, int Line, string Call);

    /// <summary>
    /// The authorization a chain declares. <see cref="Detail"/> carries the
    /// constant path when scoped, and the reason when unreadable.
    /// </summary>
    private sealed record EnforcedAuthorization(AuthorizationKind Kind, string Detail);

    private sealed record RouteGroup(string Name, string Prefix, EnforcedAuthorization? Authorization);

    /// <summary>
    /// One <c>MapGroup</c> declaration as it stands in the file, kept in file
    /// order rather than in the by-name dictionary. A name declared twice
    /// overwrites its dictionary entry — deliberately, see <see cref="Read"/> —
    /// and the overwritten group's declarations would then vanish from the count
    /// that is compared against the flat sweep.
    /// </summary>
    private sealed record GroupDeclaration(
        string File,
        int Line,
        string Name,
        string Prefix,
        int ForbiddenDeclarations);

    /// <summary>
    /// One route-handler registration. <see cref="Problem"/> is the reason the
    /// reader could not resolve it, and is <c>null</c> when it could.
    /// </summary>
    private sealed record EndpointMapping(
        string File,
        int Line,
        string Verb,
        string Route,
        AuthorizationKind Kind,
        string ScopeConstant,
        string? Summary,
        int ForbiddenDeclarations,
        string? Problem);

    private sealed record EndpointFileReading(
        string File,
        EndpointMapping[] Mappings,
        GroupDeclaration[] Groups,
        string[] Unread);

    private sealed record ScopedEndpoint(EndpointMapping Mapping, string Literal);
}

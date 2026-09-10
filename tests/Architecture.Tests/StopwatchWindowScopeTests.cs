using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards what an integration test's latency budget is a budget <b>for</b>
/// (issue #2119).
///
/// <para>
/// A closed stopwatch window — the text from
/// <c>Stopwatch &lt;v&gt; = Stopwatch.StartNew();</c> through the matching
/// <c>&lt;v&gt;.Stop();</c> — is a measurement of one named path, and the figure
/// it is asserted against is only meaningful if the window contains that path and
/// nothing else. Three calls in this suite are never part of such a path:
/// <c>CreateAdminClientAsync</c> and <c>CreateAuthenticatedClientAsync</c> each
/// mint a Keycloak token, and <c>RegisterCameraAsync</c> writes a prerequisite row
/// in a <b>different bounded context</b>. A window containing one of them is
/// timing setup, so the number it asserts is not the number its name claims — and
/// the failure mode is silent, because such a test passes.
/// </para>
///
/// <para>
/// <b>Scope is by definition, not by heuristic.</b> A <c>Stopwatch.StartNew()</c>
/// with no matching <c>&lt;v&gt;.Stop();</c> is a polling clock — it bounds a wait
/// loop and is read repeatedly while it runs — not a budget window, and this guard
/// says nothing about it. That boundary is drawn by the presence of the close, not
/// by the test's name, its folder or its trait, so it cannot be argued with per
/// file.
/// </para>
///
/// <para>
/// <b>The close must name the variable the window opened.</b>
/// <c>StreamHealthTransitionTests</c> holds two <c>.Stop();</c> calls belonging to
/// stopwatches built with <c>new Stopwatch()</c> and never with
/// <c>StartNew()</c>; a scan keyed on a bare <c>.Stop();</c> would close some other
/// file's window at that statement, or close a window at the wrong one.
/// </para>
///
/// <para>
/// Reads source from disk rather than referencing <c>Integration.Tests</c>: a
/// project reference would drag the Aspire hosting and DCP dependency graph into a
/// project that today runs in seconds with no Docker.
/// <c>IntegrationTestSelectionTests</c>, <c>LogTailCoverageTests</c> and
/// <c>GuardBanWiringTests</c> read the tree for the same reason — and this guard
/// lives outside the tree it scans on purpose, because its own counterfactuals are
/// source snippets carrying the literal violating shape, and an in-tree scanner
/// would report itself and then need the self-exemption that makes such a guard
/// untrustworthy.
/// </para>
/// </summary>
public class StopwatchWindowScopeTests
{
    private const string ScannedTree = "tests/Integration.Tests";

    /// <summary>
    /// Provisioning, not measurement: two token mints and one write into another
    /// bounded context. Neither the list nor its rationale is about one call site —
    /// narrowing it to the filed one, or widening it to silence an unrelated
    /// failure, would both make the guard mean nothing.
    /// </summary>
    private static readonly string[] ProvisioningCalls =
    [
        "CreateAdminClientAsync",
        "CreateAuthenticatedClientAsync",
        "RegisterCameraAsync",
    ];

    /// <summary>
    /// Replaced by its own line breaks rather than by nothing, so a reported line
    /// number still points at the line the reader has to open.
    /// </summary>
    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineComment = new(
        @"//[^\r\n]*",
        RegexOptions.Compiled);

    /// <summary>
    /// Every one of the fifteen declarations in the suite is spelled
    /// <c>Stopwatch &lt;ident&gt; = Stopwatch.StartNew();</c>, so the identifier is
    /// always readable from the declaration and there is no <c>var</c> case to
    /// infer.
    /// </summary>
    private static readonly Regex WindowOpen = new(
        @"\bStopwatch\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*Stopwatch\s*\.\s*StartNew\s*\(\s*\)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex Provisioning = new(
        $@"\b({string.Join('|', ProvisioningCalls)})\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void A_closed_budget_window_does_not_provision_inside_the_clock()
    {
        Window[] windows = ScannedWindows();

        windows.Length.ShouldBeGreaterThan(
            0,
            $"no closed stopwatch windows were found under {ScannedTree} — the scan is broken, not the "
            + "code. A source-scanning guard that matches nothing passes, and a passing guard that "
            + "checks nothing is indistinguishable from one that holds.");

        Violation[] violations = Ordered(windows.SelectMany(Violations)).ToArray();

        violations.ShouldBeEmpty(Explain(violations, windows.Length));
    }

    /// <summary>
    /// The counterfactual: constructing what the guard claims to catch. Its green
    /// run is what makes the real scan's verdict evidence rather than an assertion
    /// that nothing happened to match.
    /// </summary>
    [Fact]
    public void A_window_that_provisions_inside_the_clock_is_reported()
    {
        const string source = """
            public class ProvisioningInsideTests
            {
                [Fact]
                public async Task It_measures()
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    using HttpClient client = await aspire.CreateAdminClientAsync("layout-composition");
                    HttpResponseMessage response = await client.GetAsync("/layouts");
                    sw.Stop();
                }
            }
            """;

        Violation[] violations = Violations("synthetic/ProvisioningInsideTests.cs", source);

        violations.Length.ShouldBe(
            1,
            "a token mint between StartNew() and Stop() is inside the clock, so the elapsed figure "
            + "measures provisioning as well as the path the test names.");
        violations[0].Call.ShouldBe("CreateAdminClientAsync");
        violations[0].Window.StartLine.ShouldBe(6);
        violations[0].Window.EndLine.ShouldBe(9);
    }

    /// <summary>
    /// Two refusals in one snippet, and they are independent. The clock is never
    /// stopped, so it is a polling clock and out of scope by the definition above;
    /// and the bare <c>.Stop();</c> below it belongs to a stopwatch declared some
    /// other way — the shape <c>StreamHealthTransitionTests</c> actually has — so a
    /// scan keyed on <c>.Stop();</c> rather than on <c>&lt;v&gt;.Stop();</c> would
    /// close the wrong window here and report a violation that is not one.
    /// </summary>
    [Fact]
    public void A_polling_clock_is_not_a_budget_window()
    {
        const string source = """
            public class PollingTests
            {
                [Fact]
                public async Task It_waits()
                {
                    Stopwatch waiting = Stopwatch.StartNew();
                    Guid camera = await LayoutRequests.RegisterCameraAsync(aspire);
                    while (waiting.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        await Task.Delay(200);
                    }

                    Stopwatch unrelated = new();
                    unrelated.Stop();
                }
            }
            """;

        Violations("synthetic/PollingTests.cs", source).ShouldBeEmpty(
            "a clock that is never stopped bounds a wait; it asserts no budget, so nothing it "
            + "contains can misattribute one. And the bare .Stop(); belongs to a differently "
            + "declared stopwatch — closing the window on it would invent a violation.");
    }

    /// <summary>
    /// A commented-out call is a mention, not a call. Without stripping, hoisting
    /// the offending line but leaving a note about it for the next reader would
    /// keep the guard red — which teaches people to suppress it.
    /// </summary>
    [Fact]
    public void A_commented_out_call_inside_a_window_is_not_a_call()
    {
        const string source = """
            public class DocumentedWindowTests
            {
                [Fact]
                public async Task It_measures()
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    // Hoisted above the clock: await LayoutRequests.RegisterCameraAsync(aspire)
                    /* was: CreateAdminClientAsync("layout-composition") */
                    HttpResponseMessage response = await client.GetAsync("/layouts");
                    sw.Stop();
                }
            }
            """;

        Violations("synthetic/DocumentedWindowTests.cs", source).ShouldBeEmpty(
            "prose naming a provisioning call does not provision. A guard that cannot tell a comment "
            + "from a call punishes the note that explains the fix.");
    }

    private static string Explain(Violation[] violations, int windows)
    {
        List<string> message =
        [
            $"{violations.Length} of {windows} closed stopwatch windows under {ScannedTree} provision "
            + "inside the clock, so each one's elapsed figure includes work that is not on the path "
            + "the test is named for:",
            string.Empty,
        ];

        message.AddRange(Ordered(violations).Select(violation => $"  {violation}"));

        message.Add(string.Empty);
        message.AddRange(Remedy());

        return string.Join(Environment.NewLine, message);
    }

    private static string[] Remedy() =>
    [
        $"{string.Join(", ", ProvisioningCalls)} each mint a Keycloak token or write a prerequisite "
        + "row in another bounded context. None of them is part of any path this suite budgets.",
        string.Empty,
        "Fix: hoist the call to a local declared before Stopwatch.StartNew(), and say in the class "
        + "doc comment that it is a precondition rather than part of the measured path. Do not raise "
        + "the budget and do not drop the assertion — either removes the gate this guard exists to "
        + "keep honest.",
    ];

    private static Violation[] Violations(Window window) =>
        Provisioning
            .Matches(window.Body)
            .Select(match => new Violation(window, match.Groups[1].Value))
            .DistinctBy(violation => violation.Call, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The entry point the counterfactuals drive: a filename and a source string,
    /// no disk. <c>IntegrationTestSelectionTests.Describe</c> is shaped this way
    /// for the same reason.
    /// </summary>
    private static Violation[] Violations(string path, string source) =>
        ClosedWindows(path, source).SelectMany(Violations).ToArray();

    private static Window[] ClosedWindows(string path, string source)
    {
        string scanned = StripComments(source);

        return WindowOpen
            .Matches(scanned)
            .Select(open => Close(path, scanned, open))
            .OfType<Window>()
            .ToArray();
    }

    /// <summary>
    /// The window runs from the declaration to the <b>first</b>
    /// <c>&lt;variable&gt;.Stop();</c> after it. No such statement means no window:
    /// a polling clock, out of scope.
    /// </summary>
    private static Window? Close(string path, string scanned, Match open)
    {
        string variable = open.Groups["variable"].Value;
        int from = open.Index + open.Length;

        Match close = Regex.Match(
            scanned[from..],
            $@"\b{Regex.Escape(variable)}\s*\.\s*Stop\s*\(\s*\)\s*;");

        if (!close.Success)
        {
            return null;
        }

        return new Window(
            path,
            variable,
            LineOf(scanned, open.Index),
            LineOf(scanned, from + close.Index),
            scanned[from..(from + close.Index)]);
    }

    private static int LineOf(string source, int index) =>
        source.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// Block comments first, then <c>//</c> to end of line — which covers
    /// <c>///</c> XML docs, since those begin with it. A block comment is replaced
    /// by its own line breaks so that reported line numbers keep matching the file
    /// on disk.
    /// </summary>
    private static string StripComments(string source) =>
        LineComment.Replace(
            BlockComment.Replace(source, match => new string('\n', match.Value.Count(c => c == '\n'))),
            string.Empty);

    private static Window[] ScannedWindows()
    {
        DirectoryInfo root = RepositoryRoot();

        return Directory
            .EnumerateFiles(Path.Combine(root.FullName, ScannedTree), "*.cs", SearchOption.AllDirectories)
            .Select(file => Relative(root, file))
            .Where(IsSource)
            .SelectMany(relative =>
                ClosedWindows(relative, File.ReadAllText(Path.Combine(root.FullName, relative))))
            .ToArray();
    }

    /// <summary>
    /// Reported with <c>/</c> throughout. <see cref="Path.GetRelativePath"/>
    /// returns the platform separator, so a backslash in an expected string is
    /// green on Windows and red on Linux CI — this repository has been bitten by
    /// exactly that.
    /// </summary>
    private static string Relative(DirectoryInfo root, string file) =>
        Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsSource(string relative) =>
        !relative.Contains("/obj/", StringComparison.Ordinal)
        && !relative.Contains("/bin/", StringComparison.Ordinal);

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

    private static IOrderedEnumerable<Violation> Ordered(IEnumerable<Violation> violations) =>
        violations
            .OrderBy(violation => violation.Window.Path, StringComparer.Ordinal)
            .ThenBy(violation => violation.Window.StartLine)
            .ThenBy(violation => violation.Call, StringComparer.Ordinal);

    private sealed record Window(string Path, string Variable, int StartLine, int EndLine, string Body);

    /// <summary>
    /// One line, and it names the file, the window's range and the call. The
    /// record's generated <c>ToString</c> would dump <see cref="Window.Body"/> —
    /// the whole timed block — into Shouldly's collection formatter, burying the
    /// three facts a reader needs behind the source they already have.
    /// </summary>
    private sealed record Violation(Window Window, string Call)
    {
        public override string ToString() =>
            $"{Window.Path}:{Window.StartLine}-{Window.EndLine} "
            + $"(window '{Window.Variable}') calls {Call}";
    }
}

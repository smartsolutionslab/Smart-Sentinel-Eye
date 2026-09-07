using System.Text.RegularExpressions;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards which CI job reads an integration test's verdict (issues #2141, #2134).
///
/// <para>
/// <c>ci.yml:72</c> selects the Docker-free step <b>by trait</b>, deliberately:
/// the name filter it replaced read <c>~AspireFixtureReportSelectionTests</c>,
/// and the very next Docker-free class did not match it, recreating the omission
/// the step exists to remove (#2064). A trait is the one selector a new class
/// cannot silently fall outside of — but only if every class carries one.
/// </para>
///
/// <para>
/// A class carrying neither <c>[Collection(AspireCollection.Name)]</c> nor a
/// category trait declares nothing, so its verdict is deferred to the
/// thirty-minute Docker job. That is the same omission again, one layer down,
/// and it is silent: the tests pass, in the expensive job, and nobody reads a
/// green run to find out which job produced it.
/// </para>
///
/// <para>
/// The guard checks that a declaration <b>exists</b>; it does not adjudicate
/// which. Two classes lack the collection and still need a live run-mode stack
/// (<c>RunModeVariableResidueSweep</c>, <c>RunModeIngestAttributionTests</c>), so
/// inferring "no collection ⇒ Docker-free" would demand the wrong declaration of
/// them.
/// </para>
///
/// <para>
/// Reads source from disk rather than referencing <c>Integration.Tests</c>: a
/// project reference would drag the Aspire hosting and DCP dependency graph into
/// a project that today runs in seconds with no Docker — defeating a guard whose
/// entire purpose is to move a verdict out of the Docker job.
/// <c>LogTailCoverageTests</c> and <c>GuardBanWiringTests</c> read the tree for
/// the same reason.
/// </para>
/// </summary>
public class IntegrationTestSelectionTests
{
    private const string ScannedTree = "tests/Integration.Tests";
    private const string CheapStep = ".github/workflows/ci.yml:72";
    private const string ExcludeStep = ".github/workflows/ci.yml:179";

    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineComment = new(
        @"//[^\r\n]*",
        RegexOptions.Compiled);

    private static readonly Regex FactOrTheory = new(
        @"^[ \t]*\[(Fact|Theory)\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Anchored at the start of a line so an <b>attribute</b> is matched and a
    /// <b>mention</b> is not. <c>RunModeDriverTests</c> names the collection in a
    /// doc-comment and the fixture in reflection code, precisely because its job
    /// is to assert it acquires neither; a scan keying on the identifier anywhere
    /// in the file credits it, and the class escapes.
    /// </summary>
    private static readonly Regex CollectionDeclaration = new(
        @"^[ \t]*\[\s*Collection\(\s*AspireCollection\.Name\s*\)\s*\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Constrained to the four categories <c>ci.yml</c> actually knows about —
    /// the cheap-step selector at <see cref="CheapStep"/> and the exclusion
    /// filter at <see cref="ExcludeStep"/>. Matching <c>[Trait("Category"</c>
    /// without reading the value would credit any spelling:
    /// <c>[Trait("Category", "FixtureLogick")]</c> would satisfy the guard
    /// while selecting nothing in either job, running only in the thirty-minute
    /// Docker job — the precise omission this guard exists to close, now
    /// behind a declaration that looks correct.
    /// </summary>
    private static readonly Regex CategoryDeclaration = new(
        @"^[ \t]*\[\s*Trait\(\s*""Category""\s*,\s*""(FixtureLogic|Measurement|Disruptive|Maintenance)""\s*\)\s*\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Every_integration_test_class_declares_where_it_runs()
    {
        TestFile[] scanned = ScannedFiles();

        scanned.Count(file => file.Facts > 0).ShouldBeGreaterThan(
            0,
            $"no test methods were found under {ScannedTree} — the scan is broken, not the code. "
            + "A source-scanning guard that matches nothing passes, and a passing guard that checks "
            + "nothing is indistinguishable from one that holds.");

        TestFile[] undeclared = scanned.Where(file => file.Undeclared).ToArray();

        undeclared.ShouldBeEmpty(Explain(undeclared, scanned));
    }

    /// <summary>
    /// Trap 1: <c>RunModeDriverTests</c> carries the literal attribute text inside
    /// its doc-comment, and a scan that credits it lands on 23 instead of 34 —
    /// which the census did.
    ///
    /// <para>
    /// Here the line anchor is what refuses it, not <see cref="StripComments"/>:
    /// the <c>[</c> sits behind <c>/// &lt;c&gt;</c> and never begins its line.
    /// Verified by counterfactual — this case still passes with stripping removed.
    /// <see cref="A_commented_out_declaration_is_not_a_declaration"/> is the case
    /// that needs the stripping, and is why FR-003 is a requirement rather than a
    /// second opinion on this one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_doc_comment_naming_the_collection_attribute_is_not_a_declaration()
    {
        const string source = """
            /// <summary>
            /// The mutation this exists for: giving the run-mode class
            /// <c>[Collection(AspireCollection.Name)]</c>.
            /// </summary>
            public class DocumentedTests
            {
                [Fact]
                public void It_holds() { }
            }
            """;

        Describe("synthetic/DocumentedTests.cs", source).Undeclared.ShouldBeTrue(
            "prose describing the attribute is not the attribute. A guard that cannot tell a "
            + "doc-comment from a declaration is itself a member of the population it enforces.");
    }

    /// <summary>
    /// Trap 1 again, in the one shape line-anchoring does not catch. A
    /// commented-out attribute begins its own line, so the anchor sees it exactly
    /// as it sees a live one, and only <see cref="StripComments"/> tells the two
    /// apart. Without this case the doc-comment test above passes with stripping
    /// removed — the requirement would be asserted by a test that does not depend
    /// on it.
    /// </summary>
    [Fact]
    public void A_commented_out_declaration_is_not_a_declaration()
    {
        const string source = """
            /*
            [Trait("Category", "FixtureLogic")]
            */
            public class ParkedTests
            {
                [Fact]
                public void It_holds() { }
            }
            """;

        Describe("synthetic/ParkedTests.cs", source).Undeclared.ShouldBeTrue(
            "an attribute someone commented out selects nothing: the class runs where it ran "
            + "before, and the comment is the only thing that says otherwise.");
    }

    /// <summary>
    /// Trap 2, the same wrong count of 23 by the other route: naming the fixture
    /// in reflection code or in an assertion message is not being decorated with
    /// it — and here the naming exists <i>because</i> the class asserts it does
    /// not acquire the fixture.
    /// </summary>
    [Fact]
    public void Naming_the_fixture_in_code_is_not_a_declaration()
    {
        const string source = """
            public class ReflectingTests
            {
                [Fact]
                public void It_does_not_acquire_the_fixture()
                {
                    CollectionAttribute? collection = typeof(Other).GetCustomAttribute<CollectionAttribute>();
                    collection.ShouldBeNull("a collection attribute injects AspireFixture");
                    Parameters().ShouldNotContain(p => p.ParameterType.Name.Contains("AspireFixture"));
                }
            }
            """;

        Describe("synthetic/ReflectingTests.cs", source).Undeclared.ShouldBeTrue(
            "an identifier in reflection code or an assertion message is a mention, not a "
            + "declaration; the guard must match the attribute on the class declaration.");
    }

    /// <summary>
    /// A misspelled category is not a declaration <see cref="CategoryDeclaration"/> credits.
    /// Before this test existed, matching <c>[Trait("Category"</c> without reading the value
    /// meant <c>[Trait("Category", "FixtureLogick")]</c> satisfied the guard while
    /// <c>ci.yml:72</c> and <c>ci.yml:179</c> select and exclude neither — the class would run
    /// only in the thirty-minute Docker job, exactly the omission this guard exists to close,
    /// now behind a declaration that looks correct.
    /// </summary>
    [Fact]
    public void A_misspelled_category_value_is_not_a_declaration()
    {
        const string source = """
            [Trait("Category", "FixtureLogick")]
            public class MisspelledCategoryTests
            {
                [Fact]
                public void It_holds() { }
            }
            """;

        Describe("synthetic/MisspelledCategoryTests.cs", source).Undeclared.ShouldBeTrue(
            "\"FixtureLogick\" selects nothing at ci.yml:72 and is excluded by nothing at "
            + "ci.yml:179, so it is not one of the four declarations the guard recognises.");
    }

    /// <summary>
    /// No soft edge: the obligation attaches to classes that produce a verdict.
    /// A helper type produces none, and demanding a category of it would teach
    /// people to annotate files rather than to declare where tests run. The
    /// census's first pass wrongly included seven such files.
    /// </summary>
    [Fact]
    public void A_file_with_no_test_methods_is_not_asked_to_declare()
    {
        const string source = """
            public sealed record IngestSpanResult(int Total, int Attributed);

            public static class IngestSpanMeasurement
            {
                public static IngestSpanResult Measure() => new(0, 0);
            }
            """;

        Describe("synthetic/IngestSpanMeasurement.cs", source).Undeclared.ShouldBeFalse(
            "a file holding no [Fact] or [Theory] contributes no verdict to either job, so it "
            + "has nothing to declare.");
    }

    /// <summary>
    /// Either declaration satisfies the guard, because the two describe different
    /// classes of test and the guard does not adjudicate between them.
    /// </summary>
    [Fact]
    public void Either_declaration_satisfies_the_guard()
    {
        const string collectionDeclared = """
            [Collection(AspireCollection.Name)]
            public class StackTests
            {
                [Fact]
                public void It_holds() { }
            }
            """;

        const string categoryDeclared = """
            [Trait("Category", "FixtureLogic")]
            public class CheapTests
            {
                [Fact]
                public void It_holds() { }
            }
            """;

        Describe("synthetic/StackTests.cs", collectionDeclared).Undeclared.ShouldBeFalse(
            "a class in the fixture's collection has declared: it runs in the integration job.");
        Describe("synthetic/CheapTests.cs", categoryDeclared).Undeclared.ShouldBeFalse(
            "a class carrying a category has declared: the trait decides which job selects it.");
    }

    private static string Explain(TestFile[] undeclared, TestFile[] scanned)
    {
        int tests = undeclared.Sum(file => file.Facts);
        int population = scanned.Count(file => file.Facts > 0);

        List<string> message =
        [
            $"{undeclared.Length} of {population} test classes under {ScannedTree} carry neither "
            + "[Collection(AspireCollection.Name)] nor [Trait(\"Category\", …)], so their "
            + $"{tests} tests declare no job and are read only by the 30-minute integration job:",
            string.Empty,
        ];

        message.AddRange(undeclared
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => $"  {file.Path} ({file.Facts} tests)"));

        message.Add(string.Empty);
        message.Add(
            "Only \"FixtureLogic\", \"Measurement\", \"Disruptive\" and \"Maintenance\" count — that is "
            + $"the exact set {CheapStep} selects and {ExcludeStep} excludes, so any other spelling is "
            + "silently undeclared, not merely unrecognised.");
        message.Add(string.Empty);
        message.Add(
            "Add one of the legitimate declarations — the correct fix differs between them and the "
            + "wrong one is silent:");
        message.Add(
            $"  needs no stack                 → [Trait(\"Category\", \"FixtureLogic\")], selected by {CheapStep}");
        message.Add(
            "  needs a stack CI does not boot → [Trait(\"Category\", \"Measurement\" | \"Disruptive\" "
            + $"| \"Maintenance\")], excluded by {ExcludeStep}");
        message.Add(
            "  needs the fixture's stack      → [Collection(AspireCollection.Name)], no trait needed");

        return string.Join(Environment.NewLine, message);
    }

    private static TestFile[] ScannedFiles()
    {
        DirectoryInfo root = RepositoryRoot();

        return Directory
            .EnumerateFiles(Path.Combine(root.FullName, ScannedTree), "*.cs", SearchOption.AllDirectories)
            .Select(file => Relative(root, file))
            .Where(IsSource)
            .Select(relative => Describe(relative, File.ReadAllText(Path.Combine(root.FullName, relative))))
            .ToArray();
    }

    private static TestFile Describe(string path, string source)
    {
        string code = StripComments(source);

        return new TestFile(
            path,
            FactOrTheory.Count(code),
            CollectionDeclaration.IsMatch(code),
            CategoryDeclaration.IsMatch(code));
    }

    /// <summary>
    /// FR-003. Block comments first, then <c>//</c> to end of line — which covers
    /// <c>///</c> XML docs, since those begin with it.
    /// </summary>
    private static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source, string.Empty), string.Empty);

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

    private sealed record TestFile(string Path, int Facts, bool DeclaresCollection, bool DeclaresCategory)
    {
        public bool Undeclared => Facts > 0 && !DeclaresCollection && !DeclaresCategory;
    }
}

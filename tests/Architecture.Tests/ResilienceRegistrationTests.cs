namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards the single registration of the standard resilience handler.
///
/// <para>
/// <c>AddServiceDefaults</c> applies <c>AddStandardResilienceHandler</c> to every
/// client through <c>ConfigureHttpClientDefaults</c>, and every host calls
/// <c>AddServiceDefaults</c>. A second call for a particular client therefore does
/// not strengthen anything: <c>AddHttpMessageHandler</c> appends, so the client
/// ends up with two resilience pipelines, one nested inside the other. Retries
/// multiply rather than add — four attempts become sixteen — and the client
/// carries two independent circuit breakers and two 30 s budgets.
/// </para>
///
/// <para>
/// Eight registrations had done exactly that: LayoutComposition's camera guard,
/// StreamDistribution's MediaMTX gateway and camera lookup, and five of the
/// Scenario Simulator's clients. The MediaMTX one mattered most, because
/// <c>StreamHealthWatcher</c> sweeps it every two seconds.
/// </para>
///
/// <para>
/// Nothing catches this at build time and nothing catches it at run time either:
/// a doubled pipeline is not an error, it is a slower and more patient client.
/// The failure is visible only as latency during an outage, which is precisely
/// when nobody is reading registration code. Hence a guard.
/// <c>ResilienceHandlerNestingTests</c> in ServiceDefaults.Tests is its
/// counterpart — it observes the nesting this test exists to prevent, so the
/// rule is not asserted on the strength of this comment alone.
/// </para>
///
/// <para>
/// Reads source rather than reflecting over assemblies for the same reason
/// <c>GuardBanWiringTests</c> does: DI registration order leaves no trace in IL
/// that a test can distinguish from a single registration.
/// </para>
/// </summary>
public class ResilienceRegistrationTests
{
    private const string Registration = "AddStandardResilienceHandler(";

    /// <summary>
    /// Forward slashes, and <see cref="ReadSources"/> normalises to match.
    /// <c>Path.GetRelativePath</c> returns the platform separator, so a literal
    /// written with backslashes passes on a Windows developer machine and fails
    /// on Linux CI — which is the worst direction for a guard to break, because
    /// it is green exactly where it is least likely to be looked at.
    /// </summary>
    private const string SoleRegistrationSite = "src/ServiceDefaults/Extensions.cs";

    private const string SourceTree = "src";

    private const string TestTree = "tests";

    /// <summary>
    /// The one project the scan skips, and the only one. <c>ServiceDefaults.Tests</c>
    /// registers the bare handler on purpose — it is testing the handler itself, and
    /// <c>ResilienceHandlerNestingTests</c> has to be able to construct an unnarrowed
    /// pipeline in order to describe one. No other test project's clients are testing
    /// the handler; they are talking to something.
    ///
    /// <para>
    /// Expressed as a denial rather than as the list of trees worth scanning, because an
    /// allow-list is the shape of the defect this guard exists for: <c>ReadSources</c>
    /// was hard-coded to <c>src</c>, so the fixture's registration was in neither the
    /// population ADR-0143's fix changed nor the population this file defended (#2129).
    /// Naming the trees to look at fixes the one tree that was missed and re-arms the
    /// trap for the next — a new test project falls outside the list and is silently
    /// unguarded. Naming the exception covers a new project by default, and makes adding
    /// a second exception a visible act.
    /// </para>
    /// </summary>
    private const string ExemptTestProject = "tests/ServiceDefaults.Tests/";

    private const string Narrowing = "IdempotentRetry.RetryIdempotentMethodsOnly";

    private const string FixtureWiringSite = "tests/Integration.Tests/Fixtures/AspireFixture.cs";

    /// <summary>
    /// The wire between the defaults and the clients, and the one thing neither this
    /// guard nor <c>FixtureRetryPolicyTests</c> observed. That suite calls
    /// <c>FixtureHttpClients.Configure</c> directly and this scan reads registration
    /// text; delete this call and both stay green while every client the fixture hands
    /// out has no resilience handler at all. <c>POST</c> is incidentally safe that way,
    /// so the POST facts pass for the wrong reason, while the <c>GET</c> and <c>PUT</c>
    /// retries the suite relies on against a still-warming stack vanish — the
    /// latent-flake class the fix was supposed to close, reintroducible with nothing red
    /// anywhere.
    /// </summary>
    private const string FixtureWiring = "ConfigureHttpClientDefaults(FixtureHttpClients.Configure)";

    [Fact]
    public void The_standard_resilience_handler_is_called_exactly_once_across_src()
    {
        string[] callers = ReadSources(SourceTree)
            .Where(file => CodeLines(file.Value).Any(line => line.Contains(Registration, StringComparison.Ordinal)))
            .Select(file => file.Key)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        callers.ShouldBe(
            [SoleRegistrationSite],
            "ServiceDefaults already applies the standard resilience handler to every HttpClient via "
            + "ConfigureHttpClientDefaults. A second call for one client nests a whole second pipeline "
            + "inside the first rather than replacing it, so its retries multiply (4 attempts become 16) "
            + "and it gains a second circuit breaker and a second total-request budget. If a client "
            + "genuinely needs a different schedule, configure the existing pipeline's options — do not "
            + "add another handler.");
    }

    /// <summary>
    /// The counterpart to the count. A registration that had drifted out of
    /// ServiceDefaults entirely would still satisfy "exactly one", and would leave
    /// every client that is not the one named in that call with no resilience at
    /// all — silently, because an unprotected client looks identical to a
    /// protected one until something downstream fails.
    /// </summary>
    [Fact]
    public void The_one_registration_is_the_one_that_reaches_every_client()
    {
        string extensions = ReadSources(SourceTree)[SoleRegistrationSite];

        string[] code = [.. CodeLines(extensions)];

        int defaults = Array.FindIndex(code, line => line.Contains("ConfigureHttpClientDefaults", StringComparison.Ordinal));
        int resilience = Array.FindIndex(code, line => line.Contains(Registration, StringComparison.Ordinal));

        defaults.ShouldBeGreaterThanOrEqualTo(0,
            "AddServiceDefaults is expected to configure HttpClient defaults; without "
            + "ConfigureHttpClientDefaults the resilience handler below reaches whichever single client "
            + "it was attached to and no other.");

        resilience.ShouldBeGreaterThan(defaults,
            "the standard resilience handler is expected inside the ConfigureHttpClientDefaults callback, "
            + "which is what makes it apply to every client in every host. Moved out of that callback it "
            + "would protect one client and leave the rest bare while this file still, at a glance, looks "
            + "like it sets a global default.");
    }

    /// <summary>
    /// The other way to disarm the pipeline, and the quieter one. A client-level
    /// timeout is not a per-attempt cap: it wraps the whole handler chain, so a
    /// value below the pipeline's 30 s total budget cancels the retries rather
    /// than bounding them. Identity's admin client held 10 s and had, in effect,
    /// no retries at all — while reading like a client that was careful about
    /// both.
    /// </summary>
    [Fact]
    public void No_client_caps_the_resilience_pipeline_with_its_own_timeout()
    {
        (string File, string Line)[] finite = ReadSources(SourceTree)
            .SelectMany(file => CodeLines(file.Value).Select(line => (File: file.Key, Line: line.Trim())))
            .Where(entry => entry.Line.Contains(".Timeout = ", StringComparison.Ordinal))
            .Where(entry => !entry.Line.Contains("Timeout.InfiniteTimeSpan", StringComparison.Ordinal))
            .ToArray();

        finite.ShouldBeEmpty(
            "HttpClient.Timeout wraps the entire handler chain, retries included, so a finite value is a "
            + "ceiling over the whole resilience pipeline rather than a limit on one attempt. Set below "
            + "the pipeline's total budget it silently cancels the retries; the per-attempt cap that was "
            + "actually wanted already exists inside the standard handler. Configure the pipeline's "
            + "AttemptTimeout or TotalRequestTimeout instead of the client's.");
    }

    /// <summary>
    /// The tree ADR-0143's own fix did not reach, and which this guard could not
    /// see.
    ///
    /// <para>
    /// The narrowing lives inside <c>AddServiceDefaults</c>, no test project
    /// calls it, and the fixture registered the bare handler — so its clients
    /// retried <c>POST</c> exactly as if they had opted back in, with no
    /// <c>RetryEveryMethod()</c> anywhere to grep for. The registration was in
    /// neither the population the fix changed nor the population this file
    /// defended, because the reader below was hard-coded to <c>src</c> (#2129).
    /// </para>
    ///
    /// <para>
    /// Checked one registration at a time rather than one file at a time. Asking
    /// whether the file mentions the predicate anywhere passes a file that
    /// registers twice and narrows once — and now that
    /// <c>FixtureHttpClients</c> is the named home for the fixture's client
    /// defaults, it is the most likely place a second registration lands.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_resilience_registration_under_the_tests_declares_the_predicate()
    {
        Dictionary<string, string> sources = ReadSources(TestTree);

        bool wired = sources.TryGetValue(FixtureWiringSite, out string? fixture)
            && CodeLines(fixture).Any(line => line.Contains(FixtureWiring, StringComparison.Ordinal));

        wired.ShouldBeTrue(
            $"{FixtureWiringSite} is expected to apply the fixture's client defaults with "
            + $"{FixtureWiring}, and without that call the narrowing below is configuration nothing "
            + "reads. Every client the fixture hands out would be built with no resilience handler at "
            + "all — which this guard cannot see, because it reads registrations, and which "
            + "FixtureRetryPolicyTests cannot see either, because it calls FixtureHttpClients.Configure "
            + "itself. POST would still be attempted once, for the wrong reason; the GET and PUT "
            + "retries would be gone.");

        (string Path, string Line)[] registrations = [.. sources
            .Where(file => !file.Key.StartsWith(ExemptTestProject, StringComparison.Ordinal))
            .SelectMany(file => Lines(file.Value).Select(line => (Path: file.Key, Line: line)))
            .Where(entry => Registers(entry.Line))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)];

        registrations.ShouldNotBeEmpty(
            $"no resilience registration was found under {TestTree} at all, which this guard "
            + "cannot tell apart from every registration being correct. A source scan over an empty "
            + "population passes while checking nothing. If a test project's client configuration moved, "
            + "follow it rather than letting this scan match nothing.");

        string[] unnarrowed = [.. registrations
            .Where(entry => !Narrows(entry.Line))
            .Select(entry => $"{entry.Path}: {entry.Line.Trim()}")];

        unnarrowed.ShouldBeEmpty(
            $"no test project calls AddServiceDefaults, so nothing else applies ADR-0143's "
            + "narrowing to the clients one hands out. Registered bare, the handler carries the library's "
            + "own predicate, which reads the outcome and never the method — a POST is retried like a GET, "
            + "and a POST whose response was lost is indistinguishable from one that never arrived. Pass "
            + $"{Narrowing} to the registration. Do not reach for RetryEveryMethod(): it is the opt-back-in, "
            + "and using it here reinstates the defect with a justification attached to it.");
    }

    /// <summary>
    /// The counterfactual, without which the fact above rests on its own comment
    /// — it would read exactly the same if the predicate matched nothing it
    /// claims to catch.
    /// </summary>
    [Fact]
    public void The_scan_catches_a_registration_that_omits_the_predicate()
    {
        const string bare = "        http.AddStandardResilienceHandler();";
        const string narrowed = "        http.AddStandardResilienceHandler(IdempotentRetry.RetryIdempotentMethodsOnly);";
        const string described = "        // http.AddStandardResilienceHandler() is what this used to be.";
        const string quoted = "        const string shape = \"http.AddStandardResilienceHandler();\";";

        Registers(bare).ShouldBeTrue("the bare registration is the shape the guard exists to catch.");
        Narrows(bare).ShouldBeFalse("nothing in the bare registration names the predicate.");

        Registers(narrowed).ShouldBeTrue();
        Narrows(narrowed).ShouldBeTrue("the narrowed registration is the shape the guard exists to allow.");

        Registers(described).ShouldBeFalse(
            "a commented-out registration is prose, not wiring, and a guard that counted prose would flag "
            + "the file that explains it.");

        Registers(quoted).ShouldBeFalse(
            "a registration inside a string literal is prose too. The scan now covers this very file, "
            + "which spells both shapes as literals in order to test the matcher — counting them would "
            + "fail the guard for describing itself, and would have to be bought off with an exemption "
            + "entry, which is the mechanism this fact just stopped relying on.");
    }

    /// <summary>
    /// Both predicates read a <b>single line</b>, not a whole file. A file-scoped
    /// answer is the wrong unit: it reports what the file mentions somewhere
    /// rather than what each registration says, so a bare call appended to a file
    /// that already narrows elsewhere is invisible to it.
    /// </summary>
    private static bool Registers(string line) => Mentions(line, Registration);

    private static bool Narrows(string line) => Mentions(line, Narrowing);

    /// <summary>
    /// A quoted occurrence is not wiring, for the same reason <see cref="CodeLines"/>
    /// gives about a commented one. This file spells both shapes as string literals in
    /// order to test the matcher, and the scan now reaches this file: counting those
    /// would fail the guard for describing itself, and no exemption entry should have to
    /// be spent on saying so.
    /// </summary>
    private static bool Mentions(string line, string token)
    {
        int at = line.IndexOf(token, StringComparison.Ordinal);

        return at >= 0 && !IsComment(line) && !IsQuoted(line, at);
    }

    /// <summary>
    /// Odd number of quotes before the match means the match is inside one. The escaped
    /// quotes come out first: <c>\"</c> opens nothing, and counting it as if it did puts
    /// the parity back to even and reports a literal as wiring. This is not hypothetical
    /// — the counterfactual below spells a quoted registration inside a quoted string,
    /// and the first version of this method flagged it.
    /// </summary>
    private static bool IsQuoted(string line, int at) =>
        line[..at]
            .Replace("\\\"", string.Empty, StringComparison.Ordinal)
            .Count(character => character == '"') % 2 == 1;

    private static string[] Lines(string source) => source.Split('\n');

    /// <summary>
    /// Lines with the comment prefix stripped out. The registration is named in
    /// prose in a few places — including in the comment explaining why it must not
    /// be called twice — and a guard that counted those would fail for describing
    /// itself.
    /// </summary>
    private static IEnumerable<string> CodeLines(string source) =>
        Lines(source).Where(line => !IsComment(line));

    private static bool IsComment(string line) =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static Dictionary<string, string> ReadSources(string tree)
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null && !File.Exists(Path.Combine(candidate.FullName, "SmartSentinelEye.slnx")))
        {
            candidate = candidate.Parent;
        }

        DirectoryInfo root = candidate
            ?? throw new InvalidOperationException(
                $"could not locate the repository root above {AppContext.BaseDirectory}");

        string scanned = Path.Combine(root.FullName, tree);
        return Directory.EnumerateFiles(scanned, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(root.FullName, f).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
    }
}

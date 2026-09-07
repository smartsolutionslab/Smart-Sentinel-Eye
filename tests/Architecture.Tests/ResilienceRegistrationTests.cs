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

    /// <summary>
    /// Scoped to the integration fixture's tree rather than to all of
    /// <c>tests</c>. <c>ServiceDefaults.Tests</c> registers the bare handler on
    /// purpose — it is testing the handler itself, and
    /// <c>ResilienceHandlerNestingTests</c> has to be able to construct an
    /// unnarrowed pipeline in order to describe one. The fixture's clients are not
    /// testing the handler; they are talking to the stack.
    /// </summary>
    private const string IntegrationTestTree = "tests/Integration.Tests";

    private const string Narrowing = "IdempotentRetry.RetryIdempotentMethodsOnly";

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
    /// see. The narrowing lives inside <c>AddServiceDefaults</c>, no test project
    /// calls it, and the fixture registered the bare handler — so its clients
    /// retried <c>POST</c> exactly as if they had opted back in, with no
    /// <c>RetryEveryMethod()</c> anywhere to grep for. The registration was in
    /// neither the population the fix changed nor the population this file
    /// defended, because the reader below was hard-coded to <c>src</c> (#2129).
    /// </summary>
    [Fact]
    public void Every_resilience_registration_under_the_integration_tests_declares_the_predicate()
    {
        Dictionary<string, string> sources = ReadSources(IntegrationTestTree);

        string[] registrations = [.. sources
            .Where(file => Registers(file.Value))
            .Select(file => file.Key)
            .OrderBy(path => path, StringComparer.Ordinal)];

        registrations.ShouldNotBeEmpty(
            $"no resilience registration was found under {IntegrationTestTree} at all, which this guard "
            + "cannot tell apart from every registration being correct. A source scan over an empty "
            + "population passes while checking nothing. If the fixture's client configuration moved, "
            + "point this scan at wherever it went rather than letting it match nothing.");

        string[] unnarrowed = [.. registrations.Where(path => !Narrows(sources[path]))];

        unnarrowed.ShouldBeEmpty(
            $"the integration fixture does not call AddServiceDefaults, so nothing else applies ADR-0143's "
            + "narrowing to the clients it hands out. Registered bare, the handler carries the library's "
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

        Registers(bare).ShouldBeTrue("the bare registration is the shape the guard exists to catch.");
        Narrows(bare).ShouldBeFalse("nothing in the bare registration names the predicate.");

        Registers(narrowed).ShouldBeTrue();
        Narrows(narrowed).ShouldBeTrue("the narrowed registration is the shape the guard exists to allow.");

        Registers(described).ShouldBeFalse(
            "a commented-out registration is prose, not wiring, and a guard that counted prose would flag "
            + "the file that explains it.");
    }

    private static bool Registers(string source) =>
        CodeLines(source).Any(line => line.Contains(Registration, StringComparison.Ordinal));

    private static bool Narrows(string source) =>
        CodeLines(source).Any(line => line.Contains(Narrowing, StringComparison.Ordinal));

    /// <summary>
    /// Lines with the comment prefix stripped out. The registration is named in
    /// prose in a few places — including in the comment explaining why it must not
    /// be called twice — and a guard that counted those would fail for describing
    /// itself.
    /// </summary>
    private static IEnumerable<string> CodeLines(string source) =>
        source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

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

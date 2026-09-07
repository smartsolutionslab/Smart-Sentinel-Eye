using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Polly.Timeout;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// xUnit collection fixture that boots the Aspire AppHost in E2ETests mode
/// (ephemeral containers, no React dev servers) and exposes per-API
/// HttpClients + DbContext factories + auth helpers (ADR-0068).
///
/// Tests join the collection via [Collection(AspireCollection.Name)] so the
/// containers are spun up once per xUnit assembly run.
/// </summary>
public sealed partial class AspireFixture : IAsyncLifetime, IDisposable
{
    /// <summary>
    /// The one RTSP source this stack serves, published by the
    /// <c>fixture-video</c> container (spec 056) — a single H.264 clip looping
    /// on the <c>loop</c> path, with a permissive internal user that grants the
    /// anonymous read.
    ///
    /// <para>
    /// The host is a <em>container-network DNS name</em>, so this URL is
    /// reachable from the SFU container and <strong>not</strong> from the test
    /// process. A test uses it by registering a camera at it and watching the
    /// stream state, never by dialling it directly. The analogous form already
    /// in the repository is <c>SimulatorOptions.RtspHost</c>, which defaults to
    /// <c>camera-sim:8554</c> for the same reason.
    /// </para>
    ///
    /// <para>
    /// One constant per language, and there are two (spec 076, #198): this one
    /// for the C# integration suite, and <c>FIXTURE_VIDEO_RTSP_URL</c> in
    /// <c>e2e/support/live-video-wall.ts</c> for the Playwright suite. They are
    /// not quite twins — the TypeScript one honours an
    /// <c>E2E_FIXTURE_VIDEO_RTSP_URL</c> environment override, which this
    /// constant has no equivalent of.
    /// </para>
    ///
    /// <para>
    /// The string is spelled a third time in the explanatory comment above the
    /// <c>fixture-video</c> resource in <c>AppHost.cs</c>, and its <em>parts</em>
    /// live in more places still: the resource name in
    /// <c>AddContainer("fixture-video", …)</c>, the <c>loop:</c> path name in
    /// <c>src/AppHost/Resources/fixture-video.yml</c>, and the
    /// <c>targetPort: 8554</c> endpoint. Nothing checks any of them against any
    /// other — drift fails the build in zero places, and the only signal is a
    /// 30 s stream-state timeout in this suite. So a test that hard-codes the
    /// string adds a spelling to a set that is already only held together by
    /// hand: use the constant.
    /// </para>
    /// </summary>
    public const string RtspTestSourceUrl = "rtsp://fixture-video:8554/loop";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(8);

    private DistributedApplication? _app;

    /// <summary>
    /// Services whose console output is tailed for diagnostics. A resource can
    /// be <c>Running</c> and still fault every request, and the client-side
    /// <see cref="HttpRequestException"/> a test sees carries only "500" — the
    /// server's exception lives here.
    ///
    /// A resource earns its place by having a <c>RecentLogs</c> call site, and
    /// <c>LogTailCoverageTests</c> fails the build when the two disagree — the
    /// list is hand-maintained, which is how four names stayed missing while
    /// seven call sites returned a placeholder instead of a log (issue #2053).
    /// </summary>
    private static readonly string[] TailedResources =
    [
        "camera-catalog",
        "automation",
        "identity",
        "event-ingestion",
        "stream-distribution",
        "overlay-designer",
        "layout-composition",
        "system-variables",
    ];

    /// <summary>
    /// The states that mean a waited-for resource ended rather than started.
    ///
    /// <para>
    /// Four of the five are <see cref="KnownResourceStates"/> constants, and the
    /// set is Aspire's own reading:
    /// <c>ResourceNotificationService.IsContinuableState</c> stops a
    /// <c>StopOnResourceUnavailable</c> wait on
    /// <c>Running | Finished | Exited | FailedToStart | RuntimeUnhealthy</c>.
    /// </para>
    ///
    /// <para>
    /// The fifth can only ever be a string literal.
    /// <c>KnownResourceStates.TerminalStates</c> is exactly
    /// <c>{ Finished, FailedToStart, Exited }</c> and has no <c>Terminated</c>;
    /// the state exists only as
    /// <c>Aspire.Hosting.Dcp.Model.ExecutableState.Terminated</c>, which is
    /// <c>internal</c> and cannot be referenced. It reaches the snapshot because
    /// <c>ResourceSnapshotBuilder.ToSnapshot(Executable, ...)</c> copies the
    /// state text through unmodified.
    /// </para>
    /// </summary>
    private static readonly string[] FatalStartupStates =
    [
        KnownResourceStates.Finished,
        KnownResourceStates.Exited,
        KnownResourceStates.FailedToStart,
        KnownResourceStates.RuntimeUnhealthy,
        "Terminated",
    ];

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _logTails = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _logTailFailures = new(StringComparer.Ordinal);
    private CancellationTokenSource? _logCts;
    private Task[]? _logTailTasks;

    // xUnit invokes DisposeAsync; this IDisposable.Dispose only exists to
    // satisfy CA1001 (the type owns _logCts). Resource disposal happens in
    // DisposeAsync above.
    public void Dispose() => _logCts?.Dispose();

    public DistributedApplication App =>
        _app ?? throw new InvalidOperationException("Aspire AppHost has not been started.");

    public HttpClient CameraCatalog { get; private set; } = null!;

    public HttpClient StreamDistribution { get; private set; } = null!;

    public HttpClient LayoutComposition { get; private set; } = null!;

    public HttpClient OverlayDesigner { get; private set; } = null!;

    public HttpClient AuditObservability { get; private set; } = null!;

    public HttpClient EventIngestion { get; private set; } = null!;

    public HttpClient SystemVariables { get; private set; } = null!;

    public HttpClient Automation { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string[] parameters =
        [
            "Parameters:PostgresUser=postgres",
            "Parameters:PostgresPassword=testpassword",
            "Parameters:KeycloakPassword=testkeycloak",
            "Parameters:RabbitMqPassword=testmessaging",
            "E2ETests=true",
        ];

        // The startup budget covers the whole bring-up, not just StartAsync.
        // A hung CreateAsync or BuildAsync previously had no token at all and
        // would block the run indefinitely.
        using CancellationTokenSource cts = new(StartupTimeout);

        IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SmartSentinelEye_AppHost>(parameters, cts.Token)
                .ConfigureAwait(false);

        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        _app = await builder.BuildAsync(cts.Token).ConfigureAwait(false);

        _logCts = new CancellationTokenSource();

        try
        {
            await _app.StartAsync(cts.Token).ConfigureAwait(false);

            // Subscribe only once the resources exist. Started before
            // StartAsync, WatchAsync has nothing to watch and completes
            // immediately — which is why the first attempt at this captured
            // nothing and reported it as "the service said nothing".
            foreach (string resource in TailedResources)
            {
                _logTails.TryAdd(resource, new ConcurrentQueue<string>());
            }

            _logTailTasks = TailedResources
                .Select(resource => Task.Run(() => TailResourceLogsAsync(resource, _logCts.Token), _logCts.Token))
                .ToArray();

            await _app.ResourceNotifications
                .WaitForResourceAsync("keycloak", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            // Nine services wait for migrations, so a migration runner that
            // died takes the whole startup budget with it: the wait succeeded
            // on `Finished` without asking how, and the fixture then spent the
            // remaining ~7½ minutes on services that could never start (#2064).
            //
            // The predicate overload is used for its *return value* — it hands
            // back the snapshot that matched — and it filters on state alone.
            // Judging the exit code inside the predicate would fail open: an
            // unobserved code satisfies `is null or 0` and the wait proceeds as
            // before, while a known-bad code merely postpones the same shared
            // `cts` timeout to the same instant. Read after, decide after,
            // throw here.
            //
            // OrdinalIgnoreCase, not `==`: the string overload it replaces
            // generates its predicate with `Aspire.StringComparers.ResourceState`,
            // which is case-insensitive. `ToSnapshot(Executable, …)` passes the
            // state text through unmodified so the two cannot differ in fact —
            // but matching the comparer makes "the same condition" exact rather
            // than true-by-inspection.
            ResourceEvent migrations = await _app.ResourceNotifications
                .WaitForResourceAsync(
                    "migrations",
                    migration => string.Equals(
                        migration.Snapshot.State?.Text,
                        KnownResourceStates.Finished,
                        StringComparison.OrdinalIgnoreCase),
                    cts.Token)
                .ConfigureAwait(false);

            if (ExitedNonZero(migrations.Snapshot.ExitCode))
            {
                // Only on this branch: a healthy boot must not pay the bounded
                // five-second log read.
                Aspire.Hosting.ApplicationModel.ResourceLoggerService migrationLoggers =
                    _app.Services.GetRequiredService<Aspire.Hosting.ApplicationModel.ResourceLoggerService>();

                string migrationsLog =
                    await CaptureOneResourceLogAsync(migrationLoggers, "migrations").ConfigureAwait(false);

                // Not a TimeoutException, and not any OperationCanceledException:
                // the catch below would reclassify this as "did not start within
                // 8 minutes" — a failure phase 5 measured at 1m38s.
                throw new InvalidOperationException(
                    FormatMigrationFailureMessage(migrations.Snapshot.ExitCode.Value, migrationsLog));
            }

            await _app.ResourceNotifications
                .WaitForResourceAsync("camera-catalog", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("mediamtx", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            // The RTSP source behind RtspTestSourceUrl (spec 076, #198). No
            // WaitOnResourceUnavailable: that matters when waiting across a
            // restart, where the default gives up on the very transition it
            // should watch. This is a first boot of a resource that starts
            // once, like the nine waits around it. No HTTP probe either --
            // fixture-video.yml declares no `api:` block, so there is nothing
            // to poll short of opening an RTSP session, which the tests do
            // transitively; Running plus a test's own settle budget absorbs
            // FFmpeg's start.
            await _app.ResourceNotifications
                .WaitForResourceAsync("fixture-video", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("stream-distribution", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("layout-composition", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("overlay-designer", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("audit-observability", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("event-ingestion", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("system-variables", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await _app.ResourceNotifications
                .WaitForResourceAsync("automation", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            // Identity serves requests and is tailed, and was the only such
            // resource with no gate here. LogTailDeliversIntegrationTests reads
            // its tail, so without this the suite relied on identity having
            // started and logged by the time the first test ran.
            await _app.ResourceNotifications
                .WaitForResourceAsync("identity", KnownResourceStates.Running, cts.Token)
                .ConfigureAwait(false);

            await WaitForKeycloakRealmAsync(cts.Token).ConfigureAwait(false);
            await WaitForMediaMtxAsync(cts.Token).ConfigureAwait(false);

            // Running only means the process launched — it does not mean Kestrel
            // has bound its listener, so the waits above can return while the
            // first request would still be refused. OverlayDesigner is the one
            // that lost this race on the Linux runner, timing out ~9 tests while
            // passing on Windows dev boxes.
            await WaitForServiceHealthAsync("overlay-designer", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            string logTail = RecentLogs("camera-catalog");
            Dictionary<string, string> states = await CaptureResourceStateMapAsync().ConfigureAwait(false);
            string failedLogs = await CaptureFailedResourceLogsAsync(states).ConfigureAwait(false);
            throw new TimeoutException(
                FormatTimeoutMessage(StartupTimeout, states, _exitCodes, failedLogs, logTail),
                ex);
        }

        // Name the endpoint explicitly. Every service declares WithHttpEndpoint()
        // in AppHost and none declares an https one, but an ASP.NET project also
        // carries an https launch profile — so leaving the choice to
        // CreateHttpClient's default made these clients depend on which endpoint
        // that default happened to prefer. Aspire 13.4.6 changed that preference
        // to https, and CI's integration job has no dev cert (only the e2e job
        // runs `dotnet dev-certs https`), so every request failed with
        // UntrustedRoot — 54 of 75 tests, while passing on a developer machine
        // where the cert is trusted. Naming the endpoint removes the ambient
        // dependency entirely (#1133).
        CameraCatalog = App.CreateHttpClient("camera-catalog", "http");
        StreamDistribution = App.CreateHttpClient("stream-distribution", "http");
        LayoutComposition = App.CreateHttpClient("layout-composition", "http");
        OverlayDesigner = App.CreateHttpClient("overlay-designer", "http");
        AuditObservability = App.CreateHttpClient("audit-observability", "http");
        EventIngestion = App.CreateHttpClient("event-ingestion", "http");
        SystemVariables = App.CreateHttpClient("system-variables", "http");
        Automation = App.CreateHttpClient("automation", "http");
    }

    public async Task DisposeAsync()
    {
        CameraCatalog?.Dispose();
        StreamDistribution?.Dispose();
        LayoutComposition?.Dispose();
        OverlayDesigner?.Dispose();
        AuditObservability?.Dispose();
        EventIngestion?.Dispose();

        if (_logCts is not null)
        {
            await _logCts.CancelAsync().ConfigureAwait(false);
            if (_logTailTasks is not null)
            {
                try { await Task.WhenAll(_logTailTasks).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
            _logCts.Dispose();
            _logCts = null;
        }

        if (_app is not null)
        {
            // No token by design: teardown runs after _logCts is disposed and
            // must release the stack even if the run is being torn down.
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IAsyncDisposable)_app).DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One resource's snapshot: state, exit code, and every health report with
    /// its description.
    ///
    /// <para>
    /// <b>The console tail is the wrong instrument for a resource that failed to
    /// start</b> (#2038). A process that never launched has no stdout, so an
    /// empty tail is indistinguishable from a silent crash. The snapshot is
    /// where the orchestrator records what it observed, and the exit code is the
    /// datum that separates the two: present means the process ran and died,
    /// absent means it never ran at all.
    /// </para>
    /// </summary>
    public async Task<string> ResourceDiagnosticsAsync(string resourceName)
    {
        if (_app is null)
        {
            return "(no application)";
        }

        List<string> lines = [];
        using CancellationTokenSource snapshot = new(TimeSpan.FromSeconds(3));

        try
        {
            await foreach (ResourceEvent evt in _app.ResourceNotifications.WatchAsync(snapshot.Token))
            {
                if (!string.Equals(evt.Resource.Name, resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(
                    $"state={evt.Snapshot.State?.Text ?? "(unknown)"} "
                    + $"exitCode={(evt.Snapshot.ExitCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "(none)")} "
                    + $"started={evt.Snapshot.StartTimeStamp?.ToString("O", CultureInfo.InvariantCulture) ?? "(never)"} "
                    + $"stopped={evt.Snapshot.StopTimeStamp?.ToString("O", CultureInfo.InvariantCulture) ?? "(none)"}");

                foreach (HealthReportSnapshot report in evt.Snapshot.HealthReports)
                {
                    lines.Add(
                        $"  health {report.Name}={report.Status}: "
                        + $"{report.Description ?? "(no description)"} {report.ExceptionText ?? string.Empty}".TrimEnd());
                }
            }
        }
        catch (OperationCanceledException) { /* expected — the watch is bounded above */ }

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "(no snapshot observed)";
    }

    private async Task<Dictionary<string, string>> CaptureResourceStateMapAsync()
    {
        Dictionary<string, string> states = new(StringComparer.Ordinal);
        if (_app is null)
        {
            return states;
        }

        using CancellationTokenSource snapshot = new(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (ResourceEvent evt in _app.ResourceNotifications.WatchAsync(snapshot.Token))
            {
                states[evt.Resource.Name] = evt.Snapshot.State?.Text ?? "(unknown)";
                _exitCodes[evt.Resource.Name] = evt.Snapshot.ExitCode;
            }
        }
        catch (OperationCanceledException) { /* expected */ }

        return states;
    }

    // `Finished` alone does not say whether a service shut down cleanly or
    // died: both land there. The exit code is what separates them, and not
    // having it is why the one occurrence of #1918 could not be diagnosed
    // after the fact. Captured alongside the state so the next report answers
    // the question instead of raising it.
    private readonly Dictionary<string, int?> _exitCodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Which resources are worth dumping logs for after a startup timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `Finished` used to count as healthy for everything, which hid exactly
    /// the failure it needed to explain: a long-running service that exits
    /// during startup lands in `Finished`, not `Failed`. When `automation`
    /// did that (#1918) the report dumped eleven idle rebuilders and nothing
    /// for the one resource the exception named.
    /// </para>
    /// <para>
    /// So `Finished` is success only for the one-shot resources it is success
    /// for, and `NotStarted` rebuilders — dev-time helpers that never run on
    /// their own — are dropped rather than reported as failures with no logs.
    /// </para>
    /// <para>
    /// That exemption was unconditional, and it over-corrected (#2061): a
    /// `migrations` that aborted with exit code 134 was pronounced healthy and
    /// left out of the failure section while the state list two inches above
    /// printed the code. The exemption now ends where a non-zero exit begins.
    /// </para>
    /// </remarks>
    internal static string[] SelectResourcesToReport(
        Dictionary<string, string> states,
        Dictionary<string, int?> exitCodes) =>
        states
            .Where(kv => !IsHealthy(kv.Key, kv.Value, exitCodes))
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsHealthy(string name, string state, Dictionary<string, int?> exitCodes) =>
        state is "Running"
        || (state is "Finished" && IsOneShot(name) && !ExitedNonZero(name, exitCodes))
        || (state is "NotStarted" && name.EndsWith("-rebuilder", StringComparison.Ordinal));

    // "Did not exit non-zero", not "exited zero". An unobserved code means the
    // exit was never seen — the state map keeps whichever watch event arrived
    // last, and a snapshot can carry a present null — so reading unknown as
    // failure would report a `migrations` that is perfectly fine, which is
    // #1918 coming back from the other side.
    //
    // One copy of the rule, reached two ways: the timeout report looks the code
    // up in the captured state map, the migrations wait reads it straight off
    // the snapshot it matched (#2064).
    // [NotNullWhen] so the caller that throws can read `.Value` without a
    // second null check restating half the rule — a true answer is the proof.
    internal static bool ExitedNonZero([NotNullWhen(true)] int? exitCode) => exitCode is not null and not 0;

    private static bool ExitedNonZero(string name, Dictionary<string, int?> exitCodes) =>
        exitCodes.TryGetValue(name, out int? exit) && ExitedNonZero(exit);

    // Resources that run once and stop; finishing is how they succeed.
    private static bool IsOneShot(string name) =>
        name is "migrations" || name.StartsWith("migrations-", StringComparison.Ordinal);

    /// <summary>
    /// Whether a resource's state text means it is dead rather than starting.
    ///
    /// <para>
    /// <b>Fail-open on everything else</b>, <see langword="null"/>,
    /// <c>Unknown</c> and <c>NotStarted</c> included. A false positive here
    /// fails every integration run on every machine; a false negative leaves the
    /// behaviour that existed before this check. The asymmetry decides the
    /// default.
    /// </para>
    ///
    /// <para>
    /// <c>OrdinalIgnoreCase</c>, matching
    /// <c>Aspire.StringComparers.ResourceState</c>, which is <c>internal</c> and
    /// cannot be borrowed — the same reading the migrations gate above makes.
    /// </para>
    /// </summary>
    internal static bool IsFatalStartupState([NotNullWhen(true)] string? stateText) =>
        stateText is not null
        && FatalStartupStates.Contains(stateText, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The whole message a startup timeout throws, assembled where a test can
    /// read it.
    ///
    /// <para>
    /// The three sections had tests; the assembly that orders them did not, so
    /// deleting the cause line from the `throw` left every test green while the
    /// line vanished from the report a human reads. FR-005 puts the cause
    /// first — before the state list — and that is an ordering claim, which
    /// only a test over the assembled string can hold.
    /// </para>
    /// </summary>
    internal static string FormatTimeoutMessage(
        TimeSpan timeout,
        Dictionary<string, string> states,
        Dictionary<string, int?> exitCodes,
        string failedLogs,
        string logTail) =>
        $"Aspire AppHost did not start within {timeout.TotalMinutes} minutes.\n" +
        FormatLikelyCause(states, exitCodes) +
        $"Resource states:\n{FormatResourceStates(states, exitCodes)}\n" +
        $"Failed-resource logs:\n{failedLogs}\n" +
        $"Last camera-catalog logs:\n{logTail}";

    /// <summary>
    /// One line per resource. A resource that exited carries its exit code,
    /// because `Finished` alone does not distinguish a clean shutdown from a
    /// death — which is exactly what #1918's single occurrence could not answer.
    /// </summary>
    internal static string FormatResourceStates(
        Dictionary<string, string> states,
        Dictionary<string, int?> exitCodes) =>
        states.Count == 0
            ? "  (app not built)"
            : string.Join(
                '\n',
                states.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                    exitCodes.TryGetValue(kv.Key, out int? exit) && exit is not null
                        ? $"  {kv.Key}: {kv.Value} (exit code {exit})"
                        : $"  {kv.Key}: {kv.Value}"));

    /// <summary>
    /// The line that answers "what broke?" before the reader has to scan
    /// forty-five state lines for it.
    ///
    /// <para>
    /// The cause of the run that motivated #2061 was printed — once per state
    /// list, in the same typeface as the forty-four resources that were fine.
    /// Prominence is not ordering; it is a sentence at the top that names the
    /// resource. Empty when nothing exited non-zero, so the report never
    /// claims a cause it does not have.
    /// </para>
    /// </summary>
    internal static string FormatLikelyCause(
        Dictionary<string, string> states,
        Dictionary<string, int?> exitCodes)
    {
        // Both halves of the predicate: a resource that crashed and restarted
        // inside the watch window ends it `Running` and is left out of the
        // failure section, so naming it here would point the reader at a
        // section that does not exist.
        string[] died = states.Keys
            .Where(name => !IsHealthy(name, states[name], exitCodes) && ExitedNonZero(name, exitCodes))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (died.Length == 0)
        {
            return string.Empty;
        }

        string named = string.Join("; ", died.Select(name => $"{name} exited with code {exitCodes[name]}"));

        return $"Likely cause: {named} — a non-zero exit is a failure, not a clean finish.\n";
    }

    /// <summary>
    /// What the fixture says when the migration runner finished by dying.
    ///
    /// <para>
    /// Scoped to <c>migrations</c> on purpose. At that instant the nine services
    /// that wait for it are still <c>Waiting</c>, so the full timeout report
    /// would print nine sections with nothing in them about the failure — the
    /// noise #2061 removed. The cause sentence is worded like
    /// <see cref="FormatLikelyCause"/>'s so a reader who has seen a timeout
    /// report recognises it.
    /// </para>
    ///
    /// <para>
    /// Assembled here rather than at the throw for the reason the other three
    /// formatters are: the ordering claim — the code before the log — is only
    /// holdable by a test over the assembled string.
    /// </para>
    ///
    /// <para>
    /// Takes <c>int</c>, not <c>int?</c>: <see cref="ExitedNonZero(int?)"/> has
    /// already proven the code non-null at the only call site, and an <c>int?</c>
    /// parameter invites a null this method would render as
    /// <c>"exited with code  — …"</c>.
    /// </para>
    /// </summary>
    internal static string FormatMigrationFailureMessage(int exitCode, string migrationsLog) =>
        $"migrations exited with code {exitCode} — a non-zero exit is a failure, not a clean finish.\n" +
        "The startup wait stopped here rather than spending the remaining budget on services that wait for it.\n" +
        $"migrations log:\n{migrationsLog}";

    /// <summary>
    /// What the fixture says when a resource it waited for is dead by the time
    /// the last wait has returned.
    ///
    /// <para>
    /// Scoped to one resource, like <see cref="FormatMigrationFailureMessage"/>
    /// and for its reason: at this instant the others are <c>Running</c> and say
    /// nothing about the failure, so a forty-five-line state list would be
    /// forty-four rows of the noise #2061 removed. Two deaths produce two of
    /// these, joined — nothing here is phrased as a singular verdict that a
    /// second occurrence would contradict.
    /// </para>
    ///
    /// <para>
    /// Takes <c>int?</c> where <see cref="FormatMigrationFailureMessage"/> takes
    /// <c>int</c>: the container branch of <c>ResourceSnapshotBuilder</c> maps
    /// <c>ExitCode == -1</c> to <see langword="null"/>, so a dead container
    /// legitimately arrives with no code. Rendering that through an
    /// <c>int</c>-shaped formatter yields "with exit code  —", and rendering it
    /// as <c>0</c> would claim a clean exit.
    /// </para>
    ///
    /// <para>
    /// The verdict sentence deliberately does <b>not</b> say the wait stopped
    /// rather than spending the remaining budget, which
    /// <see cref="FormatMigrationFailureMessage"/> says and is true there. This
    /// check runs after all twelve waits: it spends the whole boot and only then
    /// refuses to return. It saves no time; what it changes is the verdict.
    /// </para>
    /// </summary>
    internal static string FormatResourceDeathMessage(
        string name,
        string state,
        int? exitCode,
        string resourceLog) =>
        $"{name} reached {state} "
        + (exitCode is null ? "(no exit code recorded)" : $"with exit code {exitCode.Value}")
        + " — a resource that ends during startup is a failure, not a start.\n"
        + "The fixture stopped here rather than reporting a healthy boot over a dead resource.\n"
        + $"{name} log:\n{resourceLog}";

    /// <summary>
    /// One section per selected resource, each header saying <i>why</i> that
    /// resource was selected: its state, its exit code where one exists, and
    /// which of the two failures it is.
    ///
    /// <para>
    /// A process that ran and died and a process that never reached a running
    /// state can both arrive here with nothing to show, and only the header can
    /// tell them apart. The fixture already knows the difference — see the
    /// snapshot remarks above, from #2038 — and until #2061 it did not say so in
    /// the report, so nine expected empties read as nine failures to collect
    /// evidence.
    /// </para>
    /// </summary>
    internal static string FormatFailedResourceReport(
        Dictionary<string, string> states,
        Dictionary<string, int?> exitCodes,
        Dictionary<string, string> logs)
    {
        StringBuilder report = new();
        foreach (string name in SelectResourcesToReport(states, exitCodes))
        {
            report.Append("---- ").Append(name).Append(" (")
                .Append(DescribeWhySelected(states[name], ExitCodeOf(name, exitCodes)))
                .AppendLine(") ----");
            report.AppendLine(logs.TryGetValue(name, out string? captured) ? captured : "(no logs captured)");
        }

        return report.ToString();
    }

    private static string DescribeWhySelected(string state, int? exitCode) => (state, exitCode) switch
    {
        (_, { } code) when code != 0 => $"{state}, exit code {code} — the process ran and died",
        // States the state, and stops there. The clause that used to follow —
        // "so an empty log is expected" — was written from CI, where those
        // sections are empty, and phase 5 printed it directly above 14 to 25
        // lines of real orchestration log. A header that predicts what the
        // section below it contains is a header that can be contradicted two
        // lines later.
        ("FailedToStart", _) => $"{state} — never reached a running state",
        _ => state,
    };

    private static int? ExitCodeOf(string name, Dictionary<string, int?> exitCodes) =>
        exitCodes.TryGetValue(name, out int? exit) ? exit : null;

    /// <summary>
    /// On a startup timeout, dump recent stdout for every resource that did
    /// not end up healthy. The CI Linux boot failures (#423) don't repro on
    /// Windows dev boxes, so a crashed service's own log is the only window
    /// into *why* — the fixture otherwise tails camera-catalog alone.
    /// </summary>
    private async Task<string> CaptureFailedResourceLogsAsync(Dictionary<string, string> states)
    {
        if (_app is null)
        {
            return "(app not built)";
        }

        string[] failed = SelectResourcesToReport(states, _exitCodes);

        if (failed.Length == 0)
        {
            return "(no failed resources)";
        }

        Aspire.Hosting.ApplicationModel.ResourceLoggerService loggers =
            _app.Services.GetRequiredService<Aspire.Hosting.ApplicationModel.ResourceLoggerService>();

        Dictionary<string, string> logs = new(StringComparer.Ordinal);
        foreach (string name in failed)
        {
            logs[name] = await CaptureOneResourceLogAsync(loggers, name).ConfigureAwait(false);
        }

        return FormatFailedResourceReport(states, _exitCodes, logs);
    }

    private async Task<string> CaptureOneResourceLogAsync(
        Aspire.Hosting.ApplicationModel.ResourceLoggerService loggers, string name)
    {
        if (!TryResolveResourceId(name, out string resourceId))
        {
            // Kept, and defensive rather than reachable. This method's only
            // caller derives `name` from a snapshot, so the resource has by
            // construction published one. What can still return false is a
            // *replicated* name, which TryGetCurrentState declines to resolve —
            // and this fixture has no replicas (they are off under isE2ETests).
            // It stays because the day one appears this message says so, and
            // "(no logs captured)" would not.
            return "(no DCP instance for this resource — the name is ambiguous or unpublished)";
        }

        List<string> lines = [];
        using CancellationTokenSource perResource = new(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (IReadOnlyList<LogLine> batch in
                loggers.WatchAsync(resourceId).WithCancellation(perResource.Token))
            {
                foreach (LogLine line in batch)
                {
                    lines.Add(line.Content);
                }
            }
        }
        catch (OperationCanceledException) { /* bounded read */ }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            lines.Add($"(log capture failed: {logEx.GetType().Name})");
        }

        return lines.Count == 0 ? "(no logs captured)" : string.Join('\n', lines.TakeLast(60));
    }

    /// <summary>
    /// The last few hundred lines a tailed service wrote. Use it when a test
    /// gets a status it cannot explain — a 500 tells you nothing on its own,
    /// and CI has no other route to the service's stack trace.
    ///
    /// <para>
    /// <b>A recorded tail failure is reported whether or not the queue is
    /// empty.</b> It used to be reported only when the queue was empty, which was
    /// harmless while every tail was silent and is not harmless now: a tail that
    /// ran for two minutes and then faulted would answer with 400 stale lines and
    /// look perfectly healthy. That is this branch's own thesis — a dump that
    /// reads like output when it is an omission — so the failure is appended to
    /// the lines rather than gated on their absence.
    /// </para>
    /// </summary>
    public string RecentLogs(string resourceName, int lines = 120)
    {
        if (!_logTails.TryGetValue(resourceName, out ConcurrentQueue<string>? tail))
        {
            return $"(not tailed — add '{resourceName}' to AspireFixture.TailedResources)";
        }

        string[] recent = tail.TakeLast(lines).ToArray();
        _logTailFailures.TryGetValue(resourceName, out string? failure);

        if (recent.Length == 0)
        {
            return failure is null
                ? "(tail subscribed but the resource emitted nothing)"
                : $"(log tail failed: {failure})";
        }

        string content = string.Join(Environment.NewLine, recent);

        return failure is null
            ? content
            : content
                + Environment.NewLine
                + $"(log tail faulted during this run and re-subscribed: {failure}"
                + " — lines written around that point may be missing)";
    }

    /// <summary>
    /// The DCP instance id behind an app-model name — <c>camera-catalog</c>
    /// resolves to something like <c>camera-catalog-thwaubpm</c>.
    ///
    /// <para>
    /// <c>ResourceLoggerService.WatchAsync</c> keys on that id, not on the name.
    /// Passing the name yields a stream that carries nothing and throws nothing,
    /// which is why every tail in this fixture had been silently empty since it
    /// was written, and why the seven <c>RecentLogs</c> call sites of #2053
    /// printed "(tail subscribed but the resource emitted nothing)" for services
    /// that had plainly emitted something (#2054).
    /// </para>
    ///
    /// <para>
    /// <b>One resolver, both capture paths.</b> <c>CaptureOneResourceLogAsync</c>
    /// runs only on a startup timeout, which no test can provoke, so its
    /// correctness rests entirely on sharing this code with the tail loop that
    /// <c>LogTailDeliversIntegrationTests</c> does observe. A second copy would
    /// make that claim false.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="false"/> for two different reasons, and the two
    /// callers meet them separately. In <c>TailResourceLogsAsync</c> it means the
    /// resource has published no snapshot <i>yet</i> — the tails start before the
    /// <c>WaitForResourceAsync</c> calls — which is a wait, not a fault, and that
    /// caller must not record it. In <c>CaptureOneResourceLogAsync</c> that reason
    /// cannot arise, because its caller takes the name from a snapshot; there the
    /// only remaining cause is a <i>replicated</i> name, which
    /// <c>TryGetCurrentState</c> declines to resolve. Nothing here is replicated,
    /// so that branch is defensive and unexercised.
    /// </para>
    /// </summary>
    private bool TryResolveResourceId(string resourceName, out string resourceId)
    {
        resourceId = string.Empty;

        if (_app is null)
        {
            return false;
        }

        if (!_app.ResourceNotifications.TryGetCurrentState(resourceName, out ResourceEvent? snapshot))
        {
            return false;
        }

        resourceId = snapshot.ResourceId;

        return !string.IsNullOrEmpty(resourceId);
    }

    private async Task TailResourceLogsAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        ConcurrentQueue<string> tail = _logTails.GetOrAdd(resourceName, _ => new ConcurrentQueue<string>());

        try
        {
            Aspire.Hosting.ApplicationModel.ResourceLoggerService loggers =
                _app.Services.GetRequiredService<Aspire.Hosting.ApplicationModel.ResourceLoggerService>();

            // **Re-subscribed in a loop, because a watch ends when its process
            // does.** A restarted resource is a new process, and the first
            // subscription completes the moment the old one exits — so a single
            // `await foreach` stops tailing at exactly the event worth tailing.
            // #2038's failure reported "the resource emitted nothing" for a
            // service that had plainly emitted something; what it had really
            // lost was the subscription.
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // **Resolved on every turn of the loop rather than hoisted
                    // above it — and this is defensive code that nothing here
                    // exercises.** Said plainly, because the comment it replaces
                    // asserted the id changes on every restart and that is not
                    // what was observed: on Aspire 13.5.3 (Windows) a single id
                    // survived the whole cycle,
                    //
                    //   event-ingestion-gxkpyqjx  Running  -> Stopping
                    //   event-ingestion-gxkpyqjx  Stopping -> Finished
                    //   event-ingestion-gxkpyqjx  Finished -> Starting
                    //
                    // so a resolve hoisted above this loop passes all three
                    // delivery tests, test C included. **A green suite is not
                    // evidence that hoisting would fail.** The resolve stays here
                    // because id stability is a property of this DCP build and not
                    // a published contract: the day it stops holding, a hoisted
                    // resolve re-subscribes to the instance that just died and the
                    // resource goes permanently quiet — #2038's symptom,
                    // reintroduced by the fix for #2054. Linux is unverified.
                    if (!TryResolveResourceId(resourceName, out string resourceId))
                    {
                        // Not a failure: the tails are launched before the
                        // WaitForResourceAsync calls, so a resource may simply not
                        // have published a snapshot yet. A queue that is still
                        // filling must not be reported as broken — that is the same
                        // misleading diagnostic this whole change exists to remove.
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                        continue;
                    }

                    await foreach (IReadOnlyList<LogLine> batch in
                        loggers.WatchAsync(resourceId).WithCancellation(cancellationToken))
                    {
                        foreach (LogLine line in batch)
                        {
                            tail.Enqueue(line.Content);
                            while (tail.Count > 400)
                            {
                                tail.TryDequeue(out _);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // **Recorded and retried, not recorded and abandoned.** A
                    // restart tears the watched instance down underneath us and
                    // DCP answers "resource is being deleted"; caught outside the
                    // loop, as it was, that ended the tail for the rest of the run
                    // — #2038's symptom by a third route, on the one event this
                    // loop exists to survive. RecentLogs still reports the record,
                    // so a recovered tail does not hide the gap it left.
                    _logTailFailures[resourceName] = $"{ex.GetType().Name}: {ex.Message}";
                }

                // The stream ended, faulted, or the id we resolved was already
                // stale when we watched it. Wait, then go round and re-resolve —
                // the delay is what bounds the spin, so it has to stay on the
                // re-resolving path.
                //
                // **Recovery from a stale id is reasoned, not observed.** It holds
                // only if WatchAsync(staleId) *completes*, and nothing bounds that
                // inner await foreach: a dead instance whose logger stayed open and
                // silent would park this tail for the rest of the run — #2038's
                // symptom again, arriving via staleness. No such watch has been
                // seen. Left unbounded deliberately: a timeout would force a
                // periodic re-subscribe, and the window between two subscriptions
                // is exactly where the line a test is waiting for would be lost.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            // Resolving ResourceLoggerService, or a delay that faulted past the
            // inner handler. Still must not block startup — but record why, so an
            // empty tail is distinguishable from a broken one.
            _logTailFailures[resourceName] = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task WaitForKeycloakRealmAsync(CancellationToken cancellationToken)
    {
        using HttpClient probe = CreateKeycloakClient();
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                HttpResponseMessage response = await probe.GetAsync(
                    "/realms/smart-sentinel-eye/.well-known/openid-configuration",
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // realm import still in progress
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Keycloak realm 'smart-sentinel-eye' was not reachable after 60 attempts. " +
            "Check the realm-import logs in the Aspire dashboard.");
    }

    private async Task WaitForMediaMtxAsync(CancellationToken cancellationToken)
    {
        using HttpClient probe = App.CreateHttpClient("mediamtx", "api");
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                HttpResponseMessage response = await probe.GetAsync(
                    "/v3/paths/list", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // MediaMTX still booting
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "MediaMTX /v3/paths/list was not reachable after 60 attempts.");
    }

    private async Task WaitForServiceHealthAsync(string resourceName, CancellationToken cancellationToken)
    {
        // Name the endpoint for the same reason the per-API clients do: an
        // unnamed one resolves to whichever endpoint Aspire happens to prefer.
        using HttpClient probe = App.CreateHttpClient(resourceName, "http");
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                HttpResponseMessage response = await probe.GetAsync(
                    "/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // listener not bound yet
            }
            catch (TimeoutRejectedException)
            {
                // Slow to start rather than not started. Aspire's client carries
                // the standard resilience handler, and its timeout surfaces as
                // this rather than as HttpRequestException — so without this the
                // first slow probe escapes a loop written to retry sixty times,
                // faults InitializeAsync, and fails every test in the collection
                // with an error naming none of them.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"{resourceName} /health was not reachable after 60 attempts.");
    }
}

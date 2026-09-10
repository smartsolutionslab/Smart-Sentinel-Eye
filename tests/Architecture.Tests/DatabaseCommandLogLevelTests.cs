using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Guards that a shipped <c>appsettings</c> file which logs at
/// <c>Information</c> does not thereby log every SQL statement the process
/// executes (issues #2133 and #2135, spec 127).
///
/// <para>
/// <b>EF Core emits the SQL text at <c>Information</c>, not at <c>Debug</c>.</b>
/// Read off the pinned 10.0.11 assembly: <c>LogExecutingCommand</c> (20100) is
/// <c>Debug</c>, <c>LogExecutedCommand</c> (20101) — the one carrying the
/// statement — is <c>Information</c>, and <c>LogCommandFailed</c> (20102) is
/// <c>Error</c>. So a <c>"Default": "Information"</c> with nothing said about
/// the command category logs every statement, which is the cost spec 081
/// removed from the eleven Development files and left standing in the thirteen
/// beside them.
/// </para>
///
/// <para>
/// <b>This asks a logger, it does not compare spellings.</b> Each file is bound
/// through <see cref="ConfigurationBuilder"/> exactly as a host binds it, a
/// <see cref="LoggerFactory"/> is built from the result, and the question put
/// to it is <see cref="ILogger.IsEnabled"/> at <c>Information</c> for EF's own
/// category name. A misspelled override — <c>Microsoft.EntityFramework…</c>,
/// <c>…Databases.Command</c> — is silently inert: it satisfies any guard that
/// matches text and fails this one, because the logger it produces still logs
/// the SQL. So does an override set to <c>Information</c>, <c>Debug</c> or
/// <c>Trace</c>.
/// </para>
///
/// <para>
/// <b>It bans a category, never a value.</b> Nothing here requires
/// <c>Warning</c> specifically, and nothing here lists the files in scope. A
/// file enters scope by setting a <c>Default</c> at <c>Information</c> or
/// lower, so a fourteenth service is governed the day it is added rather than
/// the day somebody remembers to extend a list.
/// </para>
///
/// <para>
/// Reads the tree from disk, like <see cref="ContainerImagePinTests"/> and
/// <see cref="LogTailCoverageTests"/>: the files are the artifact, and binding
/// them is cheaper and more faithful than booting the hosts that read them.
/// </para>
/// </summary>
public class DatabaseCommandLogLevelTests
{
    private const string SettingsTree = "src";
    private const string SettingsPattern = "appsettings*.json";

    /// <summary>
    /// EF Core's own category name rather than a string spelled here. The whole
    /// failure this guards against is a spelling that does not match EF's, and a
    /// guard that repeats the spelling under test cannot see it.
    /// </summary>
    private static readonly string SqlCategory = DbLoggerCategory.Database.Command.Name;

    /// <summary>The level <c>CommandExecuted</c> carries the statement at.</summary>
    private const LogLevel SqlLevel = LogLevel.Information;

    /// <summary>
    /// The lowest <c>Default</c> that leaves this file alone. A host logging at
    /// <c>Warning</c> already excludes the SQL, so an override there would assert
    /// a preference rather than prevent a cost.
    /// </summary>
    private const LogLevel QuietEnough = LogLevel.Warning;

    [Fact]
    public void Every_shipped_settings_file_that_logs_at_information_silences_the_sql_command_category()
    {
        Settings[] shipped = ShippedSettings();

        shipped.Length.ShouldBeGreaterThanOrEqualTo(
            20,
            $"the scan of {SettingsTree}/**/{SettingsPattern} found {shipped.Length} files that "
            + "configure a default log level, and there are two dozen. A source-scanning guard that "
            + "matches nothing passes, and a passing guard that checks nothing is indistinguishable "
            + "from one that holds.");

        Settings[] noisy = [.. shipped.Where(settings => settings.LogsSql)];

        noisy.ShouldBeEmpty(Explain(noisy));
    }

    private static string Explain(Settings[] noisy) =>
        $"{noisy.Length} shipped settings file(s) log at {SqlLevel} without silencing "
        + $"'{SqlCategory}', so every SQL statement the process executes reaches the log — EF Core "
        + "emits CommandExecuted (20101), which carries the statement text, at "
        + $"{SqlLevel}:{Environment.NewLine}"
        + string.Join(
            Environment.NewLine,
            noisy.Select(settings =>
                $"  {settings.Path} — Default '{settings.Default}', "
                + $"'{SqlCategory}' {settings.Override ?? "not set"}"))
        + Environment.NewLine
        + $"Add \"{SqlCategory}\": \"Warning\" under Logging:LogLevel, as the eleven "
        + "appsettings.Development.json files already do (spec 081, #1999).";

    /// <summary>
    /// Every settings file in <c>src</c> that configures a default level at
    /// <see cref="SqlLevel"/> or below, with what its bound logger actually does.
    /// </summary>
    private static Settings[] ShippedSettings()
    {
        DirectoryInfo root = RepositoryRoot();

        return
        [
            .. Directory
                .EnumerateFiles(Path.Combine(root.FullName, SettingsTree), SettingsPattern, SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(root, file))
                .Select(file => Read(root, file))
                .OfType<Settings>()
                .OrderBy(settings => settings.Path, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Binds one file and asks its logger, or answers <c>null</c> when the file
    /// says nothing about a default level and so is not this guard's business.
    /// </summary>
    private static Settings? Read(DirectoryInfo root, string file)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(file, optional: false)
            .Build();

        IConfigurationSection levels = configuration.GetSection("Logging:LogLevel");
        string? declared = levels["Default"];

        if (!Enum.TryParse(declared, ignoreCase: true, out LogLevel initial) || initial >= QuietEnough)
        {
            return null;
        }

        // The host's own binding, not a re-reading of the rules. MEL resolves a
        // category against exact keys, then longest prefix, then Default — three
        // rules this guard would otherwise have to restate, and a restatement
        // that drifted would fail files that are correct.
        using ILoggerFactory factory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(new AlwaysEnabledProvider());
        });

        return new Settings(
            Path: Relative(root, file),
            Default: declared,
            Override: levels[SqlCategory],
            LogsSql: factory.CreateLogger(SqlCategory).IsEnabled(SqlLevel));
    }

    private static bool IsBuildOutput(DirectoryInfo root, string file) =>
        Relative(root, file).Contains("/bin/", StringComparison.Ordinal)
        || Relative(root, file).Contains("/obj/", StringComparison.Ordinal);

    /// <summary>
    /// Reported with <c>/</c> throughout. <see cref="Path.GetRelativePath"/>
    /// returns the platform separator, so a backslash in an expected string is
    /// green on Windows and red on Linux CI.
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

    /// <summary>One settings file, and what the logger it configures does.</summary>
    private sealed record Settings(string Path, string Default, string? Override, bool LogsSql);

    /// <summary>
    /// A provider whose loggers enable everything, so the only thing deciding
    /// <see cref="ILogger.IsEnabled"/> is the file's own filter configuration.
    ///
    /// <para>
    /// Not decoration. A <see cref="LoggerFactory"/> with no providers answers
    /// <c>false</c> to every <c>IsEnabled</c> — there is nothing to ask — so a
    /// guard built without one passes on every file, including the thirteen this
    /// spec exists to find.
    /// </para>
    /// </summary>
    private sealed class AlwaysEnabledProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new AlwaysEnabledLogger();

        public void Dispose()
        {
        }

        private sealed class AlwaysEnabledLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}

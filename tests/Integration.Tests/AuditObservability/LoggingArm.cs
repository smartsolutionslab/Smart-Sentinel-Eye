using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// The logging configuration a throughput run was taken under — **both halves,
/// each with its provenance** (spec 127, #2133).
///
/// <para>
/// <b>Two levels, not one, because spec 081 shipped two edits and said neither
/// substitutes for the other.</b> <c>Default</c> silences the Debug categories;
/// the <c>Database.Command</c> override removes the SQL text, which EF Core
/// emits at <c>Information</c>. A figure attributed to "Information" without
/// saying what the command category was doing describes one of two very
/// different configurations, and ADR-0135 measured the other one.
/// </para>
///
/// <para>
/// <b>Provenance travels with each level for the reason
/// <see cref="IngestRunConditions.LogLevelWasChosen"/> gives.</b> A level nobody
/// picked for this run is whatever the appsettings happen to pin on the day, and
/// the spelling alone cannot tell an inheritance from a decision. Here it is
/// stronger than a caveat: the three arms this fact exists to compare are
/// distinguished *only* by these two variables, so a run that inherited either
/// one is not the arm its operator believes they ran.
/// </para>
/// </summary>
public sealed record LoggingArm(string Default, bool DefaultWasChosen, string Command, bool CommandWasChosen)
{
    /// <summary>The environment key the fixture's child processes read.</summary>
    public const string DefaultKey = "Logging__LogLevel__Default";

    /// <summary>
    /// The same, for EF's command category — built from EF's own category name
    /// rather than a spelling repeated here, since a spelling that does not match
    /// EF's is silently inert and is exactly what this is meant to catch.
    /// </summary>
    public static readonly string CommandKey =
        "Logging__LogLevel__" + DbLoggerCategory.Database.Command.Name;

    /// <summary>
    /// What the level falls back to when nobody set the variable. Named only as a
    /// fallback: what the appsettings pin is not this type's to know, and a
    /// sentence claiming otherwise goes false the moment those files move — which
    /// is the drift ADR-0135's amendment records.
    /// </summary>
    private const string Inherited = "(inherited)";

    public static LoggingArm Read()
    {
        string? chosenDefault = Environment.GetEnvironmentVariable(DefaultKey);
        string? chosenCommand = Environment.GetEnvironmentVariable(CommandKey);

        return new LoggingArm(
            Default: Named(chosenDefault),
            DefaultWasChosen: WasChosen(chosenDefault),
            Command: Named(chosenCommand),
            CommandWasChosen: WasChosen(chosenCommand));
    }

    /// <summary>Whether both halves were picked for this run.</summary>
    public bool IsAttributable => DefaultWasChosen && CommandWasChosen;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         default log level                     : {Provenance(Default, DefaultWasChosen)}
         {DbLoggerCategory.Database.Command.Name,-38}: {Provenance(Command, CommandWasChosen)}
         """);

    /// <summary>
    /// <b>Blank is not a choice.</b> <c>KEY=</c> yields <c>""</c> rather than
    /// <c>null</c>, and an empty value binds to no level at all — the services
    /// stay on whatever the appsettings pin. Read for nullness alone this would
    /// report a choice and attribute the run to an arm nobody ran.
    /// </summary>
    private static bool WasChosen(string? chosen) => !string.IsNullOrWhiteSpace(chosen);

    private static string Named(string? chosen) => WasChosen(chosen) ? chosen! : Inherited;

    private static string Provenance(string level, bool wasChosen) =>
        wasChosen ? $"{level} (chosen for this run)" : $"{level} — nobody chose it for this run";
}

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.MigrationRunner;

/// <summary>
/// Source-generated log methods for the MigrationRunner host (ADR-0050).
/// One-time startup logs, but kept on the same `[LoggerMessage]` pattern
/// as the rest of the solution for consistency.
/// </summary>
[ExcludeFromCodeCoverage] // source-generated logging glue, not business logic
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Running migrations for {Context}.")]
    public static partial void RunningMigrations(this ILogger logger, string context);

    [LoggerMessage(Level = LogLevel.Information, Message = "All migrations applied; MigrationRunner exiting.")]
    public static partial void AllMigrationsApplied(this ILogger logger);

    // A migration said something it wanted heard — the fab backfills raise one
    // naming how many rows they attributed. Warning, not Debug: at Debug the
    // default filter hides it and the message may as well not exist (#1394).
    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL: {Message} (SQLSTATE {SqlState})")]
    public static partial void PostgresWarning(this ILogger logger, string message, string sqlState);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PostgreSQL {Severity}: {Message}")]
    public static partial void PostgresNotice(this ILogger logger, string severity, string message);

    // Spec 019 FR-005. Warning rather than Information: a group under /fabs
    // that is not a usable fab name gets no event storage, so anyone assigned
    // to it loses every event they file. Skipping it keeps the other fabs
    // provisioned; saying nothing would make that loss unattributable.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unusable fab group name(s) under '{ParentPath}': {Names}. No event storage is provisioned for them.")]
    public static partial void UnusableFabGroupNames(this ILogger logger, string names, string parentPath);

    // Issue #2062, deliverable A. Critical because every service in the stack
    // is gated on this run: when it fails, this line is the only account of
    // why any of them failed to start.
    [LoggerMessage(Level = LogLevel.Critical, Message = "Migrations failed at {Context}. MigrationRunner is exiting non-zero; nothing after this point was migrated.")]
    public static partial void MigrationRunFailed(this ILogger logger, string context, Exception exception);

    // Issue #2062, deliverable A. Warning rather than Critical: the run stopped
    // because it was told to, and nothing after this point was migrated either —
    // but a stack that is shutting down does not need the same alarm as one whose
    // migrations broke. Sharing MigrationRunFailed's line would report a fault
    // that did not happen.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Migrations were stopped at {Context} before finishing. MigrationRunner is exiting non-zero; nothing after this point was migrated.")]
    public static partial void MigrationRunStopped(this ILogger logger, string context, Exception cancelled);

    // Issue #2062. Error rather than Critical: by the time this can fire the
    // migrations have already reported their own verdict, and a host that
    // stumbles on the way out has not undone them. It is still said out loud,
    // because it was previously the one way left for this process to end
    // through the runtime with its reason on stderr.
    [LoggerMessage(Level = LogLevel.Error, Message = "MigrationRunner failed to shut its host down cleanly. The migration result above still stands.")]
    public static partial void MigrationHostShutdownFailed(this ILogger logger, Exception exception);
}

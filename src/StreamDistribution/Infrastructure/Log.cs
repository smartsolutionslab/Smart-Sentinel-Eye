using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Infrastructure;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaMtxReconciler startup pass failed; continuing without reconcile.")]
    public static partial void ReconcilerStartupPassFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciler removed orphan MediaMTX path {Path}.")]
    public static partial void ReconcilerRemovedOrphanPath(this ILogger logger, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciler failed to remove orphan path {Path}; will retry on next restart.")]
    public static partial void ReconcilerFailedToRemoveOrphanPath(this ILogger logger, Exception exception, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciler re-added missing MediaMTX path {Path} -> {SourceUrl}.")]
    public static partial void ReconcilerReaddedMissingPath(this ILogger logger, MediaMtxPath path, string sourceUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciler failed to re-add missing path {Path}; will retry on next restart.")]
    public static partial void ReconcilerFailedToReaddMissingPath(this ILogger logger, Exception exception, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "MediaMtxReconciler startup pass complete. Configured={Configured}, expected={Expected}, removed={Removed}, readded={Readded}.")]
    public static partial void ReconcilerStartupPassComplete(this ILogger logger, int configured, int expected, int removed, int readded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream fab attribution failed; streams without a fab stay invisible and the next restart retries.")]
    public static partial void AttributionPassFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attributed {Attributed} stream(s) to a fab; {Unresolved} could not be resolved.")]
    public static partial void AttributionPassComplete(this ILogger logger, int attributed, int unresolved);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered MediaMTX path {Path} -> {Source}.")]
    public static partial void RegisteredMediaMtxPath(this ILogger logger, MediaMtxPath path, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-pointed MediaMTX path {Path} at {Source}.")]
    public static partial void RepointedMediaMtxPath(this ILogger logger, MediaMtxPath path, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed MediaMTX path {Path}.")]
    public static partial void RemovedMediaMtxPath(this ILogger logger, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying StreamDistribution EF Core migrations.")]
    public static partial void ApplyingMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "StreamDistribution migrations applied.")]
    public static partial void MigrationsApplied(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "StreamHealthWatcher started (poll every {Interval}).")]
    public static partial void StreamHealthWatcherStarted(this ILogger logger, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "StreamHealthWatcher poll iteration failed; will retry next tick.")]
    public static partial void StreamHealthWatcherPollFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaMTX health probe failed for path {Path}; skipping this tick.")]
    public static partial void HealthProbeFailed(this ILogger logger, Exception exception, MediaMtxPath path);

    // Every WHEP open is refused for as long as this holds, so the exception is
    // carried: IDX20803 names the address that could not be reached and wraps the
    // transport failure that says why — DNS, refused, or TLS (spec 119 FR-005).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The realm's OIDC discovery document could not be obtained; no WHEP bearer token can be checked until it can. Every viewer stays refused, and this is not a bad credential.")]
    public static partial void WhepIdentityProviderUnreachable(this ILogger logger, Exception exception);
}

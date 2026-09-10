using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.SystemVariables.Infrastructure;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "ReverseIndex seed: overlay-designer returned {Status}; starting with empty index. The index will populate as new OverlayRevisionPublishedV1 events arrive.")]
    public static partial void SeedNonSuccessStatus(this ILogger logger, HttpStatusCode status);

    /// <summary>
    /// Error, not Warning, and without the self-heal sentence its neighbour
    /// carries: a refused credential is not an outage. No republish repairs it,
    /// because the overlays that were already published raise no event — so the
    /// index stays empty for the life of the host, and every variable change
    /// resolves against nothing. The transient case never reaches here anyway:
    /// ADR-0143 retries GET, so a Keycloak blip is absorbed upstream.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "ReverseIndex seed: overlay-designer refused the system-variables-seeder credential ({Status}). The index is empty and stays empty until the credential is fixed and the service restarted. Check the client's secret and its sse.overlays.read scope.")]
    public static partial void SeedRefused(this ILogger logger, HttpStatusCode status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReverseIndex seed: response missing 'published' key; index left empty.")]
    public static partial void SeedMissingPublishedKey(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "ReverseIndex seeded with {Count} published overlays.")]
    public static partial void SeededOverlays(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReverseIndex seed failed; starting with empty index. Self-heal will kick in as overlay V1 events arrive.")]
    public static partial void SeedFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying SystemVariables EF Core migrations.")]
    public static partial void ApplyingMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "SystemVariables migrations applied.")]
    public static partial void MigrationsApplied(this ILogger logger);
}

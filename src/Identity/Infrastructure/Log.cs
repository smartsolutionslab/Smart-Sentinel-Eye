using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.Identity.Infrastructure;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "DisableClientAsync('{ClientId}'): no such Keycloak client; treating as no-op.")]
    public static partial void DisableClientNoOp(this ILogger logger, string clientId);

    // Spec 019. Not an error here — the caller decides what an absent parent
    // means. It means something for '/fabs' (no fab can be provisioned) and
    // nothing for a path nobody has created yet.
    [LoggerMessage(Level = LogLevel.Warning, Message = "No group at path '{ParentPath}' in realm '{Realm}'; no sub-groups to report.")]
    public static partial void FabGroupParentMissing(this ILogger logger, string parentPath, string realm);

    // Spec 052. Warning rather than error: the enrolment is already failing and
    // will say so, and the startup sweep removes the privilege from whatever is
    // left behind. It is logged at all because a client nobody finished setting
    // up should not go unmentioned.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove half-enrolled Keycloak client {ClientUuid}; the startup sweep will strip its privileges.")]
    public static partial void CouldNotRemoveHalfEnrolledClient(this ILogger logger, string clientUuid, Exception exception);

    // Spec 092, and deliberately next to the message above: that one says a
    // half-enrolled client was left behind and the startup sweep will strip it,
    // this one says the startup sweep could not run. Whoever reads the first
    // needs the second to be in the same place. Warning rather than error — the
    // API is serving, and the next start tries again.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The kiosk privilege startup sweep could not complete; enrolled kiosks may still hold inherited realm privileges until the next start.")]
    public static partial void KioskPrivilegeSweepFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying Identity EF Core migrations.")]
    public static partial void ApplyingMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Identity migrations applied.")]
    public static partial void MigrationsApplied(this ILogger logger);
}

using System.Diagnostics.CodeAnalysis;
using System.Net;
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

    // Spec 052, rewritten by spec 122 (#2166). Three arms for one question —
    // did the compensating delete remove the half-made client? — because the
    // answers are different news and a single hedged message would be checkable
    // against neither. Measured against Keycloak 26.6: success is 204 with an
    // empty body, an unknown client or realm is 404, everything else is a
    // refusal.
    //
    // Error, not the Warning this was until spec 122. The original level rested
    // on the claim in TryDeleteClientAsync's comment — that the startup sweep
    // catches a client left behind — and a Warning is right for something the
    // system clears itself. Having read the sweep: KioskPrivilegeSweep strips a
    // residue's realm roles and removes nothing, and its enrolled-kiosk query
    // cannot tell a residue from a healthy kiosk. So the client survives every
    // sweep, the existence probe keeps answering already-enrolled for it, and
    // that kiosk cannot be enrolled again until a human deletes it by hand.
    // Only manual intervention clears this.
    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak refused the compensating delete of half-enrolled client '{ClientId}' ({ClientUuid}) with {StatusCode}. The client is still in the realm holding its inherited realm roles, so enrolling this kiosk again reports already-enrolled until it is deleted by hand; the startup sweep strips its privileges but does not remove it.")]
    public static partial void HalfEnrolledClientSurvived(
        this ILogger logger, string clientId, string clientUuid, HttpStatusCode statusCode);

    // The exception arm of the message above: the delete did not reach Keycloak
    // at all, so the client is still there for the same reason and at the same
    // level. Worded to match, because whoever greps one wants the other.
    [LoggerMessage(Level = LogLevel.Error, Message = "The compensating delete of half-enrolled client '{ClientId}' ({ClientUuid}) failed. The client is still in the realm holding its inherited realm roles, so enrolling this kiosk again reports already-enrolled until it is deleted by hand; the startup sweep strips its privileges but does not remove it.")]
    public static partial void CouldNotRemoveHalfEnrolledClient(
        this ILogger logger, string clientId, string clientUuid, Exception exception);

    // The third arm, and lower, because 404 is Keycloak saying there is no such
    // client — the compensation's goal, reached. Reporting a surviving residue
    // here would send an operator hunting something that is not there. Still
    // logged: the post-create re-probe found this client a call earlier.
    //
    // A vanished realm answers 404 too ("Realm not found." rather than "Could
    // not find client"). Splitting them means parsing an error body for a case
    // in which the realm disappeared mid-enrolment, which the caller's own
    // failure already describes.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The compensating delete of half-enrolled client '{ClientId}' ({ClientUuid}) found nothing to remove; Keycloak reports no such client.")]
    public static partial void HalfEnrolledClientWasAlreadyAbsent(
        this ILogger logger, string clientId, string clientUuid);

    // Spec 092, and deliberately next to the three messages above: those say a
    // half-enrolled client was left behind and the startup sweep will strip its
    // privileges, this one says the startup sweep could not run at all. Whoever
    // reads one wants the other in the same place. Warning rather than error — the
    // API is serving, and the next start tries again.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The kiosk privilege startup sweep could not complete; enrolled kiosks may still hold inherited realm privileges until the next start.")]
    public static partial void KioskPrivilegeSweepFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying Identity EF Core migrations.")]
    public static partial void ApplyingMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Identity migrations applied.")]
    public static partial void MigrationsApplied(this ILogger logger);
}

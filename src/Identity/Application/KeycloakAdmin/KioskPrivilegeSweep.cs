using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.Identity.Application.KeycloakAdmin;

/// <summary>
/// Takes the realm's inherited privileges off every kiosk account this system
/// enrolled (spec 052 US1).
///
/// <para>
/// <b>Why a sweep and not only the enrolment path.</b> Not, any more, for the
/// reason first written here: spec 092 asked every realm this system has —
/// the persistent one, three orphaned volumes, CI, e2e — and found no client
/// carrying <c>sse.kind</c> anywhere, so the backfill population that
/// justification named is empty and cannot refill by import.
/// </para>
///
/// <para>
/// <b>The live reason is one path forward, not a population behind.</b> Enrolment
/// strips inside the create and deletes the client when the strip throws, but
/// that delete is best effort — see <c>HttpKeycloakAdminClient</c>'s
/// <c>TryDeleteClientAsync</c>, whose comment delegates the case here by name.
/// When it also fails, a client stamped <c>sse.kind=kiosk</c> survives holding
/// the privilege, with a service account and a secret the caller never
/// received, and the existence probe answers already-enrolled for it forever.
/// Enrolment reported failure, so nobody retries. This is the backstop.
/// </para>
///
/// <para>
/// <b>Safe to run on every start.</b> The removal is idempotent, so this doubles
/// as reconciliation: an account that somehow regains the privilege loses it at
/// the next boot.
/// </para>
///
/// <para>
/// <b>Bounded to accounts enrolment created</b>, because the removal takes away
/// every directly-assigned realm privilege. Applied to a person's account that
/// would strip them of everything, and it would not fail while doing it.
/// </para>
/// </summary>
public sealed class KioskPrivilegeSweep(
    IKeycloakAdminClient keycloak,
    ILogger<KioskPrivilegeSweep> logger)
{
    /// <summary>
    /// Strips every enrolled kiosk, and reports how many were reached.
    ///
    /// <para>
    /// One kiosk failing does not stop the rest: the others are independent, and
    /// leaving them holding the privilege because a different account could not
    /// be read would be the wrong trade. Each failure is logged, and the next
    /// start tries again.
    /// </para>
    /// </summary>
    public async Task<KioskSweepOutcome> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> kiosks = await keycloak.GetEnrolledKioskClientIdsAsync(cancellationToken);

        List<string> unreachable = [];
        foreach (string clientId in kiosks)
        {
            try
            {
                await keycloak.StripInheritedRealmRolesAsync(clientId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unreachable.Add(clientId);
                logger.CouldNotSweepKiosk(clientId, exception);
            }
        }

        if (kiosks.Count > 0)
        {
            logger.SweptKioskPrivileges(kiosks.Count - unreachable.Count, kiosks.Count);
        }

        // Silent otherwise on purpose (spec 092). Now that this runs on every
        // Identity start, an empty realm is what every start in every
        // environment that exists today looks like — and a line per restart
        // saying nothing happened trains an operator to skip the one that
        // matters. StreamFabAttributionService made the same call.
        return new KioskSweepOutcome(kiosks.Count, unreachable);
    }
}

/// <summary>
/// What a sweep reached. <paramref name="Unreachable"/> is named rather than
/// counted so a caller can say which kiosk still holds the privilege.
/// </summary>
public sealed record KioskSweepOutcome(int KioskCount, IReadOnlyList<string> Unreachable)
{
    public int StrippedCount => KioskCount - Unreachable.Count;
}

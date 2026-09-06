using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.MigrationRunner;

/// <summary>
/// Answers "which fabs exist" from the realm's <c>/fabs</c> group tree
/// (spec 019 FR-001).
///
/// <para>
/// This class is the only place in the system where EventIngestion's question
/// and Identity's answer meet, and it lives here for a reason that is easy to
/// lose: <c>AllowedCrossContext</c> in <c>BoundaryTests</c> is empty, so no
/// bounded context may reference another at any layer. MigrationRunner is not a
/// bounded context — it is the composition root for migrations (ADR-0067) and
/// already references all nine. Moving this file into either context would be
/// a boundary violation that the architecture test fails on.
/// </para>
/// </summary>
internal sealed class KeycloakProvisionedFabSource(
    IKeycloakAdminClient keycloak,
    ILogger<KeycloakProvisionedFabSource> logger) : IProvisionedFabSource
{
    /// <summary>The group whose children are the fabs.</summary>
    private const string FabGroupPath = "/fabs";

    /// <summary>
    /// How long the group tree may stay empty before that is taken as the
    /// answer rather than as "not yet". MigrationRunner is gated on Keycloak's
    /// <c>/health/ready</c>, which is not the same fact as "the realm is
    /// queryable" — <c>AspireFixture.WaitForKeycloakRealmAsync</c> polls the
    /// realm separately for exactly that reason. Half its 60 s, because that
    /// budget covers a cold container boot as well as the import and this one
    /// starts after readiness.
    /// </summary>
    private static readonly TimeSpan RealmWaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>Matches the fixture's poll interval; nothing here is urgent.</summary>
    private static readonly TimeSpan RealmPollInterval = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<FabIdentifier>> GetFabsAsync(CancellationToken cancellationToken)
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (true)
        {
            // Not caught: an unreachable realm must fail the run rather than
            // provision nothing and report success (FR-011). "There are no fabs"
            // and "I could not tell" are the same value and opposite facts.
            IReadOnlyList<string> names =
                await keycloak.GetSubGroupNamesAsync(FabGroupPath, cancellationToken);

            // An answer with children in it is an answer: whatever those names
            // are, the tree is there and re-asking returns the same thing.
            if (names.Count > 0)
            {
                return Usable(names);
            }

            if (waited.Elapsed >= RealmWaitBudget)
            {
                throw new InvalidOperationException(
                    $"Nothing at all under '{FabGroupPath}' after waiting " +
                    $"{RealmWaitBudget.TotalSeconds:F0}s for the realm's group tree to answer. " +
                    "Provisioning cannot continue: every event written by any fab would be lost, " +
                    "and proceeding would report success while doing nothing.");
            }

            // Said out loud, every attempt: from outside the process a pause and
            // a hang are the same thing, and nine services are gated on this one.
            logger.WaitingForFabGroups(
                FabGroupPath, waited.Elapsed.TotalSeconds, RealmWaitBudget.TotalSeconds);

            await Task.Delay(RealmPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// The fabs among <paramref name="names"/>, or a failed run. Reached only
    /// once the group tree has answered with something, so the verdict here is
    /// about the names rather than about the realm's readiness.
    /// </summary>
    private List<FabIdentifier> Usable(IReadOnlyList<string> names)
    {
        List<FabIdentifier> fabs = [];
        List<string> unusable = [];
        foreach (string name in names)
        {
            try
            {
                FabIdentifier fab = FabIdentifier.From(name);
                if (!fabs.Contains(fab))
                {
                    fabs.Add(fab);
                }
            }
            catch (ArgumentException)
            {
                // Skipped, not fatal (FR-005): one group somebody named badly
                // must not stop every other fab from getting its storage. It is
                // still reported, because silently ignoring it is how a fab ends
                // up unable to store anything with nobody knowing why.
                unusable.Add(name);
            }
        }

        if (unusable.Count > 0)
        {
            logger.UnusableFabGroupNames(string.Join(", ", unusable), FabGroupPath);
        }

        if (fabs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No usable fab found under '{FabGroupPath}' in the realm. Provisioning cannot " +
                "continue: every event written by any fab would be lost, and proceeding would " +
                "report success while doing nothing.");
        }

        return fabs;
    }
}

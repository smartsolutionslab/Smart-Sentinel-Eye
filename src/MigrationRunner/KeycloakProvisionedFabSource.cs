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
///
/// <para>
/// The question is asked once, because a second ask cannot answer differently.
/// <c>/fabs</c> and its children are imported with the realm, from the same
/// document as the <c>migration-runner</c> client whose token this call needs,
/// and Keycloak binds its HTTP listener only after that import has finished —
/// so a caller that can reach the admin API at all is talking to a realm whose
/// groups are already there. Nothing creates a fab group at runtime either.
/// An empty tree is therefore a verdict, not a "not yet".
/// </para>
/// </summary>
internal sealed class KeycloakProvisionedFabSource(
    IKeycloakAdminClient keycloak,
    ILogger<KeycloakProvisionedFabSource> logger) : IProvisionedFabSource
{
    /// <summary>The group whose children are the fabs.</summary>
    private const string FabGroupPath = "/fabs";

    public async Task<IReadOnlyList<FabIdentifier>> GetFabsAsync(CancellationToken cancellationToken)
    {
        // Not caught: an unreachable realm must fail the run rather than
        // provision nothing and report success (FR-011). "There are no fabs"
        // and "I could not tell" are the same value and opposite facts.
        IReadOnlyList<string> names =
            await keycloak.GetSubGroupNamesAsync(FabGroupPath, cancellationToken);

        if (names.Count == 0)
        {
            // Separated from the verdict below because the two send the reader
            // to different places: nothing under '/fabs' is a question about
            // the realm, while nothing usable is a question about the names in
            // it. Collapsing them is what made the run's own account of itself
            // unhelpful.
            throw new InvalidOperationException(
                $"Nothing at all under '{FabGroupPath}' in the realm: the group is absent, or " +
                "present with no children. It is imported with the realm and nothing creates it " +
                "later, so this is a realm that was imported without fabs rather than one still " +
                "importing. Provisioning cannot continue: every event written by any fab would " +
                "be lost, and proceeding would report success while doing nothing.");
        }

        return Usable(names);
    }

    /// <summary>
    /// The fabs among <paramref name="names"/>, or a failed run. Reached only
    /// once the group tree has answered with something, so the verdict here is
    /// about the names rather than about the realm.
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
                $"No usable fab found under '{FabGroupPath}' in the realm: {names.Count} group(s) " +
                $"are there and none is a usable fab name ({string.Join(", ", unusable)}). " +
                "Provisioning cannot continue: every event written by any fab would be lost, " +
                "and proceeding would report success while doing nothing.");
        }

        return fabs;
    }
}

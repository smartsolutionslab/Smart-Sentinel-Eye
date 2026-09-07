using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SmartSentinelEye.Identity.Infrastructure;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.Identity;

/// <summary>
/// Spec 092 US1 — <b>the load-bearing red.</b> A kiosk left behind by an
/// enrolment that could not roll itself back loses the long-lived-credential
/// privilege when Identity next starts, and an account this system did not
/// enrol loses nothing.
///
/// <para>
/// <b>The failure this covers is live, and the production code delegates it here
/// by name.</b> <c>HttpKeycloakAdminClient.CreateClientAsync</c> strips inside
/// the create and deletes the client if the strip throws — but that delete is
/// best effort, and when it also fails a client stamped <c>sse.kind=kiosk</c>
/// survives holding <c>offline_access</c>, with a service account and a secret
/// the caller never received. The comment at
/// <c>HttpKeycloakAdminClient.cs:353</c> says the startup sweep is the backstop
/// for exactly that. The sweep has never run: it is registered nowhere (#2132).
/// </para>
///
/// <para>
/// <b>Why the pass is driven through Identity's own registration.</b> The
/// defect is that nothing composes <c>KioskPrivilegeSweep</c>, so a test that
/// constructs it proves the opposite of what is at issue — which is precisely
/// what its five unit tests do, all five green over a class wired to nothing.
/// Identity runs in its own process and that process's container is not
/// addressable from here, so what is driven is the registration extension the
/// process calls: <c>AddIdentityInfrastructure</c>, composed in this process
/// against the fixture's real Keycloak, with only the startup services Identity
/// itself registers started. Remove the registration and these tests fail at
/// <c>startupServices.ShouldNotBeEmpty</c>.
/// </para>
///
/// <para>
/// Driving one pass rather than restarting the resource is the established
/// pattern — <c>StreamFabAttributionService.AttributeOnceAsync</c> is public for
/// the same reason, and the fixture boots once per collection.
/// </para>
///
/// <para>
/// <b>What these two tests do NOT prove, and nothing here should be read as
/// proving:</b> that the pass ran <i>at boot</i>, in a real stack. They start
/// the startup service themselves. Only the sweep's own log line, read out of a
/// running Identity API with a residue planted first, shows that — spec 092
/// phase 5, step 8. The residue has to be planted first because the steady-state
/// line is silenced, so an empty realm produces no output and would read as
/// success.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class KioskPrivilegeSweepStartupIntegrationTests(AspireFixture aspire)
{
    private static readonly IReadOnlyDictionary<string, string> KioskStamp =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sse.kind"] = "kiosk",
            ["sse.fab"] = "munich",
        };

    /// <summary>An operator-shaped account: no <c>sse.kind</c>, nothing enrolment made.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoStamp =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task A_kiosk_a_failed_enrolment_left_behind_loses_the_privilege_when_identity_starts()
    {
        RealmProbe realm = new(aspire);
        string residue = $"kiosk-residue-probe-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = CancellationToken.None;

        await realm.PlantAsync(residue, KioskStamp, cancellationToken);
        try
        {
            IReadOnlyList<string> before = await realm.EffectiveRealmRolesAsync(residue, cancellationToken);
            before.ShouldContain(
                RealmProbe.LongLivedCredentialPrivilege,
                "the provider grants this by default to accounts it creates after import; if this "
                + "residue never held it, the assertion below would pass against a sweep that "
                + "removes nothing and the whole test would prove nothing");

            await DriveIdentityStartupAsync(cancellationToken);

            IReadOnlyList<string> after = await realm.EffectiveRealmRolesAsync(residue, cancellationToken);
            after.ShouldNotContain(
                RealmProbe.LongLivedCredentialPrivilege,
                "a client stamped sse.kind=kiosk that enrolment could not delete keeps a privilege "
                + "that mints credentials which never expire, and the existence probe answers "
                + "already-enrolled for it forever. Identity's startup pass is what takes it back "
                + "(ADR-0134 Decision 1).");
        }
        finally
        {
            await realm.DeleteAsync(residue, cancellationToken);
        }
    }

    /// <summary>
    /// <b>The control, and the assertion that can fail dangerously.</b> The
    /// removal takes away <i>every</i> directly-assigned realm role and does not
    /// throw while doing it, so a pass whose idea of "a kiosk" matched
    /// everything would strip an operator bare and the test above would pass on
    /// the way past.
    ///
    /// <para>
    /// A residue is planted alongside and asserted stripped, so this cannot be
    /// satisfied by a pass that never ran: over an empty realm the bystander is
    /// untouched for the wrong reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_account_this_system_did_not_enrol_keeps_every_realm_role_it_held()
    {
        RealmProbe realm = new(aspire);
        string residue = $"kiosk-residue-probe-{Guid.CreateVersion7():N}";
        string bystander = $"operator-shaped-probe-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = CancellationToken.None;

        await realm.PlantAsync(residue, KioskStamp, cancellationToken);
        await realm.PlantAsync(bystander, NoStamp, cancellationToken);
        try
        {
            IReadOnlyList<string> before = await realm.EffectiveRealmRolesAsync(bystander, cancellationToken);
            before.ShouldNotBeEmpty("an account holding nothing cannot be shown to keep what it held");

            await DriveIdentityStartupAsync(cancellationToken);

            IReadOnlyList<string> residueAfter =
                await realm.EffectiveRealmRolesAsync(residue, cancellationToken);
            residueAfter.ShouldNotContain(
                RealmProbe.LongLivedCredentialPrivilege,
                "the pass has to have done something, or the bystander below is unchanged for the "
                + "wrong reason");

            IReadOnlyList<string> bystanderAfter =
                await realm.EffectiveRealmRolesAsync(bystander, cancellationToken);
            bystanderAfter.ShouldBe(
                before,
                ignoreOrder: true,
                "the pass is bounded to what enrolment stamped. An account it did not create must "
                + "come out of a pass holding exactly what it went in holding — the removal is "
                + "total and silent, so nothing else would report this.");
        }
        finally
        {
            await realm.DeleteAsync(residue, cancellationToken);
            await realm.DeleteAsync(bystander, cancellationToken);
        }
    }

    /// <summary>
    /// One pass, through the wiring rather than around it. Every startup service
    /// <c>AddIdentityInfrastructure</c> registers from Identity's own assembly is
    /// started; today there are none, which is the defect.
    /// </summary>
    private async Task DriveIdentityStartupAsync(CancellationToken cancellationToken)
    {
        using HttpClient keycloak = aspire.CreateKeycloakClient();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Keycloak is the real one behind the fixture. The other two are read
        // while AddIdentityInfrastructure registers and never dialled: the host
        // is not started, so Wolverine's broker and the DbContext's server are
        // only ever parsed. Identity's startup pass touches neither.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:keycloak"] = keycloak.BaseAddress!.ToString(),
            ["ConnectionStrings:identity-db"] =
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:rabbitmq"] = "amqp://unused:unused@127.0.0.1:5672",
            ["Keycloak:Realm"] = RealmProbe.Realm,
            ["Keycloak:AdminClientId"] = RealmProbe.AdminClientId,
            ["Keycloak:AdminClientSecret"] = RealmProbe.AdminClientSecret,
        });

        // Keycloak exposes https only and presents the ASP.NET dev certificate,
        // trusted on a developer machine and not on CI — the same reason
        // AspireFixture.CreateKeycloakClient states.
        builder.Services.ConfigureHttpClientDefaults(http =>
            http.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }));

        builder.AddIdentityInfrastructure();

        Type[] startupServices = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Assembly == typeof(IdentityInfrastructureModule).Assembly)
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

        startupServices.ShouldNotBeEmpty(
            "AddIdentityInfrastructure registers no startup service of Identity's own, so nothing "
            + "drives KioskPrivilegeSweep when the Identity API starts and a residue kiosk keeps "
            + "offline_access for as long as the realm lives (#2132). The class has existed since "
            + "spec 052 and has never been composed by anything.");

        // ValidateScopes, so a startup service that took the scoped
        // IKeycloakAdminClient straight into its constructor fails here rather
        // than at the first boot of the real host.
        await using ServiceProvider provider = builder.Services
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        foreach (Type startupService in startupServices)
        {
            IHostedService service = (IHostedService)ActivatorUtilities.CreateInstance(provider, startupService);
            await service.StartAsync(cancellationToken);
        }
    }
}

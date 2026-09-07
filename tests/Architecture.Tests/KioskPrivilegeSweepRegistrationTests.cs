using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure;

namespace SmartSentinelEye.Architecture.Tests;

/// <summary>
/// Spec 092 US1, Red B — <b>declaration only</b>, exactly as spec 052's T010
/// labelled its realm-file guard.
///
/// <para>
/// <b>What this proves: that the registration exists. Nothing else.</b> It reads
/// a service collection. It does not show that the Identity API started, that
/// the pass ran, or that any account lost anything. A green run here over a
/// realm full of residue kiosks is entirely possible, and would say only that
/// the wiring is declared.
/// </para>
///
/// <para>
/// <b>Why a weak test is here at all.</b> <c>KioskPrivilegeSweep</c> shipped with
/// spec 052 and was registered nowhere for the whole of its life (#2132); its
/// five unit tests are green because every one of them constructs the class,
/// which is precisely what nothing in production does. Startup wiring is not
/// visible from a test that drives the pass itself, so this is the layer where
/// it can be asserted — and container introspection is the only evidence
/// available at that layer, not good evidence. Said here rather than left for a
/// reader to infer, because this repository has twice been burned by a guard
/// that read the artefact instead of the system: spec 050's realm-file guard
/// stayed green for a whole feature while its claim was false.
/// </para>
///
/// <para>
/// <b>The evidence that the pass actually runs at boot is the sweep's own log
/// line, read out of a running Identity API with a residue planted first</b>
/// (spec 092, phase 5). Do not offer this file in its place.
/// </para>
/// </summary>
public class KioskPrivilegeSweepRegistrationTests
{
    /// <summary>
    /// The registration <c>Identity/Api/Program.cs</c> calls, and the only line
    /// in that file that decides what starts.
    /// </summary>
    private static IServiceCollection IdentityInfrastructure()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Nothing here is dialled: the collection is inspected and, below, one
        // scoped resolution is performed. Both connection strings exist only
        // because AddIdentityInfrastructure reads them while registering.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:keycloak"] = "http://127.0.0.1:8080",
            ["ConnectionStrings:identity-db"] =
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:rabbitmq"] = "amqp://unused:unused@127.0.0.1:5672",
        });

        builder.AddIdentityInfrastructure();

        return builder.Services;
    }

    /// <summary>
    /// The other caller of <c>AddKeycloakAdminClient</c> —
    /// <c>MigrationRunner/Program.cs</c> composes it on its own, for the realm's
    /// group tree, and nothing else of Identity's.
    /// </summary>
    private static IServiceCollection KeycloakAdminClientAlone()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:keycloak"] = "http://127.0.0.1:8080",
        });

        builder.AddKeycloakAdminClient();

        return builder.Services;
    }

    /// <summary>
    /// <b>Arrives green, and that is correct here.</b> It is not a red-first
    /// assertion for new behaviour — it is a fence around the one line of this
    /// change that nothing else catches. The sweep's registration belongs to
    /// <c>AddIdentityInfrastructure</c>; putting it one method down, in
    /// <c>AddKeycloakAdminClient</c>, is the natural mistake, because that is
    /// where every other Keycloak thing already is. It also compiles, passes
    /// every other test in this file, and gives <c>MigrationRunner</c> a sweep —
    /// in a process that runs to completion and exits, racing the migration it
    /// exists to perform.
    ///
    /// <para>
    /// Spec 092's own risk table records this as the single easiest thing to get
    /// wrong and says "nothing automated catches this". Now something does.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shared_keycloak_registration_starts_nothing_of_identitys()
    {
        KeycloakAdminClientAlone()
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Assembly == typeof(IdentityInfrastructureModule).Assembly)
            .ShouldBeEmpty(
            "MigrationRunner/Program.cs calls AddKeycloakAdminClient on its own and must get a "
            + "Keycloak client and nothing else. A startup service registered there rather than "
            + "in AddIdentityInfrastructure would sweep from a process that runs to completion "
            + "and exits, racing the migration it was started to perform.");
    }

    [Fact]
    public void The_identity_api_registers_a_startup_service_of_its_own()
    {
        Type[] startupServices = IdentityInfrastructure()
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Assembly == typeof(IdentityInfrastructureModule).Assembly)
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

        startupServices.ShouldNotBeEmpty(
            "AddIdentityInfrastructure registers no startup service of Identity's own, so nothing "
            + "drives KioskPrivilegeSweep when the Identity API starts. A client stamped "
            + "sse.kind=kiosk that HttpKeycloakAdminClient.TryDeleteClientAsync could not remove "
            + "keeps offline_access for as long as the realm lives, and the best-effort comment "
            + "in TryDeleteClientAsync (HttpKeycloakAdminClient.cs:363) delegates that case "
            + "here by name.");
    }

    [Fact]
    public void The_sweep_can_be_resolved_from_the_container_that_registration_builds()
    {
        using ServiceProvider provider = IdentityInfrastructure()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetService<KioskPrivilegeSweep>().ShouldNotBeNull(
            "a startup service can only drive the pass if the container it resolves from "
            + "knows how to build it; KioskPrivilegeSweep takes the scoped IKeycloakAdminClient, "
            + "so it is scoped too and is resolved through a scope rather than injected into "
            + "a singleton.");
    }
}

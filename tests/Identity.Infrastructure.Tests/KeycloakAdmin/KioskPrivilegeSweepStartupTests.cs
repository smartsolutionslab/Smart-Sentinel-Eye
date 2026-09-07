using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin;

/// <summary>
/// Spec 092 US1, Red C — <b>Identity must serve requests even when Keycloak is
/// not reachable.</b>
///
/// <para>
/// <c>KioskPrivilegeSweep.SweepAsync</c> guards each per-kiosk strip, but its
/// enumeration — the first statement of <c>SweepAsync</c>,
/// <c>KioskPrivilegeSweep.cs:56</c> — sits <i>outside</i> that try. A provider
/// that is down or refusing throws straight out of the pass. A hosted service
/// that throws from <c>StartAsync</c> stops the host, so wiring the sweep in as
/// written would make the Identity API's boot depend on Keycloak being up: the
/// API would go dark for the duration of a Keycloak restart, and the sweep it
/// was running is a background reconciliation nobody is waiting on.
/// </para>
///
/// <para>
/// Nothing here is dialled. The composition below is Identity's own registration
/// extension with a provider that refuses every call substituted in, and only
/// the startup services Identity itself registers are driven — the host is
/// never started, so Wolverine and EF are composed and left alone.
/// </para>
///
/// <para>
/// <b>What this does not prove.</b> That the pass runs at boot in a real stack.
/// Only the sweep's log line, read out of a running Identity API with a residue
/// planted first, shows that (spec 092, phase 5).
/// </para>
/// </summary>
public class KioskPrivilegeSweepStartupTests
{
    [Fact]
    public async Task A_provider_it_cannot_reach_does_not_stop_the_host_from_starting()
    {
        UnreachableKeycloakAdminClient keycloak = new();
        using ServiceProvider provider = Compose(keycloak, out Type[] startupServices);

        startupServices.ShouldNotBeEmpty(
            "AddIdentityInfrastructure registers no startup service of Identity's own, so there is "
            + "nothing to drive: the pass that would have to survive an unreachable provider does "
            + "not run at all (issue #2132).");

        Exception? thrown = await Record.ExceptionAsync(() => StartAllAsync(startupServices, provider));

        thrown.ShouldBeNull(
            "the enumeration that opens SweepAsync (KioskPrivilegeSweep.cs:56) is outside the "
            + "try, so an unreachable provider throws out of the pass; a startup service that "
            + "lets that escape takes the Identity API down with it, and Identity must serve "
            + "requests whether or not Keycloak is up. The failure belongs in the log, and the "
            + "next start tries again.");


        keycloak.EnumerationAttempts.ShouldBe(
            1,
            "the pass must actually have asked the provider. A startup service that survives by "
            + "never calling Keycloak survives this test too, and sweeps nothing in production.");
    }

    private static async Task StartAllAsync(Type[] startupServices, IServiceProvider provider)
    {
        foreach (Type startupService in startupServices)
        {
            IHostedService service = (IHostedService)ActivatorUtilities.CreateInstance(provider, startupService);
            await service.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Identity's own composition — <c>AddIdentityInfrastructure</c>, the line
    /// <c>Identity/Api/Program.cs</c> calls — with the provider replaced.
    /// Constructing <c>KioskPrivilegeSweep</c> directly here would test the class
    /// and not the wiring, which is the whole of #2132.
    /// </summary>
    private static ServiceProvider Compose(
        IKeycloakAdminClient keycloak, out Type[] startupServices)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Read while registering, and never connected to: the host is not
        // started, so Wolverine's broker and the DbContext's server are only
        // ever parsed.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:keycloak"] = "http://127.0.0.1:8080",
            ["ConnectionStrings:identity-db"] =
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:rabbitmq"] = "amqp://unused:unused@127.0.0.1:5672",
        });

        builder.AddIdentityInfrastructure();

        // Last registration wins, so this is the client the sweep resolves.
        builder.Services.AddScoped(_ => keycloak);

        startupServices = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType is not null
                && descriptor.ImplementationType.Assembly == typeof(IdentityInfrastructureModule).Assembly)
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

        // ValidateScopes, so a startup service that took the scoped
        // IKeycloakAdminClient straight into its constructor fails here rather
        // than at the first boot of the real host.
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

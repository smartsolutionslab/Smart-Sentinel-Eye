using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;

namespace SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;

/// <summary>
/// Drives <see cref="KioskPrivilegeSweep"/> once when the Identity API starts
/// (spec 092; ADR-0134 Decision 1, which names a startup sweep and had no
/// registration behind it until this).
///
/// <para>
/// <b>What it is a backstop for.</b> Enrolment strips the realm's inherited
/// privilege inside the create, and deletes the client when that strip throws —
/// but the delete is best effort. When it also fails, a client stamped
/// <c>sse.kind=kiosk</c> survives holding a privilege that mints credentials
/// which never expire, with a secret the caller never received, and the
/// existence probe answers already-enrolled for it forever. See
/// <see cref="HttpKeycloakAdminClient"/>'s <c>TryDeleteClientAsync</c>, whose
/// comment delegates that case here by name.
/// </para>
///
/// <para>
/// <b>A wrapper rather than making the pass itself a hosted service.</b> That
/// would drag <c>Microsoft.Extensions.Hosting</c> into Application and force the
/// pass to take a scope factory instead of the collaborator it actually uses.
/// <see cref="Attribution.StreamFabAttributionService"/>'s shape, for the same
/// reasons.
/// </para>
/// </summary>
public sealed class KioskPrivilegeSweepHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<KioskPrivilegeSweepHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Chosen, not inherited. The enumeration inside the pass is not
            // guarded — a provider that is down or refusing throws straight out
            // of it — and a hosted service that lets that escape stops the host.
            // Identity must serve requests whether or not Keycloak is up: the
            // sweep is a background reconciliation nobody is waiting on, and the
            // next start tries again.
            logger.KioskPrivilegeSweepFailed(exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// One pass, through a scope: <see cref="IKeycloakAdminClient"/> is
    /// registered scoped, so a singleton cannot take it and the pass is resolved
    /// rather than constructed.
    /// </summary>
    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        KioskPrivilegeSweep sweep = scope.ServiceProvider.GetRequiredService<KioskPrivilegeSweep>();

        await sweep.SweepAsync(cancellationToken);
    }
}

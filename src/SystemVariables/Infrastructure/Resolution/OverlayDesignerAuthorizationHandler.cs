using System.Net.Http.Headers;
using SmartSentinelEye.ServiceDefaults.Authentication;

namespace SmartSentinelEye.SystemVariables.Infrastructure.Resolution;

/// <summary>
/// Presents the <c>system-variables-seeder</c> service account on the one
/// request <see cref="ReverseIndexSeederHostedService"/> makes.
///
/// <para>
/// A handler rather than a header written onto the client, even though the
/// seeder sends exactly one request: the client is named and resolved from the
/// factory, so anything written onto it is visible to every later resolution of
/// that name. A handler carries per-request data per request, which is what
/// <see cref="AuthorizingHandler"/> exists for — and it sits inside the
/// resilience pipeline, so a retried <c>GET</c> re-reads the credential.
/// </para>
/// </summary>
public sealed class OverlayDesignerAuthorizationHandler(OverlayDesignerTokenProvider tokens)
    : AuthorizingHandler
{
    protected override async Task<AuthenticationHeaderValue?> AuthorizationAsync(
        CancellationToken cancellationToken) =>
        new("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
}

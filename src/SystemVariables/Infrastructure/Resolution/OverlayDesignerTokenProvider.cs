using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSentinelEye.ServiceDefaults.Authentication;

namespace SmartSentinelEye.SystemVariables.Infrastructure.Resolution;

/// <summary>
/// The <c>system-variables-seeder</c> client_credentials token
/// <see cref="ReverseIndexSeederHostedService"/> presents to overlay-designer
/// when it seeds the reverse index at host start.
///
/// <para>
/// The fifth wrapper over <see cref="ClientCredentialsTokenProvider"/>, and the
/// one that closes spec 005 T061: that task specified a seeder authenticating
/// with a Keycloak service account, the seeder shipped without it, and
/// <c>GET /overlays</c> later gained <c>sse.overlays.read</c> — so every cold
/// start was refused and the index stayed empty until an overlay was
/// republished (#2158).
/// </para>
///
/// <para>
/// Shaped on StreamDistribution's <c>CameraCatalogTokenProvider</c>, the
/// closest of the four: both are service-to-service HTTP with a single scoped
/// read, made once per host start. The cache is therefore never hit here
/// either, and costs a field — the argument that reasoning settled when the
/// cache became something shared rather than something rewritten.
/// </para>
/// </summary>
public sealed class OverlayDesignerTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ReverseIndexSeederOptions> options,
    TimeProvider clock,
    ILogger<OverlayDesignerTokenProvider> logger)
    : IDisposable
{
    /// <summary>Named client this provider mints through.</summary>
    public const string HttpClientName = "system-variables-seeder-token";

    private readonly ClientCredentialsTokenProvider tokens = new(
        httpClientFactory,
        HttpClientName,
        () => Credentials(options),
        clock,
        logger);

    public void Dispose() => tokens.Dispose();

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        tokens.GetAccessTokenAsync(cancellationToken);

    private static ClientCredentials Credentials(IOptions<ReverseIndexSeederOptions> options)
    {
        ReverseIndexSeederOptions opts = options.Value;
        return new ClientCredentials(opts.KeycloakUrl, opts.Realm, opts.ClientIdentifier, opts.ClientSecret);
    }
}

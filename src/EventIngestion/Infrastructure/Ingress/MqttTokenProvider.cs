using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSentinelEye.ServiceDefaults.Authentication;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

/// <summary>
/// The <c>event-ingestion</c> client_credentials token the MQTT subscriber
/// presents as its broker password (ADR-0100).
///
/// <para>
/// Refreshing proactively at 80 % of <c>expires_in</c> is what keeps a reconnect
/// from presenting an expired JWT; that behaviour now lives in
/// <see cref="ClientCredentialsTokenProvider"/>, alongside the four sibling
/// providers that share it — the three that had each written their own copy of
/// it, and SystemVariables' reverse-index seeder, which was specified to have
/// one from spec 005 and did not get it until spec 126.
/// </para>
/// </summary>
public sealed class MqttTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<MosquittoOptions> options,
    TimeProvider clock,
    ILogger<MqttTokenProvider> logger)
    : IDisposable
{
    /// <summary>Named client this provider mints through.</summary>
    public const string HttpClientName = "event-ingestion-token";

    private readonly ClientCredentialsTokenProvider tokens = new(
        httpClientFactory,
        HttpClientName,
        () => Credentials(options),
        clock,
        logger);

    public void Dispose() => tokens.Dispose();

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        tokens.GetAccessTokenAsync(cancellationToken);

    /// <summary>
    /// <c>Username</c>, not a separate client-id field: the go-auth plugin
    /// enforces <c>azp == username</c>, so the broker account and the Keycloak
    /// client are one name by construction.
    /// </summary>
    private static ClientCredentials Credentials(IOptions<MosquittoOptions> options)
    {
        MosquittoOptions opts = options.Value;
        return new ClientCredentials(opts.KeycloakUrl, opts.Realm, opts.Username, opts.ClientSecret);
    }
}

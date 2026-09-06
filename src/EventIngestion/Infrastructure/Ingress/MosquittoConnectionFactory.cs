using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

/// <summary>
/// Builds the MQTT client for the subscriber. Authenticates with a
/// Keycloak-minted JWT as the password (ADR-0100) — the go-auth plugin
/// enforces <c>azp == username</c>, so the client credential and the
/// <c>acl.txt</c> user are the same <c>event-ingestion</c> identity.
/// Persistent session (cleanSession=false) so a process restart resumes any
/// QoS 1 messages the broker still holds.
///
/// <para>
/// Returns the client <em>unconnected</em> together with its options, so the
/// hosted service can attach its message handler before
/// <see cref="MqttConnectionLoop"/> opens the first connection. An earlier
/// version started the client here as <c>_ = client.StartAsync(managed)</c>,
/// which discarded the Task and with it every connect failure.
/// </para>
/// </summary>
public sealed class MosquittoConnectionFactory(
    IOptions<MosquittoOptions> options,
    MqttTokenProvider tokens,
    ILogger<MosquittoConnectionFactory> logger)
{
    public async Task<MqttConnection> CreateAsync(CancellationToken cancellationToken)
    {
        MosquittoOptions opts = options.Value;

        TokenHolder token = new();

        // **Minted here when it can be, but never fatally.** This runs on
        // IHostedService.StartAsync, so an exception escaping it does not fail a
        // connection — it fails the whole host, and event-ingestion enters
        // FailedToStart because Keycloak was briefly slow. A service that cannot
        // start while its identity provider blinks is a worse property than a
        // subscriber that connects a few seconds late, and the machinery to
        // connect late already exists: MqttConnectionLoop retries with a capped
        // backoff and mints the credential again before every attempt.
        //
        // Found while investigating #2038, where a restart under load left
        // event-ingestion down.
        try
        {
            token.Value = await tokens.GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.InitialMqttTokenFailed(exception.Message);
        }

        MqttClientOptionsBuilder clientOptions = new MqttClientOptionsBuilder()
            .WithClientId(opts.ClientId)
            .WithTcpServer(opts.Host, opts.Port)
            .WithCredentials(new TokenCredentials(opts.Username, token))
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (opts.UseTls)
        {
            clientOptions = clientOptions.WithTlsOptions(builder => builder.UseTls());
        }

        return new MqttConnection(
            new MqttClientFactory().CreateMqttClient(), clientOptions.Build(), token, tokens);
    }
}

/// <summary>
/// An unconnected client plus the options it must be connected with.
/// Owns its token slot so <see cref="MqttConnectionLoop"/> can put a live
/// credential in front of every connect attempt without taking a dependency on
/// the provider itself.
/// </summary>
public sealed class MqttConnection(
    IMqttClient client,
    MqttClientOptions options,
    TokenHolder token,
    MqttTokenProvider tokens)
{
    public IMqttClient Client => client;

    public MqttClientOptions Options => options;

    /// <summary>
    /// Mints the JWT into the slot the credentials provider reads, so the
    /// connect that follows presents a live one.
    ///
    /// <para>
    /// Called by the loop <em>before every attempt</em> rather than after a
    /// failed one. v4's <c>ConnectingFailedAsync</c> could only react to a
    /// refusal, which is how a subscriber that never achieved a first connection
    /// re-presented the same dead credential every five seconds forever (#2038).
    /// Minting first makes that unreachable rather than handled.
    /// </para>
    /// </summary>
    public async Task MintCredentialAsync(CancellationToken cancellationToken)
    {
        token.Value = await tokens.GetAccessTokenAsync(cancellationToken);
    }
}

/// <summary>
/// Mutable slot holding the current JWT. Written before each connect attempt so
/// the reconnect presents a live token.
/// </summary>
public sealed class TokenHolder
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// MQTTnet's credentials provider is synchronous; it reads the latest token
/// from <see cref="TokenHolder"/> so every (re)connect presents a live JWT.
/// </summary>
public sealed class TokenCredentials(string username, TokenHolder token) : IMqttClientCredentialsProvider
{
    public string GetUserName(MqttClientOptions clientOptions) => username;

    public byte[] GetPassword(MqttClientOptions clientOptions) => Encoding.UTF8.GetBytes(token.Value);
}

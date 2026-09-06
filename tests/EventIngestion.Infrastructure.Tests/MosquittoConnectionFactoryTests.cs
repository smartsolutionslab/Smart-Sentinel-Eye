using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests;

/// <summary>
/// The startup path's tolerance of an absent Keycloak (#2038).
///
/// <para>
/// <c>CreateAsync</c> runs inside <c>IHostedService.StartAsync</c>, so an
/// exception escaping it does not fail a connection — it fails the whole host.
/// event-ingestion then enters <c>FailedToStart</c> because its identity
/// provider was briefly slow, and stays down until something restarts it.
/// </para>
///
/// <para>
/// The recovery machinery already existed: the managed client retries every five
/// seconds, and the token is re-minted on each failed attempt. Only the first
/// mint was fatal.
/// </para>
/// </summary>
public class MosquittoConnectionFactoryTests
{
    [Fact]
    public async Task A_refusing_Keycloak_does_not_stop_the_connection_being_built()
    {
        MosquittoConnectionFactory factory = Create(HttpStatusCode.ServiceUnavailable);

        MqttConnection connection = await factory.CreateAsync(CancellationToken.None);

        connection.ShouldNotBeNull(
            "the host must start. A subscriber that connects a few seconds late is a better property "
            + "than a service that cannot start while Keycloak blinks.");
        connection.Client.ShouldNotBeNull();
    }

    /// <summary>
    /// It starts with an <b>empty</b> credential rather than a stale or invented
    /// one. The broker will refuse it, which raises <c>ConnectingFailed</c> — the
    /// event the subscriber now re-mints on, and the reason that handler had to
    /// change alongside this.
    /// </summary>
    [Fact]
    public async Task A_connection_built_without_a_token_carries_an_empty_credential()
    {
        MosquittoConnectionFactory factory = Create(HttpStatusCode.ServiceUnavailable);

        MqttConnection connection = await factory.CreateAsync(CancellationToken.None);

        connection.Options.Credentials
            .ShouldNotBeNull()
            .GetPassword(connection.Options)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reachable_Keycloak_still_supplies_the_credential()
    {
        MosquittoConnectionFactory factory = Create(HttpStatusCode.OK);

        MqttConnection connection = await factory.CreateAsync(CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(
                connection.Options.Credentials.ShouldNotBeNull().GetPassword(
                    connection.Options))
            .ShouldBe("a-token", "the happy path is unchanged — the token is still minted before the first connect.");
    }

    /// <summary>
    /// Cancellation is the one failure that still propagates: the host is being
    /// torn down, and swallowing it would report a healthy start for a service
    /// that is stopping.
    /// </summary>
    [Fact]
    public async Task A_cancelled_start_is_not_swallowed()
    {
        MosquittoConnectionFactory factory = Create(HttpStatusCode.OK);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => factory.CreateAsync(cts.Token));
    }

    private static MosquittoConnectionFactory Create(HttpStatusCode keycloak)
    {
        IOptions<MosquittoOptions> options = Options.Create(new MosquittoOptions
        {
            Host = "mosquitto.test",
            Port = 1883,
            KeycloakUrl = "https://keycloak.test",
            ClientSecret = "a-secret",
        });

        MqttTokenProvider tokens = new(
            new StubHttpClientFactory(new HttpClient(new StubKeycloak(keycloak))),
            options,
            TimeProvider.System,
            NullLogger<MqttTokenProvider>.Instance);

        return new MosquittoConnectionFactory(options, tokens, NullLogger<MosquittoConnectionFactory>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubKeycloak(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return status == HttpStatusCode.OK
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"a-token","expires_in":300,"token_type":"Bearer"}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                })
                : Task.FromResult(new HttpResponseMessage(status));
        }
    }
}

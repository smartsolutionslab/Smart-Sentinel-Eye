using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Formatter;
using SmartSentinelEye.ScenarioSimulator.Configuration;
using SmartSentinelEye.ScenarioSimulator.Keycloak;
using SmartSentinelEye.ScenarioSimulator.Mqtt;
using SmartSentinelEye.ScenarioSimulator.Tests.Fakes;

namespace SmartSentinelEye.ScenarioSimulator.Tests;

/// <summary>
/// <b>The publisher's 3.1.1 pin, guarded where CI can see it.</b> The
/// subscriber's copy of this claim is in
/// <c>MosquittoConnectionFactoryTests</c>; before both of them the only coverage
/// of the protocol version was in <c>Category=Disruptive</c> integration tests,
/// which <c>ci.yml</c> excludes.
///
/// <para>
/// The publisher connects with <c>cleanSession=true</c> and has no subscription,
/// so it has no session to lose — the reason to pin it is the other one the
/// production comment gives: the broker's ACL and auth behaviour (ADR-0100) was
/// proven on 3.1.1, and a protocol that changes because a library's default did
/// is exactly the failure this branch is fixing. A publisher speaking 5.0 to a
/// broker measured on 3.1.1 also makes NFR-002's handshake figures and the
/// throughput comparison not like-for-like.
/// </para>
/// </summary>
public class MqttPublisherProtocolPinTests
{
    [Fact]
    public async Task The_publisher_speaks_MQTT_3_1_1_rather_than_the_v5_default()
    {
        FakeMqttClient client = new();
        using KeycloakTokenProvider tokens = new(
            new FakeHttpClientFactory(new HttpClient(new StubKeycloak())),
            Options.Create(new SimulatorOptions { KeycloakUrl = "https://keycloak.test" }),
            TimeProvider.System,
            NullLogger<KeycloakTokenProvider>.Instance);

        await using MqttPublisher publisher = new(
            Options.Create(new SimulatorOptions
            {
                MqttHost = "mosquitto.test:1883",
                KeycloakUrl = "https://keycloak.test",
                ClientSecret = "a-secret",
            }),
            tokens,
            NullLogger<MqttPublisher>.Instance,
            client);

        await publisher.StartAsync(CancellationToken.None);

        (await WaitUntilAsync(() => client.IsConnected)).ShouldBeTrue(
            "the publisher never connected, so there are no options to read");

        client.Options.ProtocolVersion.ShouldBe(
            MqttProtocolVersion.V311,
            "MQTTnet 5 defaults to V500. Nothing here needs MQTT 5, and a wire protocol that "
            + "changed because a library default did is what cost the subscriber its persistent "
            + "session.");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
        }

        return condition();
    }

    private sealed class StubKeycloak : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"a-token","expires_in":300,"token_type":"Bearer"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}

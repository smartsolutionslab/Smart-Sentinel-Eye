using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// Spec 022. Publishes an event the way a machine does — over the broker,
/// authenticated as the simulator (FR-002).
///
/// <para>
/// Posting to an HTTP endpoint would be shorter and would enter the chain in
/// the middle. The ingress is a join like any other, and every join this
/// feature exists to cover is one nothing else exercises, so a shortcut past
/// the first would leave it exactly as untested as the rest were.
/// </para>
///
/// <para>
/// In Fixtures rather than beside the test that needed it first: five ingestion
/// tests already carry near-verbatim copies of this publish-and-authenticate
/// helper, down to the client id and the timeout. Collapsing them onto this is
/// worth doing and is not this change — a PR that adds a test should not also
/// rewrite five unrelated ones.
/// </para>
/// </summary>
public sealed class PlantFloor(AspireFixture aspire)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";
    private const string Topic = "fab/munich/plc/station-4";

    public Task PublishAsync(string kind, int cycleTime) => PublishRawAsync(Payload(kind, cycleTime));

    /// <summary>
    /// The same bytes twice is the point of the redelivery case — a payload
    /// built twice would carry two identifiers and be two events.
    /// </summary>
    public static string Payload(string kind, int cycleTime) => JsonSerializer.Serialize(new
    {
        eventId = Guid.CreateVersion7(),
        kind,
        occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        payload = new { cycleTime },
    });

    public async Task PublishRawAsync(string payload)
    {
        string jwt = await SimulatorTokenAsync();

        Uri broker = aspire.App.GetEndpoint("mosquitto", "mqtt");
        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        MqttClientConnectResult connected = await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithClientId($"{SimulatorClientId}-{Guid.CreateVersion7():N}")
            .WithCredentials(SimulatorClientId, jwt)
            .WithTcpServer(broker.Host, broker.Port)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(30))
            .Build());
        connected.ResultCode.ShouldBe(MqttClientConnectResultCode.Success);

        MqttClientPublishResult published = await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(Topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
        published.IsSuccess.ShouldBeTrue("the broker refused the publish");

        await client.DisconnectAsync();
    }

    private async Task<string> SimulatorTokenAsync()
    {
        using HttpClient keycloak = aspire.CreateKeycloakClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = SimulatorClientId,
            ["client_secret"] = SimulatorClientSecret,
        });

        HttpResponseMessage token = await keycloak.PostAsync(
            "/realms/smart-sentinel-eye/protocol/openid-connect/token", form);
        token.EnsureSuccessStatusCode();

        return (await token.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}

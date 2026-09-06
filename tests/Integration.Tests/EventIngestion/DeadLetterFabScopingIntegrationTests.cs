using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Domain.DeadLetter;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.Shared.Kernel;
using DeadLetterAggregate = SmartSentinelEye.EventIngestion.Domain.DeadLetter.DeadLetter;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 018 T024 — SC-004. Every row in this listing carries the rejected
/// payload verbatim: one plant's unvalidated production data, which until now
/// any operator of any other plant could read in full.
///
/// <para>
/// The three deliveries are the three cases that must stay apart. A malformed
/// <em>payload</em> under a well-formed address has a plant and its own
/// operators see it; a malformed <em>address</em> has none and reaches nobody.
/// Conflating them — nulling every fab — hides the whole list while looking
/// exactly like correct scoping, which is why the third row is asserted
/// against the database directly rather than only through its absence.
/// </para>
///
/// <para>
/// Only the munich delivery goes over the broker. The dev ACL grants the one
/// principal a test can authenticate as (<c>scenario-simulator</c>) writes
/// under <c>fab/munich/</c> alone, and widening a fab-scoped broker grant to
/// publish into another plant is precisely what this feature exists to prevent
/// — so the other two rows are captured through the aggregate and written
/// straight to the table. The leg that matters is covered where it runs: the
/// munich row proves the ingress establishes the plant from the address.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class DeadLetterFabScopingIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    private const string DresdenOperator = "op-dresden@dresden.test";
    private const string MultiFabOperator = "op-multi@smart-sentinel-eye.test";
    private const string OperatorPassword = "Operator1234";

    // Seeded dev-only confidential client; the one MQTT principal with both a
    // known secret and an ACL write grant (AppHost realm + mosquitto/acl.txt).
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// SC-004. One run, because the three rows only mean anything together:
    /// each operator's list is asserted for what it contains <b>and</b> for
    /// what it does not.
    /// </summary>
    [Fact]
    public async Task Rejected_deliveries_reach_their_own_plant_and_an_unattributed_one_reaches_nobody()
    {
        string run = Guid.CreateVersion7().ToString("N")[..12];
        string munichPayload = $"not-json-munich-{run}";
        string dresdenPayload = $"not-json-dresden-{run}";
        string orphanPayload = $"not-json-orphan-{run}";

        // (b) — the common case, through the real ingress: the address is
        // well-formed, the payload is not, and the plant comes off the topic.
        DeadLetterAggregate munich = await WaitForCapturedAsync(
            "fab/munich/plc/station-4", munichPayload);
        munich.Fab.ShouldBe(
            FabIdentifier.From("munich"),
            "the ingress did not take the plant from a well-formed delivery address");

        await SeedAsync("fab/dresden/plc/station-9", FabIdentifier.From("dresden"), dresdenPayload);

        // (a) — the address itself is malformed, so there is no plant to record.
        await SeedAsync($"rejected/{run}", null, orphanPayload);

        IReadOnlyList<string> dresdenSees = await PayloadsVisibleToAsync(DresdenOperator, run);
        IReadOnlyList<string> multiFabSees = await PayloadsVisibleToAsync(MultiFabOperator, run);

        dresdenSees.ShouldBe([dresdenPayload]);

        multiFabSees.Order().ShouldBe(
            new[] { dresdenPayload, munichPayload }.Order(),
            "an operator holding both plants should see both attributed deliveries");

        multiFabSees.ShouldNotContain(
            orphanPayload,
            "a delivery whose plant could not be established reached an operator");

        // Asserted directly, because every assertion above also passes if the
        // capture path simply never wrote the row.
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        DeadLetterAggregate orphan = await database.DeadLetters.AsNoTracking()
            .SingleAsync(deadLetter => deadLetter.RawPayload == RawPayload.From(orphanPayload));
        orphan.Fab.ShouldBeNull();
    }

    /// <summary>
    /// The listing had no fab at all before this feature, so naming one was not
    /// even possible. Now it is, and naming one the caller does not hold is
    /// refused like every other read (FR-009).
    /// </summary>
    [Fact]
    public async Task Naming_a_fab_the_caller_does_not_hold_is_refused()
    {
        using HttpClient events = await ClientFor(DresdenOperator);

        HttpResponseMessage refused = await events.GetAsync("/events/dead-letters?fabId=munich");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await BodyAsync(refused));
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// The distinct payloads from this run that the given operator can read.
    /// Distinct because a redelivered rejection is captured twice, and the
    /// question here is which payloads are visible, not how many rows carry
    /// them.
    /// </summary>
    private async Task<IReadOnlyList<string>> PayloadsVisibleToAsync(string username, string run)
    {
        using HttpClient events = await ClientFor(username);
        HttpResponseMessage listed = await events.GetAsync("/events/dead-letters?limit=1000");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await BodyAsync(listed));

        JsonElement rows = await listed.Content.ReadFromJsonAsync<JsonElement>();
        return
        [
            .. rows.EnumerateArray()
                .Select(row => row.GetProperty("rawPayload").GetString())
                .Where(payload => payload is not null && payload.EndsWith(run, StringComparison.Ordinal))
                .Select(payload => payload!)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private async Task SeedAsync(string topic, FabIdentifier? fab, string rawPayload)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        database.DeadLetters.Add(DeadLetterAggregate.Capture(
            DeliveryTopic.From(topic), fab, RawPayload.From(rawPayload), RejectionReason.From("spec 018 T024 seed"), new SystemClock()));
        await database.SaveChangesAsync();
    }

    /// <summary>
    /// Publishes until the delivery is captured. Republished rather than sent
    /// once: a message published before the subscriber has connected reaches
    /// nobody, and this test may well run before it has. A redelivery that
    /// arrives late is captured twice, which is why the listings below are
    /// compared as sets.
    /// </summary>
    private async Task<DeadLetterAggregate> WaitForCapturedAsync(string topic, string rawPayload)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await PublishAsync(topic, rawPayload);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));

                await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
                DeadLetterAggregate? captured = await database.DeadLetters.AsNoTracking()
                    .FirstOrDefaultAsync(deadLetter => deadLetter.RawPayload == RawPayload.From(rawPayload));
                if (captured is not null)
                {
                    return captured;
                }
            }
        }

        throw new TimeoutException(
            $"The delivery carrying '{rawPayload}' was never dead-lettered." + Environment.NewLine +
            aspire.RecentLogs("event-ingestion"));
    }

    private async Task PublishAsync(string topic, string payload)
    {
        string jwt = await MintSimulatorTokenAsync();
        Uri broker = aspire.App.GetEndpoint("mosquitto", "mqtt");

        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithClientId($"{SimulatorClientId}-{Guid.CreateVersion7():N}")
            // The go-auth plugin requires the username to equal the token's azp.
            .WithCredentials(SimulatorClientId, jwt)
            .WithTcpServer(broker.Host, broker.Port)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .Build();

        MqttClientConnectResult connected = await client.ConnectAsync(options);
        connected.ResultCode.ShouldBe(
            MqttClientConnectResultCode.Success, "could not authenticate against the broker");

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await client.DisconnectAsync();
    }

    private async Task<string> MintSimulatorTokenAsync()
    {
        using HttpClient keycloak = aspire.CreateKeycloakClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = SimulatorClientId,
            ["client_secret"] = SimulatorClientSecret,
        });

        HttpResponseMessage response = await keycloak.PostAsync(
            "/realms/smart-sentinel-eye/protocol/openid-connect/token", form);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    private Task<HttpClient> ClientFor(string username) =>
        aspire.CreateAuthenticatedClientAsync("event-ingestion", username, OperatorPassword);

    private async Task<string> BodyAsync(HttpResponseMessage response) =>
        $"body: {await response.Content.ReadAsStringAsync()}{Environment.NewLine}" +
        $"event-ingestion log:{Environment.NewLine}{aspire.RecentLogs("event-ingestion")}";
}

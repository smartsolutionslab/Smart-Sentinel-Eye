using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 020 T021 — quickstart step 4, the step the quickstart calls the one that
/// cannot be faked.
///
/// <para>
/// Keeping an event until it is stored is exactly what turns one unstorable
/// delivery into an endless retry, so this feature's own mechanism is what could
/// bring back the defect spec 018 fixed: one bad row wedging ingestion for every
/// fab. Two claims, and both are needed — the bad delivery must stop (FR-007,
/// FR-008), and the healthy ones must not have waited for it (FR-009).
/// </para>
///
/// <para>
/// The poison is a fab whose partition has been dropped, so the database stays
/// healthy throughout. That matters: the escape writes a dead letter, and during
/// a real outage that write fails for the same reason as the event write. This
/// test is of the escape working, not of the hole underneath it.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class PoisonDeliveryEscapeIntegrationTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";
    private const string PoisonFab = "hamburg";
    private const string HealthyFab = "munich";
    private const int HealthyCount = 100;

    [Fact]
    public async Task One_unstorable_delivery_is_recorded_and_released_without_delaying_the_rest()
    {
        string poisonKind = $"Poison{Guid.CreateVersion7():N}"[..20];
        string healthyKind = $"Healthy{Guid.CreateVersion7():N}"[..20];

        await ExecuteAsync($"DROP TABLE IF EXISTS events_{PoisonFab};");
        output.WriteLine($"dropped events_{PoisonFab} — one delivery can now never be stored");

        try
        {
            await PublishAsync($"fab/{PoisonFab}/plc/dev-1", poisonKind, 1);
            DateTimeOffset publishedAt = DateTimeOffset.UtcNow;
            await PublishAsync($"fab/{HealthyFab}/plc/station-4", healthyKind, HealthyCount);
            output.WriteLine($"published 1 poisoned + {HealthyCount} healthy");

            // FR-009 first, and deliberately on a short deadline. The retry
            // window is far longer than this, so the healthy events can only be
            // here on time if the loop moved past the failure rather than
            // waiting it out.
            long healthy = await WaitForCountAsync(healthyKind, HealthyCount, TimeSpan.FromSeconds(45));
            TimeSpan took = DateTimeOffset.UtcNow - publishedAt;
            output.WriteLine($"healthy stored: {healthy} after {took.TotalSeconds:F1}s");
            healthy.ShouldBe(
                HealthyCount, "one unstorable delivery held up the fabs behind it — spec 018's defect, back");

            // FR-007/FR-008. Recorded before it is released, so the delivery is
            // never merely gone.
            string error = await WaitForDeadLetterAsync(poisonKind, TimeSpan.FromMinutes(4));
            output.WriteLine($"dead letter: {error}");
            error.ShouldNotBeNullOrEmpty("the poisoned delivery was released without being recorded");
            error.ShouldContain("not storable");

            (await CountAsync(poisonKind)).ShouldBe(0, "the poisoned event was somehow stored after all");
        }
        finally
        {
            await RestoreStorageAsync();
        }
    }

    private async Task<long> WaitForCountAsync(string kind, long expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        long count = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            count = await CountAsync(kind);
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return count;
    }

    /// <summary>
    /// The dead letter is matched on the payload rather than the topic: the
    /// escape records the delivery it gave up on, and the kind is the only thing
    /// in it that identifies this test's run.
    /// </summary>
    private async Task<string> WaitForDeadLetterAsync(string kind, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using EventIngestionDbContext database =
                await aspire.CreateEventIngestionDbContextAsync();
            string[] found = await database.Database
                .SqlQueryRaw<string>(
                    "SELECT error AS \"Value\" FROM dead_letters WHERE raw_payload LIKE {0}",
                    $"%{kind}%")
                .ToArrayAsync();

            if (found.Length > 0)
            {
                return found[0];
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        return string.Empty;
    }

    private async Task<long> CountAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        return await database.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM events WHERE kind = {0}", kind)
            .SingleAsync();
    }

    private async Task RestoreStorageAsync()
    {
        DateTime now = DateTime.UtcNow;
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS events_{PoisonFab} PARTITION OF events
                FOR VALUES IN ('{PoisonFab}') PARTITION BY RANGE (ingested_at);
            """);
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "events_{PoisonFab}_{now:yyyyMM}" PARTITION OF events_{PoisonFab}
                FOR VALUES FROM ('{now:yyyy-MM}-01') TO ('{now.AddMonths(1):yyyy-MM}-01');
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
#pragma warning disable EF1002
        await database.Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }

    private async Task PublishAsync(string topic, string kind, int count)
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
        string jwt = (await token.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;

        Uri broker = aspire.App.GetEndpoint("mosquitto", "mqtt");
        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        MqttClientConnectResult connected = await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId($"{SimulatorClientId}-{Guid.CreateVersion7():N}")
            .WithCredentials(SimulatorClientId, jwt)
            .WithTcpServer(broker.Host, broker.Port)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .Build());
        connected.ResultCode.ShouldBe(MqttClientConnectResultCode.Success);

        for (int i = 0; i < count; i++)
        {
            MqttClientPublishResult published = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Payload(kind))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());
            published.IsSuccess.ShouldBeTrue($"the broker refused publish {i} to {topic}");
        }

        await client.DisconnectAsync();
    }

    private static string Payload(string kind) => JsonSerializer.Serialize(new
    {
        eventId = Guid.CreateVersion7(),
        kind,
        occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        // The kind is repeated inside the payload because the dead letter keeps
        // the raw payload, and that is all this test has to recognise its own
        // delivery by.
        payload = new { note = $"spec 020 poison escape {kind}" },
    });
}

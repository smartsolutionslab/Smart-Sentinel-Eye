using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 020 T011 — quickstart step 1. The claim the feature is named after: an
/// interruption costs time, not events.
///
/// <para>
/// <b>Both</b> equalities are asserted. Redelivery used to be a rarity and is
/// now the ordinary way an interruption ends, so "every event arrived" and "no
/// event arrived twice" are two different claims and one of them passing tells
/// you nothing about the other.
/// </para>
///
/// <para>
/// The outage is one fab's storage rather than the whole database. Pausing the
/// shared Postgres container would take the other eight contexts and the
/// fixture's own health checks with it, and the loop cannot tell the two apart
/// anyway: a dropped partition makes the write fail against a database that is
/// otherwise perfectly healthy, which is the same code path and a stricter test
/// of it — the dead-letter escape stays reachable throughout, so the events
/// have somewhere wrong to go if the retry is not working.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class OutageRecoveryIntegrationTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";
    private const string Fab = "hamburg";
    private const int Published = 20;

    [Fact]
    public async Task Every_event_published_during_an_outage_is_stored_exactly_once_after_it()
    {
        string kind = $"Outage{Guid.CreateVersion7():N}"[..20];

        await DropStorageAsync();
        output.WriteLine($"dropped events_{Fab} — writes for this fab now fail");

        try
        {
            await PublishAsync($"fab/{Fab}/plc/dev-1", kind, Published);
            output.WriteLine($"published {Published} events while storage was away");

            await Task.Delay(TimeSpan.FromSeconds(8));
            long duringOutage = await CountAsync(kind);
            output.WriteLine($"stored during the outage: {duringOutage}");
            duringOutage.ShouldBe(0, "the outage has to be real for the rest to mean anything");
        }
        finally
        {
            // Restored even when an assertion above fails. The partition lives
            // on the fixture's shared database, so leaving it dropped fails
            // every later hamburg test with an unrelated error - which buries
            // the one that actually found something.
            await RestoreStorageAsync();
            output.WriteLine($"restored events_{Fab} — storage is possible again");
        }

        (long total, long distinct) = await WaitForAsync(kind, Published, TimeSpan.FromSeconds(90));
        output.WriteLine($"after recovery: count={total} distinct={distinct}");

        total.ShouldBe(Published, "an event published during the outage was lost");
        distinct.ShouldBe(total, "a redelivered event was stored twice");
    }

    /// <summary>
    /// Polls rather than sleeping a fixed time, so a slow recovery reads as a
    /// slow pass instead of a mysterious failure — and so the failure message
    /// carries the count it got stuck on.
    /// </summary>
    private async Task<(long Total, long Distinct)> WaitForAsync(
        string kind, long expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        (long total, long distinct) = (0, 0);

        while (DateTimeOffset.UtcNow < deadline)
        {
            (total, distinct) = await CountBothAsync(kind);
            if (total >= expected)
            {
                // One more pass: a duplicate that arrives a moment later would
                // otherwise be missed by stopping the instant the count is met.
                await Task.Delay(TimeSpan.FromSeconds(5));
                return await CountBothAsync(kind);
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return (total, distinct);
    }

    private async Task<long> CountAsync(string kind) => (await CountBothAsync(kind)).Total;

    private async Task<(long Total, long Distinct)> CountBothAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        long total = await database.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM events WHERE kind = {0}", kind)
            .SingleAsync();
        long distinct = await database.Database
            .SqlQueryRaw<long>(
                "SELECT count(DISTINCT event_id) AS \"Value\" FROM events WHERE kind = {0}", kind)
            .SingleAsync();
        return (total, distinct);
    }

    private Task DropStorageAsync() => ExecuteAsync($"DROP TABLE IF EXISTS events_{Fab};");

    private async Task RestoreStorageAsync()
    {
        DateTime now = DateTime.UtcNow;
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS events_{Fab} PARTITION OF events
                FOR VALUES IN ('{Fab}') PARTITION BY RANGE (ingested_at);
            """);
        await ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "events_{Fab}_{now:yyyyMM}" PARTITION OF events_{Fab}
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

            // A publish the broker's ACL refuses is reported here and nowhere
            // else; unchecked it reads later as "the event was lost", which is
            // the very thing this test claims to detect.
            published.IsSuccess.ShouldBeTrue($"the broker refused publish {i} to {topic}");
        }

        await client.DisconnectAsync();
    }

    private static string Payload(string kind) => JsonSerializer.Serialize(new
    {
        eventId = Guid.CreateVersion7(),
        kind,
        occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        payload = new { note = "spec 020 outage recovery" },
    });
}

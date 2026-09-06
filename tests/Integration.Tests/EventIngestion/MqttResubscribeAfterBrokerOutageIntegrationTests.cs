using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 079 US1-AC3 (#1139) — the subscriber <b>resubscribes</b> after the
/// broker comes back, not merely reconnects.
///
/// <para>
/// <b>The one assertion that separates the two worlds is that event B is
/// stored.</b> A subscriber that reconnects without resubscribing is connected,
/// <c>IsConnected</c> is <c>true</c>, it logs a connect, and the broker discards
/// every publish because it holds no matching subscription. Nothing throws.
/// EventIngestion registers no MQTT health check at all, so nothing in the
/// running system goes amber either. Connection state, a health response, a
/// "reconnected" log line and a startup subscribe count all read <i>identically</i>
/// in the broken world and the working one — which is why none of them is
/// asserted here.
/// </para>
///
/// <para>
/// <b>Event A is the control and is not decoration.</b> Without it, B's absence
/// is equally well explained by "publishing never worked in this run", and the
/// test would report a resubscribe defect for a broken fixture.
/// </para>
///
/// <para>
/// <b>Why a stop/start of the broker is decisive rather than probabilistic.</b>
/// Under the fixture mosquitto is a throwaway container: <c>AppHost.cs</c>
/// applies <c>ContainerLifetime.Persistent</c> and the <c>mosquitto-data</c>
/// volume only when <c>isRunMode &amp;&amp; !isE2ETests</c>, and the fixture boots
/// with <c>E2ETests=true</c>. The broker therefore comes back holding no session
/// and no subscription for <c>event-ingestion</c>, so there is no window in which
/// a surviving subscription could mask the defect. That is assumption A2 in the
/// spec, and this test is what discharges it.
/// </para>
///
/// <para>
/// <b>B is published repeatedly rather than once.</b> A QoS 1 publish that
/// arrives before the subscriber has resubscribed is dropped by the broker with
/// nobody to deliver it to, so a single publish would race the reconnect and go
/// red in a perfectly healthy system. Republishing the <i>same bytes</i> — one
/// <c>eventId</c>, so redelivery and duplicate publishes collapse to one row —
/// removes the race without weakening the claim: in the broken world no number
/// of publishes is ever delivered, because no subscription exists to deliver to.
/// </para>
/// </summary>
/// <remarks>
/// <b>Excluded from CI by its category</b>, for the reason
/// <see cref="RestartLosesNothingIntegrationTests"/> and
/// <see cref="OutboxSurvivesAKillTests"/> already carry: driving a resource
/// through Aspire's stop/start commands fails outright on the CI runner
/// ("Failed to stop resource"), and this test stops the broker every other
/// EventIngestion test depends on — so a platform failure here would fail the
/// whole context rather than one test. The cost is real and stated: US1-AC3 has
/// no CI coverage and is verified locally, exactly as SC-002 is
/// (specs/020-durable-ingest-ack/verification.md).
/// </remarks>
[Collection(AspireCollection.Name)]
[Trait("Category", "Disruptive")]
public class MqttResubscribeAfterBrokerOutageIntegrationTests(
    AspireFixture aspire, ITestOutputHelper output)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";
    private const string Broker = "mosquitto";
    private const string Subscriber = "event-ingestion";
    private const string Fab = "hamburg";
    private const string Topic = $"fab/{Fab}/plc/dev-1";

    private static readonly TimeSpan ControlDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryDeadline = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task An_event_published_after_a_broker_outage_is_still_ingested()
    {
        string control = $"Resub{Guid.CreateVersion7():N}"[..20];
        string afterOutage = $"Resub{Guid.CreateVersion7():N}"[..20];

        string controlPayload = Payload(control, "spec 079 control, before the outage");
        (await PublishAsync(controlPayload)).ShouldBeTrue("the broker refused the control publish");

        bool controlStored = await WaitForStoredAsync(control, ControlDeadline);
        controlStored.ShouldBeTrue(
            "the control event never arrived, so nothing this test goes on to observe about the "
            + "reconnect would mean anything — ingestion was already not working before the outage");
        output.WriteLine($"control event {control} stored; ingestion is working");

        await RestartBrokerAsync();
        output.WriteLine($"{Broker} stopped and started again — it holds no session and no subscription");

        string afterOutagePayload = Payload(afterOutage, "spec 079 probe, after the outage");
        bool stored = await PublishUntilStoredAsync(afterOutage, afterOutagePayload, RecoveryDeadline);

        if (!stored)
        {
            output.WriteLine($"---- {Subscriber} log tail ----");
            output.WriteLine(aspire.RecentLogs(Subscriber));
        }

        stored.ShouldBeTrue(
            $"event {afterOutage} never arrived in {RecoveryDeadline.TotalSeconds:F0}s after the broker "
            + "returned — the subscriber reconnected but did not resubscribe, so it is connected, "
            + "healthy, and receiving nothing. Read the log tail above: a connect line with no "
            + "resubscribe line beside it is the whole defect.");
    }

    /// <summary>
    /// Stops the broker and — whatever happens — leaves it running again.
    ///
    /// <para>
    /// The shape is copied from
    /// <see cref="RestartLosesNothingIntegrationTests"/>, whose comment records
    /// what the version without the <c>finally</c> cost: the resource stayed
    /// down and every EventIngestion test after it failed with an unrelated
    /// socket error, so one test's environment problem read as eleven defects.
    /// Here the broker is shared by more tests still.
    /// </para>
    ///
    /// <para>
    /// <c>Stop</c> then <c>Start</c> rather than <c>Restart</c>, because the
    /// outage has to be long enough for the subscriber to notice it, and because
    /// the start is then both the intended second half and the repair after a
    /// stop that failed.
    /// </para>
    ///
    /// <para>
    /// Not <c>docker stop</c>: Aspire's notion of the resource state would
    /// diverge from reality and there would be nothing to synchronise the
    /// recovery on.
    /// </para>
    /// </summary>
    private async Task RestartBrokerAsync()
    {
        ResourceCommandService commands =
            aspire.App.Services.GetRequiredService<ResourceCommandService>();

        try
        {
            ExecuteCommandResult stopped = await commands.ExecuteCommandAsync(
                Broker, KnownResourceCommands.StopCommand, CancellationToken.None);
            stopped.Success.ShouldBeTrue($"could not stop {Broker}: {stopped.Message}");

            output.WriteLine($"{Broker} stopped");
        }
        finally
        {
            // Idempotent on a running resource, so this is safe after a stop
            // that worked and is the repair after one that did not.
            await commands.ExecuteCommandAsync(
                Broker, KnownResourceCommands.StartCommand, CancellationToken.None);

            await WaitForHealthyAsync(Broker);
        }
    }

    /// <summary>
    /// <b><c>WaitOnResourceUnavailable</c> is mandatory here</b>, and that is the
    /// whole of #2038: a resource coming back passes <i>through</i> an
    /// unavailable state, and the default behaviour treats reaching one as a
    /// reason to stop waiting — abandoning the wait on exactly the transition it
    /// exists to watch. Under load the window is wider, which is why the same
    /// code passes on an idle machine and fails on a busy one.
    ///
    /// <para>
    /// The diagnostics print the broker's <i>snapshot</i> and the
    /// <i>subscriber's</i> log tail. <c>mosquitto</c> is not in the fixture's
    /// tailed resources, so asking for its tail would answer with a placeholder
    /// that reads exactly like a broker which came up and said nothing.
    /// </para>
    /// </summary>
    private async Task WaitForHealthyAsync(string resourceName)
    {
        try
        {
            await aspire.App.ResourceNotifications
                .WaitForResourceHealthyAsync(
                    resourceName, WaitBehavior.WaitOnResourceUnavailable, CancellationToken.None)
                .WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (Exception exception)
        {
            output.WriteLine($"{resourceName} did not become healthy: {exception.Message}");
            output.WriteLine($"---- {resourceName} snapshot ----");
            output.WriteLine(await aspire.ResourceDiagnosticsAsync(resourceName));
            output.WriteLine($"---- {Subscriber} log tail ----");
            output.WriteLine(aspire.RecentLogs(Subscriber));

            throw;
        }
    }

    /// <summary>
    /// Publishes the same payload until it is stored or the deadline passes.
    /// The bytes are identical every time, so the deduplication on
    /// <c>event_id</c> collapses the repeats into one row rather than turning a
    /// retry into a second event.
    /// </summary>
    private async Task<bool> PublishUntilStoredAsync(string kind, string payload, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        int attempt = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            attempt++;

            if (!await PublishAsync(payload))
            {
                // Expected for a few seconds after the broker returns: the
                // go-auth plugin re-fetches the realm JWKS and refuses CONNECT
                // until it has (assumption A3). The next attempt is a moment off.
                output.WriteLine($"attempt {attempt}: the broker would not take the publish yet");
            }

            if (await WaitForStoredAsync(kind, TimeSpan.FromSeconds(5)))
            {
                output.WriteLine($"event {kind} stored after {attempt} publish attempt(s)");
                return true;
            }
        }

        output.WriteLine($"event {kind} still absent after {attempt} publish attempt(s)");
        return false;
    }

    private async Task<bool> WaitForStoredAsync(string kind, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountAsync(kind) > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return await CountAsync(kind) > 0;
    }

    private async Task<long> CountAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        return await database.Database
            .SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM events WHERE kind = {0} AND fab_id = {1}", kind, Fab)
            .SingleAsync();
    }

    /// <summary>
    /// Answers whether the broker took the publish, rather than throwing. While
    /// the broker is coming back a refused CONNECT is the expected answer and not
    /// a failure of this test — the caller retries. The catch names
    /// <see cref="MqttCommunicationException"/> specifically, which covers both
    /// the socket failure and the refused CONNECT
    /// (<c>MqttConnectingFailedException</c> derives from it); anything else
    /// still surfaces.
    /// </summary>
    private async Task<bool> PublishAsync(string payload)
    {
        string jwt = await SimulatorTokenAsync();
        Uri broker = aspire.App.GetEndpoint(Broker, "mqtt");

        using IMqttClient client = new MqttClientFactory().CreateMqttClient();

        try
        {
            MqttClientConnectResult connected = await client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithClientId($"{SimulatorClientId}-{Guid.CreateVersion7():N}")
                .WithCredentials(SimulatorClientId, jwt)
                .WithTcpServer(broker.Host, broker.Port)
                .WithCleanSession(true)
                .WithTimeout(TimeSpan.FromSeconds(10))
                .Build());

            if (connected.ResultCode != MqttClientConnectResultCode.Success)
            {
                return false;
            }

            MqttClientPublishResult published = await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(Topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());

            await client.DisconnectAsync();

            return published.IsSuccess;
        }
        catch (MqttCommunicationException exception)
        {
            output.WriteLine($"publish attempt refused: {exception.Message}");
            return false;
        }
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

    private static string Payload(string kind, string note) => JsonSerializer.Serialize(new
    {
        eventId = Guid.CreateVersion7(),
        kind,
        occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        payload = new { note },
    });
}

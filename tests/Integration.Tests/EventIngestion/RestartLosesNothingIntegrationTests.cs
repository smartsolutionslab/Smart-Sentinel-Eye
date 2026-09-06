using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 020 T017 — quickstart step 2. The in-memory buffer used to hold up to
/// 5 000 events the system had already said it accepted; losing it lost them,
/// and nothing was even logged.
///
/// <para>
/// <b>Nothing was implemented for this.</b> It passes because an envelope
/// sitting in the channel is no longer something anyone was promised — the
/// broker has not been acknowledged, so it still holds its copy. If this fails,
/// an acknowledgement is still happening too early somewhere.
/// </para>
///
/// <para>
/// The service is restarted through Aspire rather than killed outright, because
/// a project resource is a local process the fixture would not get back. The
/// substitution is honest here and only here: nothing is acknowledged before it
/// is stored, so a graceful stop and a crash leave the broker holding exactly
/// the same set. A real kill is walked by hand in the quickstart, and what it
/// showed is on the PR.
/// </para>
/// </summary>
/// <remarks>
/// <b>Excluded from CI by its category.</b> It restarts a resource through
/// Aspire, and on the CI runner that command fails outright — "Failed to stop
/// resource". A test that goes red on a platform limitation rather than a code
/// defect teaches people to ignore red CI, which costs more than this test is
/// worth there. The cost of excluding it is real and stated: SC-002 has no CI
/// coverage, and is verified locally and by hand instead (verification.md §2).
/// </remarks>
[Collection(AspireCollection.Name)]
[Trait("Category", "Disruptive")]
public class RestartLosesNothingIntegrationTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";
    private const int Published = 500;

    [Fact]
    public async Task A_restart_mid_drain_stores_every_event_exactly_once()
    {
        string kind = $"Restart{Guid.CreateVersion7():N}"[..20];

        await PublishAsync("fab/munich/plc/station-4", kind, Published);
        output.WriteLine($"published {Published} events");

        // Deliberately not waiting for the drain: the whole question is what
        // happens to what is still in flight.
        await RestartAsync("event-ingestion");
        output.WriteLine("restarted event-ingestion mid-drain");

        (long total, long distinct) = await WaitForAsync(kind, Published, TimeSpan.FromMinutes(3));
        output.WriteLine($"after restart: count={total} distinct={distinct}");

        total.ShouldBe(Published, "an event still in the buffer at restart was lost");
        distinct.ShouldBe(total, "a redelivered event was stored twice");
    }

    /// <summary>
    /// Restarts the service, and — whatever happens — leaves it running.
    ///
    /// <para>
    /// This is the most destructive thing any test in the suite does, and the
    /// first version did it without a net: when the restart failed on CI, the
    /// service stayed down and <b>every EventIngestion test after it</b> failed
    /// with a socket error. One test's environment problem read as eleven
    /// unrelated failures, which is exactly how the real one gets buried.
    /// </para>
    /// </summary>
    private async Task RestartAsync(string resourceName)
    {
        ResourceCommandService commands =
            aspire.App.Services.GetRequiredService<ResourceCommandService>();

        try
        {
            ExecuteCommandResult result = await commands.ExecuteCommandAsync(
                resourceName, KnownResourceCommands.RestartCommand, CancellationToken.None);
            result.Success.ShouldBeTrue($"could not restart {resourceName}: {result.Message}");
        }
        finally
        {
            // Start is idempotent on a running resource, so this is safe after
            // a restart that worked and is the repair after one that did not.
            await commands.ExecuteCommandAsync(
                resourceName, KnownResourceCommands.StartCommand, CancellationToken.None);

            await WaitForHealthyAsync(resourceName);
        }
    }

    /// <summary>
    /// Waits for the resource, and on failure prints <b>its own account</b> of
    /// why it did not come back.
    ///
    /// <para>
    /// Without this the failure is "event-ingestion failed to start" and nothing
    /// more, which is where #2038 sat: two plausible causes and no way to choose
    /// between them. The fixture is already tailing this resource; the tail was
    /// simply never read on this path.
    /// </para>
    ///
    /// <para>
    /// A separate method rather than a <c>try</c> in the caller's <c>finally</c>,
    /// because CA2219 forbids raising from a finally block — and the rethrow is
    /// the point: swallowing here would turn a service that never came back into
    /// a passing test.
    /// </para>
    ///
    /// <para>
    /// <b><c>WaitOnResourceUnavailable</c>, and that is the whole of #2038.</b> A
    /// restart passes <i>through</i> an unavailable state on its way back up, and
    /// the default behaviour treats reaching one as a reason to stop waiting —
    /// so the wait abandoned the resource for the transition it exists to watch.
    /// Under load the window is wider and the wait lands inside it, which is why
    /// this failed on a busy machine and passed on an idle one for the same code.
    /// </para>
    ///
    /// <para>
    /// The evidence was the snapshot rather than the logs: <c>state=Running</c>,
    /// no exit code, no stop timestamp, and nothing on stdout. The service never
    /// crashed and never failed to start — the assertion about it did.
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
            output.WriteLine($"---- {resourceName} log tail ----");
            output.WriteLine(aspire.RecentLogs(resourceName));

            throw;
        }
    }

    private async Task<(long Total, long Distinct)> WaitForAsync(
        string kind, long expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        (long total, long distinct) = (0, 0);

        while (DateTimeOffset.UtcNow < deadline)
        {
            (total, distinct) = await CountAsync(kind);
            if (total >= expected)
            {
                // One more pass, a moment later: a duplicate arriving just
                // behind the last event would be missed by stopping the instant
                // the count is met, and duplicates are half the claim.
                await Task.Delay(TimeSpan.FromSeconds(5));
                return await CountAsync(kind);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return (total, distinct);
    }

    private async Task<(long Total, long Distinct)> CountAsync(string kind)
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
            .WithTimeout(TimeSpan.FromSeconds(30))
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
        payload = new { note = "spec 020 restart" },
    });
}

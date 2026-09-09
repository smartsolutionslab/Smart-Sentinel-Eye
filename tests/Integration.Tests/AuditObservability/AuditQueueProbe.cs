using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmartSentinelEye.AuditObservability.Infrastructure;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Reads the audit queues' in-flight population straight off RabbitMQ's
/// management API (spec 109 US3).
///
/// <para>
/// <b>The broker, not a log search.</b> ADR-0127 recorded an Aspire
/// structured-log search returning zero hits for events that were demonstrably
/// landing, so the evidence here comes from the broker and from Postgres and
/// from nowhere else.
/// </para>
///
/// <para>
/// <b>A refusal throws naming the address and the status.</b> An unreachable
/// management API and an idle queue are indistinguishable in a number, and the
/// whole reading is built on telling those two apart.
/// </para>
/// </summary>
internal static class AuditQueueProbe
{
    /// <summary>
    /// Queues are named <c>{moduleQueuePrefix}.{eventType.FullName}</c>, and the
    /// audit module's prefix is its context name. Read from the module rather
    /// than spelled here: <c>wolverine_audit</c> is the <i>outbox schema</i>, not
    /// the queue prefix, and a filter on it matches nothing and reports a mean of
    /// zero — the very failure this probe exists to refuse.
    /// </summary>
    internal static readonly string QueuePrefix = AuditObservabilityInfrastructureModule.ContextName + ".";

    /// <summary>
    /// The management plugin, with the credentials the AppHost parameterised
    /// rather than guessed: a 401 here reads exactly like "no queues".
    /// </summary>
    internal static async Task<HttpClient> ClientAsync(AspireFixture aspire, CancellationToken cancellationToken)
    {
        Uri management = aspire.App.GetEndpoint("rabbitmq", "management");
        string connection = await aspire.App.GetConnectionStringAsync("rabbitmq", cancellationToken) ?? "";
        string userInfo = new Uri(connection).UserInfo;

        HttpClient client = new() { BaseAddress = management };
        client.DefaultRequestHeaders.Authorization = new(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(userInfo))));

        return client;
    }

    /// <summary>Sums <c>messages_unacknowledged</c> across the audit queues.</summary>
    internal static async Task<int> UnacknowledgedAsync(HttpClient broker, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await broker.GetAsync("/api/queues", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the RabbitMQ management API at {broker.BaseAddress}api/queues answered "
                + $"{(int)response.StatusCode} {response.ReasonPhrase}. A sampler that reported zero "
                + "here would be reporting an unreachable broker as an empty queue.");
        }

        JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        int unacknowledged = 0;
        foreach (JsonElement queue in payload.EnumerateArray())
        {
            string name = queue.GetProperty("name").GetString() ?? "";
            if (name.StartsWith(QueuePrefix, StringComparison.Ordinal)
                && queue.TryGetProperty("messages_unacknowledged", out JsonElement outstanding))
            {
                unacknowledged += outstanding.GetInt32();
            }
        }

        return unacknowledged;
    }

    /// <summary>
    /// Polls until the audit queues report nothing unacknowledged, so a run's
    /// sample window contains that run's population and nobody else's.
    /// </summary>
    internal static async Task<int> WaitForQuiescenceAsync(
        HttpClient broker, TimeSpan deadline, CancellationToken cancellationToken)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + deadline;
        int unacknowledged = await UnacknowledgedAsync(broker, cancellationToken);

        while (unacknowledged > 0 && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            unacknowledged = await UnacknowledgedAsync(broker, cancellationToken);
        }

        return unacknowledged;
    }
}

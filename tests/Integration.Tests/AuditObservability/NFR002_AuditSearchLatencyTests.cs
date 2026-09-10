using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Spec 009 NFR-002 (T073): a cross-cutting <c>GET /audit</c> with a tight
/// time window over the warm hot tier returns within p99 ≤ 200 ms. Seeds the
/// hypertable with ~100 k rows (about one month at the 100 ev/s target),
/// spread across the last 24 h so the <c>occurred_at</c> + fab index is the
/// hot path, then runs a warm-up + measured loop of the real read endpoint.
/// </summary>
[Collection(AspireCollection.Name)]
public class NFR002_AuditSearchLatencyTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const int SeedRows = 100_000;
    private const int WarmupIterations = 100;
    private const int MeasureIterations = 1_000;
    /// <summary>
    /// <b>Threshold 200 ms p99. Observed p99 55.8 ms and 33.3 ms</b>
    /// over 1 000 requests against 100 000 seeded rows, twice (dev box,
    /// Release, 2026-09-10, issue #2149) — p50 12.8 ms / 12.5 ms,
    /// max 97.0 ms / 63.7 ms, so the budget sits about
    /// <b>3.6–6.0×</b> above the worst p99 recorded.
    ///
    /// <para>
    /// One of the healthier margins among the nine budgets #2149 found
    /// unmeasured, and worth reading with its shape in mind: the max sits at about
    /// half the budget, so the tail is the part of this measurement doing real
    /// work — which is what a p99 gate over a hypertable is for. The figure is dev-box hot
    /// tier, not CI.
    /// </para>
    ///
    /// <para>
    /// <b>A local SLO</b> (spec 009 NFR-002) for the read path — <b>not one of
    /// constitution §IV's six legs</b>, which budget the event-to-overlay path
    /// rather than a cross-cutting audit query.
    /// </para>
    /// </summary>
    private const double P99BudgetMs = 200;

    [Fact]
    public async Task Search_over_a_24h_window_p99_stays_under_200ms()
    {
        await SeedHotTierAsync();

        using HttpClient client = await aspire.CreateAdminClientAsync("audit-observability");
        string since = DateTimeOffset.UtcNow.AddHours(-24).ToString("O", CultureInfo.InvariantCulture);
        string query = $"/audit?fabId=munich&since={Uri.EscapeDataString(since)}&pageSize=50";

        // Warm up the connection pool, EF query plan, and the read path.
        for (int i = 0; i < WarmupIterations; i++)
        {
            using HttpResponseMessage warm = await client.GetAsync(query);
            warm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        double[] elapsedMs = new double[MeasureIterations];
        for (int i = 0; i < MeasureIterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await client.GetAsync(query);
            elapsedMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Array.Sort(elapsedMs);
        double p50 = elapsedMs[MeasureIterations / 2];
        double p99 = elapsedMs[(int)Math.Ceiling(MeasureIterations * 0.99) - 1];
        double max = elapsedMs[^1];

        // Reported on the success path as well as the failure path (#2149): the
        // customMessage below is built only when the assertion fails, so a green
        // run measured 1 000 requests and then threw the numbers away.
        output.WriteLine(
            $"GET /audit over a 24h window, {SeedRows} seeded rows, {MeasureIterations} requests: "
            + $"p50 = {p50:F1} ms, p99 = {p99:F1} ms, max = {max:F1} ms (budget {P99BudgetMs} ms p99)");

        p99.ShouldBeLessThan(
            P99BudgetMs,
            $"search p50 = {p50:F1} ms, p99 = {p99:F1} ms, max = {max:F1} ms over {SeedRows} rows");
    }

    private async Task SeedHotTierAsync()
    {
        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));

        string emptyJson = "{}";
        // One statement, server-side: spread occurred_at across the last 23h so
        // the rows fall inside the 24h query window and on the warm chunk(s).
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_events (
                audit_id, occurred_at, received_at, fab_id, event_kind,
                resource_kind, resource_identifier, actor_identifier,
                actor_username, event_identifier, payload, payload_size_bytes,
                schema_version)
            SELECT
                gen_random_uuid(),
                now() - (random() * INTERVAL '23 hours'),
                now(),
                'munich',
                'CameraRegisteredV1',
                'camera',
                gen_random_uuid()::text,
                gen_random_uuid(),
                NULL,
                gen_random_uuid(),
                {emptyJson}::jsonb,
                2,
                1
            FROM generate_series(1, {SeedRows})
            """);
    }
}

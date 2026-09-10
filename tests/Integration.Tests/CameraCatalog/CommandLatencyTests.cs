using System.Diagnostics;
using System.Net.Http.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.CameraCatalog;

/// <summary>
/// Latency budget enforcement (constitution §IV + ADR-0031). The command
/// path POST /cameras must stay within 200 ms p95 against the AspireFixture
/// stack. The first request includes JIT + EF first-query overhead; we drop
/// the first N samples as warmup before measuring p95.
/// </summary>
[Collection(AspireCollection.Name)]
public class CommandLatencyTests(AspireFixture aspire) : IAsyncLifetime
{
    private const int SampleCount = 100;
    private const int WarmupCount = 10;
    /// <summary>
    /// <b>Threshold 200 ms p95 — and the tightest margin of the seven budgets
    /// spec 123 measured.</b> Three observations on one dev box within twenty
    /// minutes (2026-09-10, issue #2149):
    ///
    /// <list type="bullet">
    /// <item>median 73.4 ms, <b>p95 219.4 ms — red</b>, on the first run after
    /// the machine had been busy;</item>
    /// <item>median 16.4 ms, p95 115.0 ms — green, 1.7× inside;</item>
    /// <item>median 19.7 ms, p95 49.1 ms — green.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Spec 001 promised this figure and it survives only in PR #90</b>
    /// (2026-05-26): <i>median 19.7 ms, p95 98.8 ms</i> — a 2× margin then,
    /// recorded in the PR's ADR-0031 latency section and nowhere in the tree.
    /// Against that single figure, this box spreads from <b>half it to more than
    /// twice it</b> across three runs an hour apart — which is why one
    /// observation is not a margin, and why FR-004 asks for two.
    /// </para>
    ///
    /// <para>
    /// <b>The budget is not changed here, in either direction</b> (#2141,
    /// ADR-0144). What is written down is that it is close: this test breaches
    /// on a cold, contended dev box while passing on CI's Linux runner, so the
    /// margin is real but thin, and a cold-run red here is evidence about the
    /// box before it is evidence about the code.
    /// </para>
    ///
    /// <para>
    /// <b>Not one of constitution §IV's six legs.</b> §IV budgets an
    /// asynchronous event-to-overlay path; this is a synchronous HTTP command.
    /// The distinction is spec 116's (#2119), drawn for the sibling layout
    /// budget for the same reason.
    /// </para>
    /// </summary>
    private const int BudgetMilliseconds = 200;

    public Task InitializeAsync() => aspire.ResetCameraCatalogAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task POST_cameras_p95_stays_within_the_command_path_budget()
    {
        using HttpClient client = await aspire.CreateAdminClientAsync("camera-catalog");

        List<double> latencies = new(capacity: SampleCount);

        for (int index = 0; index < SampleCount + WarmupCount; index++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/cameras",
                new { name = $"Cam-Latency-{index:D4}", rtspUrl = $"rtsp://10.0.5.{index % 250}/h264" });
            sw.Stop();

            response.EnsureSuccessStatusCode();

            if (index >= WarmupCount)
            {
                latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        latencies.Sort();
        double p95 = latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1];
        double median = latencies[latencies.Count / 2];

        // Surface the measurement in test output regardless of pass/fail.
        Console.WriteLine($"POST /cameras: n={latencies.Count} median={median:F1}ms p95={p95:F1}ms budget={BudgetMilliseconds}ms");

        p95.ShouldBeLessThan(
            BudgetMilliseconds,
            customMessage: $"POST /cameras p95 was {p95:F1}ms over {latencies.Count} samples; budget is {BudgetMilliseconds}ms.");
    }
}

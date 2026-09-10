using System.Diagnostics;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 002 T086 — measures click-to-first-frame latency through the
/// part of the WHEP handshake we control: <c>POST /streams/authorize</c>,
/// the MediaMTX external-auth callback. Asserts p95 stays under 3 s over
/// 20 sequential opens against the Aspire stack (warm-cache regime).
///
/// The real browser-side WebRTC negotiation is deferred to a headless-
/// browser harness per the plan; the auth-hook round-trip is the
/// dominant operator-controlled term in that latency budget.
/// </summary>
[Collection(AspireCollection.Name)]
public class WhepHandshakeLatencyTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    private const int Iterations = 20;
    private const int P95BudgetMilliseconds = 3000;

    public async Task InitializeAsync()
    {
        await aspire.ResetMediaMtxAsync();
        await aspire.ResetStreamDistributionAsync();
        await aspire.ResetCameraCatalogAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Whep_auth_hook_p95_stays_under_three_seconds_over_twenty_opens()
    {
        string token = await aspire.GetAccessTokenAsync(
            AspireFixture.AdminUsername, AspireFixture.AdminPassword);

        // Warm the OIDC discovery cache + JWT validator state. The first
        // call hits Keycloak's well-known endpoint; subsequent calls reuse
        // the cached signing keys. The SLO covers the warm regime.
        await aspire.StreamDistribution.PostAsJsonAsync(
            "/streams/authorize", new { token, path = $"cam-{Guid.CreateVersion7()}", action = "read" });

        double[] elapsedMs = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            Guid camera = Guid.CreateVersion7();
            Stopwatch sw = Stopwatch.StartNew();
            HttpResponseMessage response = await aspire.StreamDistribution.PostAsJsonAsync(
                "/streams/authorize", new { token, path = $"cam-{camera}", action = "read" });
            sw.Stop();
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                // Surface the server-side stack (developer exception page in the
                // E2E stack) so a CI-only 500 here is diagnosable from the log.
                string body = await response.Content.ReadAsStringAsync();
                response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK,
                    $"iteration {i}: unexpected status. response body:\n{(body.Length > 4000 ? body[..4000] : body)}");
            }
            elapsedMs[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(elapsedMs);
        // p95 = the 19th-of-20 entry (index 18 zero-based ≈ 95th percentile).
        double p95 = elapsedMs[(int)Math.Ceiling(Iterations * 0.95) - 1];
        double p50 = elapsedMs[Iterations / 2];

        // Surface the measurement whether or not it passes. The assertion's
        // customMessage is built only when the assertion fails, so a green run
        // used to discard the figure it had just produced (#2149).
        output.WriteLine(
            $"POST /streams/authorize over {Iterations} opens: p50 = {p50:F0} ms, "
            + $"p95 = {p95:F0} ms, max = {elapsedMs[^1]:F0} ms "
            + $"(budget {P95BudgetMilliseconds} ms)");

        p95.ShouldBeLessThan(
            P95BudgetMilliseconds,
            $"p50 = {p50:F0} ms, p95 = {p95:F0} ms, max = {elapsedMs[^1]:F0} ms");
    }
}

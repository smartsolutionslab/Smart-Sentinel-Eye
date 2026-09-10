using System.Globalization;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Domain.Stream;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 125 (#2192, and #2152's F15). Was <c>SfuLatencyIsReadableTests</c>,
/// which is a claim this file can no longer make.
///
/// <para>
/// <strong>MediaMTX 1.21.0-ffmpeg publishes no per-path timing family.</strong>
/// Its exposition carries 123 families, twelve of them time-dimensioned, and
/// none of the twelve is in the <c>paths</c> group: four RTP <em>jitter</em>
/// gauges on server-side RTSP/WebRTC sessions, and eight SRT-only figures for
/// a protocol this system does not use. A live RTSP <em>source pull</em> — how
/// every camera here is ingested — leaves <c>rtsp_conns</c> and
/// <c>rtsp_sessions</c> at zero, so even those jitter gauges are never
/// populated by this leg. The SFU measures its ingest by <em>volume</em>, not
/// by time.
/// </para>
///
/// <para>
/// So constitution §IV's ≤ 80 ms camera → SFU budget is <strong>not</strong>
/// discharged by anything below. What is asserted here is that RTP is
/// arriving and that the named counters saying so still exist. Spec 125's
/// <c>verification.md</c> carries the full reading.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class SfuIngestMetricsTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// Matches <c>RtspTestSourceHealthTests.SettleTimeout</c>, whose reasoning
    /// applies unchanged: ~15 s expected, 30 s asserted.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The scrape is on its own clock, so a path can be <c>ready</c> a moment
    /// before its counters are. A settle window, not a retry-until-green: the
    /// last body read is asserted on either way.
    /// </summary>
    private static readonly TimeSpan ScrapeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The address family this suite already uses for a dial that fails rather than hangs.</summary>
    private const string UnreachableRtspUrl = "rtsp://10.0.6.1/h264";

    /// <summary>
    /// Named, not matched by substring. <c>ShouldContain("paths", Case.Insensitive)</c>
    /// — what this replaced — is satisfied by any of the seven <c>paths_*</c>
    /// families, and by any future family containing those five letters.
    /// </summary>
    private static readonly string[] IngestCounters =
    [
        "paths",
        "paths_inbound_bytes",
        "paths_bytes_received",
    ];

    /// <summary>
    /// Name segments that would make a family a duration or an interval. None
    /// appears in the <c>paths</c> group today; the day one does, the leg may
    /// have become measurable and §IV needs re-reading.
    /// </summary>
    private static readonly string[] TimeDimensions =
    [
        "_us", "_ms", "_seconds", "_duration", "_jitter", "_rtt", "_latency", "_time",
    ];

    public async Task InitializeAsync()
    {
        await aspire.ResetMediaMtxAsync();
        await aspire.ResetStreamDistributionAsync();
        await aspire.ResetCameraCatalogAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// FR-001 + FR-002. Both halves read one body: the counters a dashboard
    /// would plot are present under their real names and carry traffic, and
    /// nothing in that group is a timing figure.
    /// </summary>
    [Fact]
    public async Task The_sfu_exposes_named_per_path_ingest_counters_and_no_timing_family()
    {
        string path = await PublishingPathAsync();
        string exposition = await ScrapeUntilIngestAsync(path);

        foreach (string family in IngestCounters)
        {
            string? sample = Sample(exposition, family, path);

            sample.ShouldNotBeNull(
                $"the SFU exposed no '{family}' sample for '{path}', a path it is ingesting. "
                + "Either MediaMTX renamed or dropped the family — the pin is 1.21.0-ffmpeg — "
                + "or the exposition no longer describes paths at all.");
            sample.ShouldContain("state=\"ready\"", Case.Sensitive, $"'{family}' reports the path not ready");
        }

        Value(Sample(exposition, "paths_inbound_bytes", path)!).ShouldBeGreaterThan(
            0,
            $"'{path}' is ready and the SFU has received no RTP on it, so the camera → SFU "
            + "leg carries no traffic whatever the path state says");

        AssertNoTimingFamily(exposition);
    }

    /// <summary>
    /// FR-003. The name is unchanged and is finally true: this used to be one
    /// <c>GET /v3/paths/list</c> plus <c>EnsureSuccessStatusCode()</c>, which
    /// answers 200 with an empty list while video is dead on every wall.
    ///
    /// <para>
    /// <c>/v3/paths/get</c> rather than the list the issue names, because the
    /// list paginates — 148 paths came back as 50 pages on a dev SFU — and a
    /// per-path question should not depend on which page it landed on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Turning_metrics_on_did_not_disturb_the_media_path()
    {
        string path = await PublishingPathAsync();

        JsonElement state = await PathStateAsync(path);

        state.GetProperty("ready").GetBoolean().ShouldBeTrue(
            $"the SFU lists '{path}' but is not ingesting it, so metrics are on and the media path is dead");
        state.GetProperty("tracks").GetArrayLength().ShouldBeGreaterThan(
            0, $"'{path}' is ready and announced no track");
        state.GetProperty("inboundBytes").GetInt64().ShouldBeGreaterThan(
            0, $"'{path}' is ready, announced a track and has received nothing");
    }

    /// <summary>
    /// FR-004. The control, and the reason to believe the two above. Without
    /// it a green assertion is equally consistent with a predicate that says
    /// yes to every path — the same argument <c>RtspTestSourceHealthTests</c>
    /// makes for keeping its <c>Degraded</c> case beside its <c>Healthy</c> one.
    ///
    /// <para>
    /// If this goes red the predicate stopped discriminating, and the greens
    /// above mean nothing. That is a block, not an assertion to adjust.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_path_whose_source_does_not_answer_is_not_reported_as_ingesting()
    {
        string path = await ProvisionedPathAsync(UnreachableRtspUrl, "Degraded");

        JsonElement state = await PathStateAsync(path);
        string exposition = await ScrapeAsync();

        state.GetProperty("ready").GetBoolean().ShouldBeFalse($"nothing serves '{UnreachableRtspUrl}'");
        state.GetProperty("inboundBytes").GetInt64().ShouldBe(0, "a path nothing feeds received bytes");

        Sample(exposition, "paths", path).ShouldNotBeNull(
            $"'{path}' is absent from the exposition, so the assertions above checked a surface "
            + "the metrics endpoint does not describe");
        Value(Sample(exposition, "paths_inbound_bytes", path)!).ShouldBe(
            0, "the counter the other tests assert on is non-zero for a path nothing feeds");
    }

    private void AssertNoTimingFamily(string exposition)
    {
        IReadOnlyList<string> families = PathFamilies(exposition);

        output.WriteLine($"paths families: {string.Join(", ", families)}");

        families.ShouldNotBeEmpty(
            "the exposition carried no per-path family at all, which would make the absence "
            + "asserted next vacuously true — the defect this test was rewritten to remove");

        families.Where(IsTimeDimensioned).ShouldBeEmpty(
            "MediaMTX now exposes a time-dimensioned per-path family. It exposed none at "
            + "1.21.0-ffmpeg, which is why constitution §IV's camera → SFU row cannot claim "
            + "a measured figure from this endpoint. Read §IV before deleting this assertion: "
            + "the leg may have become measurable.");
    }

    private static bool IsTimeDimensioned(string family) =>
        TimeDimensions.Any(unit => family.Contains(unit, StringComparison.Ordinal));

    /// <summary>
    /// Distinct <c>paths*</c> family names in the exposition. A family name is
    /// everything before the label brace, so the match is exact by
    /// construction: <c>paths{</c> cannot match <c>paths_inbound_bytes{</c>.
    /// </summary>
    private static IReadOnlyList<string> PathFamilies(string exposition) =>
        [.. exposition.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("paths", StringComparison.Ordinal) && line.Contains('{'))
            .Select(line => line.Split('{')[0])
            .Distinct(StringComparer.Ordinal)];

    private static string? Sample(string exposition, string family, string path) =>
        exposition.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.StartsWith(family + "{", StringComparison.Ordinal)
                && line.Contains($"name=\"{path}\"", StringComparison.Ordinal));

    private static double Value(string sample) =>
        double.Parse(sample[(sample.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);

    private Task<string> PublishingPathAsync() =>
        ProvisionedPathAsync(AspireFixture.RtspTestSourceUrl, "Healthy");

    private async Task<string> ProvisionedPathAsync(string rtspUrl, string expectedState)
    {
        using HttpClient cameras = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streams = await aspire.CreateAdminClientAsync("stream-distribution");

        HttpResponseMessage registered = await cameras.PostAsJsonAsync(
            "/cameras", new { name = $"Cam-Sfu-Ingest-{Guid.NewGuid():N}", rtspUrl });
        registered.EnsureSuccessStatusCode();

        Guid camera = await registered.Content.ReadFromJsonAsync<Guid>();
        await WaitForStateAsync(streams, camera, expectedState);

        return MediaMtxPath.For(CameraIdentifier.From(camera)).Value;
    }

    private static async Task WaitForStateAsync(HttpClient streams, Guid camera, string expectedState)
    {
        DateTime deadline = DateTime.UtcNow + SettleTimeout;
        string lastObserved = "<no response yet>";

        while (DateTime.UtcNow < deadline)
        {
            JsonElement items = await streams.GetFromJsonAsync<JsonElement>(
                $"/streams?cameraIdentifiers={camera}");

            lastObserved = items.GetArrayLength() == 1
                ? items[0].GetProperty("state").GetString() ?? "<null state>"
                : $"<{items.GetArrayLength()} streams>";

            if (string.Equals(lastObserved, expectedState, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Stream for camera {camera} did not reach '{expectedState}' within "
            + $"{SettleTimeout.TotalSeconds:F0}s. Last observed: '{lastObserved}'. "
            + $"For 'Healthy', check that '{AspireFixture.RtspTestSourceUrl}' is composed (spec 076, #198).");
    }

    private async Task<JsonElement> PathStateAsync(string path)
    {
        using HttpClient api = aspire.App.CreateHttpClient("mediamtx", "api");

        HttpResponseMessage response = await api.GetAsync($"/v3/paths/get/{path}");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> ScrapeAsync()
    {
        using HttpClient metrics = aspire.App.CreateHttpClient("mediamtx", "metrics");

        HttpResponseMessage response = await metrics.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> ScrapeUntilIngestAsync(string path)
    {
        DateTime deadline = DateTime.UtcNow + ScrapeTimeout;
        string exposition = await ScrapeAsync();

        while (DateTime.UtcNow < deadline)
        {
            string? bytes = Sample(exposition, "paths_inbound_bytes", path);
            if (bytes is not null && Value(bytes) > 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            exposition = await ScrapeAsync();
        }

        output.WriteLine($"SFU exposition: {exposition.Split('\n').Length} lines");
        return exposition;
    }
}

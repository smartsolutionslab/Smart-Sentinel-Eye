using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 076 / issue #198 — the first integration observation of the
/// <c>Provisioning → Healthy</c> edge. Every stream test before this one
/// pointed its camera at an address nothing served, so the suite only ever
/// watched the failure half of the state machine.
///
/// <para>
/// Both cases live in one file on purpose. <see
/// cref="A_camera_at_an_unreachable_address_reaches_Degraded"/> is the contrast
/// that gives <see cref="A_camera_pointed_at_the_fixture_source_reaches_Healthy"/>
/// its meaning: without it, a green <c>Healthy</c> is equally consistent with a
/// watcher that reports <c>Healthy</c> for everything, which is the exact
/// failure mode spec 056 was written to close in its own domain. Split across
/// two files, one of them can be deleted without the other, and the surviving
/// half proves nothing.
/// </para>
///
/// <para>
/// The <c>Degraded</c> case is characterisation of behaviour that already
/// holds: it passes before the AppHost gate changes and must still pass,
/// unmodified, after.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class RtspTestSourceHealthTests(AspireFixture aspire) : IAsyncLifetime
{
    /// <summary>
    /// Matches <c>ListStreamsByCamerasIntegrationTests.SettleTimeout</c>. Spec
    /// 076 assumption A3 expects <c>Healthy</c> in ~15 s — container start,
    /// FFmpeg start, the SFU's dial and three 2 s poll intervals — so 30 s is
    /// the assertion budget, not the expectation.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An address on a subnet the CI runner and a developer machine both fail
    /// to route, so the SFU's dial fails rather than hanging on a firewall.
    /// The same address family <c>ListStreamsByCamerasIntegrationTests</c> uses.
    /// </summary>
    private const string UnreachableRtspUrl = "rtsp://10.0.6.1/h264";

    public async Task InitializeAsync()
    {
        await aspire.ResetMediaMtxAsync();
        await aspire.ResetStreamDistributionAsync();
        await aspire.ResetCameraCatalogAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// AS-1. The camera is registered at the URL the AppHost's
    /// <c>fixture-video</c> resource serves; the SFU pulls it exactly as it
    /// pulls a real camera, decodes a frame, and the watcher reports it.
    /// </summary>
    [Fact]
    public async Task A_camera_pointed_at_the_fixture_source_reaches_Healthy()
    {
        using HttpClient cameraClient = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streamClient = await aspire.CreateAdminClientAsync("stream-distribution");

        Guid camera = await RegisterAsync(
            cameraClient,
            $"Cam-Fixture-Source-{Guid.NewGuid():N}",
            AspireFixture.RtspTestSourceUrl);

        string state = await WaitForStateAsync(streamClient, camera, "Healthy", SettleTimeout);

        state.ShouldBe("Healthy");
    }

    /// <summary>
    /// AS-2. Characterisation, and the control for AS-1: an address nothing
    /// answers must still settle on <c>Degraded</c>. If this ever goes red the
    /// baseline moved and AS-1's green means nothing — that is a block, not an
    /// assertion to adjust.
    /// </summary>
    [Fact]
    public async Task A_camera_at_an_unreachable_address_reaches_Degraded()
    {
        using HttpClient cameraClient = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streamClient = await aspire.CreateAdminClientAsync("stream-distribution");

        Guid camera = await RegisterAsync(
            cameraClient,
            $"Cam-Unreachable-{Guid.NewGuid():N}",
            UnreachableRtspUrl);

        string state = await WaitForStateAsync(streamClient, camera, "Degraded", SettleTimeout);

        state.ShouldBe("Degraded");
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string name, string rtspUrl)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/cameras", new { name, rtspUrl });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>
    /// Polls the batch endpoint every 500 ms until the camera's stream reports
    /// <paramref name="expectedState"/>, and returns the state it saw.
    ///
    /// <para>
    /// On expiry the message names <strong>the last state actually observed</strong>,
    /// which is the whole reason this helper exists rather than a bare
    /// <c>Task.Delay</c> plus one read. "Did not reach Healthy" cannot
    /// distinguish "stayed Provisioning" — the path was never created, a
    /// StreamDistribution or messaging defect — from "went Degraded" — the path
    /// was created and the source did not answer, a reachability defect. Those
    /// have different causes and different fixes, and the timeout is the only
    /// place a reader learns which one happened.
    /// </para>
    /// </summary>
    private static async Task<string> WaitForStateAsync(
        HttpClient streamClient, Guid camera, string expectedState, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string lastObserved = "<no response yet>";

        while (DateTime.UtcNow < deadline)
        {
            HttpResponseMessage response = await streamClient.GetAsync(
                $"/streams?cameraIdentifiers={camera}");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                JsonElement items = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (items.GetArrayLength() == 1)
                {
                    lastObserved = items[0].GetProperty("state").GetString() ?? "<null state>";
                    if (string.Equals(lastObserved, expectedState, StringComparison.Ordinal))
                    {
                        return lastObserved;
                    }
                }
                else
                {
                    lastObserved = $"<{items.GetArrayLength()} streams for the camera>";
                }
            }
            else
            {
                lastObserved = $"<HTTP {(int)response.StatusCode}>";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Stream for camera {camera} did not reach '{expectedState}' within "
            + $"{timeout.TotalSeconds:F0}s. Last observed state: '{lastObserved}'. "
            + "'Degraded' means the path was created and the RTSP source did not answer "
            + $"(is '{AspireFixture.RtspTestSourceUrl}' composed under E2ETests=true? — spec 076, #198); "
            + "'Provisioning' means no stream was ever provisioned, which is a different defect.");
    }
}

using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 002 T077 — drives the cameras-list health badge use case. Registers
/// five cameras and asserts <c>GET /streams?cameraIdentifiers=...</c>
/// returns one DTO per identifier, each in its expected state, within 30
/// seconds (the SLO for "the badge has settled by the time the user sees
/// the row").
///
/// <para>
/// The five are a deliberate mix (spec 076, #198): three point at
/// <see cref="AspireFixture.RtspTestSourceUrl"/>, which the stack serves, and
/// reach <c>Healthy</c>; two point at addresses nothing routes and reach
/// <c>Degraded</c> after the StreamHealthWatcher polls MediaMTX. Five
/// identical <c>Degraded</c> rows would pass just as well against an endpoint
/// that reported one state for every camera it was handed — a mix is what
/// shows it <em>distinguishes</em>. Until this spec no RTSP source was
/// composed in the integration lane, so the batch could only be watched
/// failing.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class ListStreamsByCamerasIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    public async Task InitializeAsync()
    {
        await aspire.ResetMediaMtxAsync();
        await aspire.ResetStreamDistributionAsync();
        await aspire.ResetCameraCatalogAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Five_cameras_settle_into_observable_states_in_the_batch_API_within_30_seconds()
    {
        using HttpClient cameraClient = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streamClient = await aspire.CreateAdminClientAsync("stream-distribution");

        (string Address, string ExpectedState)[] sources =
        [
            (AspireFixture.RtspTestSourceUrl, "Healthy"),
            (AspireFixture.RtspTestSourceUrl, "Healthy"),
            (AspireFixture.RtspTestSourceUrl, "Healthy"),
            ("rtsp://10.0.6.1/h264", "Degraded"),
            ("rtsp://10.0.6.2/h264", "Degraded"),
        ];

        Guid[] cameras = new Guid[sources.Length];
        for (int index = 0; index < cameras.Length; index++)
        {
            cameras[index] = await RegisterAsync(
                cameraClient,
                $"Cam-Badge-{index}",
                sources[index].Address);
        }

        string[] expected = [.. sources.Select(source => source.ExpectedState)];

        await WaitForBatchSettledAsync(streamClient, cameras, expected, SettleTimeout);

        HttpResponseMessage response = await streamClient.GetAsync(
            $"/streams?cameraIdentifiers={string.Join(',', cameras)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement items = await response.Content.ReadFromJsonAsync<JsonElement>();
        items.GetArrayLength().ShouldBe(cameras.Length);

        Dictionary<Guid, string> stateByCamera = items.EnumerateArray().ToDictionary(
            element => Guid.Parse(element.GetProperty("cameraIdentifier").GetString()!),
            element => element.GetProperty("state").GetString()!);

        for (int index = 0; index < cameras.Length; index++)
        {
            stateByCamera.ShouldContainKey(cameras[index]);
            stateByCamera[cameras[index]].ShouldBe(expected[index]);
        }
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string name, string rtspUrl)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/cameras", new { name, rtspUrl });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>
    /// Waits until every camera reports the state expected of it.
    ///
    /// <para>
    /// The gate used to be "has left Provisioning", which was sufficient while
    /// all five addresses were unreachable and <c>Degraded</c> was terminal.
    /// It is not sufficient against a source that answers: a reachable camera
    /// is reported <c>Degraded</c> by the first sweep that lands before
    /// MediaMTX has a frame, and only then reaches <c>Healthy</c>. Leaving
    /// <c>Provisioning</c> would therefore release the wait while the three
    /// Healthy-to-be rows were still <c>Degraded</c>, and the assertion below
    /// would fail on timing rather than on behaviour. The condition is
    /// strictly stronger than the old one — every expected state here is a
    /// non-<c>Provisioning</c> state.
    /// </para>
    /// </summary>
    private static async Task WaitForBatchSettledAsync(
        HttpClient streamClient, Guid[] cameras, string[] expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string query = string.Join(',', cameras);
        string lastObserved = "<no response yet>";

        while (DateTime.UtcNow < deadline)
        {
            HttpResponseMessage response = await streamClient.GetAsync(
                $"/streams?cameraIdentifiers={query}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                JsonElement items = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (items.GetArrayLength() == cameras.Length)
                {
                    Dictionary<Guid, string> states = items.EnumerateArray().ToDictionary(
                        element => Guid.Parse(element.GetProperty("cameraIdentifier").GetString()!),
                        element => element.GetProperty("state").GetString()!);

                    if (cameras.Select((camera, index) =>
                            states.TryGetValue(camera, out string? state)
                            && string.Equals(state, expected[index], StringComparison.Ordinal))
                        .All(matched => matched))
                    {
                        return;
                    }

                    lastObserved = string.Join(
                        ", ",
                        cameras.Select((camera, index) =>
                            $"{expected[index]}->{(states.TryGetValue(camera, out string? state) ? state : "<absent>")}"));
                }
                else
                {
                    lastObserved = $"<{items.GetArrayLength()} of {cameras.Length} streams>";
                }
            }
            else
            {
                lastObserved = $"<HTTP {(int)response.StatusCode}>";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Streams for {cameras.Length} cameras did not all reach their expected state within "
            + $"{timeout.TotalSeconds:F0}s. Expected->observed: {lastObserved}. A camera at "
            + $"'{AspireFixture.RtspTestSourceUrl}' stuck on 'Degraded' means the fixture-video "
            + "resource is composed but its RTSP path is not being served (spec 076, #198).");
    }
}

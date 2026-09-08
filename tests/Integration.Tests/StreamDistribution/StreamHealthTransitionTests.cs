using System.Diagnostics;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.StreamDistribution.Domain.Stream;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 099 / issue #119 — the two transitions the suite has never made.
/// <see cref="RtspTestSourceHealthTests"/> registers two cameras at two fixed
/// addresses and asserts two fixed outcomes; nothing anywhere takes a stream
/// that has <em>reached</em> <c>Healthy</c> and watches it fall, or takes one
/// that has fallen and watches it recover.
///
/// <para>
/// <b>Why that gap matters, stated as the thing it lets through.</b> A watcher
/// that latched <c>Healthy</c> forever — a handler returning early whenever the
/// stream is already healthy — leaves every other test in this suite green:
/// <see cref="RtspTestSourceHealthTests.A_camera_at_an_unreachable_address_reaches_Degraded"/>
/// never enters <c>Healthy</c>, so it cannot be latched out of. These two facts
/// are the ones that go red under that mutation, and phase 4a ran it to prove
/// so rather than asserting it.
/// </para>
///
/// <para>
/// <b>The outage is provoked on the SFU, not in StreamDistribution.</b> The
/// camera's path is repointed with
/// <c>PATCH /v3/config/paths/patch/cam-{camera}</c> — the same call production's
/// <c>MediaMtxRtspGateway.RepointPathAsync</c> issues — which leaves
/// StreamDistribution's own record of the camera untouched. That is the
/// faithful shape of a camera going dark: the configuration is unchanged and
/// the system has to notice. Repointing through StreamDistribution's own
/// <c>PATCH /streams</c> would instead change the record, and a watcher
/// reporting health from the last-configured URL would still pass.
/// </para>
///
/// <para>
/// <b>Blast radius is one path this test created.</b> Each fact registers its
/// own camera under a fresh <see cref="Guid"/>, so <c>cam-{guid}</c> belongs to
/// no other test and xUnit's unordered execution within the collection cannot
/// matter. <b>Nothing is stopped</b> — <c>fixture-video</c> keeps serving and
/// <c>mediamtx</c> keeps running, which is why this class needs no
/// <c>[Trait("Category", "Disruptive")]</c> and therefore runs in CI's
/// integration job rather than being excluded from it.
/// </para>
///
/// <para>
/// Shared state <em>is</em> mutated, and saying otherwise would be false:
/// <see cref="InitializeAsync"/> runs the same three resets
/// <see cref="RtspTestSourceHealthTests"/> does, and the first of them deletes
/// <em>every</em> SFU path while the other two wipe two databases. That is the
/// sibling class's behaviour inherited unchanged — and it is also what makes a
/// failed restore in AS-1's <c>finally</c> below inconsequential.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class StreamHealthTransitionTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// Reaching a first state — container start, the SFU's dial, and a few of
    /// the watcher's 2 s sweeps. Matches <c>RtspTestSourceHealthTests.SettleTimeout</c>;
    /// it is the arrangement's budget and is spent before either measurement
    /// starts.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The budget under test, and the number in both method names. The
    /// arithmetic behind it: one sweep already in flight (2 s), the SFU's dial
    /// outcome (1-2 s), and one more sweep (2 s). A transition that does not fit
    /// is a finding to file, not a number to raise.
    /// </summary>
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// An address on a subnet neither CI nor a developer machine routes, so the
    /// SFU's dial fails rather than hanging on a firewall. The same address
    /// family <see cref="RtspTestSourceHealthTests"/> uses.
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
    /// AS-1. The camera reaches <c>Healthy</c> against the fixture source, then
    /// its SFU path is repointed at an address nothing answers. The clock starts
    /// at the repoint.
    /// </summary>
    [Fact]
    public async Task Stopping_the_RTSP_source_transitions_to_Degraded_within_15_seconds()
    {
        using HttpClient cameraClient = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streamClient = await aspire.CreateAdminClientAsync("stream-distribution");

        Guid camera = await RegisterAsync(
            cameraClient,
            $"Cam-Source-Stops-{Guid.NewGuid():N}",
            AspireFixture.RtspTestSourceUrl);

        await WaitForStateAsync(streamClient, camera, "Healthy", SettleTimeout);

        string path = MediaMtxPath.For(CameraIdentifier.From(camera)).Value;
        Stopwatch sinceOutage = new();
        string state = "<not observed>";

        try
        {
            sinceOutage.Start();
            await aspire.RepointMediaMtxPathAsync(path, UnreachableRtspUrl);
            state = await WaitForStateAsync(streamClient, camera, "Degraded", TransitionTimeout);
        }
        finally
        {
            sinceOutage.Stop();
            try
            {
                await aspire.RepointMediaMtxPathAsync(path, AspireFixture.RtspTestSourceUrl);
            }
            catch (HttpRequestException restoreFailed)
            {
                // Reported rather than thrown, because an exception leaving a
                // finally *replaces* the one in flight - and the one in flight
                // here is the TimeoutException naming the last state observed,
                // which is the whole diagnostic this class exists to produce.
                // Nothing downstream depends on the restore: the next class's
                // InitializeAsync calls ResetMediaMtxAsync, which deletes every
                // SFU path (AspireFixture.Db.cs:215).
                output.WriteLine(
                    $"Restoring '{path}' to the fixture source failed: {restoreFailed.Message}");
            }
        }

        output.WriteLine(
            $"Healthy -> Degraded in {sinceOutage.Elapsed.TotalSeconds:F1}s "
            + $"(budget {TransitionTimeout.TotalSeconds:F0}s).");

        state.ShouldBe("Degraded");

        // The printed figure is the asserted one. Without this the enforced
        // quantity was the repoint call plus WaitForStateAsync's own 15 s
        // deadline, so a transition printing "15.6s (budget 15s)" passed green
        // and plan.md's "budget breached => finding" rule never bit.
        sinceOutage.Elapsed.ShouldBeLessThan(TransitionTimeout);
    }

    /// <summary>
    /// AS-2. The recovery half, and the one the domain permits rather than
    /// merely tolerates — <c>Stream.ReportHealthy</c> guards only against
    /// <c>Retired</c>. Reaching <c>Degraded</c> is arrangement; the clock starts
    /// at the repoint back to the fixture source, which is the act.
    /// </summary>
    [Fact]
    public async Task Restarting_the_RTSP_source_transitions_back_to_Healthy_within_15_seconds()
    {
        using HttpClient cameraClient = await aspire.CreateAdminClientAsync("camera-catalog");
        using HttpClient streamClient = await aspire.CreateAdminClientAsync("stream-distribution");

        Guid camera = await RegisterAsync(
            cameraClient,
            $"Cam-Source-Returns-{Guid.NewGuid():N}",
            AspireFixture.RtspTestSourceUrl);

        await WaitForStateAsync(streamClient, camera, "Healthy", SettleTimeout);

        string path = MediaMtxPath.For(CameraIdentifier.From(camera)).Value;
        Stopwatch sinceRestore = new();

        await aspire.RepointMediaMtxPathAsync(path, UnreachableRtspUrl);
        try
        {
            await WaitForStateAsync(streamClient, camera, "Degraded", TransitionTimeout);
        }
        finally
        {
            // The restore is the act under test and the cleanup at once, so it
            // belongs in the finally: a failed arrangement must still leave the
            // path pointed back at the fixture source.
            sinceRestore.Start();
            await aspire.RepointMediaMtxPathAsync(path, AspireFixture.RtspTestSourceUrl);
        }

        string state = await WaitForStateAsync(streamClient, camera, "Healthy", TransitionTimeout);
        sinceRestore.Stop();

        output.WriteLine(
            $"Degraded -> Healthy in {sinceRestore.Elapsed.TotalSeconds:F1}s "
            + $"(budget {TransitionTimeout.TotalSeconds:F0}s).");

        state.ShouldBe("Healthy");
        sinceRestore.Elapsed.ShouldBeLessThan(TransitionTimeout);
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
    /// and here that is load-bearing rather than merely helpful: "stayed Healthy"
    /// is the latch this class exists to catch, while "went Provisioning" or
    /// "went Offline" are different defects with different fixes. A bare
    /// "did not reach Degraded" cannot tell them apart.
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
            + "A stream that stayed 'Healthy' after its SFU path was repointed away is the "
            + "latch spec 099 exists to catch; 'Provisioning' means no stream was provisioned "
            + "and 'Offline' means the 5-minute window elapsed - both different defects.");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.StreamDistribution.Infrastructure.Attribution;
using SmartSentinelEye.StreamDistribution.Infrastructure.Persistence;
using CameraIdentifier = SmartSentinelEye.StreamDistribution.Domain.Stream.CameraIdentifier;
using StreamAggregate = SmartSentinelEye.StreamDistribution.Domain.Stream.Stream;
using StreamState = SmartSentinelEye.StreamDistribution.Domain.Stream.StreamState;

namespace SmartSentinelEye.Integration.Tests.StreamDistribution;

/// <summary>
/// Spec 016 T025 — SC-004 and the only test of FR-009.
///
/// <para>
/// Between the migration and the first attribution pass every stream has a
/// null fab. That window is deliberate (research.md §2) and it must fail
/// closed: an unattributed stream is shown to <em>nobody</em>, not to its own
/// fab's operator and not to a multi-fab one.
/// </para>
///
/// <para>
/// This is the case that is invisible when it works — nothing observable
/// happens — which is exactly why it is written down. The alternative, an
/// unattributed stream shown to everyone, is the defect this feature removes
/// reappearing as a transitional state.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class StreamFabAttributionIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    private const string MultiFabOperator = "op-multi@smart-sentinel-eye.test";
    private const string OperatorPassword = "Operator1234";

    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromSeconds(30);

    public async Task InitializeAsync()
    {
        await aspire.ResetMediaMtxAsync();
        await aspire.ResetStreamDistributionAsync();
        await aspire.ResetCameraCatalogAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_stream_with_no_fab_is_returned_to_nobody()
    {
        Guid camera = await ProvisionInMunichAsync();
        await BlankTheFabAsync(camera);

        // Its own fab's operator first — the tempting special case is to treat
        // a null fab as "belongs to everyone here", and this is where that
        // would show.
        using HttpClient munich = await aspire.CreateAdminClientAsync("stream-distribution");
        (await streamsVisibleTo(munich, camera)).ShouldBeFalse();
        (await munich.GetAsync($"/streams/{camera}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And a multi-fab operator, who holds every fab there is and still
        // must not see a stream that belongs to none of them.
        using HttpClient both = await aspire.CreateAuthenticatedClientAsync(
            "stream-distribution", MultiFabOperator, OperatorPassword);
        (await streamsVisibleTo(both, camera)).ShouldBeFalse();
        (await both.GetAsync($"/streams/{camera}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        static async Task<bool> streamsVisibleTo(HttpClient client, Guid camera)
        {
            HttpResponseMessage listed = await client.GetAsync($"/streams?cameraIdentifiers={camera}");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());

            JsonElement rows = await listed.Content.ReadFromJsonAsync<JsonElement>();
            return rows.GetArrayLength() > 0;
        }
    }

    /// <summary>
    /// The other half of the same guarantee: blanking the fab is what makes it
    /// invisible, so the stream must be visible before that. Without this the
    /// test above would pass against a listing that was simply broken.
    /// </summary>
    [Fact]
    public async Task The_same_stream_is_visible_while_it_still_has_its_fab()
    {
        Guid camera = await ProvisionInMunichAsync();

        using HttpClient munich = await aspire.CreateAdminClientAsync("stream-distribution");
        HttpResponseMessage read = await munich.GetAsync($"/streams/{camera}");

        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        JsonElement stream = await read.Content.ReadFromJsonAsync<JsonElement>();
        stream.GetProperty("fab").GetString().ShouldBe("munich");
    }

    /// <summary>
    /// FR-008 and FR-010 against the real mechanism: a real
    /// <c>client_credentials</c> token for the cross-fab service account
    /// (ADR-0116), a real fab-scoped <c>GET /cameras</c>, and real rows whose
    /// fab has been blanked to recreate the pre-feature state.
    ///
    /// <para>
    /// Both fabs, because the failure this guards against is the one specs
    /// 013–015 could not make: attributing everything to whichever fab
    /// happened to be live. A pass that filled both streams with munich would
    /// look like success against a single-fab assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Blanked_streams_reacquire_the_fab_of_their_own_camera()
    {
        (Guid inMunich, Guid inDresden) = await ProvisionOnePerFabAsync();
        await BlankTheFabAsync(inMunich);
        await BlankTheFabAsync(inDresden);

        IReadOnlyDictionary<Guid, string> fabsByCamera = await RealLookup().FabsByCameraAsync(CancellationToken.None);

        // The service account spans both fabs; a token holding only one would
        // resolve half of this and silently strand the other plant.
        fabsByCamera[inMunich].ShouldBe("munich");
        fabsByCamera[inDresden].ShouldBe("dresden");

        (await AttributePassAsync(fabsByCamera)).ShouldBe(2);

        await using StreamDistributionDbContext reread =
            await aspire.CreateStreamDistributionDbContextAsync();
        Dictionary<Guid, string> stored = await reread.Streams
            .AsNoTracking()
            .Where(stream => stream.Fab != null)
            .ToDictionaryAsync(stream => stream.Camera.Value, stream => stream.Fab!.Value);

        stored[inMunich].ShouldBe("munich");
        stored[inDresden].ShouldBe("dresden");
    }

    /// <summary>
    /// Spec 083 T001 — FR-001 and FR-002. A camera pulled off the wall still
    /// names the plant it stood in, and its stream's fab is that plant's
    /// whether or not the hardware is still there.
    ///
    /// <para>
    /// The assertion this test deliberately does <em>not</em> make is that the
    /// request carries a particular query string. That would prove a string,
    /// and would stay green against a catalogue that accepted the parameter
    /// and filtered the row out anyway. What is asserted is resolution: the
    /// map holds a key for a camera whose status is <c>Decommissioned</c>, and
    /// the stream that key resolves ends one pass carrying "munich".
    /// </para>
    ///
    /// <para>
    /// Retire first, blank second. <c>CameraRetiredV1</c> retires the stream
    /// and <c>Stream</c> carries an EF concurrency token, so a fab blanked
    /// before that handler lands is the write that loses — and the row this
    /// test depends on would quietly still have its fab.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stream_whose_camera_was_decommissioned_reacquires_its_fab()
    {
        Guid camera = await ProvisionInMunichAsync();
        await RetireAsync(camera);
        await BlankTheFabAsync(camera);

        // The arrange said out loud, because the whole test is meaningless if
        // the camera is not actually retired: a Registered camera resolves
        // today, and would make this green against unfixed code.
        using HttpClient cameras = await aspire.CreateAdminClientAsync("camera-catalog");
        HttpResponseMessage read = await cameras.GetAsync($"/cameras/{camera}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());

        JsonElement retired = await read.Content.ReadFromJsonAsync<JsonElement>();
        retired.GetProperty("status").GetString().ShouldBe("Decommissioned");
        retired.GetProperty("fab").GetString().ShouldBe("munich");

        IReadOnlyDictionary<Guid, string> fabsByCamera =
            await RealLookup().FabsByCameraAsync(CancellationToken.None);

        fabsByCamera.ShouldContainKey(camera);
        fabsByCamera[camera].ShouldBe("munich");

        (await AttributePassAsync(fabsByCamera)).ShouldBe(1);

        await using StreamDistributionDbContext reread =
            await aspire.CreateStreamDistributionDbContextAsync();
        StreamAggregate stored = await reread.Streams
            .AsNoTracking()
            .SingleAsync(stream => stream.Camera == CameraIdentifier.From(camera));

        stored.Fab.ShouldNotBeNull();
        stored.Fab.Value.ShouldBe("munich");
    }

    /// <summary>
    /// FR-010 on the same real lookup: a camera the catalogue does not know
    /// resolves to nothing, so its stream stays null rather than inheriting
    /// whichever fab was in the map.
    /// </summary>
    [Fact]
    public async Task A_stream_whose_camera_the_catalogue_does_not_know_stays_unattributed()
    {
        Guid known = await ProvisionInMunichAsync();
        await BlankTheFabAsync(known);

        IReadOnlyDictionary<Guid, string> fabsByCamera = await RealLookup().FabsByCameraAsync(CancellationToken.None);
        fabsByCamera.ShouldNotContainKey(Guid.CreateVersion7());

        await using StreamDistributionDbContext context =
            await aspire.CreateStreamDistributionDbContextAsync();
        List<StreamAggregate> unattributed = await context.Streams
            .Where(stream => stream.Fab == null)
            .ToListAsync();

        StreamFabAttributionService.Attribute(unattributed, new Dictionary<Guid, string>())
            .ShouldBe(0);
        unattributed.ShouldAllBe(stream => stream.Fab == null);
    }

    /// <summary>
    /// One attribution pass over whatever currently has no fab, returning how
    /// many it filled.
    ///
    /// <para>
    /// Retried on a concurrency conflict, because <c>StreamHealthWatcher</c>
    /// polls these same rows every two seconds and the stream sources here are
    /// unreachable, so it is actively writing health transitions to them. The
    /// production service does not retry either (ADR-0113) — it logs and the
    /// next host start runs again, which is exactly what this loop stands in
    /// for. Retrying only the conflict keeps every other failure loud.
    /// </para>
    /// </summary>
    private async Task<int> AttributePassAsync(IReadOnlyDictionary<Guid, string> fabsByCamera)
    {
        const int maximumAttempts = 5;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            await using StreamDistributionDbContext context =
                await aspire.CreateStreamDistributionDbContextAsync();

            List<StreamAggregate> unattributed = await context.Streams
                .Where(stream => stream.Fab == null)
                .ToListAsync();

            int attributed = StreamFabAttributionService.Attribute(unattributed, fabsByCamera);

            try
            {
                await context.SaveChangesAsync();
                return attributed;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new InvalidOperationException(
            $"Attribution lost to a concurrent health transition {maximumAttempts} times running.");
    }

    /// <summary>
    /// The production lookup, wired the way the service wires it: a real
    /// client_credentials token from the seeded service account, against the
    /// real camera-catalog endpoint. Only the Keycloak certificate handling
    /// differs — the container presents the ASP.NET dev cert, which CI does
    /// not trust (#1133).
    /// </summary>
    private CameraCatalogFabLookup RealLookup()
    {
        StreamFabAttributionOptions settings = new()
        {
            KeycloakUrl = aspire.App.GetEndpoint("keycloak").ToString(),
            Realm = "smart-sentinel-eye",
            ClientIdentifier = "stream-distribution-attribution",
            ClientSecret = "dev-only-stream-distribution-secret",
        };
        IOptions<StreamFabAttributionOptions> options = Options.Create(settings);

        HttpClientHandler acceptsDevCert = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        CameraCatalogTokenProvider tokens = new(
            new FakeHttpClientFactory(new HttpClient(acceptsDevCert)),
            options,
            TimeProvider.System,
            NullLogger<CameraCatalogTokenProvider>.Instance);

        // The lookup no longer holds the token provider — the bearer header is
        // attached per request by the delegating handler the registration adds,
        // so the test composes the same chain by hand.
        HttpClient catalogue = aspire.CreateServiceClient("camera-catalog");
        HttpClient authorised = new(
            new CameraCatalogAuthorizationHandler(tokens) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = catalogue.BaseAddress,
        };

        return new CameraCatalogFabLookup(authorised, options);
    }

    private async Task<(Guid InMunich, Guid InDresden)> ProvisionOnePerFabAsync()
    {
        using HttpClient cameras = await aspire.CreateAuthenticatedClientAsync(
            "camera-catalog", MultiFabOperator, OperatorPassword);

        Guid inMunich = await RegisterAsync(cameras, "?fabId=munich");
        Guid inDresden = await RegisterAsync(cameras, "?fabId=dresden");

        using HttpClient streams = await aspire.CreateAuthenticatedClientAsync(
            "stream-distribution", MultiFabOperator, OperatorPassword);
        await WaitForStreamAsync(streams, inMunich);
        await WaitForStreamAsync(streams, inDresden);

        return (inMunich, inDresden);
    }

    private static async Task<Guid> RegisterAsync(HttpClient cameras, string fabQuery)
    {
        HttpResponseMessage created = await cameras.PostAsJsonAsync(
            $"/cameras{fabQuery}",
            new
            {
                name = $"Cam-{Guid.NewGuid():N}"[..12],
                rtspUrl = $"rtsp://10.0.5.{Random.Shared.Next(2, 250)}/h264",
            });
        created.StatusCode.ShouldBe(
            HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> ProvisionInMunichAsync()
    {
        using HttpClient cameras = await aspire.CreateAdminClientAsync("camera-catalog");

        HttpResponseMessage created = await cameras.PostAsJsonAsync(
            "/cameras",
            new
            {
                name = $"Cam-{Guid.NewGuid():N}"[..12],
                rtspUrl = $"rtsp://10.0.5.{Random.Shared.Next(2, 250)}/h264",
            });
        created.StatusCode.ShouldBe(
            HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        Guid camera = await created.Content.ReadFromJsonAsync<Guid>();

        using HttpClient streams = await aspire.CreateAdminClientAsync("stream-distribution");
        await WaitForStreamAsync(streams, camera);

        return camera;
    }

    private async Task WaitForStreamAsync(HttpClient streams, Guid camera)
    {
        DateTime deadline = DateTime.UtcNow + ProvisionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await streams.GetAsync($"/streams/{camera}")).StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Stream for camera {camera} did not appear within {ProvisionTimeout.TotalSeconds:F0}s.{Environment.NewLine}" +
            $"stream-distribution log:{Environment.NewLine}{aspire.RecentLogs("stream-distribution")}");
    }

    /// <summary>
    /// Takes the camera off the wall and waits until StreamDistribution has
    /// seen it.
    ///
    /// <para>
    /// The wait is not politeness. <c>CameraRetiredV1</c> rides the outbox and
    /// retires the stream row, so returning before that write lands would let
    /// the caller blank a fab the retirement is about to overwrite.
    /// </para>
    /// </summary>
    private async Task RetireAsync(Guid camera)
    {
        using HttpClient cameras = await aspire.CreateAdminClientAsync("camera-catalog");

        HttpResponseMessage retired = await cameras.PostAsync($"/cameras/{camera}/retire", null);
        retired.StatusCode.ShouldBe(
            HttpStatusCode.NoContent, await retired.Content.ReadAsStringAsync());

        DateTime deadline = DateTime.UtcNow + ProvisionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await using StreamDistributionDbContext context =
                await aspire.CreateStreamDistributionDbContextAsync();

            bool seen = await context.Streams
                .AsNoTracking()
                .AnyAsync(stream =>
                    stream.Camera == CameraIdentifier.From(camera)
                    && stream.State == StreamState.Retired);

            if (seen)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException(
            $"Stream for camera {camera} was not retired within {ProvisionTimeout.TotalSeconds:F0}s.{Environment.NewLine}" +
            $"stream-distribution log:{Environment.NewLine}{aspire.RecentLogs("stream-distribution")}");
    }

    /// <summary>
    /// Recreates a row that predates the fab column. Written through SQL
    /// rather than the aggregate because the aggregate deliberately cannot
    /// express it — <c>Provision</c> requires a fab and there is no setter.
    /// </summary>
    private async Task BlankTheFabAsync(Guid camera)
    {
        await using StreamDistributionDbContext context =
            await aspire.CreateStreamDistributionDbContextAsync();

        await context.Database.ExecuteSqlAsync(
            $"UPDATE streams SET fab = NULL WHERE camera_id = {camera}");
    }
}

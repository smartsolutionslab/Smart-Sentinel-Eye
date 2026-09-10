using System.Diagnostics;
using System.Text.Json;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.LayoutComposition;

/// <summary>
/// Spec 003 T024 — end-to-end through the layout-composition API and
/// the underlying Postgres + Wolverine stack. Drives the US1 happy path:
/// create a Draft via <c>POST /layouts</c>, publish revision 1 via
/// <c>POST /layouts/{id}/revisions/1/publish</c>, and assert the
/// transition is observable on <c>GET /layouts/{id}</c> within the
/// 500 ms budget.
///
/// <para>
/// <b>What the 500 ms is a budget for</b> (issue #2119). It is a
/// <b>local SLO for the layout-composition synchronous command path</b>:
/// two HTTP round trips — create, then publish — against the fixture
/// stack, and nothing else.
/// </para>
///
/// <para>
/// It is <b>not one of constitution §IV's six legs</b>. §IV budgets the
/// asynchronous <i>event arrival → overlay rendered</i> path and divides
/// 800 ms between RTP ingest, decode, presentation buffer, event →
/// overlay state, composite and headroom. A synchronous HTTP command is
/// on none of them, so §IV neither grants this figure nor governs it,
/// and §VII's dashboard obligation — which binds implemented §IV legs —
/// is not engaged by a change to it. The distinction is written here
/// because a figure whose meaning lives nowhere near it is how a budget
/// stops meaning anything.
/// </para>
///
/// <para>
/// Consequently the camera is registered <b>before the clock starts</b>.
/// It is a precondition of building a layout (spec 017 FR-014: a tile's
/// camera must exist), not part of the path being measured, and it is a
/// round trip into the <b>camera-catalog</b> context — so timing it
/// would put another context's work inside a budget named for this one.
/// <c>SignalRRevocationIntegrationTests</c> hoists its version lookup
/// out of its window for the same reason.
/// <c>StopwatchWindowScopeTests</c> fails the build if the call moves
/// back inside.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class LayoutLifecycleIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await aspire.ResetLayoutCompositionAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_and_publish_a_layout_yields_a_Published_revision_within_500_ms()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        Guid camera = await LayoutRequests.RegisterCameraAsync(aspire);

        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage created = await layouts.PostAsJsonAsync(
            "/layouts",
            SingleTileBody($"Line-{Guid.NewGuid():N}".Substring(0, 16), camera));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid layoutIdentifier = await created.Content.ReadFromJsonAsync<Guid>();
        layoutIdentifier.ShouldNotBe(Guid.Empty);

        // Deliberately not LayoutRequests.PostAsync: that reads the version
        // first, and the extra round trip would come out of this test's 500 ms
        // budget. A just-created chain is at version 0.
        HttpRequestMessage publishRequest = new(HttpMethod.Post, $"/layouts/{layoutIdentifier}/revisions/1/publish");
        publishRequest.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        HttpResponseMessage published = await layouts.SendAsync(publishRequest);
        sw.Stop();

        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        sw.Elapsed.TotalMilliseconds.ShouldBeLessThan(500,
            $"create + publish took {sw.Elapsed.TotalMilliseconds:F0} ms");

        HttpResponseMessage fetched = await layouts.GetAsync($"/layouts/{layoutIdentifier}");
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement payload = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement revisions = payload.GetProperty("revisions");
        revisions.GetArrayLength().ShouldBe(1);
        revisions[0].GetProperty("state").GetString().ShouldBe("Published");
        revisions[0].GetProperty("revisionNumber").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_name_collision_returns_409_Conflict_with_LAYOUT_NAME_TAKEN()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string sharedName = $"Cam-{Guid.NewGuid():N}".Substring(0, 16);

        HttpResponseMessage first = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody(sharedName, await LayoutRequests.RegisterCameraAsync(aspire)));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage second = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody(sharedName, await LayoutRequests.RegisterCameraAsync(aspire)));
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("LAYOUT_NAME_TAKEN");
    }

    [Fact]
    public async Task List_with_state_Published_returns_only_chains_with_a_published_revision()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string draftName = $"Drf-{Guid.NewGuid():N}".Substring(0, 16);
        string pubName = $"Pub-{Guid.NewGuid():N}".Substring(0, 16);

        HttpResponseMessage draftRaw = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody(draftName, await LayoutRequests.RegisterCameraAsync(aspire)));
        draftRaw.EnsureSuccessStatusCode();

        HttpResponseMessage pubRaw = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody(pubName, await LayoutRequests.RegisterCameraAsync(aspire)));
        pubRaw.EnsureSuccessStatusCode();
        Guid pubIdentifier = await pubRaw.Content.ReadFromJsonAsync<Guid>();
        HttpResponseMessage publish = await LayoutRequests.PostAsync(layouts, pubIdentifier, "revisions/1/publish");
        publish.EnsureSuccessStatusCode();

        HttpResponseMessage response = await layouts.GetAsync("/layouts?state=Published");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement published = payload.GetProperty("published");
        IEnumerable<string> names = published.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!);
        names.ShouldContain(pubName);
        names.ShouldNotContain(draftName);
    }

    [Fact]
    public async Task Get_for_an_unknown_layout_returns_404()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        HttpResponseMessage response = await layouts.GetAsync($"/layouts/{Guid.CreateVersion7()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_GET_returns_401()
    {
        HttpResponseMessage response = await aspire.LayoutComposition.GetAsync(
            $"/layouts/{Guid.CreateVersion7()}");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_4_tile_2x2_wall_round_trips_through_persistence()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        Guid[] cameras =
        [
            await LayoutRequests.RegisterCameraAsync(aspire),
            await LayoutRequests.RegisterCameraAsync(aspire),
            await LayoutRequests.RegisterCameraAsync(aspire),
            await LayoutRequests.RegisterCameraAsync(aspire),
        ];
        Guid overlay = Guid.CreateVersion7();
        object body = new
        {
            name = $"Wall-{Guid.NewGuid():N}".Substring(0, 16),
            grid = new { rows = 2, cols = 2 },
            tiles = new[]
            {
                new { cameraIdentifier = cameras[0], overlayIdentifier = (Guid?)overlay, row = 0, col = 0 },
                new { cameraIdentifier = cameras[1], overlayIdentifier = (Guid?)null, row = 0, col = 1 },
                new { cameraIdentifier = cameras[2], overlayIdentifier = (Guid?)null, row = 1, col = 0 },
                new { cameraIdentifier = cameras[3], overlayIdentifier = (Guid?)null, row = 1, col = 1 },
            },
        };

        HttpResponseMessage created = await layouts.PostAsJsonAsync("/layouts", body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid layoutIdentifier = await created.Content.ReadFromJsonAsync<Guid>();

        HttpResponseMessage fetched = await layouts.GetAsync($"/layouts/{layoutIdentifier}");
        fetched.EnsureSuccessStatusCode();
        JsonElement payload = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement revision = payload.GetProperty("revisions")[0];
        revision.GetProperty("gridRows").GetInt32().ShouldBe(2);
        revision.GetProperty("gridCols").GetInt32().ShouldBe(2);
        JsonElement tiles = revision.GetProperty("tiles");
        tiles.GetArrayLength().ShouldBe(4);

        JsonElement origin = tiles.EnumerateArray()
            .Single(tile => tile.GetProperty("row").GetInt32() == 0 && tile.GetProperty("col").GetInt32() == 0);
        origin.GetProperty("cameraIdentifier").GetGuid().ShouldBe(cameras[0]);
        origin.GetProperty("overlayIdentifier").GetGuid().ShouldBe(overlay);
    }

    [Fact]
    public async Task A_single_camera_layout_persists_as_a_1x1_tile_at_origin()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        Guid camera = await LayoutRequests.RegisterCameraAsync(aspire);

        HttpResponseMessage created = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody($"Cell-{Guid.NewGuid():N}".Substring(0, 16), camera));
        created.EnsureSuccessStatusCode();
        Guid layoutIdentifier = await created.Content.ReadFromJsonAsync<Guid>();

        HttpResponseMessage fetched = await layouts.GetAsync($"/layouts/{layoutIdentifier}");
        JsonElement payload = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement revision = payload.GetProperty("revisions")[0];
        revision.GetProperty("gridRows").GetInt32().ShouldBe(1);
        revision.GetProperty("gridCols").GetInt32().ShouldBe(1);
        JsonElement tile = revision.GetProperty("tiles").EnumerateArray().Single();
        tile.GetProperty("row").GetInt32().ShouldBe(0);
        tile.GetProperty("col").GetInt32().ShouldBe(0);
        tile.GetProperty("cameraIdentifier").GetGuid().ShouldBe(camera);
    }

    /// <summary>
    /// Spec 037 T024 (ADR-0121) — recovery over real SQL, end to end.
    ///
    /// <para>
    /// Required rather than optional. The recovered draft is built by cloning
    /// the archived revision's <b>EF-owned entities</b> — the grid and each tile
    /// — under a new owner, in the same change-tracker that just loaded them.
    /// <c>Revision.NewDraft</c>'s own comment explains that this cloning exists
    /// because sharing the instances makes EF see one owned entity under two
    /// owners and throw on save, and it was written for the published-source
    /// case. A hand-written fake repository models that away by construction and
    /// so cannot answer whether it holds here.
    /// </para>
    ///
    /// <para>
    /// Asserts <b>branch, edit and publish</b>, not just the branch. A draft
    /// nobody can publish leaves the layout exactly as unusable as before while
    /// satisfying every assertion about a draft appearing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_archived_layout_can_be_branched_edited_and_published_again()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        Guid camera = await LayoutRequests.RegisterCameraAsync(aspire);
        Guid replacement = await LayoutRequests.RegisterCameraAsync(aspire);

        HttpResponseMessage created = await layouts.PostAsJsonAsync(
            "/layouts", SingleTileBody($"Recov-{Guid.NewGuid():N}".Substring(0, 16), camera));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid layoutIdentifier = await created.Content.ReadFromJsonAsync<Guid>();

        (await LayoutRequests.PostAsync(layouts, layoutIdentifier, "revisions/1/publish"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LayoutRequests.PostAsync(layouts, layoutIdentifier, "revisions/1/archive"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Stranded before spec 037: no Published revision to branch from and no
        // Draft to publish, so the chain matched none of the six behaviours.
        HttpResponseMessage branched = await LayoutRequests.PostAsync(layouts, layoutIdentifier, "draft");
        branched.StatusCode.ShouldBe(HttpStatusCode.Created);
        int recovered = await branched.Content.ReadFromJsonAsync<int>();
        recovered.ShouldBe(2);

        // FR-002: the payload came back with it, which is the whole point.
        HttpResponseMessage afterBranch = await layouts.GetAsync($"/layouts/{layoutIdentifier}");
        JsonElement branchedRevision = (await afterBranch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("revisions")
            .EnumerateArray()
            .Single(revision => revision.GetProperty("revisionNumber").GetInt32() == recovered);
        branchedRevision.GetProperty("state").GetString().ShouldBe("Draft");
        branchedRevision.GetProperty("tiles").EnumerateArray().Single()
            .GetProperty("cameraIdentifier").GetGuid().ShouldBe(camera);

        (await LayoutRequests.PatchAsync(
            layouts,
            layoutIdentifier,
            $"revisions/{recovered}",
            new
            {
                grid = new { rows = 1, cols = 1 },
                tiles = new[] { new { cameraIdentifier = replacement, overlayIdentifier = (Guid?)null, row = 0, col = 0 } },
            })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await LayoutRequests.PostAsync(layouts, layoutIdentifier, $"revisions/{recovered}/publish"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // FR-003: same chain, same identifier, the archived revision still there.
        HttpResponseMessage finished = await layouts.GetAsync($"/layouts/{layoutIdentifier}");
        JsonElement payload = await finished.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("layoutIdentifier").GetGuid().ShouldBe(layoutIdentifier);
        JsonElement revisions = payload.GetProperty("revisions");
        revisions.GetArrayLength().ShouldBe(2);
        revisions.EnumerateArray()
            .Single(revision => revision.GetProperty("revisionNumber").GetInt32() == 1)
            .GetProperty("state").GetString().ShouldBe("Archived");
        JsonElement live = revisions.EnumerateArray()
            .Single(revision => revision.GetProperty("revisionNumber").GetInt32() == recovered);
        live.GetProperty("state").GetString().ShouldBe("Published");
        live.GetProperty("tiles").EnumerateArray().Single()
            .GetProperty("cameraIdentifier").GetGuid().ShouldBe(replacement);
    }

    private static object SingleTileBody(string name, Guid camera) => new
    {
        name,
        grid = new { rows = 1, cols = 1 },
        tiles = new[] { new { cameraIdentifier = camera, overlayIdentifier = (Guid?)null, row = 0, col = 0 } },
    };
}

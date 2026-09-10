using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.LayoutComposition.Infrastructure.Broadcasting;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.OverlayDesigner;

/// <summary>
/// Spec 004 T084 — drives the US3 republish-push path through the
/// shared SignalR hub. Two clients connect; the admin publishes
/// revision 2 of an overlay; both clients receive
/// <c>OverlayRevisionPublished</c> carrying the new Label within 1 s.
/// </summary>
[Collection(AspireCollection.Name)]
public class OverlayPushIntegrationTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// <b>Threshold 1 000 ms. Observed 110 ms and 57 ms</b> for
    /// publish→push reaching two clients over the warmed path (dev box,
    /// Release, 2026-09-10, issue #2149) — about <b>9–18×</b>
    /// inside.
    ///
    /// <para>
    /// <b>Read that margin together with the warmup below.</b> It is a
    /// steady-state figure by construction: the first frame to a fresh client
    /// on a fresh stack costs ~2 s, which would breach this budget twice over.
    /// So the ~9–18× headroom describes the warm path only, and the
    /// cold path is excluded by design rather than by luck.
    /// </para>
    ///
    /// <para>
    /// <b>Spec 004 promised the figure</b> (<c>plan.md:73</c>: the PR will
    /// report measured publish-overlay → kiosk-render) and PR #417 records
    /// none — its box for the integration tests passing is left unchecked.
    /// These are the first two observations in the tree.
    /// </para>
    ///
    /// <para>
    /// <b>A local SLO, not one of §IV's six legs.</b> Spec 004 is the render
    /// substrate; <i>"the budget itself starts ticking with spec 005's
    /// variable binding"</i> (plan.md:73).
    /// </para>
    /// </summary>
    private const int PushBudgetMilliseconds = 1000;

    public async Task InitializeAsync()
    {
        await aspire.ResetOverlayDesignerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object SampleLabelBody() => new
    {
        text = "Production Line 1",
        normalizedX = 0.5m,
        normalizedY = 0.05m,
        normalizedWidth = 0.3m,
        normalizedHeight = 0.08m,
        fontSizePx = 48,
    };

    [Fact]
    public async Task Overlay_republish_pushes_to_connected_clients_within_one_second()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string accessToken = await aspire.GetAccessTokenAsync(
            AspireFixture.AdminUsername, AspireFixture.AdminPassword);

        // Seed an overlay with a Published revision so revision 2 can branch.
        HttpResponseMessage created = await overlays.PostAsJsonAsync(
            "/overlays",
            new
            {
                name = $"Psh-{Guid.NewGuid():N}".Substring(0, 16),
                label = SampleLabelBody(),
            });
        created.EnsureSuccessStatusCode();
        Guid overlayIdentifier = await created.Content.ReadFromJsonAsync<Guid>();
        HttpResponseMessage publishOne = await OverlayRequests.PostAsync(overlays, overlayIdentifier, $"revisions/1/publish");
        publishOne.EnsureSuccessStatusCode();

        // Connect two clients to the layout-composition SignalR hub
        // (spec 004 plan: overlay events fan out over the same hub).
        Uri hubUri = aspire.HubUri("layout-composition", LayoutLifecycleHub.Path);
        await using HubConnection alpha = BuildClient(hubUri, accessToken);
        await using HubConnection beta = BuildClient(hubUri, accessToken);

        // Capture frames per overlay id, so the warmup publish below and the
        // measured publish never race on a shared completion source.
        ConcurrentDictionary<Guid, TaskCompletionSource<OverlayRevisionPublishedHubMessage>> alphaFrames = new();
        ConcurrentDictionary<Guid, TaskCompletionSource<OverlayRevisionPublishedHubMessage>> betaFrames = new();
        alpha.On<JsonElement>(nameof(ILayoutLifecycleClient.OverlayRevisionPublished),
            payload => { OverlayRevisionPublishedHubMessage f = Parse(payload); alphaFrames.GetOrAdd(f.Overlay, _ => new()).TrySetResult(f); });
        beta.On<JsonElement>(nameof(ILayoutLifecycleClient.OverlayRevisionPublished),
            payload => { OverlayRevisionPublishedHubMessage f = Parse(payload); betaFrames.GetOrAdd(f.Overlay, _ => new()).TrySetResult(f); });

        await alpha.StartAsync();
        await beta.StartAsync();

        // Warm the end-to-end push path before measuring. The first frame
        // delivered to a freshly-connected client on a freshly-booted stack
        // pays a one-time cold-start cost (RabbitMQ listener provisioning +
        // SignalR negotiation) of ~2 s that does not reflect steady state. The
        // ≤1 s budget is a steady-state SLO, so warm the path with a throwaway
        // publish, wait for both clients to receive it, then measure the next.
        Guid warmupIdentifier = await CreateOverlayAsync(overlays);
        await ReferenceFromAPublishedLayoutAsync(warmupIdentifier);
        (await OverlayRequests.PostAsync(overlays, warmupIdentifier, $"revisions/1/publish"))
            .EnsureSuccessStatusCode();
        using (CancellationTokenSource warmupBudget = new(TimeSpan.FromSeconds(20)))
        {
            await Task.WhenAll(
                alphaFrames.GetOrAdd(warmupIdentifier, _ => new()).Task.WaitAsync(warmupBudget.Token),
                betaFrames.GetOrAdd(warmupIdentifier, _ => new()).Task.WaitAsync(warmupBudget.Token));
        }

        // Measured publish over the now-warm path (a fresh sibling overlay
        // exercises the same Published broadcast).
        Guid siblingIdentifier = await CreateOverlayAsync(overlays);
        await ReferenceFromAPublishedLayoutAsync(siblingIdentifier);

        // Version read before the clock starts: this window measures
        // publish→push, not the lookup.
        HttpRequestMessage publishRequest = OverlayRequests.Conditional(
            HttpMethod.Post, siblingIdentifier, "revisions/1/publish",
            await OverlayRequests.VersionAsync(overlays, siblingIdentifier));

        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage publishSibling = await overlays.SendAsync(publishRequest);
        publishSibling.EnsureSuccessStatusCode();

        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
        OverlayRevisionPublishedHubMessage[] both = await Task.WhenAll(
            alphaFrames.GetOrAdd(siblingIdentifier, _ => new()).Task.WaitAsync(budget.Token),
            betaFrames.GetOrAdd(siblingIdentifier, _ => new()).Task.WaitAsync(budget.Token));
        sw.Stop();

        // Recorded on the success path too, for the reason in #2149: the
        // customMessage below is built only on failure, so the warm-path figure
        // this budget was chosen from was invisible to every green run.
        output.WriteLine(
            $"publish->push to 2 clients (warm): {sw.Elapsed.TotalMilliseconds:F0} ms "
            + $"(budget {PushBudgetMilliseconds} ms)");

        sw.Elapsed.TotalMilliseconds.ShouldBeLessThan(
            PushBudgetMilliseconds,
            $"publish→push took {sw.Elapsed.TotalMilliseconds:F0} ms");

        both[0].Overlay.ShouldBe(siblingIdentifier);
        both[0].Text.ShouldBe("Production Line 1");
        // The frame is parsed off all four geometry fields above and, until
        // now, only Text and FontSizePx were read back. This is the only
        // end-to-end net over the EF column mapping, so a label_x/label_y
        // transposition in the persistence configuration lands here.
        both[0].NormalizedX.ShouldBe(0.5m);
        both[0].NormalizedY.ShouldBe(0.05m);
        both[0].NormalizedWidth.ShouldBe(0.3m);
        both[0].NormalizedHeight.ShouldBe(0.08m);
        both[0].FontSizePx.ShouldBe(48);
        both[1].Overlay.ShouldBe(siblingIdentifier);
    }

    private static async Task<Guid> CreateOverlayAsync(HttpClient overlays)
    {
        HttpResponseMessage created = await overlays.PostAsJsonAsync(
            "/overlays",
            new
            {
                name = $"Psh-{Guid.NewGuid():N}".Substring(0, 16),
                label = SampleLabelBody(),
            });
        created.EnsureSuccessStatusCode();
        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>
    /// Publishes a layout referencing the overlay, so this fab is among those
    /// told about it.
    ///
    /// <para>
    /// Required since spec 017 FR-010/FR-011: an overlay lifecycle frame goes
    /// only to the fabs that have a published layout carrying that overlay,
    /// and an overlay nobody references reaches nobody at all. Before that,
    /// every overlay publish went to <c>Clients.All</c> and this test's
    /// listeners heard it without any layout existing. That is precisely the
    /// leak spec 017 closes, so the test now has to earn its frame.
    /// </para>
    /// </summary>
    private async Task ReferenceFromAPublishedLayoutAsync(Guid overlay)
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");

        HttpResponseMessage created = await layouts.PostAsJsonAsync("/layouts", new
        {
            name = $"Ref-{Guid.NewGuid():N}"[..16],
            grid = new { rows = 1, cols = 1 },
            tiles = new[]
            {
                new
                {
                    cameraIdentifier = await LayoutRequests.RegisterCameraAsync(aspire),
                    overlayIdentifier = (Guid?)overlay,
                    row = 0,
                    col = 0,
                },
            },
        });
        created.EnsureSuccessStatusCode();

        Guid layout = await created.Content.ReadFromJsonAsync<Guid>();
        (await LayoutRequests.PostAsync(layouts, layout, "revisions/1/publish"))
            .EnsureSuccessStatusCode();
    }

    private static HubConnection BuildClient(Uri hubUri, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private static OverlayRevisionPublishedHubMessage Parse(JsonElement payload) =>
        new(
            Overlay: payload.GetProperty("overlay").GetGuid(),
            RevisionNumber: payload.GetProperty("revisionNumber").GetInt32(),
            Name: payload.GetProperty("name").GetString()!,
            Text: payload.GetProperty("text").GetString()!,
            NormalizedX: payload.GetProperty("normalizedX").GetDecimal(),
            NormalizedY: payload.GetProperty("normalizedY").GetDecimal(),
            NormalizedWidth: payload.GetProperty("normalizedWidth").GetDecimal(),
            NormalizedHeight: payload.GetProperty("normalizedHeight").GetDecimal(),
            FontSizePx: payload.GetProperty("fontSizePx").GetInt32(),
            PublishedAt: payload.GetProperty("publishedAt").GetDateTimeOffset());
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.LayoutComposition.Infrastructure.Broadcasting;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.LayoutComposition;

/// <summary>
/// Spec 003 T078 — drives the US3 force-disconnect path through the
/// real SignalR hub. Two clients connect, the admin archives a
/// published revision, both clients receive the
/// <c>LayoutRevisionArchived</c> push within 1 s.
/// </summary>
[Collection(AspireCollection.Name)]
public class SignalRRevocationIntegrationTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// <b>Threshold 1 000 ms. Observed 283 ms and 95 ms</b> for
    /// archive→push reaching two connected clients (dev box, Release,
    /// 2026-09-10, issue #2149) — a margin of about <b>3.5–10.5×</b>.
    ///
    /// <para>
    /// <b>Spec 003 promised this measurement and no record of it exists.</b>
    /// <c>specs/003-layout-composition/plan.md:75</c> says the PR will report
    /// the measured archive-to-force-disconnect; PR #303 restates
    /// <i>"within 1 s"</i> and gives no figure. The two above are the first
    /// written down.
    /// </para>
    ///
    /// <para>
    /// <b>A local SLO, not one of constitution §IV's six legs</b> — and on
    /// that, spec 003 is its own authority: <i>"an operator-action latency
    /// (≤ 1 s) — not the 800 ms event-to-overlay budget"</i> (plan.md:75). It
    /// shares the RabbitMQ + SignalR transport with §IV's <i>Event → overlay
    /// state ≤ 200 ms</i> leg while budgeting five times looser, which is the
    /// clearest sign the two are not the same thing.
    /// </para>
    /// </summary>
    private const int RevocationBudgetMilliseconds = 1000;

    public async Task InitializeAsync()
    {
        await aspire.ResetLayoutCompositionAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Archive_force_disconnects_connected_kiosks_within_one_second()
    {
        using HttpClient admin = await aspire.CreateAdminClientAsync("layout-composition");
        string accessToken = await aspire.GetAccessTokenAsync(
            AspireFixture.AdminUsername, AspireFixture.AdminPassword);

        // Create + publish a layout so there's something to archive.
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            "/layouts",
            new
            {
                name = $"Live-{Guid.NewGuid():N}".Substring(0, 16),
                grid = new { rows = 1, cols = 1 },
                tiles = new[] { new { cameraIdentifier = await LayoutRequests.RegisterCameraAsync(aspire), overlayIdentifier = (Guid?)null, row = 0, col = 0 } },
            });
        created.EnsureSuccessStatusCode();
        Guid layoutIdentifier = await created.Content.ReadFromJsonAsync<Guid>();
        HttpResponseMessage publish = await LayoutRequests.PostAsync(admin, layoutIdentifier, "revisions/1/publish");
        publish.EnsureSuccessStatusCode();

        Uri hubUri = aspire.HubUri("layout-composition", LayoutLifecycleHub.Path);

        await using HubConnection alpha = BuildClient(hubUri, accessToken);
        await using HubConnection beta = BuildClient(hubUri, accessToken);

        TaskCompletionSource<LayoutRevisionArchivedHubMessage> alphaSeen = new();
        TaskCompletionSource<LayoutRevisionArchivedHubMessage> betaSeen = new();
        alpha.On<JsonElement>(nameof(ILayoutLifecycleClient.LayoutRevisionArchived),
            payload => alphaSeen.TrySetResult(Parse(payload)));
        beta.On<JsonElement>(nameof(ILayoutLifecycleClient.LayoutRevisionArchived),
            payload => betaSeen.TrySetResult(Parse(payload)));

        await alpha.StartAsync();
        await beta.StartAsync();

        // Read the precondition before the clock starts: this window measures
        // archive→push, not the version lookup.
        int archiveVersion = await LayoutRequests.VersionAsync(admin, layoutIdentifier);
        HttpRequestMessage archiveRequest = new(HttpMethod.Post, $"/layouts/{layoutIdentifier}/revisions/1/archive");
        archiveRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{archiveVersion}\"");

        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage archived = await admin.SendAsync(archiveRequest);
        archived.EnsureSuccessStatusCode();

        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
        LayoutRevisionArchivedHubMessage[] both =
            await Task.WhenAll(alphaSeen.Task.WaitAsync(budget.Token), betaSeen.Task.WaitAsync(budget.Token));
        sw.Stop();

        // Recorded on the success path too: the customMessage below exists only
        // when the assertion fails, so the figure this budget was chosen from
        // was never observable from a green run (#2149).
        output.WriteLine(
            $"archive->push to 2 clients: {sw.Elapsed.TotalMilliseconds:F0} ms "
            + $"(budget {RevocationBudgetMilliseconds} ms)");

        sw.Elapsed.TotalMilliseconds.ShouldBeLessThan(
            RevocationBudgetMilliseconds,
            $"archive→push took {sw.Elapsed.TotalMilliseconds:F0} ms");

        both[0].Layout.ShouldBe(layoutIdentifier);
        both[0].RevisionNumber.ShouldBe(1);
        both[1].Layout.ShouldBe(layoutIdentifier);
        both[1].RevisionNumber.ShouldBe(1);
    }

    private static HubConnection BuildClient(Uri hubUri, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private static LayoutRevisionArchivedHubMessage Parse(JsonElement payload) =>
        new(
            Layout: payload.GetProperty("layout").GetGuid(),
            RevisionNumber: payload.GetProperty("revisionNumber").GetInt32(),
            ArchivedAt: payload.GetProperty("archivedAt").GetDateTimeOffset());
}

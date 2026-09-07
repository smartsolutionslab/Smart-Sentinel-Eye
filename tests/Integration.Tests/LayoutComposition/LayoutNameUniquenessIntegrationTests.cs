using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Infrastructure.Persistence;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.LayoutComposition;

/// <summary>
/// Spec 086 US2 — the LayoutComposition twin of
/// <c>OverlayNameUniquenessIntegrationTests</c>, scoped to a fab.
/// <c>ix_layouts_fab_name</c> is a plain btree, so
/// <c>CreateLayoutDraftCommandHandler</c> read-then-insert is the only thing
/// standing between two concurrent callers and two same-named rows in one fab.
///
/// <para>
/// <b>One</b> of these was red on the current schema, and it is the phase-4a
/// evidence (ADR-0139) for the index:
/// <see cref="A_second_live_chain_with_the_same_name_in_one_fab_is_refused_by_the_database"/>,
/// which states the guarantee against the schema and fails for exactly one
/// reason.
/// </para>
///
/// <para>
/// The concurrency test was <b>green before the change and green after</b>.
/// Twelve unawaited writers were dispatched twice on a clean box and the
/// handler's own check won every time, so it never observed the race it exists
/// to provoke; it adds <b>no</b> phase-4a evidence. What it is instead is an
/// invariant — once the index exists, "exactly one 201" holds however the race
/// resolves — which is what
/// <c>ServiceDefaults.UniquenessRaceIntegrationTests</c> has always been for
/// CameraCatalog, and it fails if the index regresses. Recorded here rather
/// than left to the PR body: a claim that a test was seen red belongs next to
/// the test, where the next reader can check it.
/// </para>
///
/// <para>
/// The remaining <b>three</b> are <b>characterisation</b> — they pass today,
/// they must pass unmodified after the index lands, and each one is a way the
/// fix could overshoot. An assertion in them that has to be edited is evidence
/// the behaviour moved, not a test to adjust. Like the concurrency test, they
/// fail only if the index regresses.
/// </para>
///
/// <para>
/// The cross-fab half of FR-019 is <b>not</b> repeated here: it is already
/// asserted by <c>LayoutFabScopingIntegrationTests.The_same_name_is_usable_in_two_fabs</c>,
/// which is on the must-not-move list for this change — an index keyed on
/// <c>name</c> alone rather than <c>(fab, name)</c> would fail exactly there.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class LayoutNameUniquenessIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    private const string Fab = "munich";

    /// <summary>
    /// Enough writers that the race is likely without being so many that the
    /// test becomes a load test — the figure
    /// <c>ServiceDefaults.UniquenessRaceIntegrationTests</c> already settled on.
    /// </summary>
    private const int Writers = 12;

    public Task InitializeAsync() => aspire.ResetLayoutCompositionAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- red: the database does not refuse a duplicate ----------------------

    /// <summary>
    /// The defect with the timing taken out — a second live chain in the same
    /// fab, inserted straight through a DbContext. Today it lands; once
    /// <c>ux_layouts_fab_name_active</c> exists Postgres refuses it with
    /// SQLSTATE 23505, the code
    /// <c>UniqueConstraintExceptionHandler</c> turns into the caller 409.
    /// </summary>
    [Fact]
    public async Task A_second_live_chain_with_the_same_name_in_one_fab_is_refused_by_the_database()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string name = UniqueName();
        await CreateAsync(layouts, name);

        await using LayoutCompositionDbContext context = await aspire.CreateLayoutCompositionDbContextAsync();
        context.Layouts.Add(Layout.CreateDraft(
            FabIdentifier.From(Fab),
            LayoutName.From(name),
            GridDimensions.Cell,
            [new Tile(
                CameraIdentifier.From(Guid.CreateVersion7()),
                Option<OverlayIdentifier>.None,
                GridPosition.From(0, 0))],
            OperatorIdentifier.From(Guid.CreateVersion7()),
            new SystemClock()));

        DbUpdateException refused = await Should.ThrowAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        refused.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// Distinct callers naming the same wall in the same fab at the same moment.
    /// Shaped after <c>ServiceDefaults.UniquenessRaceIntegrationTests</c>: every
    /// attempt is dispatched before any is awaited, and there are enough of them
    /// that some pair interleaves.
    ///
    /// <para>
    /// As on the overlay twin, this asserts an <b>occurrence</b> until the index
    /// lands and only becomes occurrence-independent afterwards, which is why
    /// <see cref="A_second_live_chain_with_the_same_name_in_one_fab_is_refused_by_the_database"/>
    /// carries the guarantee deterministically.
    /// </para>
    ///
    /// <para>
    /// <b>No request carries an <c>Idempotency-Key</c></b>, for the reason
    /// spelled out on the overlay twin: a shared key is deduplicated by
    /// <c>IdempotencyStore</c> before the race reaches the database, so a keyed
    /// burst would report the system safe when it is not.
    /// </para>
    ///
    /// <para>
    /// Every tile names the <b>same</b> camera. FR-014 requires a real camera in
    /// the layout fab, and sharing one keeps the requests differing in nothing
    /// but the fact that there are several of them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_for_one_layout_name_in_one_fab_yield_one_success_and_never_a_fault()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string contested = UniqueName();
        Guid camera = await LayoutRequests.RegisterCameraAsync(aspire, Fab);

        IEnumerable<Task<HttpResponseMessage>> attempts = Enumerable
            .Range(0, Writers)
            .Select(_ => layouts.PostAsJsonAsync("/layouts", Body(contested, camera)));

        HttpResponseMessage[] answers = await Task.WhenAll(attempts);

        try
        {
            HttpStatusCode[] statuses = [.. answers.Select(answer => answer.StatusCode)];
            int rows = await CountRowsNamedAsync(contested);

            statuses.ShouldNotContain(
                HttpStatusCode.InternalServerError,
                $"a writer losing the uniqueness race was told the server failed. Statuses: {string.Join(", ", statuses)}");

            statuses.Count(status => status == HttpStatusCode.Created).ShouldBe(
                1,
                $"exactly one writer may take '{contested}' in fab '{Fab}'. "
                + $"Statuses: {string.Join(", ", statuses)}. "
                + $"The layouts table holds {rows} row(s) with that fab and name.");
            rows.ShouldBe(1);

            statuses.Where(status => status != HttpStatusCode.Created)
                .ShouldAllBe(status => status == HttpStatusCode.Conflict);

            foreach (HttpResponseMessage loser in answers.Where(
                answer => answer.StatusCode == HttpStatusCode.Conflict))
            {
                JsonElement problem = await loser.Content.ReadFromJsonAsync<JsonElement>();
                problem.GetProperty("title").GetString().ShouldBeOneOf(
                    "RESOURCE_ALREADY_EXISTS", "LAYOUT_NAME_TAKEN");
            }
        }
        finally
        {
            foreach (HttpResponseMessage answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    // ---- characterisation: what the index must not take away ----------------

    /// <summary>
    /// FR-006 reuse clause within one fab. A total unique index on
    /// <c>(fab, name)</c> would pass both red tests above and fail this one.
    /// </summary>
    [Fact]
    public async Task An_archived_chain_releases_its_name_within_its_fab()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string name = UniqueName();
        Guid archived = await CreateAsync(layouts, name);

        HttpResponseMessage archive = await LayoutRequests.PostAsync(
            layouts, archived, "revisions/1/archive");
        archive.StatusCode.ShouldBe(HttpStatusCode.OK, await DiagnoseAsync(archive));

        HttpResponseMessage reused = await layouts.PostAsJsonAsync(
            "/layouts", Body(name, await LayoutRequests.RegisterCameraAsync(aspire, Fab)));

        reused.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(reused));
        (await CountRowsNamedAsync(name)).ShouldBe(2);
    }

    /// <summary>
    /// Spec 037 FR-009 (ADR-0121), unchanged and still the handler's own
    /// specific refusal rather than the generic one the index produces.
    /// </summary>
    [Fact]
    public async Task Branching_an_archived_chain_whose_name_was_taken_is_refused_with_LAYOUT_NAME_TAKEN()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string name = UniqueName();
        Guid stranded = await CreateAsync(layouts, name);
        (await LayoutRequests.PostAsync(layouts, stranded, "revisions/1/archive"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await layouts.PostAsJsonAsync(
            "/layouts", Body(name, await LayoutRequests.RegisterCameraAsync(aspire, Fab))))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage refused = await LayoutRequests.PostAsync(layouts, stranded, "draft");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await DiagnoseAsync(refused));
        JsonElement problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("LAYOUT_NAME_TAKEN");
    }

    /// <summary>
    /// <c>LayoutName</c> carries no normalisation, so the handler comparison
    /// and a btree on the raw column already agree. Making these names
    /// case-insensitive is a separate, user-visible change (spec 086 §1.3) and
    /// this asserts it did not arrive as a side effect of the index.
    /// </summary>
    [Fact]
    public async Task Two_layout_names_differing_only_in_case_are_distinct_chains()
    {
        using HttpClient layouts = await aspire.CreateAdminClientAsync("layout-composition");
        string lower = $"lay-case-{Guid.NewGuid():N}"[..16];
        string upper = lower.ToUpperInvariant();

        HttpResponseMessage first = await layouts.PostAsJsonAsync(
            "/layouts", Body(lower, await LayoutRequests.RegisterCameraAsync(aspire, Fab)));
        HttpResponseMessage second = await layouts.PostAsJsonAsync(
            "/layouts", Body(upper, await LayoutRequests.RegisterCameraAsync(aspire, Fab)));

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(first));
        second.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(second));
        (await CountRowsNamedAsync(lower)).ShouldBe(1);
        (await CountRowsNamedAsync(upper)).ShouldBe(1);
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<Guid> CreateAsync(HttpClient layouts, string name)
    {
        HttpResponseMessage created = await layouts.PostAsJsonAsync(
            "/layouts", Body(name, await LayoutRequests.RegisterCameraAsync(aspire, Fab)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<int> CountRowsNamedAsync(string name)
    {
        await using LayoutCompositionDbContext context = await aspire.CreateLayoutCompositionDbContextAsync();
        FabIdentifier fab = FabIdentifier.From(Fab);
        LayoutName parsed = LayoutName.From(name);

        return await context.Layouts.CountAsync(
            candidate => candidate.Fab == fab && candidate.Name == parsed);
    }

    private static object Body(string name, Guid camera) => new
    {
        name,
        grid = new { rows = 1, cols = 1 },
        tiles = new[]
        {
            new { cameraIdentifier = camera, overlayIdentifier = (Guid?)null, row = 0, col = 0 },
        },
    };

    private static string UniqueName() => $"Lay-{Guid.NewGuid():N}"[..16];

    private async Task<string> DiagnoseAsync(HttpResponseMessage response) =>
        $"body: {await response.Content.ReadAsStringAsync()}{Environment.NewLine}"
        + $"layout-composition log:{Environment.NewLine}{aspire.RecentLogs("layout-composition")}";
}

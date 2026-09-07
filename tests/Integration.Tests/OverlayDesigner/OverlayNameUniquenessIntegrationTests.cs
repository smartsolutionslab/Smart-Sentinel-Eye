using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.OverlayDesigner.Domain.Overlay;
using SmartSentinelEye.OverlayDesigner.Infrastructure.Persistence;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.OverlayDesigner;

/// <summary>
/// Spec 086 US1 — <c>POST /overlays</c> guards FR-006 (a name is unique across
/// non-archived chains) in <c>CreateOverlayDraftCommandHandler</c> only: read
/// through <c>GetByNameAsync</c>, find nothing, insert.
/// <c>ix_overlays_name</c> is a plain btree, so two writers that both read
/// nothing both insert.
///
/// <para>
/// <b>One</b> of these was red on the current schema, and it is the phase-4a
/// evidence (ADR-0139) for the index:
/// <see cref="A_second_live_chain_with_the_same_name_is_refused_by_the_database"/>,
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
/// </summary>
[Collection(AspireCollection.Name)]
public class OverlayNameUniquenessIntegrationTests(AspireFixture aspire) : IAsyncLifetime
{
    /// <summary>
    /// Enough writers that the race is likely without being so many that the
    /// test becomes a load test — the figure
    /// <c>ServiceDefaults.UniquenessRaceIntegrationTests</c> already settled on.
    /// </summary>
    private const int Writers = 12;

    public Task InitializeAsync() => aspire.ResetOverlayDesignerAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- red: the database does not refuse a duplicate ----------------------

    /// <summary>
    /// The defect with the timing taken out. A second live chain is inserted
    /// straight through a DbContext, so nothing depends on scheduling: today
    /// the write lands and the table holds two rows; once
    /// <c>ux_overlays_name_active</c> exists Postgres refuses it with SQLSTATE
    /// 23505, which is what <c>UniqueConstraintExceptionHandler</c> turns into
    /// the caller's 409.
    ///
    /// <para>
    /// Written against the schema rather than the API on purpose. The API test
    /// below can only ever observe whichever race it happens to provoke; this
    /// one states the guarantee itself and fails for exactly one reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_second_live_chain_with_the_same_name_is_refused_by_the_database()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string name = UniqueName();
        HttpResponseMessage created = await overlays.PostAsJsonAsync(
            "/overlays", new { name, label = SampleLabelBody() });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(created));

        await using OverlayDesignerDbContext context = await aspire.CreateOverlayDesignerDbContextAsync();
        context.Overlays.Add(Overlay.CreateDraft(
            OverlayName.From(name),
            Label.From(
                "Duplicate",
                NormalizedPosition.From(0.5m, 0.05m),
                NormalizedSize.From(0.3m, 0.08m),
                48),
            OperatorIdentifier.From(Guid.CreateVersion7()),
            new SystemClock()));

        DbUpdateException refused = await Should.ThrowAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        refused.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// The user-visible half: distinct callers who happen to pick the same name
    /// at the same moment. Shaped after
    /// <c>ServiceDefaults.UniquenessRaceIntegrationTests</c>, which is this
    /// repository's own answer to "how do you make the race likely" — every
    /// attempt is dispatched before any is awaited, and there are enough of
    /// them that some pair interleaves without the test becoming a load test.
    /// Issued sequentially they would be refused by the handler's own check and
    /// prove nothing.
    ///
    /// <para>
    /// <b>One difference from that precedent matters.</b> CameraCatalog already
    /// has its unique index, so there "exactly one 201" holds however the race
    /// resolves and the test asserts an invariant rather than an occurrence.
    /// Here, until the index lands, it holds only when the race actually
    /// occurs — so this test can fail to add information on a quiet run. That
    /// is why <see cref="A_second_live_chain_with_the_same_name_is_refused_by_the_database"/>
    /// exists alongside it and carries the guarantee deterministically.
    /// </para>
    ///
    /// <para>
    /// <b>No request carries an <c>Idempotency-Key</c>.</b> The header is
    /// opt-in (ADR-0142) and requests sharing a key are deduplicated by
    /// <c>IdempotencyStore</c> before the race can reach the database — a keyed
    /// burst would observe one 201 and eleven replays and conclude, wrongly,
    /// that the system is safe. Absent keys are the plainest spelling of
    /// "distinct create attempts"; distinct keys would serve as well.
    /// </para>
    ///
    /// <para>
    /// A loser code is asserted as one of two values, and that is not vagueness:
    /// after the fix both are correct and which one arrives is decided by the
    /// interleaving under test. A read that lands after the winner committed is
    /// refused by the handler with the specific <c>OVERLAY_NAME_TAKEN</c>; one
    /// that lands before it is refused by the index and surfaces as the generic
    /// <c>RESOURCE_ALREADY_EXISTS</c> (spec 086 §4). The sequential collision
    /// keeps a separate, exact assertion in
    /// <see cref="OverlayLifecycleIntegrationTests"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_for_one_overlay_name_yield_one_success_and_never_a_fault()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string contested = UniqueName();

        IEnumerable<Task<HttpResponseMessage>> attempts = Enumerable
            .Range(0, Writers)
            .Select(_ => overlays.PostAsJsonAsync(
                "/overlays", new { name = contested, label = SampleLabelBody() }));

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
                $"exactly one writer may take '{contested}'. Statuses: {string.Join(", ", statuses)}. "
                + $"The overlays table holds {rows} row(s) with that name.");
            rows.ShouldBe(1);

            statuses.Where(status => status != HttpStatusCode.Created)
                .ShouldAllBe(status => status == HttpStatusCode.Conflict);

            foreach (HttpResponseMessage loser in answers.Where(
                answer => answer.StatusCode == HttpStatusCode.Conflict))
            {
                JsonElement problem = await loser.Content.ReadFromJsonAsync<JsonElement>();
                problem.GetProperty("title").GetString().ShouldBeOneOf(
                    "RESOURCE_ALREADY_EXISTS", "OVERLAY_NAME_TAKEN");
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
    /// FR-006 reuse clause, and the assertion that stops the fix overshooting.
    /// A <b>total</b> unique index would pass every red test above and fail
    /// this one: a name would never be released, and archiving would silently
    /// become permanent confiscation of the word.
    /// </summary>
    [Fact]
    public async Task An_archived_chain_releases_its_name_for_a_new_chain()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string name = UniqueName();
        Guid archived = await CreateAsync(overlays, name);

        HttpResponseMessage archive = await OverlayRequests.PostAsync(
            overlays, archived, "revisions/1/archive");
        archive.StatusCode.ShouldBe(HttpStatusCode.OK, await DiagnoseAsync(archive));

        HttpResponseMessage reused = await overlays.PostAsJsonAsync(
            "/overlays", new { name, label = SampleLabelBody() });

        reused.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(reused));
        (await CountRowsNamedAsync(name)).ShouldBe(2);
    }

    /// <summary>
    /// Spec 037 FR-009 (ADR-0121), unchanged: a stranded chain is recoverable,
    /// but not back onto a name somebody has taken in the meantime. The refusal
    /// is the handler's own and stays specific — the index is a backstop for the
    /// race, not a replacement for the check.
    /// </summary>
    [Fact]
    public async Task Branching_an_archived_chain_whose_name_was_taken_is_refused_with_OVERLAY_NAME_TAKEN()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string name = UniqueName();
        Guid stranded = await CreateAsync(overlays, name);
        (await OverlayRequests.PostAsync(overlays, stranded, "revisions/1/archive"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await overlays.PostAsJsonAsync("/overlays", new { name, label = SampleLabelBody() }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage refused = await OverlayRequests.PostAsync(overlays, stranded, "draft");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await DiagnoseAsync(refused));
        JsonElement problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("OVERLAY_NAME_TAKEN");
    }

    /// <summary>
    /// <c>OverlayName</c> is a plain <c>StringValueObject</c> — trimmed,
    /// ordinal equality, no normalisation — so the handler comparison and a
    /// btree on the raw column already agree, and no <c>name_normalized</c>
    /// column belongs in this change (spec 086 §1.3). Recorded here because the
    /// cheapest way to make a new index "work" is to normalise the column, and
    /// that would be a user-visible behaviour change smuggled in as an index
    /// fix.
    /// </summary>
    [Fact]
    public async Task Two_overlay_names_differing_only_in_case_are_distinct_chains()
    {
        using HttpClient overlays = await aspire.CreateAdminClientAsync("overlay-designer");
        string lower = $"ovl-case-{Guid.NewGuid():N}"[..16];
        string upper = lower.ToUpperInvariant();

        HttpResponseMessage first = await overlays.PostAsJsonAsync(
            "/overlays", new { name = lower, label = SampleLabelBody() });
        HttpResponseMessage second = await overlays.PostAsJsonAsync(
            "/overlays", new { name = upper, label = SampleLabelBody() });

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(first));
        second.StatusCode.ShouldBe(HttpStatusCode.Created, await DiagnoseAsync(second));
        (await CountRowsNamedAsync(lower)).ShouldBe(1);
        (await CountRowsNamedAsync(upper)).ShouldBe(1);
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<Guid> CreateAsync(HttpClient overlays, string name)
    {
        HttpResponseMessage created = await overlays.PostAsJsonAsync(
            "/overlays", new { name, label = SampleLabelBody() });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<int> CountRowsNamedAsync(string name)
    {
        await using OverlayDesignerDbContext context = await aspire.CreateOverlayDesignerDbContextAsync();
        OverlayName parsed = OverlayName.From(name);

        return await context.Overlays.CountAsync(candidate => candidate.Name == parsed);
    }

    private static object SampleLabelBody() => new
    {
        text = "Production Line 1",
        normalizedX = 0.5m,
        normalizedY = 0.05m,
        normalizedWidth = 0.3m,
        normalizedHeight = 0.08m,
        fontSizePx = 48,
    };

    private static string UniqueName() => $"Ovl-{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// A bare status tells a reader nothing and CI has no other route to the
    /// service stack trace, so an unexpected answer carries the body and the
    /// service recent output with it.
    /// </summary>
    private async Task<string> DiagnoseAsync(HttpResponseMessage response) =>
        $"body: {await response.Content.ReadAsStringAsync()}{Environment.NewLine}"
        + $"overlay-designer log:{Environment.NewLine}{aspire.RecentLogs("overlay-designer")}";
}

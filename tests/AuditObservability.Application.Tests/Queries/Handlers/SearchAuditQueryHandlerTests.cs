using SmartSentinelEye.AuditObservability.Application.DTOs;
using SmartSentinelEye.AuditObservability.Application.Queries;
using SmartSentinelEye.AuditObservability.Application.Queries.Handlers;
using SmartSentinelEye.AuditObservability.Application.Tests.Fakes;
using SmartSentinelEye.AuditObservability.Application.Tests.TestData;
using SmartSentinelEye.Shared.Kernel;
using AuditEventEntity = SmartSentinelEye.AuditObservability.Domain.AuditEvent.AuditEvent;

namespace SmartSentinelEye.AuditObservability.Application.Tests.Queries.Handlers;

public class SearchAuditQueryHandlerTests
{
    private static SearchAuditQuery DefaultQuery(
        string? fab = "munich",
        string[]? callerFabs = null,
        string? actorUsername = null,
        string? eventKind = null,
        string? cursor = null,
        int pageSize = 50) =>
        new(fab, callerFabs ?? ["munich"], null, actorUsername, eventKind, null, null, null, null, pageSize, cursor);

    [Fact]
    public async Task Returns_rows_filtered_by_fab_in_descending_OccurredAt_order()
    {
        DateTimeOffset baseTime = DateTimeOffset.Parse("2026-05-29T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        AuditEventEntity older = new AuditEventBuilder()
            .WithOccurredAt(baseTime).WithFab("munich").Build();
        AuditEventEntity newer = new AuditEventBuilder()
            .WithOccurredAt(baseTime.AddMinutes(5)).WithFab("munich").Build();
        AuditEventEntity wrongFab = new AuditEventBuilder()
            .WithOccurredAt(baseTime.AddMinutes(10)).WithFab("berlin").Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([older, newer, wrongFab]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(DefaultQuery(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rows.Count.ShouldBe(2);
        result.Value.Rows[0].OccurredAt.ShouldBe(newer.OccurredAt);
        result.Value.Rows[1].OccurredAt.ShouldBe(older.OccurredAt);
    }

    [Fact]
    public async Task Without_fab_uses_the_caller_fab_set()
    {
        AuditEventEntity munich = new AuditEventBuilder().WithFab("munich").Build();
        AuditEventEntity berlin = new AuditEventBuilder().WithFab("berlin").Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([munich, berlin]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(fab: null, callerFabs: ["munich"]), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rows.Count.ShouldBe(1);
        result.Value.Rows[0].Fab.ShouldBe("munich");
    }

    /// <summary>
    /// This assertion is the inverse of what it used to be, deliberately.
    ///
    /// <para>
    /// A cross-fab row carries no fab, so it is not another fab's business to
    /// withhold — but the filter required <c>Fab != null</c>, which made those
    /// rows readable only by a caller belonging to no fab. Since every real
    /// operator belongs to one, that whole class of row was invisible to
    /// everybody (#1300).
    /// </para>
    ///
    /// <para>
    /// The class is smaller than the enumeration this comment used to carry.
    /// Camera events already stamped the fab before this; variable events do
    /// now (#2068), and layout events (#2071). What legitimately publishes
    /// without one is overlay events, whose domain events carry no fab at all
    /// (ADR-0115), and retention, which spans fabs. A stream-health event's
    /// fab is nullable and may still arrive null (#2076).
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_caller_with_fabs_sees_cross_fab_rows_alongside_their_own()
    {
        AuditEventEntity munich = new AuditEventBuilder().WithFab("munich").Build();
        AuditEventEntity berlin = new AuditEventBuilder().WithFab("berlin").Build();
        AuditEventEntity unscoped = new AuditEventBuilder().WithFab(null).Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([munich, berlin, unscoped]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(fab: null, callerFabs: ["munich"]), default);

        result.Value.Rows.Select(row => row.Fab).ShouldBe(["munich", null], ignoreOrder: true);
    }

    [Fact]
    public async Task Naming_a_fab_still_excludes_cross_fab_rows()
    {
        // Asking for one fab is a narrower question than "what may I see": a
        // row with no fab is not in munich, so it is not an answer to it.
        AuditEventEntity munich = new AuditEventBuilder().WithFab("munich").Build();
        AuditEventEntity unscoped = new AuditEventBuilder().WithFab(null).Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([munich, unscoped]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(fab: "munich", callerFabs: ["munich"]), default);

        result.Value.Rows.Count.ShouldBe(1);
        result.Value.Rows[0].Fab.ShouldBe("munich");
    }

    [Fact]
    public async Task A_caller_with_no_fabs_still_sees_only_cross_fab_rows()
    {
        AuditEventEntity munich = new AuditEventBuilder().WithFab("munich").Build();
        AuditEventEntity unscoped = new AuditEventBuilder().WithFab(null).Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([munich, unscoped]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(fab: null, callerFabs: []), default);

        result.Value.Rows.Count.ShouldBe(1);
        result.Value.Rows[0].Fab.ShouldBeNull();
    }

    [Fact]
    public async Task Cursor_pagination_round_trips_without_overlap()
    {
        DateTimeOffset baseTime = DateTimeOffset.Parse("2026-05-29T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        List<AuditEventEntity> rows = [..
            Enumerable.Range(0, 5)
                .Select(i => new AuditEventBuilder()
                    .WithOccurredAt(baseTime.AddMinutes(i))
                    .WithEventIdentifier(Guid.CreateVersion7())
                    .Build())];

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource(rows));

        Result<AuditPageDto, SearchAuditError> page1 = await handler.HandleAsync(DefaultQuery(pageSize: 2), default);
        page1.Value.Rows.Count.ShouldBe(2);
        page1.Value.NextCursor.ShouldNotBeNull();

        Result<AuditPageDto, SearchAuditError> page2 = await handler.HandleAsync(
            DefaultQuery(pageSize: 2, cursor: page1.Value.NextCursor), default);
        page2.Value.Rows.Count.ShouldBe(2);
        page2.Value.NextCursor.ShouldNotBeNull();

        IEnumerable<Guid> seen = page1.Value.Rows.Concat(page2.Value.Rows)
            .Select(r => r.AuditIdentifier);
        seen.Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public async Task Empty_result_returns_an_empty_page_not_an_error()
    {
        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(DefaultQuery(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rows.ShouldBeEmpty();
        result.Value.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Rejects_an_unparseable_cursor()
    {
        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(cursor: "this-is-not-a-cursor"), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<SearchAuditError.InvalidCursor>();
    }

    [Fact]
    public async Task Rejects_pageSize_above_the_maximum()
    {
        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(DefaultQuery(pageSize: 201), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<SearchAuditError.PageSizeOutOfRange>();
    }

    [Fact]
    public async Task Filter_by_actor_username_narrows_the_result()
    {
        AuditEventEntity admin = new AuditEventBuilder()
            .WithActor(Guid.CreateVersion7(), username: "admin@munich.test").Build();
        AuditEventEntity operatorRow = new AuditEventBuilder()
            .WithActor(Guid.CreateVersion7(), username: "op-3@munich.test").Build();

        SearchAuditQueryHandler handler = new(new TestAuditEventQuerySource([admin, operatorRow]));

        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(
            DefaultQuery(actorUsername: "admin@munich.test"), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Rows.Count.ShouldBe(1);
        result.Value.Rows[0].ActorUsername.ShouldBe("admin@munich.test");
    }
}

using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.EventIngestion.Application.Queries;
using SmartSentinelEye.EventIngestion.Application.Queries.Handlers;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using EventAggregate = SmartSentinelEye.EventIngestion.Domain.Event.Event;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Verifies that the <see cref="ListEventsQueryHandler"/> read query is fully
/// translatable to SQL. The events table maps its time and id columns through
/// EF Core value converters; member access on the converted CLR type
/// (<c>e.IngestedAt.Value</c>) silently fails to translate at runtime, so this
/// guards the implicit-operator + value-object-comparison form.
///
/// <para>
/// Offline by design: <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/>
/// generates the command text from the model and provider without opening a
/// connection, so there is no <see cref="AspireCollection"/> / Docker dependency.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public sealed class ListEventsTranslationTests
{
    private static EventIngestionDbContext NewContext()
    {
        DbContextOptions<EventIngestionDbContext> options =
            new DbContextOptionsBuilder<EventIngestionDbContext>()
                .UseNpgsql("Host=localhost;Database=translation-check")
                .Options;
        return new EventIngestionDbContext(options);
    }

    [Fact]
    public void Unfiltered_list_query_translates_to_sql()
    {
        using EventIngestionDbContext context = NewContext();
        ListEventsQuery query = new(
            Fabs: [FabIdentifier.From("munich")],
            Source: null,
            Device: null,
            Kind: null,
            OccurredAfter: null,
            OccurredBefore: null,
            IngestedAfter: null,
            IngestedBefore: null,
            PageSize: 100,
            Cursor: null);

        IQueryable<EventAggregate> queryable =
            ListEventsQueryHandler.BuildPagedQuery(context.Events, query, cursor: null, pageSize: 100);

        string sql = queryable.ToQueryString();

        sql.ShouldContain("ORDER BY");
        sql.ShouldContain("ingested_at");
        sql.ShouldContain("event_id");
    }

    [Fact]
    public void Fully_filtered_and_cursored_query_translates_to_sql()
    {
        using EventIngestionDbContext context = NewContext();
        DateTimeOffset anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ListEventsQuery query = new(
            Fabs: [FabIdentifier.From("munich")],
            Source: Source.From("plc"),
            Device: DeviceIdentifier.From("press-07"),
            Kind: Kind.From("Alarm"),
            OccurredAfter: anchor,
            OccurredBefore: anchor.AddHours(1),
            IngestedAfter: anchor,
            IngestedBefore: anchor.AddHours(1),
            PageSize: 50,
            Cursor: null);

        (DateTimeOffset Ingested, Guid EventId) cursor = (anchor.AddMinutes(30), Guid.CreateVersion7());

        IQueryable<EventAggregate> queryable =
            ListEventsQueryHandler.BuildPagedQuery(context.Events, query, cursor, pageSize: 50);

        // Throws "could not be translated" if any filter/order touches a
        // converted property via `.Value` instead of the value object itself.
        string sql = queryable.ToQueryString();

        sql.ShouldContain("occurred_at");
        sql.ShouldContain("ingested_at");
        sql.ShouldContain("WHERE");
        sql.ShouldContain("ORDER BY");
    }

    [Fact]
    public void Member_access_on_a_converted_column_does_not_translate()
    {
        // Pins the failure mode the value-object comparison form avoids: member
        // access on the value-converted CLR type (`e.IngestedAt.Value`) cannot
        // be translated, so the query throws at execution time. If EF Core ever
        // starts supporting this, the comparison form stays correct — but this
        // documents why the read handler must not reintroduce `.Value`.
        using EventIngestionDbContext context = NewContext();

        Should.Throw<InvalidOperationException>(() =>
            context.Events
                .OrderByDescending(e => e.IngestedAt.Value)
                .ToQueryString());
    }
}

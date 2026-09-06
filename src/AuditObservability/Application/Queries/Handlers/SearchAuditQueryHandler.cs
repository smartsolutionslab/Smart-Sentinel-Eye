using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Application.DTOs;
using SmartSentinelEye.AuditObservability.Domain.AuditEvent;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;
using AuditEventEntity = SmartSentinelEye.AuditObservability.Domain.AuditEvent.AuditEvent;
using DomainResourceKind = SmartSentinelEye.AuditObservability.Domain.AuditEvent.ResourceKind;

namespace SmartSentinelEye.AuditObservability.Application.Queries.Handlers;

public sealed class SearchAuditQueryHandler(IAuditEventQuerySource events)
    : IQueryHandler<SearchAuditQuery, Result<AuditPageDto, SearchAuditError>>
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;

    public async Task<Result<AuditPageDto, SearchAuditError>> HandleAsync(
        SearchAuditQuery query, CancellationToken cancellationToken)
    {
        Ensure.That(query).IsNotNull();

        (string? fab, IReadOnlyList<string>? callerFabs, Guid? actor, string? actorUsername, string? eventKind, string? resourceKind,
            string? resourceIdentifier, DateTimeOffset? since, DateTimeOffset? until, int rawPageSize, string? rawCursor) = query;

        int pageSize = rawPageSize <= 0 ? DefaultPageSize : rawPageSize;
        if (pageSize > MaximumPageSize)
        {
            return Failure(SearchAuditFailures.PageSizeOutOfRange(pageSize, 1, MaximumPageSize));
        }

        if (resourceKind is { } rk && DomainResourceKind.All.All(kind => kind.Value != rk))
        {
            return Failure(SearchAuditFailures.InvalidResourceKind(rk));
        }

        (DateTimeOffset OccurredAt, Guid AuditIdentifier)? cursor = null;
        if (rawCursor is not null)
        {
            cursor = AuditCursor.TryDecode(rawCursor);
            if (cursor is null)
            {
                return Failure(SearchAuditFailures.InvalidCursor(rawCursor));
            }
        }

        IQueryable<AuditEventEntity> source = events.AuditEvents;

        // Compare value objects directly (not their `.Value`): EF Core
        // translates equality on a value-converted property to a column
        // comparison, but cannot translate member access on the converted
        // CLR type (`a.EventKind.Value == x` throws "could not be translated").
        if (fab is not null)
        {
            FabIdentifier fabId = FabIdentifier.From(fab);
            source = source.Where(auditEvent => auditEvent.Fab == fabId);
        }
        else if (callerFabs.Count > 0)
        {
            // Cross-fab rows (fab = null) are included, not excluded. They are
            // not restricted to a fab, so restricting who may read them by fab
            // made them readable by nobody: every operator belongs to a fab,
            // so the whole class of row was invisible to every real caller
            // (#1300).
            //
            // That class is smaller than it was, and the enumeration this
            // replaced had gone stale. Camera, layout and variable events all
            // stamp the fab now (#2068). What legitimately publishes without
            // one is overlay events, whose domain events carry no fab at all
            // (ADR-0115), and retention. A stream-health event's fab is
            // nullable and may still arrive null (#2076).
            List<FabIdentifier> allowed = [.. callerFabs.Select(FabIdentifier.From)];
            source = source.Where(auditEvent => auditEvent.Fab == null || allowed.Contains(auditEvent.Fab));
        }
        else
        {
            // A caller with no fab membership can only see cross-fab rows.
            source = source.Where(auditEvent => auditEvent.Fab == null);
        }

        if (actor is { } actorValue)
        {
            ActorIdentifier actorId = ActorIdentifier.From(actorValue);
            source = source.Where(auditEvent => auditEvent.Actor == actorId);
        }
        if (actorUsername is not null)
        {
            // A search term too long to be an ActorUsername matched no rows while
            // this column was a string, and still should. Parsing it unguarded —
            // as the actor filter above does — would turn an over-long term into
            // a 500, which is a status this refactor must not introduce.
            ActorUsername parsedActorUsername;
            try
            {
                parsedActorUsername = ActorUsername.From(actorUsername);
            }
            catch (ArgumentException)
            {
                return Success(new AuditPageDto([], null));
            }

            source = source.Where(auditEvent => auditEvent.ActorUsername == parsedActorUsername);
        }
        if (eventKind is not null)
        {
            EventKind kind = EventKind.From(eventKind);
            source = source.Where(auditEvent => auditEvent.EventKind == kind);
        }
        if (resourceKind is not null)
        {
            DomainResourceKind resourceKindFilter = DomainResourceKind.From(resourceKind);
            source = source.Where(auditEvent => auditEvent.ResourceKind == resourceKindFilter);
        }
        if (resourceIdentifier is not null)
        {
            ResourceIdentifier resId = ResourceIdentifier.From(resourceIdentifier);
            source = source.Where(auditEvent => auditEvent.ResourceIdentifier == resId);
        }
        if (since is { } sinceFrom)
        {
            source = source.Where(auditEvent => auditEvent.OccurredAt >= sinceFrom);
        }

        if (until is { } untilTo)
        {
            source = source.Where(auditEvent => auditEvent.OccurredAt < untilTo);
        }

        if (cursor is { } c)
        {
            // Strict 'less than' for descending order; tuple compare
            // breaks ties on AuditIdentifier so concurrent inserts
            // sharing the same OccurredAt don't shift the window.
            source = source.Where(auditEvent =>
                auditEvent.OccurredAt < c.OccurredAt ||
                (auditEvent.OccurredAt == c.OccurredAt && ((Guid)auditEvent.Id).CompareTo(c.AuditIdentifier) < 0));
        }

        List<AuditEventEntity> rows = await source
            .OrderByDescending(auditEvent => auditEvent.OccurredAt)
            .ThenByDescending(auditEvent => auditEvent.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            AuditEventEntity last = rows[pageSize - 1];
            nextCursor = AuditCursor.Encode(last.OccurredAt, last.Id.Value);
            rows = rows.Take(pageSize).ToList();
        }

        AuditRowDto[] dtos = rows.Select(AuditRowMapper.Map).ToArray();
        return Success(new AuditPageDto(dtos, nextCursor));
    }
}

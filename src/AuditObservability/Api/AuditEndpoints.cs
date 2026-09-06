using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SmartSentinelEye.AuditObservability.Application.DTOs;
using SmartSentinelEye.AuditObservability.Application.Queries;
using SmartSentinelEye.AuditObservability.Application.Queries.Handlers;
using SmartSentinelEye.AuditObservability.Domain.AuditEvent;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.AuditObservability.Api;

/// <summary>
/// HTTP read API for AuditObservability (spec 009 FR-008 / 009 /
/// 010). Every endpoint is gated by <c>sse.audit.read</c>; the
/// per-fab + per-resource endpoints additionally run the shared
/// fab guard from <c>ServiceDefaults</c> (spec 008 FR-019).
/// </summary>
public static class AuditEndpoints
{
    public const string GroupClaimType = DefaultFabAuthorizationGuard.GroupClaimType;
    public const string FabGroupPrefix = DefaultFabAuthorizationGuard.FabGroupPrefix;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/audit")
            .RequireAuthorization(Scope.Sse.Audit.Read)
            .WithTags("Audit");

        group.MapGet("/", Search)
            .WithName("SearchAudit")
            .WithSummary("Cross-cutting audit search. Required scope: sse.audit.read")
            .Produces<AuditPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{resourceKind}/{resourceIdentifier}", GetTimeline)
            .WithName("GetResourceAuditTimeline")
            .WithSummary("Per-resource audit timeline. Required scope: sse.audit.read")
            .Produces<AuditPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{auditIdentifier:guid}", GetSingle)
            .WithName("GetAuditEvent")
            .WithSummary("Single audit row + full payload. Required scope: sse.audit.read")
            .Produces<AuditRowDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Search(
        [FromQuery] string? fabId,
        [FromQuery] Guid? actor,
        [FromQuery] string? actorUsername,
        [FromQuery] string? eventKind,
        [FromQuery] string? resourceKind,
        [FromQuery] string? resourceIdentifier,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] SearchAuditQueryHandler handler,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (fabId is not null)
        {
            await fabGuard.EnsureAccessAsync(user, fabId, cancellationToken);
        }

        IReadOnlyList<string> callerFabs = FabClaims.AssignedFabs(user);

        SearchAuditQuery query = new(
            Fab: fabId,
            CallerFabs: callerFabs,
            Actor: actor,
            ActorUsername: actorUsername,
            EventKind: eventKind,
            ResourceKind: resourceKind,
            ResourceIdentifier: resourceIdentifier,
            Since: since,
            Until: until,
            PageSize: pageSize ?? 0,
            Cursor: cursor);
        Result<AuditPageDto, SearchAuditError> result = await handler.HandleAsync(query, cancellationToken);

        return result.Match<IResult>(onSuccess: Results.Ok, onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> GetTimeline(
        string resourceKind,
        string resourceIdentifier,
        [FromQuery] string fabId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] GetResourceTimelineQueryHandler handler,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        await fabGuard.EnsureAccessAsync(user, fabId, cancellationToken);

        // Parsed here rather than in the handler, which called From unguarded: a
        // malformed route or query value threw ArgumentException and surfaced as a
        // 500. Malformed input from a caller is a client error (ADR-0139, FR-020).
        // ResourceKind stays a string: the handler already answers an unknown kind
        // with AUDIT_TIMELINE_UNKNOWN_RESOURCE_KIND, and that code is part of the
        // contract. These two had no guard at all.
        ResourceIdentifier parsedResourceIdentifier;
        FabIdentifier parsedFab;
        try
        {
            parsedResourceIdentifier = ResourceIdentifier.From(resourceIdentifier);
            parsedFab = FabIdentifier.From(fabId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "AUDIT_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        Result<AuditPageDto, GetResourceTimelineError> result = await handler.HandleAsync(
            new GetResourceTimelineQuery(
                ResourceKind: resourceKind,
                ResourceIdentifier: parsedResourceIdentifier,
                Fab: parsedFab,
                Since: since,
                Until: until,
                PageSize: pageSize ?? 0,
                Cursor: cursor),
            cancellationToken);

        return result.Match<IResult>(onSuccess: Results.Ok, onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> GetSingle(
        Guid auditIdentifier,
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] GetAuditEventQueryHandler handler,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // An empty Guid cannot identify a row, so it is refused here rather than
        // reaching the query as one (ADR-0139, FR-020).
        AuditEventIdentifier parsedIdentifier;
        try
        {
            parsedIdentifier = AuditEventIdentifier.From(auditIdentifier);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "AUDIT_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        GetAuditEventQuery query = new(parsedIdentifier);
        Result<AuditRowDto, GetAuditEventError> result = await handler.HandleAsync(query, cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(
                title: result.Error.Code, detail: result.Error.Message,
                statusCode: (int)result.Error.Status);
        }

        AuditRowDto row = result.Value;
        if (row.Fab is not null)
        {
            await fabGuard.EnsureAccessAsync(user, row.Fab, cancellationToken);
        }
        return Results.Ok(row);
    }

}

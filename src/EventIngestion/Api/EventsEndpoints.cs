using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSentinelEye.EventIngestion.Application.DTOs;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Api;

/// <summary>
/// Minimal-API endpoint group for EventIngestion (ADR-0070): the manual
/// and webhook write paths plus the read API. Handlers are split across
/// partial files — <c>EventsEndpoints.Writes.cs</c> and
/// <c>EventsEndpoints.Reads.cs</c>.
/// </summary>
public static partial class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder writes = app.MapGroup("/events").WithTags("Events");

        // 403 became reachable with spec 018: the caller may name a fab they do
        // not hold, or hold none at all. 400 was already declared but now
        // covers a second cause — a multi-fab operator omitting fabId. Spec 013
        // shipped this wrong on one endpoint and it took a review to catch.
        writes.MapPost("/manual", IngestManual)
            .RequireAuthorization(Scope.Sse.Events.Write)
            .WithName("IngestManualEvent")
            .WithSummary(
                "Ingest an event into the resolved fab. Omit fabId when you belong to exactly one; "
                + "name it when you belong to several (ADR-0114). The fab is never taken from the "
                + "request unchecked (spec 018 FR-006). Stores the event before answering, so a 201 "
                + "means it is readable at the Location (spec 020 FR-001). "
                + "Required scope: sse.events.write")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            // 409 is the idempotency guard's, not the write's: this route inserts,
            // so the write itself conflicts with nothing — but a retry arriving while
            // the first request with the same Idempotency-Key is still in flight is
            // refused IDEMPOTENT_REQUEST_IN_PROGRESS (ADR-0142).
            .ProducesProblem(StatusCodes.Status409Conflict)
            // Spec 020: 429 still means overload, but the thing being bounded is
            // now the number of concurrent writes rather than a queue these
            // endpoints no longer use.
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Spec 019: the fab has no event storage yet — temporary by
            // construction. Spec 020 adds the other cause: the write itself
            // failed, so nothing was accepted and the caller can retry.
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        writes.MapPost("/webhook/{integrationName}", IngestWebhook)
            .AllowAnonymous() // auth is the static bearer token, not OIDC
            .WithName("IngestWebhookEvent")
            .WithSummary(
                "Ingest an event delivered by a registered webhook integration. No OIDC scope: the "
                + "caller presents the integration's own Authorization: Bearer token and the handler "
                + "validates it — by default a SHA-256 match against the stored hash, or, once the "
                + "integration is rotated, a Keycloak JWT that must carry sse.events.write, an azp "
                + "matching the integration's client and the group /fabs/{fabId}. In both modes the "
                + "delivery's fabId must be the integration's own, and every refusal collapses to one "
                + "401 so the answer never reveals which integrations exist.")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        RouteGroupBuilder reads = app.MapGroup("/events")
            .RequireAuthorization(Scope.Sse.Events.Read)
            .WithTags("EventsRead");

        reads.MapGet("/", ListEvents)
            .WithName("ListEvents")
            .WithSummary("List events from the fabs you hold. Required scope: sse.events.read")
            .Produces<EventPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        reads.MapGet("/{eventId:guid}", GetEvent)
            .WithName("GetEvent")
            .WithSummary("Read one event by its identifier, within the fabs you hold. Required scope: sse.events.read")
            .Produces<EventDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        reads.MapGet("/dead-letters", ListDeadLetters)
            .WithName("ListDeadLetters")
            .WithSummary(
                "List rejected deliveries from the fabs you hold. A delivery whose plant could "
                + "not be established from its address is returned to nobody (spec 018 FR-011). "
                + "Required scope: sse.events.read")
            .Produces<IReadOnlyList<DeadLetterDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}

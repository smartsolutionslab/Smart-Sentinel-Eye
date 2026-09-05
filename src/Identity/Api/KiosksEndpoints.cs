using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SmartSentinelEye.Identity.Api.Requests;
using SmartSentinelEye.Identity.Application.Commands;
using SmartSentinelEye.Identity.Application.Commands.Handlers;
using SmartSentinelEye.Identity.Application.DTOs;
using SmartSentinelEye.Identity.Application.Queries;
using SmartSentinelEye.Identity.Application.Queries.Handlers;
using SmartSentinelEye.Identity.Domain.RegisteredClient;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.ServiceDefaults.Idempotency;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Identity.Api;

public static class KiosksEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string EnrollEndpoint = "POST /kiosks/enroll";

    public static IEndpointRouteBuilder MapKiosksEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/kiosks")
            .RequireAuthorization(Scope.Sse.Identity.KioskClients.Write)
            .WithTags("IdentityKiosks");

        group.MapPost("/enroll", Enroll)
            .WithName("EnrollKiosk")
            .WithSummary("Enroll a new kiosk in the fab. Required scope: sse.identity.kiosks.write")
            .Produces<KioskCredentialsDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // 409 because DisableKioskCommandHandler loads a client that already
        // exists, mutates it and saves — the lost update ADR-0113 Layer 2
        // answers as AGGREGATE_VERSION_STALE.
        group.MapDelete("/{clientId}", Disable)
            .WithName("DisableKiosk")
            .WithSummary("Disable an enrolled kiosk. Required scope: sse.identity.kiosks.write")
            .Produces<Guid>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        RouteGroupBuilder reads = app.MapGroup("/kiosks")
            .RequireAuthorization(Scope.Sse.Identity.KioskClients.Read)
            .WithTags("IdentityKiosks");

        reads.MapGet("/", List)
            .WithName("ListKiosks")
            .WithSummary("List enrolled kiosks, optionally filtered by fab. Required scope: sse.identity.kiosks.read")
            .Produces<IReadOnlyList<RegisteredClientSummaryDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> List(
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] ListKiosksQueryHandler handler,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        [FromQuery] string? fabId = null)
    {
        Option<FabIdentifier> fab;
        if (string.IsNullOrWhiteSpace(fabId))
        {
            fab = Option<FabIdentifier>.None;
        }
        else
        {
            FabIdentifier parsed;
            try
            {
                parsed = FabIdentifier.From(fabId);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(
                    title: "KIOSK_INVALID_INPUT", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await fabGuard.EnsureAccessAsync(user, parsed.Value, cancellationToken);
            fab = Option<FabIdentifier>.Some(parsed);
        }

        Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError> result =
            await handler.HandleAsync(new ListKiosksQuery(fab), cancellationToken);

        return result.Match<IResult>(
            onSuccess: Results.Ok,
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> Enroll(
        [FromBody] EnrollKioskRequest body,
        [FromQuery] string fabId,
        [AsParameters] EnrollKioskServices services,
        HttpContext http,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        Ensure.That(body).IsNotNull();
        Ensure.That(http).IsNotNull();

        IFabAuthorizationGuard fabGuard = services.FabGuard;
        EnrollKioskCommandHandler handler = services.Handler;

        if (!IdempotencyHeaders.TryRead(http.Request, out Option<IdempotencyKey> key, out IResult? keyProblem))
        {
            return keyProblem;
        }

        ClientId clientId;
        FabIdentifier fab;
        try
        {
            clientId = ClientId.From(body.ClientId);
            fab = FabIdentifier.From(fabId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "KIOSK_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        await fabGuard.EnsureAccessAsync(user, fab.Value, cancellationToken);

        OperatorIdentifier actingOperator = user.ToOperatorIdentifier();

        return await IdempotentRequest.ExecuteAsync(
            new IdempotentExecution(
                key.Map(supplied => IdempotencyScope.For(supplied, EnrollEndpoint, actingOperator.Value.ToString())),
                services.Idempotency,
                services.Clock),
            async token =>
            {
                Result<KioskCredentialsDto, EnrollKioskError> result = await handler.HandleAsync(
                    new EnrollKioskCommand(clientId, fab, actingOperator), token);

                return result.Match(
                    onSuccess: dto => IdempotentOutcome.Created(
                        dto.RegisteredClientIdentifier, Results.Created($"/kiosks/{dto.ClientId}", dto)),
                    onFailure: error => IdempotentOutcome.NothingCreated(error.ToProblem()));
            },
            async (enrolled, token) =>
            {
                Result<ReplayedClientDto, ReplayRegistrationError> replayed =
                    await services.Replay.HandleAsync(
                        new ReplayRegisteredClientQuery(RegisteredClientIdentifier.From(enrolled)), token);

                // Every field of a kiosk's answer is server-held, so unlike the
                // device endpoint this one needs nothing from the replayed body.
                return replayed.Match<IResult>(
                    onSuccess: replay => Results.Created(
                        $"/kiosks/{replay.ClientId}",
                        new KioskCredentialsDto(
                            replay.RegisteredClientIdentifier,
                            replay.ClientId,
                            replay.Fab,
                            replay.ClientSecret)),
                    onFailure: error => error.ToProblem());
            },
            cancellationToken);
    }

    /// <summary>
    /// The enrolment endpoint's collaborators, bundled with
    /// <c>[AsParameters]</c> so the handler keeps a readable signature (ADR-0084).
    /// </summary>
    private sealed record EnrollKioskServices(
        [FromServices] IFabAuthorizationGuard FabGuard,
        [FromServices] EnrollKioskCommandHandler Handler,
        [FromServices] ReplayRegisteredClientQueryHandler Replay,
        [FromServices] IIdempotencyStore Idempotency,
        [FromServices] TimeProvider Clock);

    private static async Task<IResult> Disable(
        string clientId,
        [FromServices] DisableKioskCommandHandler handler,
        CancellationToken cancellationToken)
    {
        ClientId parsed;
        try
        {
            parsed = ClientId.From(clientId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "KIOSK_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        Result<RegisteredClientIdentifier, DisableKioskError> result = await handler.HandleAsync(
            new DisableKioskCommand(parsed), cancellationToken);

        return result.Match<IResult>(
            onSuccess: id => Results.Ok(id.Value),
            onFailure: error => error.ToProblem());
    }
}

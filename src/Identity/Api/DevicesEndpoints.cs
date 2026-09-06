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

public static class DevicesEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string RegisterEndpoint = "POST /devices/register";

    public static IEndpointRouteBuilder MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/devices")
            .RequireAuthorization(Scope.Sse.Identity.DeviceClients.Write)
            .WithTags("IdentityDevices");

        // 403 is declared on all three because the scope itself produces it:
        // AddScopePolicies builds each sse.* policy as RequireAuthenticatedUser()
        // plus a claim assertion, so a caller who authenticates without the scope
        // is forbidden rather than challenged. On DELETE /devices/{clientId} that is
        // the only producer, because it runs no fab guard; the other two can reach
        // 403 through IFabAuthorizationGuard as well.
        group.MapPost("/register", Register)
            .WithName("RegisterDevice")
            .WithSummary("Register a new PLC or inference device. Required scope: sse.identity.devices.write")
            .Produces<DeviceCredentialsDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // 409 because DisableDeviceCommandHandler loads a client that already
        // exists, mutates it and saves — the lost update ADR-0113 Layer 2
        // answers as AGGREGATE_VERSION_STALE.
        group.MapDelete("/{clientId}", Disable)
            .WithName("DisableDevice")
            .WithSummary("Disable a registered device. Required scope: sse.identity.devices.write")
            .Produces<Guid>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        RouteGroupBuilder reads = app.MapGroup("/devices")
            .RequireAuthorization(Scope.Sse.Identity.DeviceClients.Read)
            .WithTags("IdentityDevices");

        reads.MapGet("/", List)
            .WithName("ListDevices")
            .WithSummary("List registered devices, optionally filtered by fab. Required scope: sse.identity.devices.read")
            .Produces<IReadOnlyList<RegisteredClientSummaryDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> List(
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] ListDevicesQueryHandler handler,
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
                    title: "DEVICE_INVALID_INPUT", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await fabGuard.EnsureAccessAsync(user, parsed.Value, cancellationToken);
            fab = Option<FabIdentifier>.Some(parsed);
        }

        Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError> result =
            await handler.HandleAsync(new ListDevicesQuery(fab), cancellationToken);

        return result.Match<IResult>(
            onSuccess: Results.Ok,
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> Register(
        [FromBody] RegisterDeviceRequest body,
        [FromQuery] string fabId,
        [AsParameters] RegisterDeviceServices services,
        HttpContext http,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        Ensure.That(body).IsNotNull();
        Ensure.That(http).IsNotNull();

        IFabAuthorizationGuard fabGuard = services.FabGuard;
        RegisterDeviceCommandHandler handler = services.Handler;

        if (!IdempotencyHeaders.TryRead(http.Request, out Option<IdempotencyKey> key, out IResult? keyProblem))
        {
            return keyProblem;
        }

        FabIdentifier fab;
        try
        {
            fab = FabIdentifier.From(fabId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "DEVICE_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        await fabGuard.EnsureAccessAsync(user, fab.Value, cancellationToken);

        OperatorIdentifier actingOperator = user.ToOperatorIdentifier();

        return await IdempotentRequest.ExecuteAsync(
            new IdempotentExecution(
                key.Map(supplied => IdempotencyScope.For(supplied, RegisterEndpoint, actingOperator.Value.ToString())),
                services.Idempotency,
                services.Clock),
            async token =>
            {
                Result<DeviceCredentialsDto, RegisterDeviceError> result = await handler.HandleAsync(
                    new RegisterDeviceCommand(body.DeviceType, body.DeviceIdentifier, fab, actingOperator), token);

                // A refusal is recorded as a success of no registration: there is
                // nothing to replay, and the release below is what lets the
                // caller retry after fixing whatever was wrong.
                return result.Match(
                    onSuccess: dto => IdempotentOutcome.Created(
                        dto.RegisteredClientIdentifier, Results.Created($"/devices/{dto.ClientId}", dto)),
                    onFailure: error => IdempotentOutcome.NothingCreated(error.ToProblem()));
            },
            async (client, token) =>
            {
                Result<ReplayedClientDto, ReplayRegistrationError> replayed =
                    await services.Replay.HandleAsync(
                        new ReplayRegisteredClientQuery(RegisteredClientIdentifier.From(client)), token);

                // The device type and identifier come from the retry's own body:
                // a transparent retry is the same request, so it carries them.
                return replayed.Match<IResult>(
                    onSuccess: replay => Results.Created(
                        $"/devices/{replay.ClientId}",
                        new DeviceCredentialsDto(
                            replay.RegisteredClientIdentifier,
                            replay.ClientId,
                            body.DeviceType,
                            body.DeviceIdentifier,
                            replay.Fab,
                            replay.ClientSecret)),
                    onFailure: error => error.ToProblem());
            },
            cancellationToken);
    }

    /// <summary>
    /// The registration endpoint's collaborators, bundled with
    /// <c>[AsParameters]</c> so the handler keeps a readable signature (ADR-0084)
    /// now that idempotency adds three more.
    /// </summary>
    private sealed record RegisterDeviceServices(
        [FromServices] IFabAuthorizationGuard FabGuard,
        [FromServices] RegisterDeviceCommandHandler Handler,
        [FromServices] ReplayRegisteredClientQueryHandler Replay,
        [FromServices] IIdempotencyStore Idempotency,
        [FromServices] TimeProvider Clock);

    private static async Task<IResult> Disable(
        string clientId,
        [FromServices] DisableDeviceCommandHandler handler,
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
                title: "DEVICE_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        Result<RegisteredClientIdentifier, DisableDeviceError> result = await handler.HandleAsync(
            new DisableDeviceCommand(parsed), cancellationToken);

        return result.Match<IResult>(
            onSuccess: id => Results.Ok(id.Value),
            onFailure: error => error.ToProblem());
    }
}

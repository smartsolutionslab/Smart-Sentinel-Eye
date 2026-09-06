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

/// <summary>
/// Endpoint for the hard-cut webhook migration (spec 008 US5 /
/// FR-014). Admins call this to lift a spec-006 webhook integration
/// onto a Keycloak service-account client; subsequent calls roll
/// the secret.
/// </summary>
public static class WebhookRotationEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string RotateEndpoint = "POST /webhook-integrations/{name}/rotate";

    public static IEndpointRouteBuilder MapWebhookRotationEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/webhook-integrations")
            .RequireAuthorization(Scope.Sse.Webhooks.Write)
            .WithTags("IdentityWebhookRotation");

        group.MapPost("/{name}/rotate", Rotate)
            .WithName("RotateWebhookClient")
            .WithSummary("Rotate a webhook integration's bearer onto a Keycloak JWT. Send If-Match with the version from GET /webhook-integrations to rotate an existing client, or If-None-Match: * to create one. Required scope: sse.webhooks.write")
            .Produces<WebhookClientCredentialsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // Same scope as the rotation rather than a new sse.webhooks.read: this
        // list exists only to supply the If-Match the rotation demands, so
        // anyone who may rotate must be able to read it. A separate read scope
        // would let a principal hold the write scope its own description
        // promises and still be unable to rotate anything.
        group.MapGet("/", List)
            .WithName("ListWebhookClients")
            .WithSummary("List webhook service-account clients and the version each must be rotated at. Required scope: sse.webhooks.write")
            .Produces<IReadOnlyList<RegisteredClientSummaryDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> List(
        [FromServices] IFabAuthorizationGuard fabGuard,
        [FromServices] ListWebhookClientsQueryHandler handler,
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
                    title: "WEBHOOK_INVALID_INPUT", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await fabGuard.EnsureAccessAsync(user, parsed.Value, cancellationToken);
            fab = Option<FabIdentifier>.Some(parsed);
        }

        Result<IReadOnlyList<RegisteredClientSummaryDto>, ListClientsError> result =
            await handler.HandleAsync(new ListWebhookClientsQuery(fab), cancellationToken);

        return result.Match<IResult>(
            onSuccess: Results.Ok,
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> Rotate(
        string name,
        [FromBody] RotateWebhookClientRequest body,
        HttpRequest request,
        [AsParameters] RotateWebhookServices services,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        Ensure.That(body).IsNotNull();
        Ensure.That(request).IsNotNull();

        IFabAuthorizationGuard fabGuard = services.FabGuard;
        RotateWebhookClientCommandHandler handler = services.Handler;

        if (!IdempotencyHeaders.TryRead(request, out Option<IdempotencyKey> key, out IResult? keyProblem))
        {
            return keyProblem;
        }

        FabIdentifier fab;
        try
        {
            fab = FabIdentifier.From(body.FabId);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "WEBHOOK_INVALID_INPUT", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The guard runs before the precondition is parsed, per
        // IFabAuthorizationGuard's contract that it is called right after model
        // binding. Reading If-Match first would answer a caller who may not
        // touch this fab at all with 428 and an invitation to retry, instead of
        // the 403 they have earned.
        await fabGuard.EnsureAccessAsync(user, fab.Value, cancellationToken);

        // This endpoint upserts, so the caller states which operation it means:
        // If-None-Match: * to create the client, If-Match: "N" to rotate the
        // one at version N. See ConcurrencyHeaders for why a lone If-Match
        // cannot carry both.
        if (!ConcurrencyHeaders.TryReadUpsertPrecondition(request, out Option<int> expectedVersion, out IResult? precondition))
        {
            return precondition;
        }

        OperatorIdentifier actingOperator = user.ToOperatorIdentifier();

        // **A replay never reaches the precondition, and that is the point of
        // applying idempotency here.** The header is still parsed above — a
        // malformed or missing one is a 400 or 428 either way — but it is
        // *enforced* by the command handler, comparing the expected version to
        // the aggregate's, and a replay returns without calling the handler at
        // all. That matters because a retry carries the same If-Match it sent
        // the first time, while the first attempt already bumped the version:
        // re-running the command would answer 412 for a rotation that succeeded,
        // and the caller would have lost a secret it can no longer obtain. The
        // key proves this is the same request, which is a stronger statement
        // than the version it was written against.
        //
        // Rotation is also where a blind retry does real damage rather than
        // merely reporting badly: rotating twice invalidates the secret the
        // first attempt already delivered. If-Match happens to prevent that
        // today, but only because the version moved — it guards against
        // concurrent writers, not against a duplicate of one writer's request.
        return await IdempotentRequest.ExecuteAsync(
            new IdempotentExecution(
                key.Map(supplied => IdempotencyScope.For(supplied, RotateEndpoint, actingOperator.Value.ToString())),
                services.Idempotency,
                services.Clock),
            async token =>
            {
                Result<WebhookClientCredentialsDto, RotateWebhookClientError> result = await handler.HandleAsync(
                    new RotateWebhookClientCommand(name, fab, actingOperator, expectedVersion), token);

                return result.Match(
                    onSuccess: dto => IdempotentOutcome.Created(dto.RegisteredClientIdentifier, Results.Ok(dto)),
                    onFailure: error => IdempotentOutcome.NothingCreated(error.ToProblem()));
            },
            async (rotated, token) =>
            {
                Result<ReplayedClientDto, ReplayRegistrationError> replayed =
                    await services.Replay.HandleAsync(
                        new ReplayRegisteredClientQuery(RegisteredClientIdentifier.From(rotated)), token);

                // The integration name comes from the route the retry repeated.
                return replayed.Match<IResult>(
                    onSuccess: replay => Results.Ok(
                        new WebhookClientCredentialsDto(
                            replay.RegisteredClientIdentifier,
                            replay.Version,
                            replay.ClientId,
                            name,
                            replay.Fab,
                            replay.ClientSecret)),
                    onFailure: error => error.ToProblem());
            },
            cancellationToken);
    }

    /// <summary>
    /// The rotation endpoint's collaborators, bundled with
    /// <c>[AsParameters]</c> so the handler keeps a readable signature (ADR-0084).
    /// </summary>
    private sealed record RotateWebhookServices(
        [FromServices] IFabAuthorizationGuard FabGuard,
        [FromServices] RotateWebhookClientCommandHandler Handler,
        [FromServices] ReplayRegisteredClientQueryHandler Replay,
        [FromServices] IIdempotencyStore Idempotency,
        [FromServices] TimeProvider Clock);
}

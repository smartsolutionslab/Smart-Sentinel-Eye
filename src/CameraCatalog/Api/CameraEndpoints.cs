using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SmartSentinelEye.CameraCatalog.Api.Requests;
using SmartSentinelEye.CameraCatalog.Application.Commands;
using SmartSentinelEye.CameraCatalog.Application.Commands.Handlers;
using SmartSentinelEye.CameraCatalog.Application.DTOs;
using SmartSentinelEye.CameraCatalog.Application.Queries;
using SmartSentinelEye.CameraCatalog.Application.Queries.Handlers;
using SmartSentinelEye.CameraCatalog.Domain.Camera;
using SmartSentinelEye.ServiceDefaults;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.ServiceDefaults.Idempotency;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.CameraCatalog.Api;

/// <summary>
/// Minimal-API endpoint group for the Camera Catalog (ADR-0070). All routes
/// require the admin policy per spec 001-register-camera FR-010.
/// </summary>
public static class CameraEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string RegisterEndpoint = "POST /cameras";

    public static IEndpointRouteBuilder MapCameraCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder writes = app.MapGroup("/cameras")
            .WithTags("Cameras")
            .RequireAuthorization(Scope.Sse.Cameras.Write);

        // 403 became reachable when fab resolution landed (spec 015 T024): the
        // caller may name a fab they do not hold, or hold none at all. 400 was
        // already declared but now covers a second cause — a multi-fab operator
        // omitting fabId. Declaring them keeps the generated OpenAPI from
        // claiming a status that can happen cannot; spec 013 shipped this wrong
        // on one endpoint and it took a review to catch.
        writes.MapPost("/", Register)
            .WithName("RegisterCamera")
            .WithSummary(
                "Register a camera in the resolved fab. Omit fabId when you belong to exactly one fab; "
                + "name it when you belong to several (ADR-0114). Required scope: sse.cameras.write")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // 404 rather than 403 for another fab's camera (spec 028 FR-004), and
        // it is declared here so the generated OpenAPI says so: a 403 would
        // confirm the camera exists, letting an operator enumerate another
        // plant's cameras one request at a time. 409 covers the lost update,
        // not the domain conflict — two different races. Retiring is
        // idempotent, so an already-retired camera is 204 rather than a
        // conflict; but the handler loads a camera that already exists,
        // mutates it and saves, so a concurrent write to the same row makes
        // EF's affected-row check disagree and the shared handler answers
        // AGGREGATE_VERSION_STALE (ADR-0113 Layer 2, ADR-0119). The
        // idempotence of the first race says nothing about the second.
        writes.MapPost("/{camera:guid}/retire", Retire)
            .WithName("RetireCamera")
            .WithSummary(
                "Retire a camera. Terminal, and idempotent — retiring one already retired succeeds. "
                + "Its name becomes available again within its own fab. Required scope: sse.cameras.write")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // 412 and 428 are declared because both are reachable and neither is a
        // failure of the request's content: 428 when no If-Match is sent, 412
        // when the version quoted is stale (ADR-0113, no retry on conflict).
        // 409 is the retired camera — terminal, so a correction describes
        // nothing (FR-005).
        // The name became editable with spec 033 (ADR-0120), which is safe here
        // and would not be for a rule or a variable: a camera is addressed by
        // its identifier, so its name is an attribute and nothing refers to the
        // old value. 409 now has a second cause — CAMERA_NAME_TAKEN, which is
        // deliberately not a lost update and never becomes possible by
        // re-reading (ADR-0119).
        writes.MapPatch("/{camera:guid}", Patch)
            .WithName("PatchCamera")
            .WithSummary(
                "Correct a camera's RTSP address or its name — exactly one per request, each applied under "
                + "its own version. Requires If-Match with the version from GET /cameras/{camera} or from a "
                + "listing row. The fab and identifier are immutable. Required scope: sse.cameras.write")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        RouteGroupBuilder reads = app.MapGroup("/cameras")
            .WithTags("Cameras")
            .RequireAuthorization(Scope.Sse.Cameras.Read);

        // 404 for another fab's camera, never 403 — the same choice the retire
        // endpoint makes above, and declared here so the generated OpenAPI says
        // so. This is the endpoint spec 015 had to withdraw FR-006 for: without
        // a single-camera read there was nothing to refuse, and the
        // non-enumeration guarantee had nowhere to live.
        reads.MapGet("/{camera:guid}", Get)
            .WithName("GetCamera")
            .WithSummary(
                "Read one camera by its identifier. Returns retired cameras too, with their status. "
                + "The ETag carries the version to quote when changing it. Required scope: sse.cameras.read")
            .Produces<CameraDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        reads.MapGet("/", List)
            .WithName("ListCameras")
            .WithSummary(
                "List cameras in your fabs. Omit fabId to span all of them; name one to narrow. "
                + "A read does not have to choose (spec 015 FR-005). Retired cameras are excluded "
                + "unless includeRetired=true; every row carries its status. "
                + "Required scope: sse.cameras.read")
            .Produces<CameraListPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> Register(
        [FromBody] RegisterCameraRequest request,
        [AsParameters] RegisterCameraServices services,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        [FromQuery] string fabId = "")
    {
        Ensure.That(request).IsNotNull();
        Ensure.That(httpContext).IsNotNull();

        RegisterCameraCommandHandler handler = services.Handler;
        IFabAuthorizationGuard fabGuard = services.FabGuard;

        if (!IdempotencyHeaders.TryRead(httpContext.Request, out Option<IdempotencyKey> key, out IResult? keyProblem))
        {
            return keyProblem;
        }

        CameraName name;
        RtspUrl url;
        try
        {
            (name, url) = request;
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "CAMERA_INVALID_REQUEST",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        Result<FabIdentifier, IResult> fabResolution =
            await ResolveWriteFabAsync(httpContext.User, fabId, fabGuard, cancellationToken);
        if (fabResolution.IsFailure)
        {
            return fabResolution.Error;
        }

        FabIdentifier fab = fabResolution.Value;

        OperatorIdentifier registeredBy = ResolveOperator(httpContext);

        RegisterCameraCommand command = new(fab, name, url, registeredBy);

        return await IdempotentRequest.ExecuteCreateAsync(
            new IdempotentExecution(
                key.Map(supplied => IdempotencyScope.For(supplied, RegisterEndpoint, registeredBy.Value.ToString())),
                services.Idempotency,
                services.Clock),
            identifier => $"/cameras/{identifier}",
            async token => (await handler.HandleAsync(command, token)).Match(
                onSuccess: identifier => Result<Guid, IResult>.Success(identifier.Value),
                onFailure: error => Result<Guid, IResult>.Failure(error.ToProblem())),
            cancellationToken);
    }

    /// <summary>
    /// The registration endpoint's collaborators, bundled with
    /// <c>[AsParameters]</c> so the handler keeps a readable signature (ADR-0084).
    /// </summary>
    private sealed record RegisterCameraServices(
        [FromServices] RegisterCameraCommandHandler Handler,
        [FromServices] IFabAuthorizationGuard FabGuard,
        [FromServices] IIdempotencyStore Idempotency,
        [FromServices] TimeProvider Clock);

    private static async Task<IResult> Retire(
        [FromRoute] Guid camera,
        [FromServices] RetireCameraCommandHandler handler,
        [FromServices] IFabAuthorizationGuard fabGuard,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        [FromQuery] string fabId = "")
    {
        Result<FabIdentifier, IResult> fabResolution =
            await ResolveWriteFabAsync(httpContext.User, fabId, fabGuard, cancellationToken);
        if (fabResolution.IsFailure)
        {
            return fabResolution.Error;
        }

        FabIdentifier fab = fabResolution.Value;

        RetireCameraCommand command = new(fab, CameraIdentifier.From(camera), ResolveOperator(httpContext));

        Result<CameraIdentifier, RetireCameraError> result =
            await handler.HandleAsync(command, cancellationToken);

        // 204 rather than 200: nothing useful comes back. The caller asked for
        // the camera to be retired and it is — returning the aggregate would
        // invite a client to treat the response as a read model.
        return result.Match<IResult>(
            onSuccess: _ => Results.NoContent(),
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> Patch(
        [FromRoute] Guid camera,
        [FromBody] PatchCameraRequest request,
        [FromServices] ChangeCameraAddressCommandHandler addressHandler,
        [FromServices] RenameCameraCommandHandler renameHandler,
        [FromServices] IFabAuthorizationGuard fabGuard,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        [FromQuery] string fabId = "")
    {
        Ensure.That(request).IsNotNull();

        // Fab first — before If-Match is read, before the body is parsed, and
        // before the camera is looked up (FR-007). Answering 428 or 400 for
        // another fab's camera would confirm that camera exists, which is the
        // enumeration FR-006 exists to prevent. The cheap checks are the
        // tempting ones to hoist; they must stay below this.
        Result<FabIdentifier, IResult> fabResolution =
            await ResolveWriteFabAsync(httpContext.User, fabId, fabGuard, cancellationToken);
        if (fabResolution.IsFailure)
        {
            return fabResolution.Error;
        }

        FabIdentifier fab = fabResolution.Value;

        if (!ConcurrencyHeaders.TryReadExpectedVersion(
                httpContext.Request, out int expectedVersion, out IResult? precondition))
        {
            return precondition;
        }

        bool changingAddress = !string.IsNullOrWhiteSpace(request.RtspUrl);
        bool changingName = !string.IsNullOrWhiteSpace(request.Name);

        // Exactly one. Each attribute has its own command and its own If-Match
        // check, so a request carrying both would need the second to see a
        // version the first had already advanced — see PatchCameraRequest for
        // why that is not built. Neither is a request that expresses nothing.
        if (changingAddress == changingName)
        {
            return Results.Problem(
                title: "CAMERA_INVALID_REQUEST",
                detail: changingAddress
                    ? "Send either rtspUrl or name, not both: each is applied under its own version."
                    : "Send either rtspUrl or name.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        CameraIdentifier identifier = CameraIdentifier.From(camera);
        OperatorIdentifier actor = ResolveOperator(httpContext);

        if (changingName)
        {
            CameraName name;
            try
            {
                name = CameraName.From(request.Name);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(
                    title: "CAMERA_INVALID_REQUEST",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result<CameraIdentifier, RenameCameraError> renamed = await renameHandler.HandleAsync(
                new RenameCameraCommand(fab, identifier, name, expectedVersion, actor),
                cancellationToken);

            return renamed.Match<IResult>(
                onSuccess: _ => Results.NoContent(),
                onFailure: error => error.ToProblem());
        }

        RtspUrl url;
        try
        {
            url = RtspUrl.From(request.RtspUrl);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "CAMERA_INVALID_REQUEST",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        ChangeCameraAddressCommand command = new(
            fab, identifier, url, expectedVersion, actor);

        Result<CameraIdentifier, ChangeCameraAddressError> result =
            await addressHandler.HandleAsync(command, cancellationToken);

        // 204 rather than 200: the caller asked for the address to be a
        // particular value and it is. The new version travels on the ETag for
        // a caller making a second change without re-reading.
        return result.Match<IResult>(
            onSuccess: _ => Results.NoContent(),
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> Get(
        [FromRoute] Guid camera,
        [FromServices] GetCameraQueryHandler handler,
        [FromServices] IFabAuthorizationGuard fabGuard,
        System.Security.Claims.ClaimsPrincipal user,
        HttpResponse response,
        CancellationToken cancellationToken,
        [FromQuery] string fabId = "")
    {
        // Fab first, before anything else is evaluated (FR-007). Nothing about
        // the camera is looked at until the caller's fabs are known, so a
        // refusal cannot be shaped by whether the camera happens to exist.
        Result<IReadOnlyList<FabIdentifier>, IResult> fabsResolution =
            await ResolveReadFabsAsync(user, fabId, fabGuard, cancellationToken);
        if (fabsResolution.IsFailure)
        {
            return fabsResolution.Error;
        }

        IReadOnlyList<FabIdentifier> fabs = fabsResolution.Value;

        GetCameraQuery query = new(fabs, CameraIdentifier.From(camera));

        Result<CameraDto, GetCameraError> result =
            await handler.HandleAsync(query, cancellationToken);

        return result.Match<IResult>(
            onSuccess: found =>
            {
                // The version the caller must echo back in If-Match (ADR-0113).
                response.Headers.ETag = ConcurrencyHeaders.ETag(found.Version);

                return Results.Ok(found);
            },
            onFailure: error => error.ToProblem());
    }

    private static async Task<IResult> List(
        [FromServices] ListCamerasQueryHandler handler,
        [FromServices] IFabAuthorizationGuard fabGuard,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken,
        [FromQuery] string fabId = "",
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        [FromQuery] int? offset = null,
        [FromQuery] int? limit = null,
        [FromQuery] bool includeRetired = false,
        // Spec 055. Optional, and a fragment matching nothing is an empty page
        // rather than a failure: "no camera is called that" is an answer, not an
        // error, and an operator needs to tell it from a request they got wrong.
        [FromQuery] string? name = null)
    {
        Result<IReadOnlyList<FabIdentifier>, IResult> fabsResolution =
            await ResolveReadFabsAsync(user, fabId, fabGuard, cancellationToken);
        if (fabsResolution.IsFailure)
        {
            return fabsResolution.Error;
        }

        IReadOnlyList<FabIdentifier> fabs = fabsResolution.Value;

        ListCamerasQuery query = new(
            Fabs: fabs,
            Sort: sort ?? ListCamerasDefaults.DefaultSort,
            Order: order ?? ListCamerasDefaults.DefaultOrder,
            Offset: offset ?? ListCamerasDefaults.DefaultOffset,
            Limit: limit ?? ListCamerasDefaults.DefaultLimit,
            IncludeRetired: includeRetired,
            NameFragment: name);

        Result<CameraListPageDto, ListCamerasError> result =
            await handler.HandleAsync(query, cancellationToken);

        return result.Match<IResult>(
            onSuccess: Results.Ok,
            onFailure: error => error.ToProblem());
    }

    private static OperatorIdentifier ResolveOperator(HttpContext httpContext)
    {
        // The JWT 'sub' claim ends up under different names depending on whether
        // System.IdentityModel.Tokens.Jwt remapped it (MapInboundClaims=true ->
        // NameIdentifier URI; false -> raw "sub"). Try the common variants and,
        // as a last resort, the first Guid-valued claim on the principal.
        string? subject =
            httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User.FindFirst("nameid")?.Value;

        if (subject is null)
        {
            string allClaims = string.Join(", ", httpContext.User.Claims.Select(claim => $"{claim.Type}={claim.Value}"));
            throw new InvalidOperationException(
                $"Authenticated principal is missing the subject claim. Available claims: {allClaims}");
        }

        if (!Guid.TryParse(subject, out Guid subjectId))
        {
            throw new InvalidOperationException($"Subject claim is not a valid Guid: {subject}.");
        }

        return OperatorIdentifier.From(subjectId);
    }

    /// <summary>
    /// CameraCatalog's binding of the shared decision table (ADR-0114) to its
    /// own <see cref="FabIdentifier"/>. The table itself lives in
    /// <see cref="FabResolution"/>; this feature adds no resolution mechanism,
    /// it applies the existing one (spec 015).
    /// </summary>
    private static async Task<Result<FabIdentifier, IResult>> ResolveWriteFabAsync(
        System.Security.Claims.ClaimsPrincipal user,
        string fabId,
        IFabAuthorizationGuard fabGuard,
        CancellationToken cancellationToken)
    {
        Result<string, IResult> resolution = await FabResolution.ResolveForWriteAsync(
            user, fabId, fabGuard, "CAMERA_FAB_REQUIRED", cancellationToken);
        if (resolution.IsFailure)
        {
            return Result<FabIdentifier, IResult>.Failure(resolution.Error);
        }

        try
        {
            return Result<FabIdentifier, IResult>.Success(FabIdentifier.From(resolution.Value));
        }
        catch (ArgumentException ex)
        {
            return Result<FabIdentifier, IResult>.Failure(Results.Problem(
                title: "CAMERA_INVALID_REQUEST", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest));
        }
    }

    /// <summary>
    /// The fabs a read may span. Unlike a write, nothing has to be chosen: a
    /// multi-fab caller listing sees all of theirs (FR-005).
    ///
    /// <para>
    /// Parsed per entry rather than all-or-nothing. One group under
    /// <c>/fabs/</c> that is not a usable fab name would otherwise fail the
    /// whole read, hiding every camera in the fabs the caller legitimately
    /// holds. Mirrors RulesEndpoints, where that was a real defect.
    /// </para>
    /// </summary>
    private static async Task<Result<IReadOnlyList<FabIdentifier>, IResult>> ResolveReadFabsAsync(
        System.Security.Claims.ClaimsPrincipal user,
        string fabId,
        IFabAuthorizationGuard fabGuard,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> resolved =
            await FabResolution.ResolveForReadAsync(user, fabId, fabGuard, cancellationToken);

        List<FabIdentifier> fabs = [];
        foreach (string candidate in resolved)
        {
            try
            {
                fabs.Add(FabIdentifier.From(candidate));
            }
            catch (ArgumentException)
            {
                // Skipped, not reported: a caller cannot act on a message about
                // someone else's group configuration, and if nothing is usable
                // the request still fails below.
            }
        }

        if (fabs.Count == 0)
        {
            return Result<IReadOnlyList<FabIdentifier>, IResult>.Failure(Results.Problem(
                title: "CAMERA_FAB_REQUIRED",
                detail: "None of your fab groups is a usable fab name.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        return Result<IReadOnlyList<FabIdentifier>, IResult>.Success(fabs);
    }
}

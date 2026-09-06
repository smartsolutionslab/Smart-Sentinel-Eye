using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSentinelEye.OverlayDesigner.Application.DTOs;
using SmartSentinelEye.OverlayDesigner.Domain.Overlay;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.OverlayDesigner.Api;

/// <summary>
/// Minimal-API endpoint group for OverlayDesigner (ADR-0070), covering
/// the overlay draft/revision lifecycle: create, read, list, publish,
/// archive, branch, edit, revert. The handlers are split across partial
/// files by message kind — <c>OverlayEndpoints.Commands.cs</c> and
/// <c>OverlayEndpoints.Queries.cs</c> — mirroring the Application layout.
/// </summary>
public static partial class OverlayEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string CreateEndpoint = "POST /overlays";

    public static IEndpointRouteBuilder MapOverlayEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/overlays")
            .WithTags("Overlays");

        // 403 is declared on every mapping below because the scope itself produces
        // it: AddScopePolicies builds each sse.* policy as RequireAuthenticatedUser()
        // plus a claim assertion, so a caller who authenticates without
        // sse.overlays.read or .write is forbidden rather than challenged. That is
        // the whole of it here. Unlike the contexts whose declarations cite fab
        // resolution, OverlayDesigner runs no fab guard, so nothing else in this
        // file can answer 403.
        group.MapPost("/", CreateDraft)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("CreateOverlayDraft")
            .WithSummary(
                "Create a new overlay chain, with its first revision in Draft. "
                + "Required scope: sse.overlays.write")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{overlayIdentifier:guid}", GetOne)
            .RequireAuthorization(Scope.Sse.Overlays.Read)
            .WithName("GetOverlay")
            .WithSummary("Read one overlay chain by its identifier. Required scope: sse.overlays.read")
            .Produces<OverlayDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", List)
            .RequireAuthorization(Scope.Sse.Overlays.Read)
            .WithName("ListOverlays")
            .WithSummary("List overlay chains. Required scope: sse.overlays.read")
            .Produces<ListOverlaysResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/publish", Publish)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("PublishOverlayRevision")
            .WithSummary(
                "Publish a Draft revision of an overlay, archiving the previously published one in "
                + "the same unit of work. Required scope: sse.overlays.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/archive", Archive)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("ArchiveOverlayRevision")
            .WithSummary("Archive a revision of an overlay. Required scope: sse.overlays.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{overlayIdentifier:guid}/draft", BranchDraft)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("BranchDraftOverlayRevision")
            .WithSummary(
                "Branch a new Draft revision off the overlay chain's current Published revision. "
                + "Required scope: sse.overlays.write")
            .Produces<int>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/{overlayIdentifier:guid}/revisions/{revisionNumber:int}", EditDraft)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("EditDraftOverlayRevision")
            .WithSummary(
                "Edit a Draft revision's label in place. "
                + "Required scope: sse.overlays.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{overlayIdentifier:guid}/revisions/{revisionNumber:int}/revert", Revert)
            .RequireAuthorization(Scope.Sse.Overlays.Write)
            .WithName("RevertOverlayRevision")
            .WithSummary(
                "Revert a Published overlay revision to Draft. "
                + "Required scope: sse.overlays.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }
}

/// <summary>
/// Envelope shape returned by <c>GET /overlays</c>. Either
/// <see cref="Chains"/> (admin view) or <see cref="Published"/>
/// (binding picker) is populated depending on the state filter; the
/// other is empty.
/// </summary>
public sealed record ListOverlaysResponse(
    IReadOnlyList<OverlayDto> Chains,
    IReadOnlyList<PublishedOverlayDto> Published);

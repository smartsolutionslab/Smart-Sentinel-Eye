using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSentinelEye.LayoutComposition.Application.DTOs;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.ServiceDefaults.Authorization;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Api;

/// <summary>
/// Minimal-API endpoint group for LayoutComposition (ADR-0070), covering
/// the layout draft/revision lifecycle: create, read, list, publish,
/// archive, branch, edit, revert. The handlers are split across partial
/// files by message kind — <c>LayoutEndpoints.Commands.cs</c> and
/// <c>LayoutEndpoints.Queries.cs</c> — mirroring the Application layout.
/// </summary>
public static partial class LayoutEndpoints
{
    /// <summary>Route identity an idempotency key is scoped to (ADR-0142).</summary>
    private const string CreateEndpoint = "POST /layouts";

    public static IEndpointRouteBuilder MapLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        Ensure.That(app).IsNotNull();

        RouteGroupBuilder group = app.MapGroup("/layouts")
            .WithTags("Layouts");

        // 403 became reachable on every endpoint with spec 017: a caller may
        // name a fab they do not hold, or hold none at all. Declaring it keeps
        // the generated OpenAPI from claiming a status that can happen cannot;
        // spec 013 shipped this wrong on one endpoint and it took a review to
        // catch.
        //
        // 404 stays the answer for a layout in another fab (FR-006) — the
        // caller addressed a layout, so "forbidden" would confirm it exists.
        // A 403 here is never about *which* layout was addressed. It is about
        // the fab the caller named, or about the scope: the policy each
        // mapping selects is RequireAuthenticatedUser() plus a claim
        // assertion, so a caller who authenticates without sse.layouts.read or
        // .write is forbidden rather than challenged (spec 085). That second
        // producer has been reachable since spec 008 and went unnamed here.
        group.MapPost("/", CreateDraft)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("CreateLayoutDraft")
            .WithSummary(
                "Create a new layout chain in the resolved fab, with its first revision in Draft. "
                + "Required scope: sse.layouts.write")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{layoutIdentifier:guid}", GetOne)
            .RequireAuthorization(Scope.Sse.Layouts.Read)
            .WithName("GetLayout")
            .WithSummary(
                "Read one layout chain by its identifier, within your fabs. "
                + "Required scope: sse.layouts.read")
            .Produces<LayoutDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", List)
            .RequireAuthorization(Scope.Sse.Layouts.Read)
            .WithName("ListLayouts")
            .WithSummary("List layout chains in your fabs. Required scope: sse.layouts.read")
            .Produces<ListLayoutsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/publish", Publish)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("PublishRevision")
            .WithSummary(
                "Publish a Draft revision of a layout, archiving the previously published one in the "
                + "same unit of work. Required scope: sse.layouts.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/archive", Archive)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("ArchiveRevision")
            .WithSummary("Archive a revision of a layout. Required scope: sse.layouts.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{layoutIdentifier:guid}/draft", BranchDraft)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("BranchDraftRevision")
            .WithSummary(
                "Branch a new Draft revision off the layout chain's current Published revision. "
                + "Required scope: sse.layouts.write")
            .Produces<int>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/{layoutIdentifier:guid}/revisions/{revisionNumber:int}", EditDraft)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("EditDraftRevision")
            .WithSummary(
                "Replace a Draft revision's grid and tile set in place. "
                + "Required scope: sse.layouts.write")
            .Produces<int>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{layoutIdentifier:guid}/revisions/{revisionNumber:int}/revert", Revert)
            .RequireAuthorization(Scope.Sse.Layouts.Write)
            .WithName("RevertRevision")
            .WithSummary(
                "Revert a Published layout revision to Draft. "
                + "Required scope: sse.layouts.write")
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
/// Envelope shape returned by <c>GET /layouts</c>. Either
/// <see cref="Chains"/> (admin view) or <see cref="Published"/>
/// (kiosk picker) is populated depending on the state filter; the
/// other is empty.
/// </summary>
public sealed record ListLayoutsResponse(
    IReadOnlyList<LayoutDto> Chains,
    IReadOnlyList<PublishedLayoutDto> Published);

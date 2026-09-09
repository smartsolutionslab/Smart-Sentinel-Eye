using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Application.Auth;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Application.Commands.Handlers;

public sealed class AuthorizeWhepCommandHandler(
    IWhepAuthValidator whepAuth,
    IStreamRepository streams,
    ILogger<AuthorizeWhepCommandHandler> logger)
    : ICommandHandler<AuthorizeWhepCommand, Result<MediaMtxPath, AuthorizeWhepError>>
{
    /// <summary>
    /// Watching a stream is a read, so this gate asks for the read scope —
    /// still accepting the grandfathered management bundle, which is the rule
    /// <c>RequireScopeExtensions</c> applies to every other endpoint through its
    /// authorization policy. The two strings are repeated rather than referenced
    /// because Application stays ASP.NET-free (ADR-0051).
    ///
    /// <para>
    /// Spec 041: this asked for the management bundle <em>alone</em>, so no
    /// kiosk could pass it — not the browser kiosk, and not an enrolled device,
    /// whose bundle has never carried it. Constitution §VIII says a kiosk holds
    /// view-only scopes, and watching video is the view.
    /// </para>
    /// </summary>
    private const string RequiredScope = "sse.streams.read";

    private const string LegacyManagementBundle = "sse.management";

    public async Task<Result<MediaMtxPath, AuthorizeWhepError>> HandleAsync(
        AuthorizeWhepCommand command,
        CancellationToken cancellationToken)
    {
        Ensure.That(command).IsNotNull();

        (MediaMtxPath? path, string? bearerToken, Option<MediaMtxAction> action, Option<ReportedMediaMtxAction> reportedAction) = command;

        // The action is answered before the token, and that order is the point:
        // an absent or unpermitted action is refused whatever the caller holds,
        // so answering the token first would hand a publish a 401 — the status
        // that invites the client back with credentials, when no credential can
        // make this request acceptable.
        if (!action.HasValue)
        {
            RefuseUnknownAction(reportedAction, path);
            return Failure(AuthorizeWhepFailures.ActionUnknown());
        }

        // Nothing in this product publishes through this hook: every path is fed
        // by MediaMTX pulling the camera's RTSP source. Until now the only thing
        // refusing a publisher was MediaMTX itself declining them on a path with
        // a static `source` (MediaMtxRtspGateway.cs:32) — another component's
        // configuration file rather than this code, and one nobody here would
        // notice losing.
        if (action.Value == MediaMtxAction.Publish)
        {
            logger.RefusedWhepAction(action.Value, path);
            return Failure(AuthorizeWhepFailures.ActionNotPermitted());
        }

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return Failure(AuthorizeWhepFailures.Unauthorized());
        }

        Option<WhepAuthSubject> subject = await whepAuth.ValidateAsync(bearerToken, cancellationToken);

        if (!subject.HasValue)
        {
            return Failure(AuthorizeWhepFailures.Unauthorized());
        }

        if (!subject.Value.Scopes.Contains(RequiredScope, StringComparer.Ordinal)
            && !subject.Value.Scopes.Contains(LegacyManagementBundle, StringComparer.Ordinal))
        {
            return Failure(AuthorizeWhepFailures.Forbidden());
        }

        Option<Stream> stream = await streams.GetByPathAsync(path, cancellationToken);

        if (stream.HasValue && stream.Value.State == StreamState.Offline)
        {
            return Failure(AuthorizeWhepFailures.StreamUnavailable());
        }

        return Success(path);
    }

    /// <summary>
    /// The refusal is identical either way — <c>WHEP_ACTION_UNKNOWN</c>, 403,
    /// same detail back to MediaMTX. Only the diagnosis differs, and it has to:
    /// a field that moved and a field that was renamed are two different things
    /// to go and read the release notes about, and spec 074's single hedged
    /// message told an operator neither (spec 115, issue #2105).
    ///
    /// <para>
    /// What arrived is a log field and never part of the answer. Echoing it back
    /// to the sender would reflect input across a trust boundary for no
    /// diagnostic gain — the operator reads it here.
    /// </para>
    /// </summary>
    private void RefuseUnknownAction(Option<ReportedMediaMtxAction> reportedAction, MediaMtxPath path)
    {
        if (reportedAction.HasValue)
        {
            logger.RefusedUnrecognisedWhepAction(reportedAction.Value, path);
        }
        else
        {
            logger.RefusedAbsentWhepAction(path);
        }
    }
}

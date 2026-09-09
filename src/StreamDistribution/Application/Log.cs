using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Application;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Stream already exists for camera {Camera}; skipping provision (idempotent).")]
    public static partial void StreamAlreadyExists(this ILogger logger, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaMTX path registration failed for camera {Camera}.")]
    public static partial void PathRegistrationFailed(this ILogger logger, Exception exception, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioned stream {Stream} for camera {Camera} at path {Path}.")]
    public static partial void ProvisionedStream(this ILogger logger, StreamIdentifier stream, CameraIdentifier camera, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected health transition for camera {Camera}.")]
    public static partial void RejectedHealthTransition(this ILogger logger, Exception exception, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provision attempt failed for camera {Camera}: {Code} {Message}.")]
    public static partial void ProvisionAttemptFailed(this ILogger logger, CameraIdentifier camera, string code, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera {Camera} address changed but has no provisioned stream; nothing to re-point.")]
    public static partial void NoStreamToRepoint(this ILogger logger, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera {Camera} address changed but its stream is retired; not re-pointed.")]
    public static partial void SkippedRepointOfRetiredStream(this ILogger logger, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaMTX path re-point failed for camera {Camera}; the stream already holds the new address.")]
    public static partial void PathRepointFailed(this ILogger logger, Exception exception, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-pointed stream {Stream} for camera {Camera} at path {Path}.")]
    public static partial void RepointedStream(this ILogger logger, StreamIdentifier stream, CameraIdentifier camera, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-point attempt failed for camera {Camera}: {Code} {Message}.")]
    public static partial void RepointAttemptFailed(this ILogger logger, CameraIdentifier camera, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retire attempt failed for camera {Camera}: {Code} {Message}.")]
    public static partial void RetireAttemptFailed(this ILogger logger, CameraIdentifier camera, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera {Camera} was registered without a fab; no stream provisioned (spec 016 FR-004).")]
    public static partial void CameraRegisteredWithoutFab(this ILogger logger, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera {Camera} was retired but has no provisioned stream; nothing to retire.")]
    public static partial void NoStreamToRetire(this ILogger logger, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MediaMTX path removal failed for camera {Camera}; the stream is already retired.")]
    public static partial void PathRemovalFailed(this ILogger logger, Exception exception, CameraIdentifier camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retired stream {Stream} for camera {Camera} and removed path {Path}.")]
    public static partial void RetiredStream(this ILogger logger, StreamIdentifier stream, CameraIdentifier camera, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream {Stream} provisioned for camera {Camera} at path {Path} by {Operator}.")]
    public static partial void StreamProvisioned(this ILogger logger, StreamIdentifier stream, CameraIdentifier camera, MediaMtxPath path, OperatorIdentifier @operator);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a WHEP {Action} on path {Path}: nothing in this product publishes through the external-auth hook, so no token admits this action.")]
    public static partial void RefusedWhepAction(this ILogger logger, MediaMtxAction action, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a WHEP request naming action {ReportedAction} on path {Path}: this build recognises only read, publish and playback. Check for MediaMTX version skew first — the broker image is pinned, so compare that pin against the release that renamed or replaced this action; every viewer stays refused closed until this build learns it.")]
    public static partial void RefusedUnrecognisedWhepAction(this ILogger logger, ReportedMediaMtxAction reportedAction, MediaMtxPath path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a WHEP request on path {Path}: MediaMTX sent no action field at all — the field was absent, not merely unfamiliar. Check for MediaMTX version skew first — the broker image is pinned, so compare that pin against the release that moved or dropped the field; every viewer stays refused closed until this build reads it from wherever it went.")]
    public static partial void RefusedAbsentWhepAction(this ILogger logger, MediaMtxPath path);
}

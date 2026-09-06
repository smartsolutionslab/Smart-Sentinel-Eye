using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace SmartSentinelEye.ScenarioSimulator;

/// <summary>
/// Source-generated log methods for the Scenario Simulator (ADR-0050).
/// </summary>
[ExcludeFromCodeCoverage] // source-generated logging glue, not business logic
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Seeding scenario '{Scenario}' with {AssetCount} asset(s).")]
    public static partial void SeedingScenario(this ILogger logger, string scenario, int assetCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scenario '{Scenario}' seeded.")]
    public static partial void ScenarioSeeded(this ILogger logger, string scenario);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Asset '{Scenario}'/'{Asset}' could not be seeded: {Error}. Its tile will be missing; the rest of the run continues.")]
    public static partial void AssetSeedFailed(this ILogger logger, string scenario, string asset, string error);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Wall seeding failed: {Error}. Walls may be stale or missing; the worker stays up.")]
    public static partial void WallSeedFailed(this ILogger logger, string error);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Seeding finished INCOMPLETE: {FailedCount} step(s) failed. The stack is running but the simulated plants are only partly set up.")]
    public static partial void SeedingIncomplete(this ILogger logger, int failedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active scenario '{Scenario}' not found in configuration; nothing to seed.")]
    public static partial void ScenarioNotFound(this ILogger logger, string scenario);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered camera '{Name}' -> {RtspUrl}.")]
    public static partial void CameraRegistered(this ILogger logger, string name, string rtspUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera '{Name}' already registered; skipping (idempotent).")]
    public static partial void CameraAlreadyRegistered(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read back camera '{Name}' ({Reason}); it will not be correlated to a wall tile.")]
    public static partial void CameraReadBackFailed(this ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioned camera-sim loop path '{Path}' playing '{Clip}'.")]
    public static partial void CameraSimPathProvisioned(this ILogger logger, string path, string clip);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replaced camera-sim path '{Path}'; it now plays '{Clip}'.")]
    public static partial void CameraSimPathReplaced(this ILogger logger, string path, string clip);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "camera-sim path '{Path}' now carries the label '{Label}' for camera {Camera}. "
            + "The label and hue belong to the path: two cameras registered at the same simulator URL "
            + "share it, and the later one wins.")]
    public static partial void CameraSimPathLabelled(this ILogger logger, string path, string label, Guid camera);

    [LoggerMessage(Level = LogLevel.Error, Message = "Asset '{Asset}' names clip '{Clip}', which is not in the clips directory. Add it (scripts/generate-sim-clips.sh) or correct the scenario file.")]
    public static partial void ClipMissing(this ILogger logger, string asset, string clip);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled camera-sim loop paths for {ReconciledCount} of {AssetCount} asset(s).")]
    public static partial void CameraSimReconciled(this ILogger logger, int reconciledCount, int assetCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reconcile camera-sim loop path '{Path}': {Error}.")]
    public static partial void CameraSimReconcileFailed(this ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera URL {Url} has no path component; not a simulated camera, skipping.")]
    public static partial void SkippedNonSimulatedCamera(this ILogger logger, string url);

    // --- M2 seeding (Phase A overlays / Phase B rules / Phase D wall) ---

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded overlay '{Name}' -> {Overlay}.")]
    public static partial void OverlaySeeded(this ILogger logger, string name, Guid overlay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlay '{Name}' already exists and needs no publishing; reusing {Overlay} (idempotent).")]
    public static partial void OverlayAlreadyExists(this ILogger logger, string name, Guid overlay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlay '{Name}' ({Overlay}) was left in Draft; published it.")]
    public static partial void OverlayDraftPublished(this ILogger logger, string name, Guid overlay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Overlay '{Name}' ({Overlay}) came back with no revisions; cannot tell whether it still needs publishing.")]
    public static partial void OverlayRevisionsMissing(this ILogger logger, string name, Guid overlay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded rule '{Name}' -> overlay {Overlay}.")]
    public static partial void RuleSeeded(this ILogger logger, string name, Guid overlay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rule '{Name}' already exists; skipping (idempotent).")]
    public static partial void RuleAlreadyExists(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded wall layout '{Name}' ({Rows}x{Cols}) -> {Layout}.")]
    public static partial void WallSeeded(this ILogger logger, string name, int rows, int cols, Guid layout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wall layout '{Name}' already exists and needs no publishing; skipping (idempotent).")]
    public static partial void WallAlreadyExists(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wall layout '{Name}' ({Layout}) had different tiles; re-tiled and published revision {Revision}.")]
    public static partial void WallRetiled(this ILogger logger, string name, Guid layout, int revision);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wall layout '{Name}' ({Layout}) was left in Draft; published it.")]
    public static partial void WallDraftPublished(this ILogger logger, string name, Guid layout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wall layout '{Name}' ({Layout}) came back with no revisions; cannot tell whether it still needs publishing.")]
    public static partial void WallRevisionsMissing(this ILogger logger, string name, Guid layout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded camera {Camera} for asset '{Asset}'.")]
    public static partial void AssetCameraRecorded(this ILogger logger, string asset, Guid camera);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wall not yet complete: {Ready}/{Total} asset(s) have both overlay and camera.")]
    public static partial void WallNotYetComplete(this ILogger logger, int ready, int total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asset '{Asset}' is missing its {Field}; cannot seed it.")]
    public static partial void AssetMissingField(this ILogger logger, string asset, string field);

    // --- M2 billet timeline + MQTT publisher ---

    [LoggerMessage(Level = LogLevel.Information, Message = "Billet run started: {Stations} station(s), dwell {DwellMs}ms, tick {TickMs}ms.")]
    public static partial void BilletRunStarted(this ILogger logger, int stations, int dwellMs, int tickMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Billet entered station '{Asset}' (device '{Device}').")]
    public static partial void BilletEnteredStation(this ILogger logger, string asset, string device);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Emitted {Topic} = {Value} {Unit} (kind '{Kind}').")]
    public static partial void BilletSampleEmitted(this ILogger logger, string topic, double value, string unit, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Billet run complete; looping after {LoopGapMs}ms.")]
    public static partial void BilletRunComplete(this ILogger logger, int loopGapMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT publisher connected to '{Host}' as '{Username}'.")]
    public static partial void MqttPublisherConnected(this ILogger logger, string host, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT publisher disconnected from '{Host}'; reconnecting.")]
    public static partial void MqttPublisherDisconnected(this ILogger logger, string host);

    // Replaces MqttPublishFailed, whose only caller passed the topic "(connect)".
    // A connect failure is not a publish failure, and a publish failure has no
    // line at all by design: samples the broker did not take are counted and
    // reported once per outage, because the timeline emits many a second.
    [LoggerMessage(Level = LogLevel.Error, Message = "MQTT publisher could not connect to '{Host}': {Error}.")]
    public static partial void MqttPublisherConnectFailed(this ILogger logger, string host, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not refresh the MQTT token before reconnect: {Error}.")]
    public static partial void MqttPublisherTokenFailed(this ILogger logger, string error);

    // Without this the publisher's backoff is invisible: a wall that has stopped
    // animating looks the same whether the loop is waiting or has stopped.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "MQTT publisher retrying in {DelaySeconds:F1}s (attempt {Attempt}).")]
    public static partial void MqttPublisherRetryScheduled(this ILogger logger, double delaySeconds, int attempt);

    // One line per outage, not one per drop. The timeline emits many samples a
    // second, so per-sample logging would bury the summary it is meant to be.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} sample(s) dropped while the broker was away ({Seconds:F1}s). "
            + "The gap in the timeline is the outage; nothing was buffered, because a replayed "
            + "sample carries a stale occurredAt at a live wall.")]
    public static partial void MqttSamplesDropped(this ILogger logger, long count, double seconds);
}

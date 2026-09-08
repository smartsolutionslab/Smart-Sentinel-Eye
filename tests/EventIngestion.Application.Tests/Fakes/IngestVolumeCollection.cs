namespace SmartSentinelEye.EventIngestion.Application.Tests.Fakes;

/// <summary>
/// Serialises every class that listens to <c>SmartSentinelEye.IngestVolume</c>.
/// <c>Meter</c> is process-wide and xUnit parallelises classes within an
/// assembly, so without this a listener also collects what another class is
/// emitting at the same moment — the flake that made
/// <c>EventToOverlayLatencyTests</c> fail roughly one run in six. A tag filter
/// cannot separate these three: all of them emit <c>source="manual"</c>.
/// </summary>
[CollectionDefinition(Name)]
#pragma warning disable CA1711 // xUnit convention requires the "Collection" suffix
public class IngestVolumeCollection
#pragma warning restore CA1711
{
    public const string Name = "ingest-volume";

    // xUnit reads the attribute off this type; nothing constructs it.
    protected IngestVolumeCollection()
    {
    }
}

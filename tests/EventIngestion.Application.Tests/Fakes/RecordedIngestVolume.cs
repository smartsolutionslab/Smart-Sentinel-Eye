using System.Diagnostics.Metrics;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Domain.Event;

namespace SmartSentinelEye.EventIngestion.Application.Tests.Fakes;

/// <summary>
/// Collects what <see cref="IngestVolume"/> actually recorded, by subscribing
/// to the meter the implementation writes to rather than to a mock. The thing
/// under test is that a measurement reaches the meter — or does not — and only
/// a listener can tell those apart.
///
/// <para>
/// Filtered on <b>both</b> the meter name and the instrument name, so nothing
/// another instrument emits can leak in. That is necessary but not sufficient:
/// <c>Meter</c> is process-wide and every test class here emits
/// <c>source="manual"</c> at some point, so a tag filter cannot separate them
/// the way <c>EventToOverlayLatencyTests</c> separates segments. The classes
/// share <see cref="IngestVolumeCollection"/> instead, which serialises them
/// against each other and leaves the rest of the assembly parallel.
/// </para>
/// </summary>
public sealed class RecordedIngestVolume : IDisposable
{
    private readonly List<IngestMeasurement> measurements = [];
    private readonly MeterListener listener;

    public RecordedIngestVolume()
    {
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, active) =>
            {
                if (instrument.Meter.Name == IngestVolume.MeterName
                    && instrument.Name == IngestVolume.MetricName)
                {
                    active.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            Dictionary<string, object?> captured = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                captured[tag.Key] = tag.Value;
            }

            measurements.Add(new IngestMeasurement(measurement, captured));
        });

        listener.Start();
    }

    /// <summary>Every measurement the instrument emitted, in order.</summary>
    public IReadOnlyList<IngestMeasurement> Measurements => measurements;

    /// <summary>The measurements tagged with one source.</summary>
    public IReadOnlyList<IngestMeasurement> For(Source source) =>
        [.. measurements.Where(measurement => measurement.SourceTag == source.Value)];

    /// <summary>How many events were counted against one source in total.</summary>
    public long TotalFor(Source source) => For(source).Sum(measurement => measurement.Count);

    public void Dispose() => listener.Dispose();
}

/// <summary>One recorded measurement and the tags it carried.</summary>
public sealed record IngestMeasurement(long Count, IReadOnlyDictionary<string, object?> Tags)
{
    /// <summary>
    /// The value of the <c>source</c> tag, or null when the measurement did not
    /// carry one — which is itself a failure worth reading in an assertion.
    /// </summary>
    public string? SourceTag =>
        Tags.TryGetValue(IngestVolume.SourceTag, out object? value) ? value?.ToString() : null;
}

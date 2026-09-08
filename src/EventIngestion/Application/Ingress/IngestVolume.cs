using System.Diagnostics;
using System.Diagnostics.Metrics;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Application.Ingress;

/// <summary>
/// Counts events accepted <b>into the ingestion store</b>, broken down by the
/// source that sent them (spec 006 US5, issues #619 / #622).
///
/// <para>
/// <b>Deliberately not called <c>ChannelMetrics</c></b>, the name spec 006's
/// plan used. That name was written while all three ingress paths went through
/// the bounded channel; <see cref="IIngestChannel"/> records that this stopped
/// being true at spec 020 — <em>"the HTTP write paths no longer use this
/// channel at all"</em>. A <c>ChannelMetrics</c> counting manual and webhook
/// arrivals would be named after a component it does not observe, which is the
/// mislabelling <c>WallSkew</c> exists to warn about.
/// </para>
///
/// <para>
/// <b>Not a request counter.</b> ASP.NET Core's own
/// <c>http.server.request.duration</c> already counts requests; duplicating it
/// here would make a rejected flood read as ingestion volume. A measurement is
/// recorded only once the store has committed.
/// </para>
/// </summary>
public static class IngestVolume
{
    /// <summary>
    /// Registered by the context's own <c>AddEventIngestionInfrastructure</c>.
    /// A meter nobody registers records into nothing and raises no error.
    /// </summary>
    public const string MeterName = "SmartSentinelEye.IngestVolume";

    /// <summary>
    /// The instrument's name. Matches the <c>sse.</c>-prefixed family
    /// (<c>sse.latency.segment.duration</c>, <c>sse.wall.skew</c>,
    /// <c>sse.overlay.label_delay</c>) and claims the first name in the
    /// <c>sse.ingest.*</c> space.
    /// </summary>
    public const string MetricName = "sse.ingest.events";

    /// <summary>
    /// The only dimension (FR-002/FR-003). Its values are
    /// <c>plc | inference | manual | webhook</c> — the wire tokens
    /// <see cref="Source"/> already carries, rather than a parallel vocabulary.
    /// </summary>
    public const string SourceTag = "source";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Ingested = Meter.CreateCounter<long>(
        name: MetricName,
        unit: "{event}",
        description: "Events accepted into the ingestion store, by the source that sent them.");

    /// <summary>Records one accepted event against its source.</summary>
    public static void Record(Source source) => Record(source, 1);

    /// <summary>
    /// Records <paramref name="count"/> accepted events against one source, so
    /// a batch is one measurement carrying N rather than N measurements
    /// (FR-005).
    /// </summary>
    public static void Record(Source source, long count)
    {
        Ensure.That(source).IsNotNull();

        // A count of zero is not an arrival of zero events — it would publish a
        // series claiming the source is alive and idle — and a negative one can
        // only mean the caller computed it wrongly. Dropped rather than thrown,
        // following WallSkew and LabelDelay: a broken caller shows as missing
        // data instead of as a counter that went backwards.
        if (count <= 0)
        {
            return;
        }

        Ingested.Add(count, new TagList { { SourceTag, source.Value } });
    }
}

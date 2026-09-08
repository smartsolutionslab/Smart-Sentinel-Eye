using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Application.Tests.Fakes;
using SmartSentinelEye.EventIngestion.Domain.Event;

namespace SmartSentinelEye.EventIngestion.Application.Tests.Ingress;

/// <summary>
/// Spec 103 FR-001/FR-002/FR-005/FR-007 (issues #619, #622). The instrument
/// itself: does a recorded ingest reach the meter, under which tag, and in how
/// many measurements.
/// </summary>
[Collection(IngestVolumeCollection.Name)]
public class IngestVolumeTests
{
    /// <summary>
    /// FR-001. The one-argument form is what the single-event funnel calls, and
    /// one accepted event is one event.
    /// </summary>
    [Fact]
    public void A_recorded_ingest_counts_one_against_its_source()
    {
        using RecordedIngestVolume recorded = new();

        IngestVolume.Record(Source.Manual);

        IngestMeasurement measurement = recorded.Measurements.ShouldHaveSingleItem();
        measurement.Count.ShouldBe(1);
        measurement.SourceTag.ShouldBe("manual");
    }

    /// <summary>
    /// FR-002/FR-003. Four sources are four tag values on one instrument — MQTT
    /// is not collapsed into a single bucket, and the tag reuses the wire token
    /// <see cref="Source"/> already carries rather than inventing a parallel
    /// vocabulary.
    /// </summary>
    [Theory]
    [InlineData("plc")]
    [InlineData("inference")]
    [InlineData("manual")]
    [InlineData("webhook")]
    public void Each_source_is_counted_under_its_own_tag(string token)
    {
        using RecordedIngestVolume recorded = new();

        IngestVolume.Record(Source.From(token));

        IngestMeasurement measurement = recorded.Measurements.ShouldHaveSingleItem();
        measurement.Tags.Keys.ShouldBe([IngestVolume.SourceTag]);
        measurement.SourceTag.ShouldBe(token);
    }

    /// <summary>
    /// FR-005. A drained batch is <b>one</b> measurement carrying N, not N
    /// measurements carrying one. The distinction is invisible in a summed
    /// dashboard and very visible in the exporter's cost.
    /// </summary>
    [Fact]
    public void A_batch_records_one_measurement_carrying_its_whole_count()
    {
        using RecordedIngestVolume recorded = new();

        IngestVolume.Record(Source.Plc, 200);

        recorded.Measurements.ShouldHaveSingleItem().Count.ShouldBe(200);
    }

    /// <summary>
    /// FR-007. A batch that stored nothing has nothing to report. Recording a
    /// zero would publish a time series that says "this source is alive and
    /// receiving no events", which is a different claim.
    /// </summary>
    [Fact]
    public void A_count_of_zero_records_nothing()
    {
        using RecordedIngestVolume recorded = new();

        IngestVolume.Record(Source.Manual, 0);

        recorded.Measurements.ShouldBeEmpty("nothing was stored, so nothing arrived");
    }

    /// <summary>
    /// FR-007. Negative is impossible from a count of stored rows, so it can
    /// only mean a caller computed it wrongly. Dropped rather than thrown,
    /// following <c>WallSkew</c> and <c>LabelDelay</c> — a broken caller shows
    /// as missing data instead of as a counter that went backwards.
    /// </summary>
    [Fact]
    public void A_negative_count_records_nothing()
    {
        using RecordedIngestVolume recorded = new();

        IngestVolume.Record(Source.Manual, -3);

        recorded.Measurements.ShouldBeEmpty("a monotonic counter cannot go backwards");
    }
}

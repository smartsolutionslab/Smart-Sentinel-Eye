using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.StreamDistribution.Domain.Stream;

namespace SmartSentinelEye.StreamDistribution.Domain.Tests.Stream;

/// <summary>
/// Spec 115. The counterpart to <see cref="MediaMtxActionTests"/>: that one is
/// the vocabulary this product decides on, this one is the evidence the refusal
/// quotes.
/// </summary>
public class ReportedMediaMtxActionTests
{
    [Fact]
    public void TryFrom_returns_None_when_MediaMTX_sent_no_action_field()
    {
        ReportedMediaMtxAction.TryFrom(null).HasValue.ShouldBeFalse();
    }

    [Fact]
    public void TryFrom_keeps_the_text_that_arrived()
    {
        Option<ReportedMediaMtxAction> reported = ReportedMediaMtxAction.TryFrom("stream");

        reported.HasValue.ShouldBeTrue();
        reported.Value.Value.ShouldBe("stream");
    }

    /// <summary>
    /// An empty <c>action</c> is a value that arrived. Collapsing it into the
    /// absent case would re-create exactly the ambiguity this type exists to
    /// remove — unlike <c>MediaMtxAction.TryFrom</c>, which rightly refuses it.
    /// </summary>
    [Fact]
    public void An_empty_action_field_is_a_value_that_arrived_not_an_absent_one()
    {
        ReportedMediaMtxAction.TryFrom("").HasValue.ShouldBeTrue();
        ReportedMediaMtxAction.TryFrom("   ").HasValue.ShouldBeTrue();
    }

    [Fact]
    public void From_caps_a_long_value_and_marks_it_truncated()
    {
        ReportedMediaMtxAction reported = ReportedMediaMtxAction.From(new string('a', 200));

        reported.Value.Length.ShouldBe(ReportedMediaMtxAction.MaximumLength + 1);
        reported.Value.ShouldBe(new string('a', ReportedMediaMtxAction.MaximumLength) + "…");
    }

    [Fact]
    public void From_leaves_a_value_at_the_cap_unmarked()
    {
        ReportedMediaMtxAction reported =
            ReportedMediaMtxAction.From(new string('a', ReportedMediaMtxAction.MaximumLength));

        reported.Value.ShouldBe(new string('a', ReportedMediaMtxAction.MaximumLength));
    }

    /// <summary>
    /// A <c>\r\n</c> that survived would forge a second line in any text sink,
    /// which is a log-injection finding wherever the value came from.
    /// </summary>
    [Fact]
    public void From_neutralises_control_characters_so_a_value_cannot_forge_a_log_line()
    {
        ReportedMediaMtxAction reported = ReportedMediaMtxAction.From("read\r\nwarn: authorized");

        reported.Value.ShouldNotContain("\n");
        reported.Value.ShouldNotContain("\r");
        reported.Value.ShouldBe("read��warn: authorized");
    }

    [Fact]
    public void ToString_returns_the_bounded_value()
    {
        ReportedMediaMtxAction.From("stream").ToString().ShouldBe("stream");
    }
}

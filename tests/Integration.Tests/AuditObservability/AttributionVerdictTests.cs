namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Spec 053 US2 / SC-003 — "we could not tell" has to be a result the run
/// produces, not a sentence somebody remembers to write.
///
/// <para>
/// <b>These need no stack.</b> The decision is pure arithmetic over a measured
/// skew, and keeping it that way is deliberate: the failure path is the one
/// that matters here, and a failure path reachable only by finding a genuinely
/// skewed machine would never be exercised.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class AttributionVerdictTests
{
    private static RelativeSkew Skew(double milliseconds, double residualMilliseconds) =>
        new(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(residualMilliseconds));

    [Fact]
    public void Clocks_within_the_threshold_leave_the_attribution_standing()
    {
        AttributionVerdict verdict = AttributionVerdict.For(Skew(2, 1));

        verdict.IsEstablished.ShouldBeTrue();
        verdict.Standing.ShouldBe(AttributionStanding.Established);
    }

    /// <summary>
    /// The case the whole story exists for. An attribution over clocks this far
    /// apart is a confident, specific, wrong answer — and it would be used to
    /// move a requirement.
    /// </summary>
    [Fact]
    public void Clocks_beyond_the_threshold_leave_it_not_established()
    {
        AttributionVerdict verdict = AttributionVerdict.For(Skew(40, 1));

        verdict.IsEstablished.ShouldBeFalse();
        verdict.Standing.ShouldBe(AttributionStanding.NotEstablished);
        verdict.Reason.ShouldContain("describes the clocks as much as the pipeline");
    }

    /// <summary>
    /// **The mutation that produces a plausible wrong answer rather than an
    /// obviously broken one**, and the reason the decision is taken on the worst
    /// case rather than the measured skew.
    ///
    /// <para>
    /// Nine milliseconds is comfortably inside a ten-millisecond threshold. A
    /// residual of forty swallows the threshold whole — the clocks could be
    /// anywhere within forty-nine milliseconds of each other, which is more than
    /// the entire gap being investigated. Deciding on the skew alone reports this
    /// as established.
    /// </para>
    /// </summary>
    [Fact]
    public void A_small_skew_with_a_large_residual_is_not_established()
    {
        AttributionVerdict verdict = AttributionVerdict.For(Skew(9, 40));

        verdict.IsEstablished.ShouldBeFalse(
            "a measurement that could be wrong by 40 ms cannot establish anything to within 10 ms, "
            + "however small the number it happened to produce");
    }

    /// <summary>
    /// Direction must not matter. A consumer clock behind the publisher's is
    /// exactly as damaging as one ahead, and an absolute-value slip here would
    /// pass every test above.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(-40)]
    public void The_direction_of_the_disagreement_does_not_matter(double milliseconds)
    {
        AttributionVerdict.For(Skew(milliseconds, 1)).IsEstablished.ShouldBeFalse();
    }

    /// <summary>
    /// Exactly at the threshold stands. Stated so the boundary is a decision
    /// rather than an accident of which comparison operator was typed.
    /// </summary>
    [Fact]
    public void Exactly_at_the_threshold_the_attribution_stands()
    {
        AttributionVerdict.For(Skew(10, 0)).IsEstablished.ShouldBeTrue();
    }

    /// <summary>
    /// Residuals add across two independent readings. Taking the larger, or an
    /// average, would understate the uncertainty and make a skew look better
    /// established than it is.
    /// </summary>
    [Fact]
    public void Two_readings_uncertainties_add_rather_than_merge()
    {
        ClockOffset publisher = new(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(4));
        ClockOffset consumer = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));

        RelativeSkew skew = RelativeSkew.Between(publisher, consumer);

        skew.Skew.TotalMilliseconds.ShouldBe(2, 0.001);
        skew.Residual.TotalMilliseconds.ShouldBe(9, 0.001, "4 ms and 5 ms of independent uncertainty add");
    }

    /// <summary>
    /// The reported reason carries the numbers. A verdict whose explanation says
    /// only "clocks disagree" is one nobody can check.
    /// </summary>
    [Fact]
    public void The_reason_carries_the_figures_it_was_decided_on()
    {
        AttributionVerdict verdict = AttributionVerdict.For(Skew(40, 2));

        verdict.Reason.ShouldContain("42");
        verdict.ToString().ShouldStartWith("NOT ESTABLISHED");
    }
}

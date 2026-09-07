namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Spec 053 US1 — the arithmetic of the breakdown, tested without a stack.
///
/// <para>
/// <b>The dangerous output here is a breakdown that looks complete and is not.</b>
/// These fix the two properties that stop it: a remainder that is computed
/// rather than supplied, and a requirement span reported as a range rather than
/// as a single confident figure it cannot support.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class IngestAttributionTests
{
    private static IngestAttribution Attribution(
        double total, double beforeHandler, double inHandler, double write,
        int rows = 1_000, int missing = 0, double perRowResidual = 0) =>
        new(total, beforeHandler, inHandler, write, rows, missing, perRowResidual);

    /// <summary>
    /// **The distinction this type exists to keep, and which an earlier version
    /// of the measurement run collapsed.**
    ///
    /// <para>
    /// Medians do not add. A perfectly sound apparatus — every row's parts
    /// summing exactly to that row's span — can still print parts that do not sum
    /// to the printed total, because the median of a sum is not the sum of
    /// medians. Blaming the stamps for that would condemn a working instrument.
    /// </para>
    /// </summary>
    [Fact]
    public void A_median_gap_is_not_an_apparatus_fault()
    {
        IngestAttribution attribution =
            Attribution(total: 85, beforeHandler: 20, inHandler: 5, write: 3, perRowResidual: 0);

        attribution.UnattributedMs.ShouldBe(60, 0.001, "the printed figures do not reconcile");
        attribution.PartsCoverEveryRow.ShouldBeTrue("yet every row's parts cover its own span exactly");
    }

    /// <summary>
    /// The other direction, which is the one worth failing over: the printed
    /// figures reconcile perfectly and the stamps are still wrong.
    /// </summary>
    [Fact]
    public void Figures_that_reconcile_can_still_rest_on_broken_stamps()
    {
        IngestAttribution attribution =
            Attribution(total: 80, beforeHandler: 60, inHandler: 20, write: 5, perRowResidual: 4.2);

        attribution.UnattributedMs.ShouldBe(0, 0.001, "the printed figures reconcile exactly");
        attribution.PartsCoverEveryRow.ShouldBeFalse("but the rows behind them do not");
    }

    [Fact]
    public void A_sound_apparatus_leaves_no_per_row_residual()
    {
        Attribution(80, 60, 20, 5).PartsCoverEveryRow.ShouldBeTrue();
    }

    /// <summary>
    /// Sign-independent: a residual in either direction is a disagreement. Only
    /// its magnitude matters, and a stamp landing *after* the one that should
    /// follow it produces the negative case.
    /// </summary>
    [Theory]
    [InlineData(4.2)]
    [InlineData(-4.2)]
    public void A_residual_in_either_direction_is_a_disagreement(double residual)
    {
        Attribution(80, 60, 20, 5, perRowResidual: residual).PartsCoverEveryRow.ShouldBeFalse();
    }

    [Fact]
    public void A_span_its_parts_cover_leaves_nothing_over()
    {
        IngestAttribution attribution = Attribution(total: 80, beforeHandler: 60, inHandler: 20, write: 5);

        attribution.UnattributedMs.ShouldBe(0, 0.001);
        attribution.AttributedFraction.ShouldBe(1, 0.001);
    }

    /// <summary>
    /// **The mutation this exists for.** A breakdown whose parts silently absorb
    /// a difference reads as a complete account of the span. Computing the
    /// remainder rather than accepting one means it cannot be set to zero by
    /// anybody who would rather it were.
    ///
    /// <para>
    /// Here it also means something specific: the two parts are consecutive
    /// intervals covering the whole span, so a gap is the apparatus disagreeing
    /// with itself rather than time nobody could place.
    /// </para>
    /// </summary>
    [Fact]
    public void A_breakdown_that_does_not_add_up_says_so()
    {
        IngestAttribution attribution = Attribution(total: 85, beforeHandler: 20, inHandler: 5, write: 3);

        attribution.UnattributedMs.ShouldBe(60, 0.001);
        attribution.AttributedFraction.ShouldBeLessThan(0.8);
        attribution.Describe().ShouldContain("unattributed");
    }

    /// <summary>
    /// **The requirement's span is a range, not a number**, and that is the
    /// honest consequence of having no publisher-side stamp. Reporting a single
    /// figure would claim a precision the apparatus cannot deliver.
    /// </summary>
    [Fact]
    public void The_requirement_span_is_bounded_rather_than_measured()
    {
        IngestAttribution attribution = Attribution(total: 80, beforeHandler: 60, inHandler: 20, write: 5);

        attribution.RequirementSpanFloorMs.ShouldBe(25, 0.001, "from handler entry, which is after the handover");
        attribution.RequirementSpanCeilingMs.ShouldBe(85, 0.001, "as if the handover happened at the change itself");
        attribution.RequirementSpanWidthMs.ShouldBe(60, 0.001, "the width is the cost of the missing stamp");
    }

    /// <summary>
    /// The floor cannot exceed the ceiling. Trivially true as written, and
    /// asserted because a sign slip in either expression would produce a range
    /// that reads plausibly and is inside out.
    /// </summary>
    [Theory]
    [InlineData(80, 60, 20, 5)]
    [InlineData(30, 5, 25, 40)]
    [InlineData(0, 0, 0, 0)]
    public void The_floor_never_exceeds_the_ceiling(double total, double before, double inHandler, double write)
    {
        IngestAttribution attribution = Attribution(total, before, inHandler, write);

        attribution.RequirementSpanFloorMs.ShouldBeLessThanOrEqualTo(attribution.RequirementSpanCeilingMs);
    }

    /// <summary>
    /// The two spans differ at both ends, in opposite directions — a single
    /// "they differ by 55 ms" would hide that one end adds and the other
    /// subtracts, which is exactly the conflation three decisions have made.
    /// </summary>
    [Fact]
    public void The_difference_between_the_spans_is_split_front_and_back()
    {
        IngestAttribution attribution = Attribution(total: 80, beforeHandler: 60, inHandler: 20, write: 5);

        attribution.FrontOverhangMs.ShouldBe(60, 0.001, "in the historic figure, outside the requirement");
        attribution.BackShortfallMs.ShouldBe(5, 0.001, "in the requirement, outside the historic figure");

        (attribution.TotalMs - attribution.FrontOverhangMs + attribution.BackShortfallMs)
            .ShouldBe(attribution.RequirementSpanFloorMs, 0.001,
                "removing the overhang and adding the shortfall lands on the floor");
    }

    /// <summary>
    /// A run that produced nothing must not report itself as fully explained.
    /// Dividing by a zero total is the obvious way to get 100% out of no data.
    /// </summary>
    [Fact]
    public void An_empty_run_is_not_fully_attributed()
    {
        Attribution(0, 0, 0, 0, rows: 0).AttributedFraction.ShouldBe(0);
    }

    /// <summary>
    /// Rows without stamps are counted rather than skipped. A run that measured
    /// nine hundred of a thousand events and said nothing would report the nine
    /// hundred as though they were the population.
    /// </summary>
    [Fact]
    public void Rows_that_arrived_without_stamps_are_counted()
    {
        IngestAttribution attribution = Attribution(80, 60, 20, 5, rows: 1_000, missing: 12);

        attribution.EveryRowStamped.ShouldBeFalse();
        attribution.Describe().ShouldContain("12 missing stamps");
    }

    /// <summary>
    /// **This test previously pinned the wrong claim.** It asserted the
    /// before-handler line reads "crosses two clocks", which is how the whole
    /// apparatus described itself until review found it backwards: the two
    /// processes stamping that leg share one OS clock, and the leg that really
    /// crosses two is the write. The assertion held the error in place rather
    /// than catching it — a label test is only as good as the label.
    /// </summary>
    [Fact]
    public void The_description_carries_both_spans_and_the_remainder()
    {
        string description = Attribution(80, 60, 20, 5).Describe();

        description.ShouldContain("observed span");
        description.ShouldContain("requirement span");
        description.ShouldContain("unattributed");
        description.ShouldContain("per-row residual");
        description.ShouldContain("between");
    }

    /// <summary>
    /// Which leg is labelled as crossing clocks, asserted because getting it the
    /// other way round is what shipped and what a reader would act on.
    /// </summary>
    [Fact]
    public void The_write_leg_is_the_one_labelled_as_crossing_clocks()
    {
        string description = Attribution(80, 60, 20, 5).Describe();

        description.ShouldContain("write (two clocks");
        description.ShouldContain("before handler (two processes, one clock)");
        // The old label, which attributed the clock risk to the leg that does
        // not carry it. Asserted absent so it cannot quietly return.
        description.ShouldNotContain("crosses two clocks");
    }
}

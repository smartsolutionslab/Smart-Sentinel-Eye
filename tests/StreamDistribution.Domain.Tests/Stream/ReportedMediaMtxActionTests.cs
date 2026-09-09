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

    /// <summary>
    /// <b>Security review F2.</b> <c>char.IsControl</c> was one category short.
    /// U+2028/U+2029 are real line breaks in some sinks and in every
    /// JavaScript-side viewer, and U+202E reorders everything after it for a
    /// human reader without touching a byte — the same class of defect as the
    /// <c>\r\n</c> above, reached by a different door.
    /// </summary>
    // Given as numeric code points, not literals: U+2028 inside a C# string
    // literal is a newline to the compiler ("CS1010: Newline in constant"),
    // which is this finding demonstrating itself.
    [Theory]
    [InlineData(0x2028)]  // Zl - a line break in some sinks
    [InlineData(0x2029)]  // Zp - likewise
    [InlineData(0x202E)]  // Cf - right-to-left override
    [InlineData(0x200B)]  // Cf - zero-width space
    [InlineData(0x0007)]  // Cc - the category the old filter already caught
    public void From_neutralises_characters_that_rewrite_the_line_around_them(int codePoint)
    {
        string dangerous = char.ConvertFromUtf32(codePoint);

        ReportedMediaMtxAction reported = ReportedMediaMtxAction.From("read" + dangerous + "publish");

        reported.Value.ShouldNotContain(dangerous);
        reported.Value.ShouldBe("read�publish");
    }

    /// <summary>
    /// A cap counted in <c>char</c>s could fall between the halves of a
    /// surrogate pair and leave a lone surrogate. Counting runes cannot.
    /// </summary>
    [Fact]
    public void From_never_splits_a_surrogate_pair_at_the_cap()
    {
        string astral = string.Concat(Enumerable.Repeat("\U0001F4F7", 100));

        ReportedMediaMtxAction reported = ReportedMediaMtxAction.From(astral);

        reported.Value.ShouldBe(
            string.Concat(Enumerable.Repeat("\U0001F4F7", ReportedMediaMtxAction.MaximumLength)) + "…");
        reported.Value.Any(char.IsSurrogate).ShouldBeTrue();

        // 64 runes is 128 chars plus the mark - the cap counts runes, not chars.
        reported.Value.Length.ShouldBe((ReportedMediaMtxAction.MaximumLength * 2) + 1);
        reported.Value.ShouldNotContain("�");
    }

    /// <summary>
    /// Clamped, never rejected. A factory that threw on a long or hostile value
    /// would turn a diagnosable 403 into a 500 at exactly the moment the
    /// diagnosis is wanted, which is the failure this type exists to avoid.
    /// </summary>
    [Fact]
    public void From_clamps_rather_than_rejects()
    {
        Should.NotThrow(() => ReportedMediaMtxAction.From(new string((char)0x202E, 5000)));
    }

    [Fact]
    public void ToString_returns_the_bounded_value()
    {
        ReportedMediaMtxAction.From("stream").ToString().ShouldBe("stream");
    }
}

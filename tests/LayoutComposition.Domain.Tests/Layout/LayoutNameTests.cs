using SmartSentinelEye.LayoutComposition.Domain.Layout;

namespace SmartSentinelEye.LayoutComposition.Domain.Tests.Layout;

public class LayoutNameTests
{
    [Fact]
    public void From_accepts_a_normal_name()
    {
        LayoutName name = LayoutName.From("Line-1 Entrance");
        name.Value.ShouldBe("Line-1 Entrance");
    }

    [Fact]
    public void From_trims_leading_and_trailing_whitespace()
    {
        LayoutName name = LayoutName.From("  Line-1  ");
        name.Value.ShouldBe("Line-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_rejects_blank_input(string raw)
    {
        Action act = () => LayoutName.From(raw);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void From_rejects_input_above_the_maximum_length()
    {
        string tooLong = new('a', LayoutName.MaximumLength + 1);
        Action act = () => LayoutName.From(tooLong);
        act.ShouldThrow<ArgumentException>();
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("two\rlines")]
    public void From_rejects_input_containing_a_line_break(string raw)
    {
        Should.Throw<ArgumentException>(() => LayoutName.From(raw));
    }

    /// <summary>
    /// Spec 086 §1.3. Equality is ordinal and there is no normalised form, so
    /// two names differing only in case are two different names — to the create
    /// handler and to a Postgres btree on the raw column, identically. That
    /// agreement is why the partial unique index needs no
    /// <c>name_normalized</c> companion, and this records the premise rather
    /// than leaving it implied. Making these names case-insensitive, as
    /// <c>CameraName</c> became, is a separate user-visible change.
    /// </summary>
    [Fact]
    public void Names_differing_only_in_case_are_not_equal()
    {
        LayoutName upper = LayoutName.From("Wall A");
        LayoutName lower = LayoutName.From("wall a");

        upper.ShouldNotBe(lower);
        upper.Value.ShouldBe("Wall A");
        lower.Value.ShouldBe("wall a");
    }
}

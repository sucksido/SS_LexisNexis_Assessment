using OrderIntake.Domain.Orders;

namespace OrderIntake.UnitTests.Domain;

/// <summary>
/// Rounding does not currently fire anywhere on the order path: prices are held
/// to whole cents and quantities are whole numbers, so every product is already
/// exact. That is worth being honest about rather than claiming a rounding
/// policy the code never exercises.
///
/// It is tested here, directly, because it is a guard for the first feature that
/// changes that — a percentage discount, a tax rate, a currency conversion — and
/// a guard nobody exercises is a guard nobody notices breaking.
/// </summary>
public sealed class MoneyTests
{
    // Attribute arguments cannot be decimal literals in C#, so these arrive as
    // double and are converted. Each is exact under that conversion.
    [Theory]
    [InlineData(2.345, 2.35)]
    [InlineData(2.344, 2.34)]
    [InlineData(2.346, 2.35)]
    [InlineData(0.005, 0.01)]
    [InlineData(0.125, 0.13)]
    [InlineData(1.115, 1.12)]
    [InlineData(-2.345, -2.35)]
    [InlineData(-0.005, -0.01)]
    public void Rounding_goes_away_from_zero_at_the_midpoint(double amount, double expected) =>
        Money.Round((decimal)amount).Should().Be((decimal)expected);

    /// <summary>
    /// Pins the reason the rounding mode is stated explicitly instead of inherited.
    /// If this test ever fails, someone has reached for the default.
    /// </summary>
    [Fact]
    public void The_dotnet_default_would_answer_differently()
    {
        decimal.Round(2.345m, 2).Should().Be(2.34m, "the framework default is MidpointRounding.ToEven");
        Money.Round(2.345m).Should().Be(2.35m, "which is not what an invoice should say");
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(10.5, true)]
    [InlineData(10.99, true)]
    [InlineData(0, true)]
    [InlineData(10.125, false)]
    [InlineData(9.995, false)]
    [InlineData(19.9999, false)]
    public void Cent_precision_is_recognised(double amount, bool expected) =>
        Money.IsCentPrecision((decimal)amount).Should().Be(expected);

    /// <summary>
    /// Trailing zeroes are a scale difference, not a value difference. A price
    /// entered as 10.50 and one entered as 10.5 are the same price, and the
    /// precision check must not disagree.
    /// </summary>
    [Fact]
    public void Trailing_zeroes_do_not_affect_the_precision_check()
    {
        Money.IsCentPrecision(10.50m).Should().BeTrue();
        Money.IsCentPrecision(10.500m).Should().BeTrue();
        Money.IsCentPrecision(10.5000m).Should().BeTrue();
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData("  zar  ", "ZAR")]
    [InlineData("Eur", "EUR")]
    public void Currency_codes_are_normalised(string input, string expected) =>
        Money.NormaliseCurrency(input).Should().Be(expected);

    [Fact]
    public void Formatting_always_shows_both_cent_digits() =>
        Money.Format(1234.5m, "USD").Should().Be("USD 1234.50");
}

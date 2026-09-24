using System.Globalization;
using System.Text.RegularExpressions;
using OrderIntake.Domain.Common;

namespace OrderIntake.Domain.Orders;

/// <summary>
/// Money helpers. Deliberately *not* a full value object: EF Core owned types
/// would add mapping ceremony for very little gain at this size, and every
/// amount in this system shares one currency per order.
///
/// What matters is that the rounding rule lives in exactly one place. Getting
/// this wrong is how a line of 3 x 9.995 becomes 29.98 in the UI and 29.985 in
/// the database.
/// </summary>
public static class Money
{
    public const int Scale = 2;

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rounds to 2 decimal places using "away from zero" (a.k.a. commercial rounding).
    ///
    /// .NET's default is banker's rounding (MidpointRounding.ToEven), which turns
    /// 2.345 into 2.34. That is correct for statistics and surprising for invoices,
    /// so we opt out of it explicitly rather than inherit it by accident.
    /// </summary>
    public static decimal Round(decimal amount) =>
        decimal.Round(amount, Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// True when the amount is already expressed in whole cents.
    ///
    /// The single definition of "a price we are willing to accept". Both the
    /// request validator and the Order aggregate ask this question, and they
    /// must not be able to drift apart on the answer.
    /// </summary>
    public static bool IsCentPrecision(decimal amount) => Round(amount) == amount;

    /// <summary>Validates and normalises an ISO 4217 alphabetic currency code.</summary>
    public static string NormaliseCurrency(string? currency)
    {
        var trimmed = (currency ?? string.Empty).Trim().ToUpperInvariant();

        if (!CurrencyPattern.IsMatch(trimmed))
        {
            throw new DomainValidationException(
                $"Currency must be a 3-letter ISO 4217 code such as USD or ZAR, but was '{currency}'.");
        }

        return trimmed;
    }

    public static string Format(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{currency} {Round(amount):0.00}");
}

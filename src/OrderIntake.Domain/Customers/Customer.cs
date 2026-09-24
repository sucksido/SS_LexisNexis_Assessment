using System.Text.RegularExpressions;
using OrderIntake.Domain.Common;

namespace OrderIntake.Domain.Customers;

/// <summary>
/// A customer, identified by email.
///
/// Its own aggregate: Order references it by id rather than holding the object,
/// so renaming a customer never requires touching an order, and an order can be
/// loaded without dragging customer state along with it.
/// </summary>
public sealed class Customer
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Required by EF Core's materialiser.
    private Customer()
    {
    }

    private Customer(string email, string name)
    {
        Id = Guid.NewGuid();
        Email = NormaliseEmail(email);
        Name = NormaliseName(name);
    }

    public Guid Id { get; private set; }

    /// <summary>Lower-cased and trimmed, so "Sam@Corp.com" and "sam@corp.com" are one customer.</summary>
    public string Email { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public static Customer Create(string email, string name) => new(email, name);

    /// <summary>
    /// Keeps the display name current when a returning customer submits under a
    /// corrected spelling. Email is the identity and is intentionally immutable.
    /// </summary>
    public void UpdateName(string name)
    {
        var normalised = NormaliseName(name);

        if (!string.Equals(Name, normalised, StringComparison.Ordinal))
        {
            Name = normalised;
        }
    }

    public static string NormaliseEmail(string? email)
    {
        var trimmed = (email ?? string.Empty).Trim().ToLowerInvariant();

        if (!EmailPattern.IsMatch(trimmed))
        {
            throw new DomainValidationException($"'{email}' is not a valid email address.");
        }

        return trimmed;
    }

    private static string NormaliseName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("Customer name is required.");
        }

        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }
}

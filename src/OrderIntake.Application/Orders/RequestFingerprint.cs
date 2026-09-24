using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Application.Orders;

/// <summary>
/// Produces a stable hash of the *material* content of a submission.
///
/// This is what turns "ignore the second call" into real idempotency. When a
/// reference arrives that we have seen before, the fingerprint tells us whether
/// it is the same order arriving twice (return the original, all is well) or a
/// different order wearing the same reference (a mistake worth reporting).
///
/// Material fields are currency and, per line, SKU + quantity + unit price:
/// the things that change what the customer is billed. Notes and product display
/// names are deliberately excluded, so a rep who resubmits after fixing a typo in
/// the notes gets their original order back instead of a conflict they cannot act on.
///
/// Lines are sorted before hashing, so the same basket entered in a different
/// order still fingerprints identically.
/// </summary>
public static class RequestFingerprint
{
    public static string Compute(SubmitOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();

        var lines = request.Lines
            .Select(line => new
            {
                Sku = (line.Sku ?? string.Empty).Trim().ToUpperInvariant(),
                line.Quantity,
                // Normalises scale so 89.9 and 89.90 hash alike. Anything finer
                // than a cent is rejected before it reaches here, so this cannot
                // quietly merge two genuinely different prices into one hash.
                UnitPrice = Money.Round(line.UnitPrice)
            })
            .OrderBy(line => line.Sku, StringComparer.Ordinal)
            .ThenBy(line => line.Quantity)
            .Select(line => string.Create(
                CultureInfo.InvariantCulture,
                $"{line.Sku}|{line.Quantity}|{line.UnitPrice:0.00}"));

        // A newline-delimited canonical form. Simple, debuggable by eye, and
        // free of the field-ordering instability that serialising the DTO
        // straight to JSON would introduce.
        var canonical = string.Join('\n', new[] { $"CUR|{currency}" }.Concat(lines));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

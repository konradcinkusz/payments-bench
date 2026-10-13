using System.Security.Cryptography;
using System.Text;

namespace PaymentsBench.Application.Payments;

public static class RequestHash
{
    /// <summary>A stable hash of the fields that make two requests "the same request". Order and case are fixed here, not by the caller.</summary>
    public static string Of(InitiatePayment command)
    {
        var canonical = string.Join('|',
            command.EndToEndId,
            command.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            command.Currency.ToUpperInvariant(),
            command.DebtorAccount,
            command.CreditorAccount,
            command.Scheme);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

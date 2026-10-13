namespace PaymentsBench.Domain;

/// <summary>
/// An amount in one ISO 4217 currency. Arithmetic across currencies is a bug, not a conversion,
/// so it throws. Amounts are decimals scaled to the currency's minor unit by the caller; this
/// type does not round.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new DomainException($"Currency must be a three-letter ISO 4217 code, got '{currency}'.");
        }

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public bool IsPositive => Amount > 0;

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount, SameCurrency(left, right));

    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount, SameCurrency(left, right));

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    private static string SameCurrency(Money left, Money right) =>
        left.Currency == right.Currency
            ? left.Currency
            : throw new DomainException($"Cannot combine {left.Currency} with {right.Currency}.");

    public override string ToString() => $"{Amount:0.00} {Currency}";
}

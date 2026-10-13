using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace PaymentsBench.Api.Validation;

/// <summary>The value must be the name of a member of <typeparamref name="TEnum"/>; null is left to <c>[Required]</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class KnownValueAttribute<TEnum> : ValidationAttribute
    where TEnum : struct, Enum
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        value is null or "" || (value is string s && Enum.TryParse<TEnum>(s, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            ? ValidationResult.Success
            : new ValidationResult(
                $"'{value}' is not a known {typeof(TEnum).Name}. Known values: {string.Join(", ", Enum.GetNames<TEnum>())}.",
                validationContext.MemberName is { } member ? [member] : null);
}

/// <summary>The decimal may carry at most <c>maxPlaces</c> digits after the decimal point.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MaxDecimalPlacesAttribute(int maxPlaces) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not decimal amount)
        {
            return ValidationResult.Success;
        }

        // Rounding to the limit and comparing ignores trailing zeros: 1.500m is two decimal places.
        return Math.Round(amount, maxPlaces) == amount
            ? ValidationResult.Success
            : new ValidationResult(
                string.Create(CultureInfo.InvariantCulture, $"Must have at most {maxPlaces} decimal places."),
                validationContext.MemberName is { } member ? [member] : null);
    }
}

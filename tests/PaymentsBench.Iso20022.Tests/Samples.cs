namespace PaymentsBench.Iso20022.Tests;

internal static class Samples
{
    public const string TwoTransactions = "pain001-two-transactions.xml";
    public const string UnmappedServiceLevel = "pain001-unmapped-service-level.xml";
    public const string ThreeEntries = "camt053-three-entries.xml";

    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));
}

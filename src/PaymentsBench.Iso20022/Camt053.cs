using System.Globalization;
using System.Xml.Linq;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Iso20022;

public static class Camt053
{
    public static readonly XNamespace Namespace = "urn:iso:std:iso:20022:tech:xsd:camt.053.001.08";

    /// <summary>The value ISO 20022 prescribes for an end-to-end id the originator did not supply.</summary>
    public const string NotProvided = "NOTPROVIDED";
}

/// <summary>
/// Reads a <c>camt.053.001.08</c> bank-to-customer statement into <see cref="Statement"/>.
/// <list type="bullet">
/// <item>The statement date is the date written in <c>Stmt/CreDtTm</c> (not converted to UTC): one
/// mandatory field, no inference from the entries. It is informational; reconciliation matches on the
/// date it asked for and on each line's own booking date.</item>
/// <item>The account is <c>Acct/Id/IBAN</c>, or <c>Acct/Id/Othr/Id</c> when there is no IBAN.</item>
/// <item>One <see cref="StatementLine"/> per <c>TxDtls</c>; an entry with none is one line, with the end-to-end
/// id <see cref="Camt053.NotProvided"/>. A <c>TxDtls</c> without its own <c>Amt</c> takes the entry's, which
/// is ambiguous when an entry has several, so that is refused. Reversal entries are refused too.</item>
/// </list>
/// </summary>
public static class Camt053Reader
{
    public const int DefaultMaxBytes = 10 * 1_048_576;

    public static async Task<Statement> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken) =>
        Read(await SecureXml.LoadAsync(stream, maxBytes, cancellationToken));

    public static Statement Read(string xml) => Read(SecureXml.Load(xml, DefaultMaxBytes));

    public static Statement Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var statement = Cursor.Root(document, Camt053.Namespace).Required("BkToCstmrStmt", "Stmt");
        var created = statement.Required("CreDtTm");
        if (!DateTimeOffset.TryParse(created.Text(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdAt))
        {
            throw new MessageFormatException(created.Path, $"'{created.Text()}' is not a date and time.");
        }

        var account = statement.Required("Acct", "Id");
        var accountId = account.Optional("IBAN") is { } iban ? iban.Text() : account.Required("Othr", "Id").Text();

        var lines = statement.Repeated("Ntry").SelectMany(ReadEntry).ToList();
        return new Statement(DateOnly.FromDateTime(createdAt.DateTime), accountId, lines);
    }

    private static IEnumerable<StatementLine> ReadEntry(Cursor entry)
    {
        if (entry.Optional("RvslInd") is { } reversal && string.Equals(reversal.Text(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new MessageFormatException(reversal.Path, "reversal entries are not supported.");
        }

        var entryAmount = entry.Required("Amt");
        var direction = Direction(entry.Required("CdtDbtInd"));
        var bookingDate = entry.Required("BookgDt", "Dt").Date();
        var details = entry.Optional("NtryDtls")?.Repeated("TxDtls") ?? [];

        if (details.Count == 0)
        {
            yield return new StatementLine(Camt053.NotProvided, entryAmount.Amount(), entryAmount.Currency(), bookingDate, direction);
            yield break;
        }

        foreach (var detail in details)
        {
            var amount = detail.Optional("Amt") ?? (details.Count == 1 ? entryAmount : throw new MessageFormatException(detail.Path + "/Amt", "is required when an entry has several transaction details."));
            var endToEndId = detail.Optional("Refs", "EndToEndId")?.Text() ?? Camt053.NotProvided;
            yield return new StatementLine(endToEndId, amount.Amount(), amount.Currency(), bookingDate, direction);
        }
    }

    private static StatementDirection Direction(Cursor indicator) => indicator.Text() switch
    {
        "DBIT" => StatementDirection.Debit,
        "CRDT" => StatementDirection.Credit,
        var other => throw new MessageFormatException(indicator.Path, $"'{other}' is not DBIT or CRDT."),
    };
}

/// <summary>
/// Writes a <see cref="Statement"/> as a <c>camt.053.001.08</c> document: one entry per line, each with one
/// transaction detail. The shape is the published one, not validated against the official schema.
/// </summary>
public static class Camt053Writer
{
    public static string Write(Statement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var ns = Camt053.Namespace;
        var id = $"STMT-{statement.Date:yyyyMMdd}";
        var created = statement.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
        var accountId = IsIban(statement.Account)
            ? new XElement(ns + "IBAN", statement.Account)
            : new XElement(ns + "Othr", new XElement(ns + "Id", statement.Account));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                ns + "Document",
                new XElement(
                    ns + "BkToCstmrStmt",
                    new XElement(ns + "GrpHdr", new XElement(ns + "MsgId", id), new XElement(ns + "CreDtTm", created)),
                    new XElement(
                        ns + "Stmt",
                        new XElement(ns + "Id", id),
                        new XElement(ns + "CreDtTm", created),
                        new XElement(ns + "Acct", new XElement(ns + "Id", accountId)),
                        statement.Lines.Select(line => Entry(ns, line))))));
        return XmlText.Render(document);
    }

    private static XElement Entry(XNamespace ns, StatementLine line) =>
        new(
            ns + "Ntry",
            new XElement(ns + "Amt", new XAttribute("Ccy", line.Currency), XmlText.Amount(line.Amount)),
            new XElement(ns + "CdtDbtInd", line.Direction == StatementDirection.Debit ? "DBIT" : "CRDT"),
            new XElement(ns + "Sts", new XElement(ns + "Cd", "BOOK")),
            new XElement(ns + "BookgDt", new XElement(ns + "Dt", line.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            new XElement(ns + "BkTxCd", new XElement(ns + "Prtry", new XElement(ns + "Cd", "NTRF"))),
            new XElement(ns + "NtryDtls", new XElement(ns + "TxDtls", new XElement(ns + "Refs", new XElement(ns + "EndToEndId", line.EndToEndId)))));

    private static bool IsIban(string account) =>
        account.Length is >= 5 and <= 34
        && account[..2].All(c => c is >= 'A' and <= 'Z')
        && account[2..4].All(char.IsAsciiDigit)
        && account[4..].All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
}

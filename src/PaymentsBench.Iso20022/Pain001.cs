using System.Xml.Linq;
using PaymentsBench.Application.Payments;

namespace PaymentsBench.Iso20022;

public static class Pain001
{
    public static readonly XNamespace Namespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";
}

/// <summary>
/// Reads a <c>pain.001.001.09</c> customer credit transfer initiation into one <see cref="InitiatePayment"/>
/// per <c>CdtTrfTxInf</c>. This is the anti-corruption edge (P11): the dialect ends here and the commands
/// are the internal model, unchanged. The idempotency key of each command is
/// <c>MsgId/PmtInfId/EndToEndId</c>, so posting the same file twice is the same request twice.
/// A document is read whole or refused whole; nothing is half-read.
/// </summary>
public static class Pain001Reader
{
    private const int Max35 = 35;
    private const int MaxIban = 34;

    public static async Task<IReadOnlyList<InitiatePayment>> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken) =>
        Read(await SecureXml.LoadAsync(stream, maxBytes, cancellationToken));

    public static IReadOnlyList<InitiatePayment> Read(string xml) => Read(SecureXml.Load(xml));

    public static IReadOnlyList<InitiatePayment> Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var initiation = Cursor.Root(document, Pain001.Namespace).Required("CstmrCdtTrfInitn");
        var messageId = initiation.Required("GrpHdr", "MsgId").Text(Max35);
        var blocks = initiation.Repeated("PmtInf");
        if (blocks.Count == 0)
        {
            throw new MessageFormatException(initiation.Path + "/PmtInf", "is required but missing.");
        }

        var commands = new List<InitiatePayment>();
        foreach (var block in blocks)
        {
            commands.AddRange(ReadBlock(messageId, block));
        }

        if (initiation.Optional("GrpHdr", "NbOfTxs") is { } declared && declared.Text() != commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new MessageFormatException(declared.Path, $"declares {declared.Text()} transactions, the document carries {commands.Count}.");
        }

        return commands;
    }

    private static IEnumerable<InitiatePayment> ReadBlock(string messageId, Cursor block)
    {
        var paymentInfoId = block.Required("PmtInfId").Text(Max35);
        var serviceLevel = block.Required("PmtTpInf", "SvcLvl", "Cd");
        var scheme = ServiceLevels.TryGetScheme(serviceLevel.Text(), out var mapped)
            ? mapped
            : throw new MessageFormatException(serviceLevel.Path, $"service level '{serviceLevel.Text()}' is not supported; supported: {string.Join(", ", ServiceLevels.Known)}.");
        var debtorAccount = block.Required("DbtrAcct", "Id", "IBAN").Text(MaxIban);

        var transactions = block.Repeated("CdtTrfTxInf");
        if (transactions.Count == 0)
        {
            throw new MessageFormatException(block.Path + "/CdtTrfTxInf", "is required but missing.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transaction in transactions)
        {
            var endToEndId = transaction.Required("PmtId", "EndToEndId").Text(Max35);
            if (!seen.Add(endToEndId))
            {
                throw new MessageFormatException(transaction.Path + "/PmtId/EndToEndId", $"'{endToEndId}' appears twice in one payment information block.");
            }

            var amount = transaction.Required("Amt", "InstdAmt");
            yield return new InitiatePayment(
                $"{messageId}/{paymentInfoId}/{endToEndId}",
                endToEndId,
                amount.Amount(),
                amount.Currency(),
                debtorAccount,
                transaction.Required("CdtrAcct", "Id", "IBAN").Text(MaxIban),
                scheme);
        }
    }
}

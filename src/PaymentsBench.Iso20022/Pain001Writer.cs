using System.Globalization;
using System.Xml.Linq;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Iso20022;

/// <summary>
/// Writes <see cref="PartnerTransfer"/>s as a <c>pain.001.001.09</c> document: one <c>PmtInf</c> per
/// scheme and debtor account, named <c>{messageId}-{n}</c>. Party names are not in the internal model,
/// so none are written. The output is the published shape, not validated against the official schema.
/// </summary>
public static class Pain001Writer
{
    public static string Write(string messageId, DateTimeOffset createdAt, IReadOnlyList<PartnerTransfer> transfers, string initiatingParty = "Payments Bench (simulated)")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(transfers);
        if (transfers.Count == 0)
        {
            throw new ArgumentException("A pain.001 document carries at least one transfer.", nameof(transfers));
        }

        var ns = Pain001.Namespace;
        var blocks = transfers
            .GroupBy(t => (t.Scheme, t.DebtorAccount))
            .Select((group, index) => Block(ns, $"{messageId}-{index + 1}", createdAt, group.Key.Scheme, group.Key.DebtorAccount, group.ToList()));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                ns + "Document",
                new XElement(
                    ns + "CstmrCdtTrfInitn",
                    new XElement(
                        ns + "GrpHdr",
                        new XElement(ns + "MsgId", messageId),
                        new XElement(ns + "CreDtTm", createdAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)),
                        new XElement(ns + "NbOfTxs", transfers.Count.ToString(CultureInfo.InvariantCulture)),
                        new XElement(ns + "InitgPty", new XElement(ns + "Nm", initiatingParty))),
                    blocks)));
        return XmlText.Render(document);
    }

    private static XElement Block(XNamespace ns, string paymentInfoId, DateTimeOffset createdAt, string scheme, string debtorAccount, List<PartnerTransfer> transfers)
    {
        if (!ServiceLevels.TryGetServiceLevel(scheme, out var serviceLevel))
        {
            throw new ArgumentException($"Scheme '{scheme}' has no ISO 20022 service level.", nameof(transfers));
        }

        return new XElement(
            ns + "PmtInf",
            new XElement(ns + "PmtInfId", paymentInfoId),
            new XElement(ns + "PmtMtd", "TRF"),
            new XElement(ns + "NbOfTxs", transfers.Count.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "CtrlSum", XmlText.Amount(transfers.Sum(t => t.Amount))),
            new XElement(ns + "PmtTpInf", new XElement(ns + "SvcLvl", new XElement(ns + "Cd", serviceLevel))),
            new XElement(ns + "ReqdExctnDt", new XElement(ns + "Dt", createdAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            new XElement(ns + "Dbtr"),
            new XElement(ns + "DbtrAcct", new XElement(ns + "Id", new XElement(ns + "IBAN", debtorAccount))),
            transfers.Select(t => new XElement(
                ns + "CdtTrfTxInf",
                new XElement(ns + "PmtId", new XElement(ns + "EndToEndId", t.EndToEndId)),
                new XElement(ns + "Amt", new XElement(ns + "InstdAmt", new XAttribute("Ccy", t.Currency), XmlText.Amount(t.Amount))),
                new XElement(ns + "CdtrAcct", new XElement(ns + "Id", new XElement(ns + "IBAN", t.CreditorAccount))))));
    }
}

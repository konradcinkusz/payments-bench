using System.Globalization;
using System.Security;
using System.Text;

namespace PaymentsBench.PartnerSimulator;

/// <summary>
/// The simulated bank's camt.053.001.08 rendering. Written here, by hand, on purpose: it shares no code
/// with the reader in <c>PaymentsBench.Iso20022</c>, so a test that parses this output proves the
/// reader against something other than itself. Simulated; not a file issued by a bank.
/// </summary>
internal static class StatementXml
{
    public const string MediaType = "application/xml";

    public static string Render(DateOnly date, string account, IEnumerable<(string EndToEndId, decimal Amount, string Currency, string Direction)> entries)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.053.001.08\">");
        xml.AppendLine("  <BkToCstmrStmt>");
        xml.AppendLine($"    <GrpHdr><MsgId>SIM-{day}</MsgId><CreDtTm>{day}T00:00:00Z</CreDtTm></GrpHdr>");
        xml.AppendLine("    <Stmt>");
        xml.AppendLine($"      <Id>SIM-{day}</Id>");
        xml.AppendLine($"      <CreDtTm>{day}T00:00:00Z</CreDtTm>");
        xml.AppendLine($"      <Acct><Id><Othr><Id>{SecurityElement.Escape(account)}</Id></Othr></Id></Acct>");
        foreach (var (endToEndId, amount, currency, direction) in entries)
        {
            xml.AppendLine("      <Ntry>");
            xml.AppendLine(CultureInfo.InvariantCulture, $"        <Amt Ccy=\"{SecurityElement.Escape(currency)}\">{amount.ToString("0.00", CultureInfo.InvariantCulture)}</Amt>");
            xml.AppendLine($"        <CdtDbtInd>{(direction == "Credit" ? "CRDT" : "DBIT")}</CdtDbtInd>");
            xml.AppendLine("        <Sts><Cd>BOOK</Cd></Sts>");
            xml.AppendLine($"        <BookgDt><Dt>{day}</Dt></BookgDt>");
            xml.AppendLine($"        <NtryDtls><TxDtls><Refs><EndToEndId>{SecurityElement.Escape(endToEndId)}</EndToEndId></Refs></TxDtls></NtryDtls>");
            xml.AppendLine("      </Ntry>");
        }

        xml.AppendLine("    </Stmt>");
        xml.AppendLine("  </BkToCstmrStmt>");
        xml.AppendLine("</Document>");
        return xml.ToString();
    }
}

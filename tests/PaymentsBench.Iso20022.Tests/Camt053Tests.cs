using PaymentsBench.Application.Ports;

namespace PaymentsBench.Iso20022.Tests;

public class Camt053Tests
{
    private static readonly DateOnly Day = new(2026, 10, 14);

    [Fact]
    public void A_statement_with_three_entries_becomes_three_lines()
    {
        var statement = Camt053Reader.Read(Samples.Read(Samples.ThreeEntries));

        Assert.Equal(Day, statement.Date);
        Assert.Equal("GB33BUKB20201555555555", statement.Account);
        Assert.Equal(
            [
                new StatementLine("E2E-0001", 125.50m, "EUR", Day, StatementDirection.Debit),
                new StatementLine("E2E-0002", 40.00m, "GBP", Day, StatementDirection.Debit),
                new StatementLine("E2E-0003", 300.00m, "EUR", Day, StatementDirection.Credit),
            ],
            statement.Lines);
    }

    [Fact]
    public void Read_write_read_gives_back_the_same_statement()
    {
        var first = Camt053Reader.Read(Samples.Read(Samples.ThreeEntries));

        var second = Camt053Reader.Read(Camt053Writer.Write(first));

        Assert.Equal(first.Date, second.Date);
        Assert.Equal(first.Account, second.Account);
        Assert.Equal(first.Lines, second.Lines);
    }

    [Fact]
    public void An_account_with_no_iban_round_trips_as_its_other_identification()
    {
        var statement = new Statement(Day, "NOSTRO-SIM", [new StatementLine("E2E-1", 1.00m, "EUR", Day, StatementDirection.Debit)]);

        var xml = Camt053Writer.Write(statement);
        var back = Camt053Reader.Read(xml);

        Assert.Contains("<Othr>", xml, StringComparison.Ordinal);
        Assert.Equal("NOSTRO-SIM", back.Account);
        Assert.Equal(statement.Lines, back.Lines);
    }

    [Fact]
    public void The_statement_date_is_the_date_as_written_not_converted_to_utc()
    {
        var xml = Samples.Read(Samples.ThreeEntries).Replace("2026-10-14T23:30:00+00:00", "2026-10-15T00:30:00+02:00", StringComparison.Ordinal);

        Assert.Equal(new DateOnly(2026, 10, 15), Camt053Reader.Read(xml).Date);
    }

    [Fact]
    public void An_entry_with_several_transaction_details_is_several_lines()
    {
        var xml = Wrap("""
            <Ntry>
              <Amt Ccy="EUR">30.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt>
              <NtryDtls>
                <TxDtls><Refs><EndToEndId>E2E-A</EndToEndId></Refs><Amt Ccy="EUR">10.00</Amt></TxDtls>
                <TxDtls><Refs><EndToEndId>E2E-B</EndToEndId></Refs><Amt Ccy="EUR">20.00</Amt></TxDtls>
              </NtryDtls>
            </Ntry>
            """);

        var lines = Camt053Reader.Read(xml).Lines;

        Assert.Equal(
            [
                new StatementLine("E2E-A", 10.00m, "EUR", Day, StatementDirection.Debit),
                new StatementLine("E2E-B", 20.00m, "EUR", Day, StatementDirection.Debit),
            ],
            lines);
    }

    [Fact]
    public void Several_transaction_details_without_their_own_amounts_are_ambiguous()
    {
        var xml = Wrap("""
            <Ntry>
              <Amt Ccy="EUR">30.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt>
              <NtryDtls>
                <TxDtls><Refs><EndToEndId>E2E-A</EndToEndId></Refs></TxDtls>
                <TxDtls><Refs><EndToEndId>E2E-B</EndToEndId></Refs></TxDtls>
              </NtryDtls>
            </Ntry>
            """);

        var ex = Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(xml));

        Assert.Equal("Document/BkToCstmrStmt/Stmt/Ntry[1]/NtryDtls/TxDtls[1]/Amt", ex.ElementPath);
    }

    [Fact]
    public void An_entry_with_no_transaction_details_is_one_line_with_no_end_to_end_id_provided()
    {
        var xml = Wrap("""<Ntry><Amt Ccy="EUR">2.50</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""");

        var line = Assert.Single(Camt053Reader.Read(xml).Lines);

        Assert.Equal(new StatementLine("NOTPROVIDED", 2.50m, "EUR", Day, StatementDirection.Debit), line);
    }

    [Theory]
    [InlineData("""<Ntry><Amt Ccy="EUR">1.00</Amt><CdtDbtInd>XXXX</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/CdtDbtInd")]
    [InlineData("""<Ntry><Amt Ccy="EUR">1.00</Amt><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/CdtDbtInd")]
    [InlineData("""<Ntry><Amt Ccy="EUR">1.00</Amt><CdtDbtInd>DBIT</CdtDbtInd></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/BookgDt")]
    [InlineData("""<Ntry><Amt Ccy="EUR">1.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>14/10/2026</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/BookgDt/Dt")]
    [InlineData("""<Ntry><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/Amt")]
    [InlineData("""<Ntry><Amt Ccy="EUR">many</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/Amt")]
    [InlineData("""<Ntry><Amt Ccy="EUR">1.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><RvslInd>true</RvslInd><BookgDt><Dt>2026-10-14</Dt></BookgDt></Ntry>""", "Document/BkToCstmrStmt/Stmt/Ntry[1]/RvslInd")]
    public void Each_invalid_entry_is_refused_naming_the_element(string entry, string expectedPath)
    {
        var ex = Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(Wrap(entry)));

        Assert.Equal(expectedPath, ex.ElementPath);
    }

    [Fact]
    public void A_statement_with_no_account_is_refused()
    {
        var xml = Samples.Read(Samples.ThreeEntries).Replace("<IBAN>GB33BUKB20201555555555</IBAN>", string.Empty, StringComparison.Ordinal);

        var ex = Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(xml));

        Assert.Equal("Document/BkToCstmrStmt/Stmt/Acct/Id/Othr", ex.ElementPath);
    }

    [Fact]
    public void A_statement_with_no_creation_time_is_refused()
    {
        var xml = Samples.Read(Samples.ThreeEntries).Replace("<CreDtTm>2026-10-14T23:30:00+00:00</CreDtTm>\n      <Acct>", "<Acct>", StringComparison.Ordinal);

        var ex = Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(xml));

        Assert.Equal("Document/BkToCstmrStmt/Stmt/CreDtTm", ex.ElementPath);
    }

    [Fact]
    public void A_document_in_another_namespace_is_refused()
    {
        var xml = Samples.Read(Samples.ThreeEntries).Replace("camt.053.001.08", "camt.053.001.02", StringComparison.Ordinal);

        Assert.Equal("Document", Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(xml)).ElementPath);
    }

    [Fact]
    public void A_pain001_document_is_not_a_statement()
    {
        Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(Samples.Read(Samples.TwoTransactions)));
    }

    [Fact]
    public void A_document_with_a_dtd_is_refused()
    {
        var xml = Samples.Read(Samples.ThreeEntries)
            .Replace("<Document ", "<!DOCTYPE Document [<!ENTITY xxe SYSTEM \"file:///etc/hostname\">]>\n<Document ", StringComparison.Ordinal)
            .Replace("<AcctSvcrRef>SIM-REF-0001</AcctSvcrRef>", "<AcctSvcrRef>&xxe;</AcctSvcrRef>", StringComparison.Ordinal);

        Assert.Throws<MessageFormatException>(() => Camt053Reader.Read(xml));
    }

    [Fact]
    public async Task A_statement_stream_over_the_limit_is_refused()
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Samples.Read(Samples.ThreeEntries)));

        await Assert.ThrowsAsync<MessageTooLargeException>(() => Camt053Reader.ReadAsync(stream, 1024, CancellationToken.None));
    }

    private static string Wrap(string entries) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">
          <BkToCstmrStmt>
            <Stmt>
              <CreDtTm>2026-10-14T00:00:00Z</CreDtTm>
              <Acct><Id><IBAN>GB33BUKB20201555555555</IBAN></Id></Acct>
              {entries}
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;
}

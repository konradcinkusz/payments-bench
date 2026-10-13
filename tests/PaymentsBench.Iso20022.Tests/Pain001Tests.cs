using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;

namespace PaymentsBench.Iso20022.Tests;

public class Pain001Tests
{
    private const string Debtor = "DE89370400440532013000";

    [Fact]
    public void A_document_with_two_transactions_becomes_two_commands()
    {
        var commands = Pain001Reader.Read(Samples.Read(Samples.TwoTransactions));

        Assert.Equal(
            [
                new InitiatePayment("MSG-2026-0001/MSG-2026-0001-1/E2E-0001", "E2E-0001", 125.50m, "EUR", Debtor, "FR1420041010050500013M02606", "sepa"),
                new InitiatePayment("MSG-2026-0001/MSG-2026-0001-1/E2E-0002", "E2E-0002", 40.00m, "EUR", Debtor, "NL91ABNA0417164300", "sepa"),
            ],
            commands);
    }

    [Fact]
    public void Read_write_read_gives_back_the_same_commands()
    {
        var first = Pain001Reader.Read(Samples.Read(Samples.TwoTransactions));
        var transfers = first.Select(c => new PartnerTransfer(c.EndToEndId, c.Amount, c.Currency, c.DebtorAccount, c.CreditorAccount, c.Scheme)).ToList();

        var written = Pain001Writer.Write("MSG-2026-0001", new DateTimeOffset(2026, 10, 14, 8, 15, 0, TimeSpan.Zero), transfers);
        var second = Pain001Reader.Read(written);

        Assert.Equal(first, second);
        Assert.Contains("urn:iso:std:iso:20022:tech:xsd:pain.001.001.09", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Transfers_from_two_debtor_accounts_are_written_as_two_payment_information_blocks()
    {
        var transfers = new List<PartnerTransfer>
        {
            new("E2E-A", 10m, "EUR", Debtor, "FR1420041010050500013M02606", "sepa"),
            new("E2E-B", 20m, "EUR", "ES9121000418450200051332", "NL91ABNA0417164300", "sepa"),
            new("E2E-C", 30m, "EUR", Debtor, "NL91ABNA0417164300", "sepa"),
        };

        var commands = Pain001Reader.Read(Pain001Writer.Write("MSG-X", DateTimeOffset.UnixEpoch, transfers));

        Assert.Equal(["MSG-X/MSG-X-1/E2E-A", "MSG-X/MSG-X-1/E2E-C", "MSG-X/MSG-X-2/E2E-B"], commands.Select(c => c.IdempotencyKey));
    }

    [Fact]
    public void A_scheme_with_no_service_level_cannot_be_written()
    {
        var transfers = new List<PartnerTransfer> { new("E2E-A", 10m, "USD", Debtor, "FR1420041010050500013M02606", "swift") };

        Assert.Throws<ArgumentException>(() => Pain001Writer.Write("MSG-X", DateTimeOffset.UnixEpoch, transfers));
    }

    [Fact]
    public void An_unmapped_service_level_is_refused_naming_the_element()
    {
        var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(Samples.Read(Samples.UnmappedServiceLevel)));

        Assert.Equal("Document/CstmrCdtTrfInitn/PmtInf[1]/PmtTpInf/SvcLvl/Cd", ex.ElementPath);
        Assert.Contains("URGP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_in_another_namespace_is_refused()
    {
        var xml = Samples.Read(Samples.TwoTransactions).Replace("pain.001.001.09", "pain.001.001.03", StringComparison.Ordinal);

        var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(xml));

        Assert.Equal("Document", ex.ElementPath);
    }

    [Fact]
    public void A_camt053_document_is_not_a_pain001()
    {
        Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(Samples.Read(Samples.ThreeEntries)));
    }

    [Fact]
    public void Text_that_is_not_xml_is_refused()
    {
        var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read("{ \"amount\": 1 }"));

        Assert.Equal("Document", ex.ElementPath);
    }

    [Fact]
    public void A_truncated_document_is_refused()
    {
        var xml = Samples.Read(Samples.TwoTransactions);

        Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(xml[..(xml.Length / 2)]));
    }

    [Theory]
    [InlineData("<EndToEndId>E2E-0001</EndToEndId>", "", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/PmtId/EndToEndId")]
    [InlineData("<MsgId>MSG-2026-0001</MsgId>", "", "Document/CstmrCdtTrfInitn/GrpHdr/MsgId")]
    [InlineData("<IBAN>DE89370400440532013000</IBAN>", "", "Document/CstmrCdtTrfInitn/PmtInf[1]/DbtrAcct/Id/IBAN")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt>125.50</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt/@Ccy")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"eur\">125.50</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt/@Ccy")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">abc</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">0.00</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">-5.00</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">1.005</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">1e3</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<InstdAmt Ccy=\"EUR\">125.50</InstdAmt>", "<InstdAmt Ccy=\"EUR\">1,50</InstdAmt>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[1]/Amt/InstdAmt")]
    [InlineData("<EndToEndId>E2E-0002</EndToEndId>", "<EndToEndId>E2E-0001</EndToEndId>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[2]/PmtId/EndToEndId")]
    [InlineData("<EndToEndId>E2E-0002</EndToEndId>", "<EndToEndId>0123456789012345678901234567890123456</EndToEndId>", "Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf[2]/PmtId/EndToEndId")]
    [InlineData("<NbOfTxs>2</NbOfTxs>\n      <CtrlSum>165.50</CtrlSum>\n      <InitgPty>", "<NbOfTxs>3</NbOfTxs>\n      <CtrlSum>165.50</CtrlSum>\n      <InitgPty>", "Document/CstmrCdtTrfInitn/GrpHdr/NbOfTxs")]
    public void Each_invalid_document_is_refused_naming_the_element(string original, string replacement, string expectedPath)
    {
        var xml = Samples.Read(Samples.TwoTransactions);
        Assert.Contains(original, xml, StringComparison.Ordinal);

        var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(ReplaceFirst(xml, original, replacement)));

        Assert.Equal(expectedPath, ex.ElementPath);
    }

    [Fact]
    public void A_payment_information_block_with_no_transactions_is_refused()
    {
        var xml = Samples.Read(Samples.TwoTransactions);
        var start = xml.IndexOf("<CdtTrfTxInf>", StringComparison.Ordinal);
        var end = xml.LastIndexOf("</CdtTrfTxInf>", StringComparison.Ordinal) + "</CdtTrfTxInf>".Length;

        var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(xml.Remove(start, end - start)));

        Assert.Equal("Document/CstmrCdtTrfInitn/PmtInf[1]/CdtTrfTxInf", ex.ElementPath);
    }

    [Fact]
    public void A_document_with_an_external_entity_is_refused_and_nothing_is_fetched()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"iso20022-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xml = Samples.Read(Samples.TwoTransactions)
                .Replace("<Document ", $"<!DOCTYPE Document [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]>\n<Document ", StringComparison.Ordinal)
                .Replace("<Nm>Example Initiator Ltd</Nm>", "<Nm>&xxe;</Nm>", StringComparison.Ordinal);

            var ex = Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(xml));

            Assert.DoesNotContain("TOP-SECRET-CONTENT", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void A_document_with_an_entity_expansion_bomb_is_refused()
    {
        const string bomb = "<!DOCTYPE Document [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>\n";
        var xml = Samples.Read(Samples.TwoTransactions)
            .Replace("<Document ", bomb + "<Document ", StringComparison.Ordinal)
            .Replace("<Nm>Example Initiator Ltd</Nm>", "<Nm>&c;</Nm>", StringComparison.Ordinal);

        Assert.Throws<MessageFormatException>(() => Pain001Reader.Read(xml));
    }

    [Fact]
    public void A_document_over_the_size_limit_is_refused_before_it_is_parsed()
    {
        var xml = Samples.Read(Samples.TwoTransactions);

        Assert.Throws<MessageTooLargeException>(() => SecureXml.Load(xml, maxBytes: 100));
    }

    [Fact]
    public async Task A_stream_over_the_size_limit_is_refused_without_reading_it_all()
    {
        await using var endless = new EndlessStream();

        await Assert.ThrowsAsync<MessageTooLargeException>(() => Pain001Reader.ReadAsync(endless, 64 * 1024, CancellationToken.None));

        Assert.True(endless.BytesServed <= (64 * 1024) + 8192);
    }

    [Fact]
    public async Task A_stream_is_read_like_a_string()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "samples", Samples.TwoTransactions));

        var commands = await Pain001Reader.ReadAsync(stream, SecureXml.DefaultMaxBytes, CancellationToken.None);

        Assert.Equal(2, commands.Count);
    }

    private static string ReplaceFirst(string text, string original, string replacement)
    {
        var index = text.IndexOf(original, StringComparison.Ordinal);
        return text.Remove(index, original.Length).Insert(index, replacement);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            BytesServed += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)' ');
            BytesServed += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

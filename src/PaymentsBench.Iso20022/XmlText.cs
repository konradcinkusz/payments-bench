using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PaymentsBench.Iso20022;

internal static class XmlText
{
    public static string Amount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Indented UTF-8 text, declaration included, so the same document always renders the same bytes.</summary>
    public static string Render(XDocument document)
    {
        var output = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using (var writer = new StringWriterWithEncoding(output, settings.Encoding))
        using (var xml = XmlWriter.Create(writer, settings))
        {
            document.Save(xml);
        }

        return output.ToString();
    }

    private sealed class StringWriterWithEncoding(StringBuilder builder, Encoding encoding) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding { get; } = encoding;
    }
}

using System.Xml;
using System.Xml.Linq;

namespace PaymentsBench.Iso20022;

/// <summary>
/// The only way a document enters this project. A document type definition is refused outright
/// (no entity can be declared, so none can be expanded or fetched), no resolver is attached (nothing
/// is ever downloaded), and the size is bounded before the parser sees a byte. ISO 20022 messages
/// carry no DTD, so refusing one loses nothing.
/// </summary>
public static class SecureXml
{
    public const int DefaultMaxBytes = 1_048_576;

    public static async Task<XDocument> LoadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new MessageTooLargeException(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return Parse(buffer, maxBytes);
    }

    public static XDocument Load(string xml, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var buffer = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        if (buffer.Length > maxBytes)
        {
            throw new MessageTooLargeException(maxBytes);
        }

        return Parse(buffer, maxBytes);
    }

    private static XDocument Parse(Stream stream, int maxBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new MessageFormatException("Document", $"not well-formed XML ({ex.Message})");
        }
    }
}

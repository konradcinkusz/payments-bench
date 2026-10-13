using System.Globalization;
using System.Xml.Linq;

namespace PaymentsBench.Iso20022;

/// <summary>An element together with its path from the document root, so every failure can name where it happened.</summary>
internal sealed class Cursor
{
    private readonly XNamespace _namespace;

    private Cursor(XElement element, string path, XNamespace ns)
    {
        Element = element;
        Path = path;
        _namespace = ns;
    }

    public XElement Element { get; }

    public string Path { get; }

    /// <summary>The document's root, which must be <c>Document</c> in the expected namespace.</summary>
    public static Cursor Root(XDocument document, XNamespace ns)
    {
        var root = document.Root ?? throw new MessageFormatException("Document", "has no root element.");
        return root.Name == ns + "Document"
            ? new Cursor(root, "Document", ns)
            : throw new MessageFormatException("Document", $"expected root element {{{ns.NamespaceName}}}Document, found {{{root.Name.NamespaceName}}}{root.Name.LocalName}.");
    }

    public Cursor Required(string name) =>
        Optional(name) ?? throw new MessageFormatException(Path + "/" + name, "is required but missing.");

    public Cursor? Optional(string name)
    {
        var child = Element.Element(_namespace + name);
        return child is null ? null : new Cursor(child, Path + "/" + name, _namespace);
    }

    /// <summary>Walks a slash-separated chain of single children, naming the full path if any link is missing.</summary>
    public Cursor Required(params string[] names) => names.Aggregate(this, (cursor, name) => cursor.Required(name));

    public Cursor? Optional(params string[] names)
    {
        var cursor = this;
        foreach (var name in names)
        {
            cursor = cursor.Optional(name);
            if (cursor is null)
            {
                return null;
            }
        }

        return cursor;
    }

    public IReadOnlyList<Cursor> Repeated(string name) =>
        Element.Elements(_namespace + name)
            .Select((element, index) => new Cursor(element, $"{Path}/{name}[{index + 1}]", _namespace))
            .ToList();

    public string Text(int maxLength = int.MaxValue)
    {
        var value = Element.Value.Trim();
        if (value.Length == 0)
        {
            throw new MessageFormatException(Path, "must not be empty.");
        }

        return value.Length <= maxLength ? value : throw new MessageFormatException(Path, $"must be at most {maxLength} characters.");
    }

    public string Attribute(string name)
    {
        var value = Element.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? throw new MessageFormatException(Path + "/@" + name, "is required but missing.") : value;
    }

    public string Currency(string attribute = "Ccy")
    {
        var value = Attribute(attribute);
        return value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z')
            ? value
            : throw new MessageFormatException(Path + "/@" + attribute, "must be exactly three uppercase letters.");
    }

    /// <summary>A positive amount with at most two decimal places: the same limits as the JSON initiation body.</summary>
    public decimal Amount()
    {
        var text = Text();
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            throw new MessageFormatException(Path, $"'{text}' is not a decimal amount.");
        }

        if (amount <= 0)
        {
            throw new MessageFormatException(Path, "must be greater than zero.");
        }

        return decimal.Round(amount, 2) == amount ? amount : throw new MessageFormatException(Path, "must have at most two decimal places.");
    }

    public DateOnly Date()
    {
        var text = Text();
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new MessageFormatException(Path, $"'{text}' is not a date (yyyy-MM-dd).");
    }
}

namespace PaymentsBench.Iso20022;

/// <summary>
/// An ISO 20022 document the edge cannot accept: not well-formed, the wrong namespace, a missing
/// element, a value that is not valid. <see cref="ElementPath"/> names where, so the caller can fix it.
/// </summary>
public class MessageFormatException : Exception
{
    public MessageFormatException(string elementPath, string message)
        : base($"{elementPath}: {message}")
    {
        ElementPath = elementPath;
    }

    public MessageFormatException()
    {
        ElementPath = string.Empty;
    }

    public MessageFormatException(string message)
        : base(message)
    {
        ElementPath = string.Empty;
    }

    public MessageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
        ElementPath = string.Empty;
    }

    /// <summary>The path of the offending element, for example <c>Document/CstmrCdtTrfInitn/PmtInf[1]/PmtId</c>.</summary>
    public string ElementPath { get; }
}

/// <summary>The document is larger than the limit the caller set.</summary>
public sealed class MessageTooLargeException : MessageFormatException
{
    public MessageTooLargeException(long limitBytes)
        : base("Document", $"larger than the {limitBytes} byte limit.")
    {
    }

    public MessageTooLargeException()
    {
    }

    public MessageTooLargeException(string message)
        : base(message)
    {
    }

    public MessageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

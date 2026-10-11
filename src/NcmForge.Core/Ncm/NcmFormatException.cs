namespace NcmForge.Core.Ncm;

/// <summary>Raised when a file is not a well-formed .ncm container.</summary>
public sealed class NcmFormatException : Exception
{
    public NcmFormatException(string message) : base(message)
    {
    }

    public NcmFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

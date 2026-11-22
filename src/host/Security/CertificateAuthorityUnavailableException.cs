namespace ButtonBox.Security;

/// <summary>
/// The local CA exists on disk but cannot be used (half-present, undecryptable or inconsistent).
/// Never recovered by creating a new CA: that would force every paired device to pair again.
/// </summary>
public sealed class CertificateAuthorityUnavailableException : Exception
{
    public CertificateAuthorityUnavailableException(string message) : base(message)
    {
    }

    public CertificateAuthorityUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

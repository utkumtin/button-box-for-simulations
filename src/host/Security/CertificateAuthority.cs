using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ButtonBox.Core;

namespace ButtonBox.Security;

/// <summary>
/// The per-install local CA (decisions §9). The CA key lives DPAPI-protected on disk and is decrypted only while signing.
/// The CA can only sign for <c>*.local</c> and private IPv4 ranges (X.509 Name Constraints).
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(ProductInfo.Name + "/ca-key/v1");

    private static readonly (byte[] Address, byte[] Mask)[] PermittedRanges =
    [
        ([10, 0, 0, 0], [255, 0, 0, 0]),
        ([172, 16, 0, 0], [255, 240, 0, 0]),
        ([192, 168, 0, 0], [255, 255, 0, 0]),
    ];

    private readonly string _keyPath;
    private readonly string _serverKeyName;
    private readonly X509Certificate2 _certificate;

    private CertificateAuthority(X509Certificate2 certificate, string keyPath, string serverKeyName)
    {
        _certificate = certificate;
        _keyPath = keyPath;
        _serverKeyName = serverKeyName;
        CertificateDer = certificate.Export(X509ContentType.Cert);
        Sha256Fingerprint = Convert.ToHexString(SHA256.HashData(CertificateDer));
    }

    /// <summary>DER of the CA certificate (public; this is what phones download).</summary>
    public byte[] CertificateDer { get; }

    /// <summary>SHA-256 of <see cref="CertificateDer"/>, 64 upper-case hex digits.</summary>
    public string Sha256Fingerprint { get; }

    /// <summary>
    /// Loads the CA from <paramref name="caDirectory"/> (ca.cer + ca.key) or creates it when neither file exists.
    /// Throws <see cref="CertificateAuthorityUnavailableException"/> when only one file exists, the key cannot be decrypted,
    /// or certificate and key do not match.
    /// </summary>
    public static CertificateAuthority LoadOrCreate(string caDirectory, string serverKeyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(caDirectory);
        ArgumentException.ThrowIfNullOrEmpty(serverKeyName);
        var certPath = Path.Combine(caDirectory, "ca.cer");
        var keyPath = Path.Combine(caDirectory, "ca.key");
        var hasCert = File.Exists(certPath);
        var hasKey = File.Exists(keyPath);

        if (hasCert != hasKey)
        {
            throw new CertificateAuthorityUnavailableException(
                "The CA is incomplete: exactly one of ca.cer and ca.key exists. Restore the missing file or reset pairing on purpose.");
        }

        if (hasCert)
        {
            X509Certificate2 loaded;
            try
            {
                loaded = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(certPath));
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            {
                throw new CertificateAuthorityUnavailableException("The CA certificate cannot be read.", e);
            }

            try
            {
                using var key = ReadKey(keyPath);
                using var certKey = loaded.GetECDsaPublicKey();
                if (certKey is null
                    || !CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), certKey.ExportSubjectPublicKeyInfo()))
                {
                    throw new CertificateAuthorityUnavailableException("The CA certificate and key do not belong together.");
                }
            }
            catch
            {
                loaded.Dispose();
                throw;
            }

            return new CertificateAuthority(loaded, keyPath, serverKeyName);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cn = $"{ProductInfo.Name} Local CA {Convert.ToHexString(RandomNumberGenerator.GetBytes(2))}";
        var request = new CertificateRequest($"CN={cn}", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.30"), EncodeNameConstraints(), true));
        var now = DateTimeOffset.UtcNow;
        using var selfSigned = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(10));
        var der = selfSigned.Export(X509ContentType.Cert);

        var plain = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            AtomicFile.WriteAllBytes(keyPath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // The certificate is written last: a crash in between leaves a lone key, which is refused loudly on the next start.
        AtomicFile.WriteAllBytes(certPath, der);
        return new CertificateAuthority(X509CertificateLoader.LoadCertificate(der), keyPath, serverKeyName);
    }

    /// <summary>
    /// Issues a server certificate for <paramref name="dnsName"/> (<c>name.local</c>) and the given private IPv4 addresses.
    /// The key is a persisted per-user CNG key named like the serverKeyName passed to <see cref="LoadOrCreate"/>; each call replaces it.
    /// The caller must stop the server that uses the previous certificate and dispose that certificate before calling again,
    /// because the key name is reused.
    /// </summary>
    public X509Certificate2 IssueServerCertificate(string dnsName, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(dnsName);
        ArgumentNullException.ThrowIfNull(addresses);
        if (!IsPermittedDnsName(dnsName))
        {
            throw new ArgumentException("The DNS name must be a single label followed by .local.", nameof(dnsName));
        }

        foreach (var address in addresses)
        {
            if (!IsPermittedAddress(address))
            {
                throw new ArgumentException("Every address must be an IPv4 address in 10/8, 172.16/12 or 192.168/16.", nameof(addresses));
            }
        }

        return IssueServerCertificateUnchecked(dnsName, addresses);
    }

    /// <summary>Removes the persisted server key. No-op when absent.</summary>
    public static void DeleteServerKey(string serverKeyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverKeyName);
        if (CngKey.Exists(serverKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.None))
        {
            using var key = CngKey.Open(serverKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.None);
            key.Delete();
        }
    }

    public void Dispose() => _certificate.Dispose();

    // Test hook (InternalsVisibleTo): proves the Name Constraints, not just our own input checks, stop foreign names.
    internal X509Certificate2 IssueServerCertificateUnchecked(string dnsName, IReadOnlyList<IPAddress> addresses)
    {
        var creation = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
        };
        using var cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, _serverKeyName, creation);
        using var serverKey = new ECDsaCng(cngKey);

        var request = new CertificateRequest($"CN={dnsName}", serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        foreach (var address in addresses)
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var caSki = _certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single();
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(caSki));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        if (serial[0] == 0)
        {
            serial[0] = 1;
        }

        var now = DateTimeOffset.UtcNow;
        X509Certificate2 signed;
        // The CA key exists in memory only inside this block.
        using (var caKey = ReadKey(_keyPath))
        using (var caWithKey = _certificate.CopyWithPrivateKey(caKey))
        {
            signed = request.Create(caWithKey, now.AddHours(-1), now.AddDays(397).AddHours(-1), serial);
        }

        using (signed)
        {
            return signed.CopyWithPrivateKey(serverKey);
        }
    }

    private static ECDsa ReadKey(string keyPath)
    {
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(plain, out _);
                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new CertificateAuthorityUnavailableException("The CA key cannot be read or decrypted (wrong Windows user or damaged file).", e);
        }
        finally
        {
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    private static bool IsPermittedDnsName(string name)
    {
        const string suffix = ".local";
        if (name.Length <= suffix.Length || name.Length > 63 + suffix.Length || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var label = name.AsSpan(0, name.Length - suffix.Length);
        if (label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        foreach (var c in label)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPermittedAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        foreach (var (range, mask) in PermittedRanges)
        {
            var inside = true;
            for (var i = 0; i < 4; i++)
            {
                inside &= (bytes[i] & mask[i]) == range[i];
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    // permitted: dNSName "local", iPAddress 10/8, 172.16/12, 192.168/16; nothing excluded.
    internal static byte[] EncodeNameConstraints()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            using (writer.PushSequence())
            {
                writer.WriteCharacterString(UniversalTagNumber.IA5String, "local", new Asn1Tag(TagClass.ContextSpecific, 2));
            }

            foreach (var (address, mask) in PermittedRanges)
            {
                using (writer.PushSequence())
                {
                    writer.WriteOctetString([.. address, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }
        }

        return writer.Encode();
    }
}

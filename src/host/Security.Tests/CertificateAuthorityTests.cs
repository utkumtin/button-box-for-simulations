using Xunit;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ButtonBox.Security;

namespace ButtonBox.Security.Tests;

public sealed class CertificateAuthorityTests : IDisposable
{
    private const string Host = "buttonbox-test.local";
    private static readonly IPAddress[] Lan = [IPAddress.Parse("192.168.1.20")];

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bb-ca-" + Guid.NewGuid().ToString("N"));
    // Never the product key name: tests must not touch a developer's real server key.
    private readonly string _keyName = "bb-test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        CertificateAuthority.DeleteServerKey(_keyName);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private CertificateAuthority NewCa() => CertificateAuthority.LoadOrCreate(_dir, _keyName);

    private static X509Certificate2 Public(CertificateAuthority ca) => X509CertificateLoader.LoadCertificate(ca.CertificateDer);

    private static X509Chain NewChain(X509Certificate2 ca)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain;
    }

    // Why: the Name Constraints are what limit the damage if the CA key ever leaks; they must be critical and exactly this set.
    [Fact]
    public void Name_constraints_are_critical_and_permit_only_local_and_private_ranges()
    {
        using var ca = NewCa();
        using var cert = Public(ca);
        var ext = cert.Extensions.Single(e => e.Oid?.Value == "2.5.29.30");
        Assert.True(ext.Critical);

        var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
        var seq = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var permitted = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        Assert.False(seq.HasData, "no excluded subtrees");

        var subtrees = new List<string>();
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            if (subtree.PeekTag().TagValue == 2)
            {
                subtrees.Add("dns:" + subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
            }
            else
            {
                var bytes = subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                subtrees.Add("ip:" + new IPAddress(bytes[..4]) + "/" + new IPAddress(bytes[4..]));
            }

            subtree.ThrowIfNotEmpty();
        }

        Assert.Equal(["dns:local", "ip:10.0.0.0/255.0.0.0", "ip:172.16.0.0/255.240.0.0", "ip:192.168.0.0/255.255.0.0"], subtrees);
    }

    // Why: pathLen 0 and keyCertSign-only stop the CA from being used to mint further CAs.
    [Fact]
    public void Basic_constraints_and_key_usage_are_critical()
    {
        using var ca = NewCa();
        using var cert = Public(ca);
        var bc = cert.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(bc.Critical);
        Assert.True(bc.CertificateAuthority);
        Assert.True(bc.HasPathLengthConstraint);
        Assert.Equal(0, bc.PathLengthConstraint);
        var ku = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(ku.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, ku.KeyUsages);
        Assert.Contains("Local CA", cert.Subject);
        Assert.Matches(@"\A[0-9A-F]{64}\z", ca.Sha256Fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(ca.CertificateDer)), ca.Sha256Fingerprint);
    }

    // Why: a CA key lying around in plaintext would let any process of the user mint certificates for the LAN.
    [Fact]
    public void Key_file_is_protected_and_reload_gives_the_same_ca()
    {
        string fingerprint;
        using (var ca = NewCa())
        {
            fingerprint = ca.Sha256Fingerprint;
        }

        var stored = File.ReadAllBytes(System.IO.Path.Combine(_dir, "ca.key"));
        // Reach the plaintext PKCS#8 the same way the product does (DPAPI) and make sure it is not what is on disk.
        var plain = ProtectedData.Unprotect(stored, System.Text.Encoding.UTF8.GetBytes("button-box-for-simulations/ca-key/v1"), DataProtectionScope.CurrentUser);
        Assert.NotEqual(plain, stored);
        Assert.False(stored.AsSpan().IndexOf(plain.AsSpan(plain.Length - 32)) >= 0, "key material must not appear in the file");

        using var reloaded = NewCa();
        Assert.Equal(fingerprint, reloaded.Sha256Fingerprint);
        Assert.True(File.Exists(System.IO.Path.Combine(_dir, "ca.cer")));
    }

    // Why: silently minting a new CA would invalidate every paired phone; the user must decide that on purpose.
    [Fact]
    public void Missing_key_or_cert_throws_instead_of_creating_a_new_ca()
    {
        using (NewCa())
        {
        }

        var key = System.IO.Path.Combine(_dir, "ca.key");
        var cer = System.IO.Path.Combine(_dir, "ca.cer");
        var cerBytes = File.ReadAllBytes(cer);
        var keyBytes = File.ReadAllBytes(key);

        File.Delete(key);
        Assert.Throws<CertificateAuthorityUnavailableException>(NewCa);
        Assert.False(File.Exists(key));

        File.WriteAllBytes(key, keyBytes);
        File.Delete(cer);
        Assert.Throws<CertificateAuthorityUnavailableException>(NewCa);
        Assert.False(File.Exists(cer));

        File.WriteAllBytes(cer, cerBytes);
        using var again = NewCa();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cerBytes)), again.Sha256Fingerprint);
    }

    // Why: a damaged key and a key from another CA must both stop the app instead of producing certs nobody trusts.
    [Fact]
    public void Corrupt_key_or_mismatched_cert_throws()
    {
        using (NewCa())
        {
        }

        var key = System.IO.Path.Combine(_dir, "ca.key");
        var original = File.ReadAllBytes(key);
        File.WriteAllBytes(key, [1, 2, 3, 4]);
        Assert.Throws<CertificateAuthorityUnavailableException>(NewCa);
        File.WriteAllBytes(key, original);

        var otherDir = _dir + "-other";
        try
        {
            using (CertificateAuthority.LoadOrCreate(otherDir, _keyName))
            {
            }

            File.Copy(System.IO.Path.Combine(otherDir, "ca.cer"), System.IO.Path.Combine(_dir, "ca.cer"), overwrite: true);
            Assert.Throws<CertificateAuthorityUnavailableException>(NewCa);
        }
        finally
        {
            if (Directory.Exists(otherDir))
            {
                Directory.Delete(otherDir, true);
            }
        }
    }

    // Why: this is the trust path phones use; a cert that does not chain to the CA is useless.
    [Fact]
    public void Server_certificate_chains_to_the_ca_and_has_the_expected_shape()
    {
        using var ca = NewCa();
        using var caCert = Public(ca);
        using var server = ca.IssueServerCertificate(Host, Lan);
        using var chain = NewChain(caCert);
        Assert.True(chain.Build(server), string.Join(";", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.Empty(chain.ChainStatus);
        Assert.True(server.HasPrivateKey);
        Assert.True(server.NotAfter - server.NotBefore < TimeSpan.FromDays(398));
        var san = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(Host, san.EnumerateDnsNames());
        Assert.Contains(Lan[0], san.EnumerateIPAddresses());
        Assert.False(server.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    // Why: Name Constraints are enforced by the client, not by us; this proves the CA cert itself blocks foreign names
    // even if our own input checks were bypassed.
    [Fact]
    public void Certificate_for_a_foreign_name_fails_the_name_constraint()
    {
        using var ca = NewCa();
        using var caCert = Public(ca);
        using var foreign = ca.IssueServerCertificateUnchecked("example.com", Lan);
        using var chain = NewChain(caCert);
        Assert.False(chain.Build(foreign));
        Assert.Contains(chain.ChainStatus, s => s.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));

        using var publicIp = ca.IssueServerCertificateUnchecked(Host, [IPAddress.Parse("8.8.8.8")]);
        using var chain2 = NewChain(caCert);
        Assert.False(chain2.Build(publicIp));
        Assert.Contains(chain2.ChainStatus, s => s.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    // Why: our own input checks are the first line of defence; they must refuse what the CA must never sign.
    [Theory]
    [InlineData("example.com")]
    [InlineData("a.b.local")]
    [InlineData(".local")]
    [InlineData("local")]
    [InlineData("-a.local")]
    [InlineData("a_b.local")]
    [InlineData("a.local\n")]
    [InlineData("")]
    public void Foreign_dns_names_are_rejected(string name)
    {
        using var ca = NewCa();
        Assert.Throws<ArgumentException>(() => ca.IssueServerCertificate(name, Lan));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void Public_or_non_ipv4_addresses_are_rejected(string address)
    {
        using var ca = NewCa();
        Assert.Throws<ArgumentException>(() => ca.IssueServerCertificate(Host, [IPAddress.Parse(address)]));
    }

    // Why: the whole private ranges, including their edges, must be accepted or LAN setups would break.
    [Fact]
    public void Private_range_edges_are_accepted()
    {
        using var ca = NewCa();
        using var cert = ca.IssueServerCertificate(Host, new[] { "10.255.255.255", "172.16.0.0", "172.31.255.255", "192.168.0.1" }.Select(IPAddress.Parse).ToList());
        Assert.NotNull(cert);
    }

    // Why: Kestrel/Schannel rejected ephemeral keys in spike S1; this is the proof that our persisted-key certificate works,
    // including after the key was replaced for a new certificate (IP change).
    [Fact]
    public async Task Tls_handshake_works_with_persisted_key_certificate_and_after_reissue()
    {
        using var ca = NewCa();
        using var caCert = Public(ca);
        for (var round = 0; round < 2; round++)
        {
            using var server = ca.IssueServerCertificate(Host, Lan);
            await HandshakeAsync(server, caCert);
        }
    }

    // Why: a client that trusts only our CA must refuse a certificate from any other CA.
    [Fact]
    public async Task Handshake_fails_when_the_client_trusts_a_different_ca()
    {
        using var ca = NewCa();
        var otherDir = _dir + "-other";
        try
        {
            using var other = CertificateAuthority.LoadOrCreate(otherDir, _keyName + "-o");
            using var otherCert = Public(other);
            using var server = ca.IssueServerCertificate(Host, Lan);
            await Assert.ThrowsAnyAsync<Exception>(() => HandshakeAsync(server, otherCert));
        }
        finally
        {
            CertificateAuthority.DeleteServerKey(_keyName + "-o");
            if (Directory.Exists(otherDir))
            {
                Directory.Delete(otherDir, true);
            }
        }
    }

    private static async Task HandshakeAsync(X509Certificate2 serverCert, X509Certificate2 trustedCa)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                using var ssl = new SslStream(accepted.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var buffer = new byte[4];
                await ssl.ReadExactlyAsync(buffer);
                await ssl.WriteAsync(buffer);
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientSsl = new SslStream(client.GetStream(), false, (_, cert, _, errors) =>
            {
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 || cert is null)
                {
                    return false;
                }

                using var chain = NewChain(trustedCa);
                return chain.Build(new X509Certificate2(cert)) && chain.ChainStatus.Length == 0;
            });
            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host });
            await clientSsl.WriteAsync(new byte[] { 1, 2, 3, 4 });
            var echo = new byte[4];
            await clientSsl.ReadExactlyAsync(echo);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, echo);
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Verifies the managed OpenSSL <c>X509_NAME_hash</c> implementation against values produced
/// by real OpenSSL (3.0.13, Debian 13): each expected hash below was captured empirically via
/// <c>openssl x509 -noout -subject_hash</c>. libldap's <c>TrustedCertificatesDirectory</c>
/// lookup on Linux only finds the CA if these names match OpenSSL's, so a drift here breaks
/// LDAPS trust at runtime.
/// </summary>
public class OpenSslSubjectHashTests
{
    [Theory]
    // openssl req -subj '/CN=Aspire OpenLDAP Probe CA'
    [InlineData("CN=Aspire OpenLDAP Probe CA", 0x60d2801fu)]
    // Canonicalization: ASCII case-folding and whitespace collapsing must yield the SAME hash —
    // openssl reports 60d2801f for '/CN=ASPIRE  OPENLDAP   PROBE CA' too.
    [InlineData("CN=ASPIRE  OPENLDAP   PROBE CA", 0x60d2801fu)]
    // openssl req -subj '/C=US/ST=Test State/O=Ex Ample/CN=Multi RDN Test'
    [InlineData("CN=Multi RDN Test, O=Ex Ample, ST=Test State, C=US", 0xc6e026e5u)]
    // openssl req -utf8 -subj '/CN=Ünïcode Tæst CA' — non-ASCII passes through un-lowercased
    [InlineData("CN=Ünïcode Tæst CA", 0xa97cd82fu)]
    public void Subject_Hash_Matches_OpenSsl(string subject, uint expected)
    {
        using var cert = CreateSelfSigned(subject);
        Assert.Equal(expected, OpenLdapUnixTlsTrust.ComputeOpenSslSubjectHash(cert));
    }

    [Fact]
    public void EnsureTrustDirectory_Stages_Pem_Under_Its_Subject_Hash_Name()
    {
        var caPath = Path.Combine(Path.GetTempPath(), $"aspire-openldap-test-ca-{Guid.NewGuid():N}.crt");
        string? trustDir = null;
        try
        {
            using var cert = CreateSelfSigned("CN=Aspire OpenLDAP Probe CA");
            File.WriteAllText(caPath, cert.ExportCertificatePem());

            trustDir = OpenLdapUnixTlsTrust.EnsureTrustDirectory(caPath);

            var hashedPath = Path.Combine(trustDir, "60d2801f.0");
            Assert.True(File.Exists(hashedPath), $"expected staged CA at {hashedPath}");
            Assert.Equal(File.ReadAllText(caPath), File.ReadAllText(hashedPath));

            // Idempotent: same CA file stages into the same directory.
            Assert.Equal(trustDir, OpenLdapUnixTlsTrust.EnsureTrustDirectory(caPath));
        }
        finally
        {
            File.Delete(caPath);
            DeleteTrustDir(trustDir);
        }
    }

    [Fact]
    public void EnsureTrustDirectory_Separates_Different_Cas()
    {
        var caPath1 = Path.Combine(Path.GetTempPath(), $"aspire-openldap-test-ca-{Guid.NewGuid():N}.crt");
        var caPath2 = Path.Combine(Path.GetTempPath(), $"aspire-openldap-test-ca-{Guid.NewGuid():N}.crt");
        string? trustDir1 = null, trustDir2 = null;
        try
        {
            using var ca1 = CreateSelfSigned("CN=First CA");
            using var ca2 = CreateSelfSigned("CN=Second CA");
            File.WriteAllText(caPath1, ca1.ExportCertificatePem());
            File.WriteAllText(caPath2, ca2.ExportCertificatePem());

            trustDir1 = OpenLdapUnixTlsTrust.EnsureTrustDirectory(caPath1);
            trustDir2 = OpenLdapUnixTlsTrust.EnsureTrustDirectory(caPath2);

            // Different CA content must never share a directory — libldap trusts everything in
            // the configured directory, so a shared path would silently widen trust.
            Assert.NotEqual(trustDir1, trustDir2);
        }
        finally
        {
            File.Delete(caPath1);
            File.Delete(caPath2);
            DeleteTrustDir(trustDir1);
            DeleteTrustDir(trustDir2);
        }
    }

    // Trust directories are content-addressed under the system temp path; the freshly
    // generated per-run CAs above are unique, so deleting them cannot race another test.
    private static void DeleteTrustDir(string? trustDir)
    {
        if (trustDir is not null && Directory.Exists(trustDir))
        {
            Directory.Delete(trustDir, recursive: true);
        }
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}

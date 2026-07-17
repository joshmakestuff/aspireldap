using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// The generated-certificate cache must only be reused when the full set (CA, server cert,
/// server key) is valid and mutually consistent — a corrupt or mismatched cached file used
/// to survive as "fresh" for up to two years as long as server.crt itself parsed.
/// </summary>
public class CertificateGeneratorTests : IDisposable
{
    private readonly string _appHostDir = Directory.CreateTempSubdirectory("aspire-openldap-certtest-").FullName;

    [Fact]
    public void Valid_Cached_Set_Is_Reused()
    {
        var first = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var firstServerCert = File.ReadAllText(first.ServerCertPath);

        var second = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.Equal(first.ServerCertPath, second.ServerCertPath);
        Assert.Equal(firstServerCert, File.ReadAllText(second.ServerCertPath));
    }

    [Fact]
    public void Corrupt_Ca_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        File.WriteAllText(certs.CaCertPath, "not a certificate");

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual("not a certificate", File.ReadAllText(regenerated.CaCertPath));
        AssertConsistentSet(regenerated);
    }

    [Fact]
    public void Mismatched_Server_Key_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var originalCert = File.ReadAllText(certs.ServerCertPath);

        using (var unrelatedKey = RSA.Create(2048))
        {
            File.WriteAllText(certs.ServerKeyPath, unrelatedKey.ExportRSAPrivateKeyPem());
        }

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual(originalCert, File.ReadAllText(regenerated.ServerCertPath));
        AssertConsistentSet(regenerated);
    }

    [Fact]
    public void Wrong_Root_Ca_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var originalCert = File.ReadAllText(certs.ServerCertPath);

        // A parseable CA that did NOT sign the cached server certificate — the old
        // expiry-only check accepted this silently.
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request = new CertificateRequest("CN=Unrelated CA", key, HashAlgorithmName.SHA256);
            using var unrelatedCa = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
            File.WriteAllText(certs.CaCertPath, unrelatedCa.ExportCertificatePem());
        }

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual(originalCert, File.ReadAllText(regenerated.ServerCertPath));
        AssertConsistentSet(regenerated);
    }

    private static void AssertConsistentSet(OpenLdapCertificateGenerator.GeneratedCertificates certs)
    {
        // Pairing throws if the key doesn't match the certificate.
        using var serverCert = X509Certificate2.CreateFromPemFile(certs.ServerCertPath, certs.ServerKeyPath);
        using var caCert = Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.LoadPemCertificate(certs.CaCertPath);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        Assert.True(chain.Build(serverCert), "regenerated server certificate must chain to the regenerated CA");
    }

    public void Dispose() => Directory.Delete(_appHostDir, recursive: true);
}

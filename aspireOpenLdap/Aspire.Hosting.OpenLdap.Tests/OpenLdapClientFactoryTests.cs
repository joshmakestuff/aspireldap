using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// CA-trust loading in <see cref="OpenLdapClientFactory"/>: a configured CaCertFile is an
/// explicit trust choice, so a missing or unloadable file must fail closed with an actionable
/// error instead of silently falling back to the platform trust store.
/// </summary>
public class OpenLdapClientFactoryTests
{
    private static OpenLdapClientFactory CreateFactory(string caCertFile, OpenLdapClientSettings? settings = null)
    {
        var connectionString = OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldaps://localhost:1636;BaseDN=dc=example,dc=org;" +
            "BindDN=cn=admin,dc=example,dc=org;BindPassword=pw;" +
            $"CaCertFile={Aspire.OpenLdap.ConnectionStringQuoting.Quote(caCertFile)}");
        return new OpenLdapClientFactory(connectionString, settings ?? new OpenLdapClientSettings());
    }

    [Fact]
    public void CreateConnection_Throws_When_Configured_Ca_File_Is_Missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.crt");
        var factory = CreateFactory(missing);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());
        Assert.Contains(missing, ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void CreateConnection_Throws_When_Configured_Ca_File_Is_Not_A_Certificate()
    {
        var malformed = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.crt");
        File.WriteAllText(malformed, "this is not a PEM certificate");
        try
        {
            var factory = CreateFactory(malformed);

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());
            Assert.Contains(malformed, ex.Message);
        }
        finally
        {
            File.Delete(malformed);
        }
    }

    [Fact]
    public void CreateConnection_Ignores_Missing_Ca_File_When_Custom_Trust_Is_Disabled()
    {
        // TrustConnectionStringCaCertificate=false is the documented opt-out to system trust;
        // only then may a dangling CaCertFile path be ignored.
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.crt");
        var factory = CreateFactory(missing, new OpenLdapClientSettings { TrustConnectionStringCaCertificate = false });

        using var connection = factory.CreateConnection();
        Assert.NotNull(connection);
    }

    [Theory]
    [InlineData("Endpoint=ldap://h:1389;BaseDN=a;BindDN=b;BindPassword=c", true)]                    // plain ldap
    [InlineData("Endpoint=ldaps://h:1636;BaseDN=a;BindDN=b;BindPassword=c", true)]                   // ldaps, no CA file
    [InlineData("Endpoint=ldaps://h:1636;BaseDN=a;BindDN=b;BindPassword=c;CaCertFile=x", false)]     // custom trust disabled
    public void CreateConnection_Rejects_Inert_DisableTlsHostnameValidation(string connectionString, bool trustCa)
    {
        // The setting only modifies the custom-CA trust path; any configuration where that
        // path is not in play must fail loud instead of silently ignoring the opt-out.
        var factory = new OpenLdapClientFactory(
            OpenLdapConnectionStringBuilder.Parse(connectionString),
            new OpenLdapClientSettings
            {
                DisableTlsHostnameValidation = true,
                TrustConnectionStringCaCertificate = trustCa,
            });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());
        Assert.Contains("DisableTlsHostnameValidation", ex.Message);
    }

    [Fact]
    public void CreateConnection_Succeeds_With_A_Valid_Ca_File()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=factory-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"ca-{Guid.NewGuid():N}.crt");
        File.WriteAllText(path, ca.ExportCertificatePem());
        try
        {
            var factory = CreateFactory(path);

            if (OperatingSystem.IsMacOS())
            {
                // Explicit platform expectation, not a silent zero-assertion pass: macOS
                // LDAP.framework supports neither the managed verification callback nor
                // OpenSSL-style trust options, so custom CA trust is refused up front with
                // the documented actionable message.
                var ex = Assert.Throws<PlatformNotSupportedException>(() => factory.CreateConnection());
                Assert.Contains(nameof(OpenLdapClientSettings.TrustConnectionStringCaCertificate), ex.Message);
                return;
            }

            using var connection = factory.CreateConnection();
            Assert.NotNull(connection);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

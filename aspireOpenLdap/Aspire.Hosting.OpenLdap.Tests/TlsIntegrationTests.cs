using System.DirectoryServices.Protocols;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-backed LDAPS coverage through the REAL connection paths — the AppHost health check
/// and <see cref="OpenLdapClientFactory"/> — not just the managed validation helper. On Linux
/// (CI) this crosses the native libldap boundary via <c>TrustedCertificatesDirectory</c>; on
/// Windows it exercises the <c>VerifyServerCertificate</c> callback. Regression guard for the
/// review finding that generated-CA LDAPS failed on Linux before the first request.
/// </summary>
public class TlsIntegrationTests
{
    [Fact]
    public async Task RequiredTls_Resource_Is_Healthy_And_Client_Searches_Over_Ldaps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(["--OpenLdap:Tls=true"], cts.Token);

        await using var app = await appHost.BuildAsync(cts.Token);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await app.StartAsync(cts.Token);

        // On Linux the health check itself connects over LDAPS through native libldap trust,
        // so reaching healthy is already the positive native-path assertion.
        await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("openldap", cts.Token);
        Assert.NotNull(connectionString);

        var settings = OpenLdapConnectionStringBuilder.Parse(connectionString!);
        Assert.True(settings.UsesLdaps);
        Assert.NotNull(settings.CaCertFile);

        if (OperatingSystem.IsMacOS())
        {
            // The client factory refuses custom CA trust on macOS (Apple LDAP.framework
            // limitation); the hosting-side health gate above is the macOS coverage.
            return;
        }

        // Positive: the client integration's real connection path trusts the generated CA.
        var factory = new OpenLdapClientFactory(settings, new OpenLdapClientSettings());
        using (var connection = factory.CreateConnection())
        {
            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest(settings.BaseDn, "(objectClass=*)", SearchScope.Base, "dn"));
            Assert.Equal(ResultCode.Success, response.ResultCode);
        }

        // Negative: a server certificate that does not chain to the trusted CA must be
        // rejected by the actual connection path (native handshake on Linux, callback on
        // Windows) — not merely by a managed helper returning false.
        var wrongCaPath = Path.Combine(Path.GetTempPath(), $"aspire-openldap-wrong-ca-{Guid.NewGuid():N}.crt");
        try
        {
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var request = new CertificateRequest("CN=Wrong Root CA", key, HashAlgorithmName.SHA256);
                using var wrongCa = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                await File.WriteAllTextAsync(wrongCaPath, wrongCa.ExportCertificatePem(), cts.Token);
            }

            var wrongSettings = new OpenLdapConnectionStringBuilder
            {
                Endpoint = settings.Endpoint,
                BaseDn = settings.BaseDn,
                BindDn = settings.BindDn,
                BindPassword = settings.BindPassword,
                CaCertFile = wrongCaPath,
            };
            var wrongFactory = new OpenLdapClientFactory(wrongSettings, new OpenLdapClientSettings());
            using var badConnection = wrongFactory.CreateConnection();
            Assert.Throws<LdapException>(() => badConnection.SendRequest(
                new SearchRequest("", "(objectClass=*)", SearchScope.Base, "dn")));
        }
        finally
        {
            File.Delete(wrongCaPath);
        }
    }
}

using System.DirectoryServices.Protocols;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using AspireOpenLdap.TestAppHost;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-backed LDAPS coverage through the REAL connection paths — the AppHost health check
/// and <see cref="OpenLdapClientFactory"/> — not just the managed validation helper. On Linux
/// (CI) this crosses the native libldap boundary via <c>TrustedCertificatesDirectory</c>; on
/// Windows it exercises the <c>VerifyServerCertificate</c> callback. Regression guard for the
/// review finding that generated-CA LDAPS failed on Linux before the first request.
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class TlsIntegrationTests(AppHostFixture appHost, ITestOutputHelper output)
{
    private const string MacOsLimitation =
        "LIMITATION (macOS): client-side custom CA trust is unsupported (Apple LDAP.framework " +
        "rejects both the managed verification callback and OpenSSL-style trust options). The " +
        "client half of this LDAPS witness did not run; the AppHost health gate is the macOS " +
        "coverage. See docs/testing.md.";

    [Fact]
    public async Task RequiredTls_Resource_Is_Healthy_And_Client_Searches_Over_Ldaps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        // Reaching healthy is already the positive native-path assertion: on Linux the health
        // check itself connects over LDAPS through native libldap trust.
        var started = await appHost.StartAsync(TestAppHostScenarios.Tls, cts.Token);

        var settings = started.Settings;
        Assert.True(settings.UsesLdaps);
        Assert.NotNull(settings.CaCertFile);

        // #72: Build() is a second emitter of this format, in the client package, and the
        // hosting emitter cannot use it (the password is a deferred ParameterResource). This
        // pins the two against REAL emitted output — including the CaCertFile arm — so key
        // names, order, and quoting cannot drift apart across the package boundary.
        Assert.Equal(started.ConnectionString, settings.Build());

        if (OperatingSystem.IsMacOS())
        {
            // macOS reaches here with LESS coverage than Linux/Windows, and that reduction is
            // asserted rather than silently returned (#64): Apple's LDAP.framework supports
            // neither the managed verification callback nor OpenSSL-style trust options, so the
            // client factory refuses custom CA trust up front with an actionable message. If
            // that refusal ever stops happening, this test must fail rather than quietly skip
            // the whole client half of the witness.
            var unsupported = Assert.Throws<PlatformNotSupportedException>(
                () => new OpenLdapClientFactory(settings, new OpenLdapClientSettings()).CreateConnection());
            Assert.Contains(
                nameof(OpenLdapClientSettings.TrustConnectionStringCaCertificate),
                unsupported.Message,
                StringComparison.Ordinal);

            // Everything below needs client-side custom CA trust, which macOS cannot provide.
            // The hosting-side health gate asserted above is the macOS LDAPS coverage. Reported
            // to the test log so a green macOS run states what it did NOT cover (xunit 2.9 has
            // no dynamic skip, so the reduction is recorded rather than marked "skipped").
            output.WriteLine(MacOsLimitation);
            return;
        }

        // Positive: the client integration's real connection path trusts the generated CA.
        // SendRequest throws on a non-success result code, so the entry the base-scope search
        // returns is the assertion that carries information.
        var factory = new OpenLdapClientFactory(settings, new OpenLdapClientSettings());
        using (var connection = factory.CreateConnection())
        {
            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest(settings.BaseDn, "(objectClass=*)", SearchScope.Base, "dn"));
            var entry = Assert.Single(response.Entries.Cast<SearchResultEntry>());
            Assert.Equal(settings.BaseDn, entry.DistinguishedName, ignoreCase: true);
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

    [Fact]
    public async Task OptionalTls_Serves_Plain_Ldap_And_Ldaps_Side_By_Side()
    {
        // WithTls() WITHOUT WithRequiredTls(): the documented "LDAPS available, plain LDAP
        // still accepted" mode, previously only the required-TLS path had a runtime witness.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var started = await appHost.StartAsync(TestAppHostScenarios.TlsOptional, cts.Token);
        var settings = started.Settings;

        // Not required → the connection string stays plain ldap://, but advertises the CA so
        // clients can opt into the LDAPS endpoint.
        Assert.False(settings.UsesLdaps);
        Assert.NotNull(settings.CaCertFile);

        // Emitter equivalence on the ldap:// arm — see the note in the TLS-required test.
        Assert.Equal(started.ConnectionString, settings.Build());

        // Plain path serves.
        var plainFactory = new OpenLdapClientFactory(settings, new OpenLdapClientSettings());
        using (var connection = plainFactory.CreateConnection())
        {
            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest(settings.BaseDn, "(objectClass=*)", SearchScope.Base, "dn"));
            var entry = Assert.Single(response.Entries.Cast<SearchResultEntry>());
            Assert.Equal(settings.BaseDn, entry.DistinguishedName, ignoreCase: true);
        }

        // LDAPS is served side by side on the ldaps endpoint, trusted via the generated CA.
        var ldapsEndpoint = started.App.GetEndpoint("openldap", "ldaps");
        var ldapsSettings = new OpenLdapConnectionStringBuilder
        {
            Endpoint = new Uri($"ldaps://{ldapsEndpoint.Host}:{ldapsEndpoint.Port}"),
            BaseDn = settings.BaseDn,
            BindDn = settings.BindDn,
            BindPassword = settings.BindPassword,
            CaCertFile = settings.CaCertFile,
        };
        var ldapsFactory = new OpenLdapClientFactory(ldapsSettings, new OpenLdapClientSettings());

        if (OperatingSystem.IsMacOS())
        {
            // Same reduction as the required-TLS test, asserted rather than silently returned
            // (#64): the refusal is the macOS behavior, so it is what gets pinned here.
            var unsupported = Assert.Throws<PlatformNotSupportedException>(
                () => ldapsFactory.CreateConnection());
            Assert.Contains(
                nameof(OpenLdapClientSettings.TrustConnectionStringCaCertificate),
                unsupported.Message,
                StringComparison.Ordinal);
            output.WriteLine(MacOsLimitation);
            return;
        }

        using var ldapsConnection = ldapsFactory.CreateConnection();
        var ldapsResponse = (SearchResponse)ldapsConnection.SendRequest(
            new SearchRequest(settings.BaseDn, "(objectClass=*)", SearchScope.Base, "dn"));
        var ldapsEntry = Assert.Single(ldapsResponse.Entries.Cast<SearchResultEntry>());
        Assert.Equal(settings.BaseDn, ldapsEntry.DistinguishedName, ignoreCase: true);
    }
}

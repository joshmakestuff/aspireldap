using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Health check that performs an authenticated root DSE query against the OpenLDAP server
/// using the resource's admin credentials. Uses LDAPS when the resource requires TLS.
/// </summary>
internal sealed class OpenLdapHealthCheck(OpenLdapResource resource) : IHealthCheck
{
    // LDAP result code for invalid credentials (RFC 4511).
    private const int InvalidCredentialsResultCode = 49;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointName = resource.TlsRequired
                ? OpenLdapResource.LdapsEndpointName
                : OpenLdapResource.LdapEndpointName;

            var allocatedEndpoint = resource.GetEndpoint(endpointName);
            if (allocatedEndpoint is null || !allocatedEndpoint.IsAllocated)
            {
                return HealthCheckResult.Unhealthy($"LDAP endpoint '{endpointName}' is not allocated.");
            }

            var password = await resource.AdminPasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admin password parameter resolved to null.");

            var bindDn = $"cn={resource.AdminUsername},{resource.LdapRoot}";

            using var connection = new LdapConnection(
                new LdapDirectoryIdentifier(allocatedEndpoint.Host, allocatedEndpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindDn, password),
                Timeout = TimeSpan.FromSeconds(5),
            };
            connection.SessionOptions.ProtocolVersion = 3;

            if (resource.TlsRequired)
            {
                connection.SessionOptions.SecureSocketLayer = true;
                if (resource.CaCertHostPath is { } caPath)
                {
                    var ca = LoadCertificateFromPemFile(caPath);
                    connection.SessionOptions.VerifyServerCertificate = (_, serverCert) =>
                        ChainsTo(serverCert, ca);
                }
            }

            // Root DSE query: base DN = "", scope = Base
            var request = new SearchRequest(
                distinguishedName: "",
                ldapFilter: "(objectClass=*)",
                searchScope: SearchScope.Base,
                attributeList: ["namingContexts"]);

            var response = await Task.Run(
                () => (SearchResponse)connection.SendRequest(request),
                cancellationToken);

            if (response.ResultCode == ResultCode.Success && response.Entries.Count > 0)
            {
                return HealthCheckResult.Healthy("LDAP root DSE query succeeded.");
            }

            return HealthCheckResult.Unhealthy(
                $"LDAP root DSE query returned unexpected result: {response.ResultCode}");
        }
        catch (LdapException ex) when (ex.ErrorCode == InvalidCredentialsResultCode)
        {
            return HealthCheckResult.Unhealthy("LDAP authentication failed.", ex);
        }
        catch (LdapException ex)
        {
            return HealthCheckResult.Unhealthy("LDAP connection failed.", ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Unexpected error during LDAP health check.", ex);
        }
    }

    private static bool ChainsTo(X509Certificate serverCert, X509Certificate2 trustedRoot)
    {
        using var chainCert = X509CertificateLoader.LoadCertificate(serverCert.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreInvalidName;
        return chain.Build(chainCert);
    }

    private static X509Certificate2 LoadCertificateFromPemFile(string path)
    {
        var pemText = File.ReadAllText(path);
        var fields = PemEncoding.Find(pemText);
        var derBytes = Convert.FromBase64String(pemText[fields.Base64Data]);
        return X509CertificateLoader.LoadCertificate(derBytes);
    }
}

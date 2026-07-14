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
/// <remarks>
/// macOS path: <see cref="OpenLdapResourceBuilderExtensions.WithRequiredTls"/> skips the
/// server-side <c>LDAP_REQUIRE_TLS</c> enforcement on macOS, so the health check connects
/// to the plain LDAP port with the admin bind. See that method's remarks for the full
/// rationale (Apple's <c>LDAP.framework</c> can't trust our self-signed CA from managed
/// code). The Linux carve-out lives in the same place.
/// </remarks>
internal sealed class OpenLdapHealthCheck(OpenLdapResource resource) : IHealthCheck
{
    // LDAP result code for invalid credentials (RFC 4511).
    private const int InvalidCredentialsResultCode = 49;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var useTls = resource.TlsRequired && !OperatingSystem.IsMacOS();

        try
        {
            var endpointName = useTls
                ? OpenLdapResource.LdapsEndpointName
                : OpenLdapResource.LdapEndpointName;

            var allocatedEndpoint = resource.GetEndpoint(endpointName);
            if (allocatedEndpoint is null || !allocatedEndpoint.IsAllocated)
            {
                return HealthCheckResult.Unhealthy($"LDAP endpoint '{endpointName}' is not allocated.");
            }

            var password = await resource.AdminPasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admin password parameter resolved to null.");

            var bindDn = $"cn={resource.AdminUsername},{resource.BaseDn}";

            using var connection = new LdapConnection(
                new LdapDirectoryIdentifier(allocatedEndpoint.Host, allocatedEndpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindDn, password),
                Timeout = TimeSpan.FromSeconds(5),
            };
            connection.SessionOptions.ProtocolVersion = 3;

            if (useTls)
            {
                connection.SessionOptions.SecureSocketLayer = true;
                if (resource.CaCertHostPath is { } caPath)
                {
                    var ca = LoadCertificateFromPemFile(caPath);
                    connection.SessionOptions.VerifyServerCertificate = (_, serverCert) =>
                        ChainsTo(serverCert, ca);
                }
            }

            // Root DSE query: base DN = "", scope = Base.
            // The "aspire-healthcheck" attribute is a sentinel — slapd logs the attribute list
            // verbatim, so a downstream log parser can drop healthcheck probes by matching this
            // name. slapd returns nothing for it since it's not a real attribute.
            var request = new SearchRequest(
                distinguishedName: "",
                ldapFilter: "(objectClass=*)",
                searchScope: SearchScope.Base,
                attributeList: ["namingContexts", "aspire-healthcheck"]);

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
            return HealthCheckResult.Unhealthy(DescribeAuthFailure(), ex);
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

    private string DescribeAuthFailure()
    {
        // A persistent data mount that predates this run keeps whatever admin password the
        // directory was first initialized with — the container skips LDAP_ADMIN_PASSWORD when
        // it finds existing data — so the current credentials fail forever with err=49.
        var mounts = resource.Annotations.OfType<ContainerMountAnnotation>()
            .Where(m => m.Target == OpenLdapResource.DataPath)
            .ToList();

        if (mounts.Any(m => m.Type == ContainerMountType.Volume))
        {
            return "LDAP authentication failed. The data volume may predate this run and hold a " +
                "different admin password — the resource's \"Reset data volume\" command " +
                "reinitializes it with the current credentials.";
        }

        if (mounts.Count > 0)
        {
            return "LDAP authentication failed. The data bind mount may predate this run and hold " +
                "a different admin password — clear the host directory to reinitialize with the " +
                "current credentials.";
        }

        return "LDAP authentication failed.";
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

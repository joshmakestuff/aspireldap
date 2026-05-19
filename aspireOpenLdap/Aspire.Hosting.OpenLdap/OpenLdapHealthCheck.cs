using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Health check that performs an authenticated root DSE query against the OpenLDAP server
/// using the resource's admin credentials.
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
            var allocatedEndpoint = resource.GetEndpoint(OpenLdapResource.LdapEndpointName);
            if (allocatedEndpoint is null || !allocatedEndpoint.IsAllocated)
            {
                return HealthCheckResult.Unhealthy("LDAP endpoint is not allocated.");
            }

            var password = resource.AdminPasswordParameter?.Value
                ?? resource.AdminPassword
                ?? OpenLdapResource.DefaultAdminPassword;
            var bindDn = $"cn={resource.AdminUsername},{resource.LdapRoot}";

            using var connection = new LdapConnection(
                new LdapDirectoryIdentifier(allocatedEndpoint.Host, allocatedEndpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindDn, password),
                Timeout = TimeSpan.FromSeconds(5),
            };
            connection.SessionOptions.ProtocolVersion = 3;

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
}

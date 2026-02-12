using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Health check that performs an anonymous root DSE query against the OpenLDAP server.
/// A successful response indicates that the LDAP server is accepting connections.
/// </summary>
internal sealed class OpenLdapHealthCheck(OpenLdapResource resource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = resource.LdapEndpoint;
            var host = endpoint.Resource.Name; // container name for inter-container communication
            var port = resource.LdapEndpoint.Port;

            // Use the allocated host endpoint for health checks from the host machine.
            var allocatedEndpoint = resource.GetEndpoint(OpenLdapResource.LdapEndpointName);
            if (allocatedEndpoint is null || !allocatedEndpoint.IsAllocated)
            {
                return HealthCheckResult.Unhealthy("LDAP endpoint is not allocated.");
            }

            var ldapHost = allocatedEndpoint.Host;
            var ldapPort = allocatedEndpoint.Port;

            using var connection = new LdapConnection(
                new LdapDirectoryIdentifier(ldapHost, ldapPort, fullyQualifiedDnsHostName: false, connectionless: false));

            connection.AuthType = AuthType.Anonymous;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Timeout = TimeSpan.FromSeconds(5);

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

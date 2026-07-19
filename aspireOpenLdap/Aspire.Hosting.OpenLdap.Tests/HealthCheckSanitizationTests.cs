using Aspire.OpenLdap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Health check failures must report only the exception TYPE, never the exception object:
/// LDAP diagnostics can embed server-supplied directory data (DNs, matched values), and
/// health reporters log <see cref="HealthCheckResult.Exception"/> — the same no-PII rule the
/// telemetry mapping enforces.
/// </summary>
public class HealthCheckSanitizationTests
{
    [Fact]
    public async Task Failed_Client_Health_Check_Reports_Type_But_Not_The_Exception()
    {
        // Loopback port 1: connection refused fast, no server involved.
        var connectionString = OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldap://127.0.0.1:1;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=x");
        var factory = new OpenLdapClientFactory(
            connectionString,
            new OpenLdapClientSettings { Timeout = TimeSpan.FromSeconds(5) });
        var healthCheck = new OpenLdapClientHealthCheck(factory);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Contains("Exception", result.Description);
    }
}

using Aspire.Hosting.ApplicationModel;
using Aspire.OpenLdap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Health check failures must report only the exception TYPE, never the exception object:
/// LDAP diagnostics can embed server-supplied directory data (DNs, matched values), and
/// health reporters log <see cref="HealthCheckResult.Exception"/> — the same no-PII rule the
/// telemetry mapping enforces. Both checks are driven by a failure whose exception message
/// PROVABLY carries a sentinel, and the result must contain none of it — with the safe
/// description asserted exactly, so new leaky text cannot ride along unnoticed.
/// </summary>
public class HealthCheckSanitizationTests
{
    [Fact]
    public async Task Failed_Client_Health_Check_Reports_Type_But_Not_The_Exception()
    {
        // The missing CA file's path doubles as the sentinel: the factory's fail-closed
        // diagnostic embeds the full path in its exception message.
        var sentinelPath = Path.Combine(Path.GetTempPath(), $"sentinel-cn=secret-{Guid.NewGuid():N}.crt");
        var connectionString = OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldaps://localhost:1636;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=x;" +
            $"CaCertFile={Aspire.OpenLdap.ConnectionStringQuoting.Quote(sentinelPath)}");
        var factory = new OpenLdapClientFactory(connectionString, new OpenLdapClientSettings());

        // Prove the sentinel really is in the underlying diagnostic — otherwise the
        // no-leak assertions below would be vacuous.
        var underlying = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());
        Assert.Contains(sentinelPath, underlying.Message);

        var healthCheck = new OpenLdapClientHealthCheck(factory);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Equal(
            "Unexpected error during LDAP health check (InvalidOperationException).",
            result.Description);
    }

    [Fact]
    public async Task Failed_Hosting_Health_Check_Reports_Type_But_Not_The_Exception()
    {
        const string sentinel = "cn=sentinel-secret,dc=example,dc=org";

        var builder = DistributedApplication.CreateBuilder();
        var password = builder.AddResource(new ParameterResource(
            "pw", _ => throw new InvalidOperationException($"resolver leaked: {sentinel}"), secret: true));
        var ldap = builder.AddOpenLdap("ldap", password);

        // The endpoint gate runs before password resolution, so allocate one; the check then
        // fails on the sentinel-bearing password resolver without any network involved.
        var endpoint = ldap.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(e => e.Name == OpenLdapResource.LdapEndpointName);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", LoopbackTcpServer.ReserveClosedPort());

        var healthCheck = new OpenLdapHealthCheck(ldap.Resource);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Equal(
            "Unexpected error during LDAP health check (InvalidOperationException).",
            result.Description);
    }
}

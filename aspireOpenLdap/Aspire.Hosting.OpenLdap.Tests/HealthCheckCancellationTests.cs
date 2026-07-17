using System.Diagnostics;
using Aspire.OpenLdap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Cancellation must surface as <see cref="OperationCanceledException"/> (the caller's
/// shutdown/timeout), not be swallowed into an Unhealthy result — and the check must return
/// promptly instead of blocking out the LDAP request timeout.
/// </summary>
public class HealthCheckCancellationTests
{
    [Fact]
    public async Task Canceled_Client_Health_Check_Preserves_Cancellation_And_Returns_Promptly()
    {
        // TEST-NET-1 address: never routable, so without cancellation this check would sit in
        // TCP connect / LDAP timeout territory (30 s) rather than failing fast.
        var connectionString = OpenLdapConnectionStringBuilder.Parse(
            "Endpoint=ldap://192.0.2.1:389;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=x");
        var factory = new OpenLdapClientFactory(
            connectionString,
            new OpenLdapClientSettings { Timeout = TimeSpan.FromSeconds(30) });
        var healthCheck = new OpenLdapClientHealthCheck(factory);

        var canceled = new CancellationToken(canceled: true);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => healthCheck.CheckHealthAsync(new HealthCheckContext(), canceled));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"canceled health check took {stopwatch.Elapsed} — it must not wait out the LDAP timeout");
    }
}

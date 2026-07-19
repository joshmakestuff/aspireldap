using System.Diagnostics;
using Aspire.OpenLdap;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Cancellation must surface as <see cref="OperationCanceledException"/> (the caller's
/// shutdown/timeout), not be swallowed into an Unhealthy result — and the check must return
/// promptly instead of blocking out the LDAP request timeout. The server is a test-owned
/// loopback endpoint that accepts the connection and withholds the LDAP response, so the
/// check is genuinely in flight (dispatch has begun) when the token fires.
/// </summary>
public class HealthCheckCancellationTests
{
    [Fact]
    public async Task Canceled_Client_Health_Check_Preserves_Cancellation_And_Returns_Promptly()
    {
        using var server = new LoopbackTcpServer();

        var ldapTimeout = TimeSpan.FromSeconds(30);
        var connectionString = OpenLdapConnectionStringBuilder.Parse(
            $"Endpoint=ldap://127.0.0.1:{server.Port};BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=x");
        var factory = new OpenLdapClientFactory(
            connectionString,
            new OpenLdapClientSettings { Timeout = ldapTimeout });
        var healthCheck = new OpenLdapClientHealthCheck(factory);

        using var cts = new CancellationTokenSource();
        var checkTask = healthCheck.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        // Cancel only once the client has actually connected — the request is dispatched and
        // waiting on a response that will never come.
        await server.FirstConnection.WaitAsync(TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkTask);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"canceled health check took {stopwatch.Elapsed} after cancellation — it must not wait out the LDAP timeout");

        // Account for the abandoned worker explicitly: the check's internal probe task keeps
        // waiting on the withheld response after cancellation, and how long the LDAP stack
        // takes to give up on a silent-but-open server is platform-dependent. The test owns
        // both socket ends, so disposing the server (RST via zero linger) unblocks the
        // worker's pending I/O immediately — it dies with the test instead of aging out a
        // 30-second timeout while holding a socket.
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Fast DI tests for the public client registration surface — no container, no network.
/// The real-LDAP witness for a registered client lives in <see cref="OpenLdapClientTelemetryTests"/>.
/// </summary>
public class OpenLdapClientRegistrationTests
{
    private static string ConnectionString(int port = 1389) =>
        $"Endpoint=ldap://127.0.0.1:{port};BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=pw";

    [Fact]
    public void AddOpenLdapClient_Registers_Factory_Connection_Client_And_HealthCheck()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["ConnectionStrings:openldap"] = ConnectionString();
        builder.AddOpenLdapClient("openldap");
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<OpenLdapClientFactory>());
        using var connection = host.Services.GetRequiredService<LdapConnection>();
        Assert.NotNull(connection);
        using var client = host.Services.GetRequiredService<OpenLdapClient>();
        Assert.NotNull(client);

        var healthOptions = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        var registration = Assert.Single(healthOptions.Value.Registrations);
        Assert.Equal("openldap_openldap", registration.Name);
    }

    [Fact]
    public void AddKeyedOpenLdapClient_Registers_Keyed_Services_Only()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["ConnectionStrings:primary"] = ConnectionString();
        builder.AddKeyedOpenLdapClient("primary");
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredKeyedService<OpenLdapClientFactory>("primary"));
        using var connection = host.Services.GetRequiredKeyedService<LdapConnection>("primary");
        Assert.NotNull(connection);
        using var client = host.Services.GetRequiredKeyedService<OpenLdapClient>("primary");
        Assert.NotNull(client);

        // The keyed registration must not bleed into the unkeyed service graph.
        Assert.Null(host.Services.GetService<OpenLdapClientFactory>());

        var healthOptions = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        var registration = Assert.Single(healthOptions.Value.Registrations);
        Assert.Equal("openldap_primary", registration.Name);
    }

    [Fact]
    public void DisableTracing_And_DisableMetrics_Suppress_Telemetry_Without_A_Server()
    {
        // A just-released ephemeral port: Send fails fast with a connection error. The error
        // path still emits telemetry when enabled, so no real server (or container) is needed
        // for either half of this witness.
        var closedPort = LoopbackTcpServer.ReserveClosedPort();

        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenLdapInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenLdapInstrumentation.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, _, _) => measurements++);
        meterListener.Start();

        // Enabled settings: the failed send must emit exactly one error span and one measurement
        // (this also proves the listeners work, so the suppression asserts below are not vacuous).
        using (var host = BuildHost(closedPort, configure: null))
        using (var client = host.Services.GetRequiredService<OpenLdapClient>())
        {
            Assert.ThrowsAny<Exception>(() => client.Send(new SearchRequest()));
        }
        var errorSpan = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, errorSpan.Status);
        Assert.Equal(1, measurements);

        // Disabled settings: the same failing send must emit nothing new.
        using (var host = BuildHost(closedPort, s =>
        {
            s.DisableTracing = true;
            s.DisableMetrics = true;
        }))
        using (var client = host.Services.GetRequiredService<OpenLdapClient>())
        {
            Assert.ThrowsAny<Exception>(() => client.Send(new SearchRequest()));
        }
        Assert.Single(spans);
        Assert.Equal(1, measurements);
    }

    private static IHost BuildHost(int port, Action<OpenLdapClientSettings>? configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["ConnectionStrings:openldap"] = ConnectionString(port);
        builder.AddOpenLdapClient("openldap", configure);
        return builder.Build();
    }
}

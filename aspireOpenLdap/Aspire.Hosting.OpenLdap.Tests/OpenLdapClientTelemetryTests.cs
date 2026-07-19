using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.DirectoryServices.Protocols;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Integration test through the CONSUMER-FACING path: the client is registered with
/// <see cref="OpenLdapClientExtensions.AddOpenLdapClient"/> and resolved from DI — exactly what
/// the README tells users to do — not hand-assembled from factory internals. A real search
/// through both <see cref="OpenLdapClient.Send"/> and <see cref="OpenLdapClient.SendAsync"/>
/// must each produce one <c>LDAP search</c> span and one duration measurement on the
/// <c>Aspire.OpenLdap</c> source/meter. Requires Docker (gated like the other integration tests).
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class OpenLdapClientTelemetryTests
{
    [Fact]
    public async Task Search_Through_Registered_OpenLdapClient_Emits_Spans_And_DurationMetrics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await app.StartAsync(cts.Token);
        await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("openldap", cts.Token);
        Assert.NotNull(connectionString);

        // Consumer-facing registration: AddOpenLdapClient reads the connection string from
        // configuration, and OpenLdapClient comes out of DI.
        var hostBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        hostBuilder.Configuration["ConnectionStrings:openldap"] = connectionString;
        hostBuilder.AddOpenLdapClient("openldap");
        using var host = hostBuilder.Build();

        using var client = host.Services.GetRequiredService<OpenLdapClient>();
        var baseDn = OpenLdapConnectionStringBuilder.Parse(connectionString!).BaseDn;

        // Listen for spans from the Aspire.OpenLdap activity source.
        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenLdapInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        // Listen for the duration metric on the Aspire.OpenLdap meter.
        var measurements = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenLdapInstrumentation.Name &&
                    instrument.Name == "db.client.operation.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) => measurements.Add((value, tags.ToArray())));
        meterListener.Start();

        // Both public send paths against the same already-running resource: no second
        // AppHost/container for the async witness.
        var request = new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Subtree, attributeList: null);
        var syncResponse = (SearchResponse)client.Send(request);
        var asyncResponse = (SearchResponse)await client.SendAsync(
            new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Subtree, attributeList: null),
            cts.Token);

        Assert.True(syncResponse.Entries.Count > 0, "Expected the directory's default tree to return entries.");
        Assert.Equal(syncResponse.Entries.Count, asyncResponse.Entries.Count);

        Assert.Equal(2, spans.Count);
        Assert.All(spans, span =>
        {
            Assert.Equal("LDAP search", span.DisplayName);
            Assert.Equal("search", span.GetTagItem("db.operation.name"));
            Assert.Equal("openldap", span.GetTagItem("db.system.name"));
            Assert.Equal(syncResponse.Entries.Count, span.GetTagItem("db.ldap.entries_returned"));
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
        });

        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.True(measurement.Value > 0, "Duration should be positive.");
            Assert.Contains(measurement.Tags, t => t.Key == "db.operation.name" && (string?)t.Value == "search");
            Assert.Contains(measurement.Tags, t => t.Key == "db.system.name" && (string?)t.Value == "openldap");
        });
    }
}

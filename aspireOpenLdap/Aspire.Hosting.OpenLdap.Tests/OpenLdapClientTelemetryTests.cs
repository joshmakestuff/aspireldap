using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.DirectoryServices.Protocols;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Integration test: a real Search issued through <see cref="OpenLdapClient"/> against the
/// container must produce one <c>LDAP search</c> span and one duration measurement on the
/// <c>Aspire.OpenLdap</c> source/meter. Requires Docker (gated like the other integration tests).
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class OpenLdapClientTelemetryTests
{
    [Fact]
    public async Task Search_Through_OpenLdapClient_Emits_Span_And_DurationMetric()
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

        var parsed = OpenLdapConnectionStringBuilder.Parse(connectionString!);
        var settings = new OpenLdapClientSettings();
        var factory = new OpenLdapClientFactory(parsed, settings);
        using var client = new OpenLdapClient(factory.CreateConnection(), settings, parsed);

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

        var request = new SearchRequest(parsed.BaseDn, "(objectClass=*)", SearchScope.Subtree, attributeList: null);
        var response = (SearchResponse)client.Send(request);

        Assert.True(response.Entries.Count > 0, "Expected the directory's default tree to return entries.");

        var span = Assert.Single(spans);
        Assert.Equal("LDAP search", span.DisplayName);
        Assert.Equal("search", span.GetTagItem("db.operation.name"));
        Assert.Equal("openldap", span.GetTagItem("db.system.name"));
        Assert.Equal(response.Entries.Count, span.GetTagItem("db.ldap.entries_returned"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);

        var measurement = Assert.Single(measurements);
        Assert.True(measurement.Value > 0, "Duration should be positive.");
        Assert.Contains(measurement.Tags, t => t.Key == "db.operation.name" && (string?)t.Value == "search");
        Assert.Contains(measurement.Tags, t => t.Key == "db.system.name" && (string?)t.Value == "openldap");
    }
}

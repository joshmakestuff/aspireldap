using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class OpenLdapResourceTests
{
    [Fact]
    public async Task OpenLdap_Resource_Becomes_Healthy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(cts.Token);

        await using var app = await appHost.BuildAsync(cts.Token);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync(cts.Token);

        // The bundled Dockerfile is built on first run, so this may take a while on a cold machine.
        await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);
    }
}

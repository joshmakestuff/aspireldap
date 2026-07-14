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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(cts.Token);

        await using var app = await appHost.BuildAsync(cts.Token);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync(cts.Token);

        // The bundled Dockerfile is built on first run, so this may take a while on a cold machine.
        await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);
    }

    [Fact]
    public void WithDataVolume_Default_Name_Is_Scoped_To_The_AppHost()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithDataVolume();

        var mount = ldap.Resource.Annotations
            .OfType<ContainerMountAnnotation>()
            .Single(m => m.Target == "/data/openldap");

        Assert.Equal(ContainerMountType.Volume, mount.Type);
        Assert.Equal(VolumeNameGenerator.Generate(ldap, "data"), mount.Source);
        // The old default was the globally shared "{resourceName}-data".
        Assert.NotEqual("ldap-data", mount.Source);
        Assert.EndsWith("-ldap-data", mount.Source);
    }

    [Fact]
    public void WithDataVolume_Explicit_Name_Is_Preserved()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithDataVolume("shared-ldap-data");

        var mount = ldap.Resource.Annotations
            .OfType<ContainerMountAnnotation>()
            .Single(m => m.Target == "/data/openldap");

        Assert.Equal("shared-ldap-data", mount.Source);
    }
}

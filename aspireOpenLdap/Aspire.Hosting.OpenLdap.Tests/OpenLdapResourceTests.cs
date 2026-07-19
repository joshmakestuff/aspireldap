using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

// The becomes-healthy smoke test that used to live here was deleted (#41): the telemetry
// integration starts the same default AppHost, waits for health, and performs a real search.
public class OpenLdapResourceTests
{
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

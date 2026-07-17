using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class OpenLdapBuilderModelTests
{
    [Fact]
    public void Endpoints_Are_Proxied_With_Dynamic_Host_Ports_By_Default()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        var endpoints = ldap.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e =>
        {
            Assert.True(e.IsProxied);
            // No pinned host port — Aspire allocates one per run, so two AppHosts can coexist.
            Assert.Null(e.Port);
        });
    }

    [Fact]
    public void WithLdapPort_And_WithLdapsPort_Pin_Host_Ports()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithLdapPort(1389).WithLdapsPort(1636);

        var endpoints = ldap.Resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);

        Assert.Equal(1389, endpoints["ldap"].Port);
        Assert.Equal(1636, endpoints["ldaps"].Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_Of_Range_Ports_Fail_At_The_Fluent_Call(int port)
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap");

        Assert.Throws<ArgumentOutOfRangeException>(() => ldap.WithLdapPort(port));
        Assert.Throws<ArgumentOutOfRangeException>(() => ldap.WithLdapsPort(port));
    }

    [Fact]
    public void WithTls_Custom_Files_Mounts_Each_File_At_Its_Fixed_Container_Path()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-tls-test");
        try
        {
            // Deliberately non-default filenames (the original bug mounted only the directory
            // and assumed server.crt/server.key/ca.crt).
            var cert = Path.Combine(dir.FullName, "certificate.pem");
            var key = Path.Combine(dir.FullName, "private-key.pem");
            var ca = Path.Combine(dir.FullName, "root.pem");
            File.WriteAllText(cert, "cert");
            File.WriteAllText(key, "key");
            File.WriteAllText(ca, "ca");

            var builder = DistributedApplication.CreateBuilder();
            var ldap = builder.AddOpenLdap("ldap").WithTls(cert, key, ca);

            var mounts = ldap.Resource.Annotations.OfType<ContainerMountAnnotation>()
                .ToDictionary(m => m.Target!);

            Assert.Equal(cert, mounts["/tls/server.crt"].Source);
            Assert.Equal(key, mounts["/tls/server.key"].Source);
            Assert.Equal(ca, mounts["/tls/ca.crt"].Source);
            Assert.All(mounts.Values, m => Assert.True(m.IsReadOnly));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithTls_Missing_File_Fails_At_Model_Construction()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddOpenLdap("ldap").WithTls(
                "Z:\\does\\not\\exist\\server.crt",
                "Z:\\does\\not\\exist\\server.key",
                "Z:\\does\\not\\exist\\ca.crt"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task WithPhpLdapAdmin_Respects_Parent_Settings_Applied_Later()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithPhpLdapAdmin()
            // Everything below is applied AFTER the sidecar — the original bug froze the
            // sidecar's view of the parent at the WithPhpLdapAdmin call.
            .WithBaseDn("dc=late,dc=org")
            .WithAdminUsername("root");

        var admin = Assert.Single(builder.Resources.OfType<PhpLdapAdminResource>());
        var env = await EvaluateEnvironmentAsync(admin);

        Assert.Equal("dc=late,dc=org", env["LDAP_BASE_DN"]);
        Assert.Equal("cn=root,dc=late,dc=org", env["LDAP_USERNAME"]);
        Assert.Equal("1389", env["LDAP_PORT"]);
        Assert.False(env.ContainsKey("LDAP_CONNECTION"));
    }

    [Fact]
    public async Task WithPhpLdapAdmin_Respects_Tls_Required_Later()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-tls-test");
        try
        {
            var cert = Path.Combine(dir.FullName, "c.pem");
            var key = Path.Combine(dir.FullName, "k.pem");
            var ca = Path.Combine(dir.FullName, "ca.pem");
            File.WriteAllText(cert, "cert");
            File.WriteAllText(key, "key");
            File.WriteAllText(ca, "ca");

            var builder = DistributedApplication.CreateBuilder();
            var ldap = builder.AddOpenLdap("ldap")
                .WithPhpLdapAdmin()
                .WithTls(cert, key, ca)
                .WithRequiredTls();

            var admin = Assert.Single(builder.Resources.OfType<PhpLdapAdminResource>());
            var env = await EvaluateEnvironmentAsync(admin);

            Assert.Equal("1636", env["LDAP_PORT"]);
            Assert.Equal("ldaps", env["LDAP_CONNECTION"]);
            Assert.Equal("true", env["LDAP_SSL"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<Dictionary<string, string>> EvaluateEnvironmentAsync(IResource resource)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        var result = new Dictionary<string, string>();
        foreach (var (name, value) in context.EnvironmentVariables)
        {
            result[name] = value switch
            {
                string s => s,
                IValueProvider provider => await provider.GetValueAsync() ?? string.Empty,
                _ => value?.ToString() ?? string.Empty,
            };
        }
        return result;
    }
}

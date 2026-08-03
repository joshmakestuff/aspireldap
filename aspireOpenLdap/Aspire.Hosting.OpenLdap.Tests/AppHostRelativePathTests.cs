using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Witnesses for #57: relative schema/seed/TLS paths resolve against the AppHost project
/// directory (matching Aspire's own WithBindMount), not the process working directory.
/// Every test here creates its files ONLY under a temp AppHost directory while the test
/// process CWD points elsewhere — under the old CWD-based resolution these calls threw
/// FileNotFoundException, so a passing mount-source assertion proves the new base.
/// </summary>
public class AppHostRelativePathTests
{
    private static IDistributedApplicationBuilder CreateBuilderWithAppHostDirectory(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
        });

    [Fact]
    public void Relative_Schema_File_Resolves_From_The_AppHost_Directory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-relpath-schema");
        try
        {
            var schemaPath = Path.Combine(dir.FullName, "custom.ldif");
            File.WriteAllText(schemaPath, "dn: cn=custom,cn=schema,cn=config\n");

            var builder = CreateBuilderWithAppHostDirectory(dir.FullName);
            var ldap = builder.AddOpenLdap("ldap").WithSchema("custom.ldif");

            var mount = Assert.Single(
                ldap.Resource.Annotations.OfType<ContainerMountAnnotation>(),
                m => m.Target == "/schema/custom.ldif");
            Assert.Equal(schemaPath, mount.Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Relative_Schema_Directory_Resolves_From_The_AppHost_Directory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-relpath-schemas");
        try
        {
            var schemasDir = Path.Combine(dir.FullName, "schemas");
            Directory.CreateDirectory(schemasDir);

            var builder = CreateBuilderWithAppHostDirectory(dir.FullName);
            var ldap = builder.AddOpenLdap("ldap").WithSchemas("schemas");

            var mount = Assert.Single(
                ldap.Resource.Annotations.OfType<ContainerMountAnnotation>(),
                m => m.Target == "/schemas");
            Assert.Equal(schemasDir, mount.Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Relative_Seed_File_And_Seed_Directory_Resolve_From_The_AppHost_Directory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-relpath-seed");
        try
        {
            var seedFile = Path.Combine(dir.FullName, "seed.ldif");
            File.WriteAllText(seedFile, "dn: dc=example,dc=org\n");
            var seedDir = Path.Combine(dir.FullName, "seeds");
            Directory.CreateDirectory(seedDir);

            var builder = CreateBuilderWithAppHostDirectory(dir.FullName);
            var fromFile = builder.AddOpenLdap("ldap1").WithSeedData("seed.ldif");
            var fromDir = builder.AddOpenLdap("ldap2").WithSeedData("seeds");

            var fileMount = Assert.Single(
                fromFile.Resource.Annotations.OfType<ContainerMountAnnotation>(),
                m => m.Target == "/ldifs/seed.ldif");
            Assert.Equal(seedFile, fileMount.Source);

            var dirMount = Assert.Single(
                fromDir.Resource.Annotations.OfType<ContainerMountAnnotation>(),
                m => m.Target == "/ldifs");
            Assert.Equal(seedDir, dirMount.Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Relative_Tls_File_Paths_Resolve_From_The_AppHost_Directory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-relpath-tls");
        try
        {
            var cert = Path.Combine(dir.FullName, "server.pem");
            var key = Path.Combine(dir.FullName, "server-key.pem");
            var ca = Path.Combine(dir.FullName, "root.pem");
            File.WriteAllText(cert, "cert");
            File.WriteAllText(key, "key");
            File.WriteAllText(ca, "ca");

            var builder = CreateBuilderWithAppHostDirectory(dir.FullName);
            var ldap = builder.AddOpenLdap("ldap")
                .WithTls("server.pem", "server-key.pem", "root.pem");

            var mounts = ldap.Resource.Annotations.OfType<ContainerMountAnnotation>()
                .ToDictionary(m => m.Target!);
            Assert.Equal(cert, mounts["/tls/server.crt"].Source);
            Assert.Equal(key, mounts["/tls/server.key"].Source);
            Assert.Equal(ca, mounts["/tls/ca.crt"].Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_Relative_Path_Errors_Report_The_AppHost_Resolved_Path()
    {
        // The diagnostic must show WHERE the integration actually looked — the AppHost-based
        // path — or a user debugging a typo is sent to the wrong directory.
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-relpath-missing");
        try
        {
            var builder = CreateBuilderWithAppHostDirectory(dir.FullName);
            var ldap = builder.AddOpenLdap("ldap");

            var schemaEx = Assert.Throws<FileNotFoundException>(() => ldap.WithSchema("nope.ldif"));
            Assert.Contains(Path.Combine(dir.FullName, "nope.ldif"), schemaEx.Message);

            var seedEx = Assert.Throws<FileNotFoundException>(() => ldap.WithSeedData("nope"));
            Assert.Contains(Path.Combine(dir.FullName, "nope"), seedEx.Message);

            var tlsEx = Assert.Throws<DistributedApplicationException>(
                () => ldap.WithTls("a.pem", "b.pem", "c.pem"));
            Assert.Contains(Path.Combine(dir.FullName, "a.pem"), tlsEx.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

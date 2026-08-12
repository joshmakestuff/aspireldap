using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Witnesses for #128: <see cref="OpenLdapMounts.PrepareGeneratedFile"/> is called on a
/// deterministic path shared by every parallel builder in this test process (and by
/// concurrent test processes in the mutation run), so it must create the placeholder
/// atomically, tolerate concurrent callers, and never truncate content a prior run wrote.
/// </summary>
public class GeneratedFilePlaceholderTests
{
    private static IResourceBuilder<OpenLdapResource> LdapIn(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
        }).AddOpenLdap("ldap");

    [Fact]
    public void Creates_Empty_Placeholder_And_Returns_Stable_Path()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-placeholder-");
        try
        {
            var ldap = LdapIn(dir.FullName);

            var first = OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif");
            var second = OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif");

            Assert.Equal(first, second);
            Assert.True(File.Exists(first));
            Assert.Equal(string.Empty, File.ReadAllText(first));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Preserves_Existing_Content()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-placeholder-");
        try
        {
            var ldap = LdapIn(dir.FullName);
            var path = OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif");
            File.WriteAllText(path, "dn: ou=written-by-a-prior-run\n");

            var again = OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif");

            Assert.Equal(path, again);
            Assert.Equal("dn: ou=written-by-a-prior-run\n", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_Calls_On_The_Same_Path_All_Succeed()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-ldap-placeholder-");
        try
        {
            // One builder shared by every task: the method only reads AppHostDirectory from it,
            // and sharing maximizes the chance the calls truly collide on the same path.
            var ldap = LdapIn(dir.FullName);
            var expected = Path.Combine(dir.FullName, "obj", "aspire-openldap-seed", "ldap-seed.ldif");
            Assert.Equal(expected, OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif"));

            for (var iteration = 0; iteration < 50; iteration++)
            {
                // Deleted only BETWEEN iterations — concurrent deletion is outside the contract.
                File.Delete(expected);

                var gate = new TaskCompletionSource();
                var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    await gate.Task;
                    return OpenLdapMounts.PrepareGeneratedFile(ldap, "aspire-openldap-seed", "ldap-seed.ldif");
                })).ToArray();

                gate.SetResult();
                var paths = await Task.WhenAll(tasks);

                Assert.All(paths, p => Assert.Equal(expected, p));
                Assert.True(File.Exists(expected));
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

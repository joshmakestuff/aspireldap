using System.DirectoryServices.Protocols;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using LdifDotNet;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Regression guard for issue #3: the health check must not report healthy until a large
/// seed has fully loaded. Before the fix, the init daemon bound the public LDAP port while
/// <c>ldapadd</c> was still streaming entries, so a <c>WaitFor(openldap)</c> dependent could
/// observe a partially-seeded directory. The init daemon now binds <c>ldapi:///</c> only, so
/// the public port opens only after setup (and thus the seed) completes.
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class LargeSeedHealthGatingTests
{
    private const int UserCount = 1500;

    [Fact]
    public async Task LargeSeed_IsFullyLoaded_TheInstant_Resource_Is_Healthy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var seedDir = Directory.CreateTempSubdirectory("aspire-openldap-seed-").FullName;
        try
        {
            var ldifPath = Path.Combine(seedDir, "00-seed.ldif");
            await File.WriteAllTextAsync(ldifPath, GenerateLargeSeed(UserCount), cts.Token);

            DockerCli.WidenPermissionsForContainer(seedDir);

            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(
                    [$"--OpenLdap:SeedDir={seedDir}"],
                    cts.Token);

            await using var app = await appHost.BuildAsync(cts.Token);

            var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await app.StartAsync(cts.Token);

            // The bundled Dockerfile is built on first run, so this can take a while cold.
            await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);

            // The moment health goes green, the whole seed must already be queryable.
            var connectionString = await app.GetConnectionStringAsync("openldap", cts.Token);
            Assert.NotNull(connectionString);

            var count = CountSubtreeEntries(connectionString!);

            // Exactly base (dc=example,dc=org) + ou=people + UserCount users: fewer means the
            // health check went green before the seed finished; more means something else got
            // seeded that this test does not know about.
            Assert.Equal(UserCount + 2, count);
        }
        finally
        {
            Directory.Delete(seedDir, recursive: true);
        }
    }

    private static string GenerateLargeSeed(int userCount)
    {
        var records = new List<LdifRecord>
        {
            new LdifContentRecord("dc=example,dc=org",
                new LdifAttribute("objectClass", "dcObject", "organization"),
                new LdifAttribute("dc", "example"),
                new LdifAttribute("o", "Example")),
            new LdifContentRecord("ou=people,dc=example,dc=org",
                new LdifAttribute("objectClass", "organizationalUnit"),
                new LdifAttribute("ou", "people")),
        };

        for (var i = 0; i < userCount; i++)
        {
            var uid = $"user{i:D5}";
            records.Add(new LdifContentRecord($"uid={uid},ou=people,dc=example,dc=org",
                new LdifAttribute("objectClass", "inetOrgPerson"),
                new LdifAttribute("uid", uid),
                new LdifAttribute("cn", $"User {i:D5}"),
                new LdifAttribute("sn", $"Surname{i:D5}")));
        }

        return LdifWriter.WriteToString(records, new LdifWriterOptions { IncludeVersionLine = false });
    }

    private static int CountSubtreeEntries(string connectionString)
    {
        // The supported client path: OpenLdapConnectionStringBuilder handles the quoted/escaped
        // values the hosting side emits (a naive Split(';') breaks on generated passwords
        // containing ';' or quotes), and the factory owns connection configuration.
        var settings = OpenLdapConnectionStringBuilder.Parse(connectionString);
        var factory = new OpenLdapClientFactory(
            settings,
            new OpenLdapClientSettings { Timeout = TimeSpan.FromSeconds(30) });
        using var connection = factory.CreateConnection();

        // Admin binds as the database rootDN, which is exempt from slapd's size/time limits,
        // so a single subtree search returns the full set.
        var request = new SearchRequest(
            settings.BaseDn,
            "(objectClass=*)",
            SearchScope.Subtree,
            attributeList: "dn");

        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count;
    }
}

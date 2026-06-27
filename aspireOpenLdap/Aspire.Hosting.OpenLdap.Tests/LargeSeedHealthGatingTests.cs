using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
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

            // CreateTempSubdirectory makes a 0700 dir, but the OpenLDAP container runs as a
            // non-root user (uid != the test host's) and must read the bind-mounted seed.
            // Widen perms on Linux so the container can traverse the dir and read the LDIF.
            // (Docker Desktop on Windows/macOS exposes mounts as world-accessible, which hid
            // this when running the test locally.)
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(seedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(ldifPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

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

            // base (dc=example,dc=org) + ou=people + UserCount users.
            Assert.True(
                count >= UserCount + 2,
                $"Expected at least {UserCount + 2} entries the instant the resource reported healthy, " +
                $"but only {count} were present — the health check went green before the seed finished.");
        }
        finally
        {
            Directory.Delete(seedDir, recursive: true);
        }
    }

    private static string GenerateLargeSeed(int userCount)
    {
        var sb = new StringBuilder();
        sb.Append("dn: dc=example,dc=org\n");
        sb.Append("objectClass: dcObject\n");
        sb.Append("objectClass: organization\n");
        sb.Append("dc: example\n");
        sb.Append("o: Example\n\n");
        sb.Append("dn: ou=people,dc=example,dc=org\n");
        sb.Append("objectClass: organizationalUnit\n");
        sb.Append("ou: people\n\n");

        for (var i = 0; i < userCount; i++)
        {
            sb.Append($"dn: uid=user{i:D5},ou=people,dc=example,dc=org\n");
            sb.Append("objectClass: inetOrgPerson\n");
            sb.Append($"uid: user{i:D5}\n");
            sb.Append($"cn: User {i:D5}\n");
            sb.Append($"sn: Surname{i:D5}\n\n");
        }

        return sb.ToString();
    }

    private static int CountSubtreeEntries(string connectionString)
    {
        string? endpoint = null, baseDn = null, bindDn = null, bindPassword = null;
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx < 0) continue;
            var key = segment[..idx];
            var value = segment[(idx + 1)..];
            switch (key)
            {
                case "Endpoint": endpoint = value; break;
                case "BaseDN": baseDn = value; break;
                case "BindDN": bindDn = value; break;
                case "BindPassword": bindPassword = value; break;
            }
        }

        Assert.NotNull(endpoint);
        Assert.NotNull(baseDn);
        Assert.NotNull(bindDn);
        Assert.NotNull(bindPassword);

        var uri = new Uri(endpoint!);

        using var connection = new LdapConnection(
            new LdapDirectoryIdentifier(uri.Host, uri.Port, fullyQualifiedDnsHostName: false, connectionless: false))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, bindPassword),
            Timeout = TimeSpan.FromSeconds(30),
        };
        connection.SessionOptions.ProtocolVersion = 3;

        // Admin binds as the database rootDN, which is exempt from slapd's size/time limits,
        // so a single subtree search returns the full set.
        var request = new SearchRequest(
            baseDn,
            "(objectClass=*)",
            SearchScope.Subtree,
            attributeList: "dn");

        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count;
    }
}

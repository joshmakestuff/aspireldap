using System.DirectoryServices.Protocols;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Runtime witness for the fake-data seeding pipeline: the TestAppHost's
/// <c>--OpenLdap:FakeData=true</c> scenario runs <c>WithFakeDirectory(people: 5, groups: 2,
/// seed: 1)</c> plus one typed user, so the deferred materialization (the
/// <c>OnBeforeResourceStarted</c> hook) is exercised against a live slapd — not simulated by
/// calling the materializer directly like the fast tests do.
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class FakeDataIntegrationTests
{
    [Fact]
    public async Task FakeDirectory_Materializes_And_Loads_Into_The_Running_Container()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(
                ["--OpenLdap:FakeData=true"],
                cts.Token);

        await using var app = await appHost.BuildAsync(cts.Token);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await app.StartAsync(cts.Token);
        await notifications.WaitForResourceHealthyAsync("openldap", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("openldap", cts.Token);
        Assert.NotNull(connectionString);

        var settings = OpenLdapConnectionStringBuilder.Parse(connectionString!);
        var factory = new OpenLdapClientFactory(
            settings,
            new OpenLdapClientSettings { Timeout = TimeSpan.FromSeconds(30) });
        using var connection = factory.CreateConnection();

        // Exactly the 5 fake people plus the typed user under ou=people.
        var people = (SearchResponse)connection.SendRequest(new SearchRequest(
            $"ou=people,{settings.BaseDn}",
            "(objectClass=inetOrgPerson)",
            SearchScope.OneLevel,
            "uid", "userPassword"));
        Assert.Equal(6, people.Entries.Count);

        // Runtime witness of the documented contract: fake people carry no userPassword.
        // The admin bind is the rootDN, exempt from ACLs, so an absent attribute here means
        // absent in the directory — not hidden. The one typed user proves the attribute
        // request itself works (its password is stored {SSHA}-hashed).
        var withPassword = people.Entries.Cast<SearchResultEntry>()
            .Where(e => e.Attributes.Contains("userPassword"))
            .ToArray();
        var typedUser = Assert.Single(withPassword);
        Assert.Contains("uid=svc", typedUser.DistinguishedName, StringComparison.Ordinal);

        // Both fake groups exist and every member resolves to a real fake-person entry.
        var groups = (SearchResponse)connection.SendRequest(new SearchRequest(
            $"ou=groups,{settings.BaseDn}",
            "(objectClass=groupOfNames)",
            SearchScope.OneLevel,
            "member"));
        Assert.Equal(2, groups.Entries.Count);

        var personDns = people.Entries.Cast<SearchResultEntry>()
            .Select(e => e.DistinguishedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SearchResultEntry group in groups.Entries)
        {
            var members = group.Attributes["member"].GetValues(typeof(string)).Cast<string>().ToArray();
            Assert.NotEmpty(members);
            Assert.All(members, m => Assert.Contains(m, personDns));
        }
    }
}

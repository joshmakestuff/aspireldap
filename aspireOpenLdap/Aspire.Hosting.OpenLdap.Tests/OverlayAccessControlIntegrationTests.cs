using System.DirectoryServices.Protocols;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.OpenLdap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-backed runtime witnesses for the privileged cn=config apply paths behind
/// <c>WithOverlay(...)</c> and <c>WithAccessControl(...)</c> (issue #38). LDIF generation for
/// both is unit-tested; these tests prove the generated files actually apply inside the
/// container and change server behavior — overlay population and ACL enforcement — via the
/// TestAppHost's <c>--OpenLdap:ConfigWitness</c> scenario.
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class OverlayAccessControlIntegrationTests
{
    private const string SvcDn = "uid=svc,ou=users,dc=example,dc=org";
    private const string AliceDn = "uid=alice,ou=users,dc=example,dc=org";

    [Fact]
    public async Task MemberOf_Overlay_Populates_MemberOf_On_Seeded_Members()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var app = await StartConfigWitnessAppAsync(cts.Token);

        var settings = await GetAdminSettingsAsync(app, cts.Token);
        var factory = new OpenLdapClientFactory(settings, new OpenLdapClientSettings());
        using var connection = factory.CreateConnection();

        // memberOf is operational (overlay-maintained), so it must be requested explicitly.
        // Its presence proves the whole chain: module load + olcOverlay entry applied via
        // ldapadd -Y EXTERNAL, and applied BEFORE the data load so seed-time group members
        // were populated.
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(AliceDn, "(objectClass=*)", SearchScope.Base, "memberOf"));
        Assert.Equal(ResultCode.Success, response.ResultCode);

        var entry = Assert.Single(response.Entries.Cast<SearchResultEntry>());
        var memberOf = entry.Attributes["memberOf"];
        Assert.NotNull(memberOf);
        Assert.Contains("cn=devs,ou=groups,dc=example,dc=org",
            memberOf.GetValues(typeof(string)).Cast<string>());
    }

    [Fact]
    public async Task AccessControl_Rules_Grant_Svc_And_Deny_Others()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var app = await StartConfigWitnessAppAsync(cts.Token);

        var admin = await GetAdminSettingsAsync(app, cts.Token);

        // Both non-root binds succeeding is itself a witness: with the declared rules applied,
        // slapd's implicit default is deny, so binds only work through the generated
        // "attrs=userPassword ... by anonymous auth" rule.
        using var svcConnection = CreateUserConnection(admin, SvcDn, "svc-password");
        using var aliceConnection = CreateUserConnection(admin, AliceDn, "alice-password");

        // The granted principal reads the restricted subtree.
        var svcSearch = (SearchResponse)svcConnection.SendRequest(new SearchRequest(
            admin.BaseDn, "(cn=classified)", SearchScope.Subtree, "cn"));
        Assert.Equal(ResultCode.Success, svcSearch.ResultCode);
        var classified = Assert.Single(svcSearch.Entries.Cast<SearchResultEntry>());
        Assert.Equal("cn=classified,ou=secret,dc=example,dc=org", classified.DistinguishedName);

        // Another authenticated user gets nothing from the restricted subtree — the entry is
        // silently invisible, not merely unreadable.
        var aliceSearch = (SearchResponse)aliceConnection.SendRequest(new SearchRequest(
            admin.BaseDn, "(cn=classified)", SearchScope.Subtree, "cn"));
        Assert.Equal(ResultCode.Success, aliceSearch.ResultCode);
        Assert.Empty(aliceSearch.Entries.Cast<SearchResultEntry>());

        // But the deny is scoped: outside ou=secret the catch-all read rule still applies,
        // so alice sees ordinary entries.
        var aliceReadsSvc = (SearchResponse)aliceConnection.SendRequest(new SearchRequest(
            SvcDn, "(objectClass=*)", SearchScope.Base, "uid"));
        Assert.Equal(ResultCode.Success, aliceReadsSvc.ResultCode);
        Assert.Single(aliceReadsSvc.Entries.Cast<SearchResultEntry>());
    }

    private static async Task<DistributedApplication> StartConfigWitnessAppAsync(CancellationToken cancellationToken)
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AspireOpenLdap_TestAppHost>(["--OpenLdap:ConfigWitness=true"], cancellationToken);

        var app = await appHost.BuildAsync(cancellationToken);
        try
        {
            var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await app.StartAsync(cancellationToken);
            await notifications.WaitForResourceHealthyAsync("openldap", cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<OpenLdapConnectionStringBuilder> GetAdminSettingsAsync(
        DistributedApplication app, CancellationToken cancellationToken)
    {
        var connectionString = await app.GetConnectionStringAsync("openldap", cancellationToken);
        Assert.NotNull(connectionString);
        return OpenLdapConnectionStringBuilder.Parse(connectionString!);
    }

    private static LdapConnection CreateUserConnection(
        OpenLdapConnectionStringBuilder admin, string bindDn, string password)
    {
        var settings = new OpenLdapConnectionStringBuilder
        {
            Endpoint = admin.Endpoint,
            BaseDn = admin.BaseDn,
            BindDn = bindDn,
            BindPassword = password,
        };
        return new OpenLdapClientFactory(settings, new OpenLdapClientSettings()).CreateConnection();
    }
}

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
        // were populated. (SendRequest throws on non-success result codes, so the entry-shape
        // assertions below are the real checks — no ResultCode assert needed.)
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(AliceDn, "(objectClass=*)", SearchScope.Base, "memberOf"));

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

        // Explicit Bind() is the auth-rule witness. CreateConnection never binds (SDS.P binds
        // lazily on the first request), and with the rules applied slapd's implicit default is
        // deny — verified empirically: without the generated "attrs=userPassword ... by
        // anonymous auth" rule these binds fail with invalid credentials, so succeeding here
        // proves that rule landed.
        using var svcConnection = CreateUserConnection(admin, SvcDn, "svc-password");
        using var aliceConnection = CreateUserConnection(admin, AliceDn, "alice-password");
        svcConnection.Bind();
        aliceConnection.Bind();

        // The granted principal reads the restricted subtree (rule {1}). SendRequest throws
        // on non-success codes, so the entry-shape assertions are the real checks.
        var svcSearch = (SearchResponse)svcConnection.SendRequest(new SearchRequest(
            admin.BaseDn, "(cn=classified)", SearchScope.Subtree, "cn"));
        var classified = Assert.Single(svcSearch.Entries.Cast<SearchResultEntry>());
        Assert.Equal("cn=classified,ou=secret,dc=example,dc=org", classified.DistinguishedName);

        // Another authenticated user gets nothing from the restricted subtree — an ACL-denied
        // search returns Success with zero entries (silent invisibility), never an error code.
        var aliceSearch = (SearchResponse)aliceConnection.SendRequest(new SearchRequest(
            admin.BaseDn, "(cn=classified)", SearchScope.Subtree, "cn"));
        Assert.Empty(aliceSearch.Entries.Cast<SearchResultEntry>());

        // But the deny is scoped: outside ou=secret the catch-all read rule ({2}) still
        // applies, so alice sees ordinary entries.
        var aliceReadsSvc = (SearchResponse)aliceConnection.SendRequest(new SearchRequest(
            SvcDn, "(objectClass=*)", SearchScope.Base, "uid"));
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
        // Carry CaCertFile so the copy stays faithful if this scenario is ever combined with
        // TLS — dropping it would silently swap custom CA trust for the platform store.
        var settings = new OpenLdapConnectionStringBuilder
        {
            Endpoint = admin.Endpoint,
            BaseDn = admin.BaseDn,
            BindDn = bindDn,
            BindPassword = password,
            CaCertFile = admin.CaCertFile,
        };
        return new OpenLdapClientFactory(settings, new OpenLdapClientSettings()).CreateConnection();
    }
}

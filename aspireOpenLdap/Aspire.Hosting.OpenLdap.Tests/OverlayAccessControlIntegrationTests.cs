using System.DirectoryServices.Protocols;
using Aspire.Hosting.ApplicationModel;
using Aspire.OpenLdap;
using AspireOpenLdap.TestAppHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-backed runtime witnesses for the privileged cn=config apply paths behind
/// <c>WithOverlay(...)</c> and <c>WithAccessControl(...)</c> (issue #38). LDIF generation for
/// both is unit-tested; these tests prove the generated files actually apply inside the
/// container and change server behavior — overlay population and ACL enforcement — via the
/// TestAppHost's <c>config-witness</c> scenario.
/// </summary>
[Collection(AppHostCollection.Name)]
[Trait("Category", "Integration")]
public class OverlayAccessControlIntegrationTests(AppHostFixture appHost)
{
    private const string SvcDn = "uid=svc,ou=users,dc=example,dc=org";
    private const string AliceDn = "uid=alice,ou=users,dc=example,dc=org";

    [Fact]
    public async Task MemberOf_Overlay_Populates_MemberOf_On_Seeded_Members()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // Both facts in this class ask for the same scenario, so they share one boot.
        var started = await appHost.StartAsync(TestAppHostScenarios.ConfigWitness, cts.Token);
        var app = started.App;

        var settings = started.Settings;
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

        // (#58) export-ldif dashboard-command witness, folded in here so the suite doesn't
        // pay another container start. Executing through ResourceCommandService proves the
        // whole chain — container-runtime resolution, `exec` against the live container, and
        // slapcat reading the seeded directory — not just the handler wiring.
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var export = await commands.ExecuteCommandAsync("openldap", "export-ldif", cts.Token);
        Assert.True(export.Success, export.Message);
        var exported = Assert.IsType<CommandResultData>(export.Data);
        Assert.Contains($"dn: {AliceDn}", exported.Value?.ToString());
    }

    [Fact]
    public async Task AccessControl_Rules_Grant_Svc_And_Deny_Others()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var admin = (await appHost.StartAsync(TestAppHostScenarios.ConfigWitness, cts.Token)).Settings;

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

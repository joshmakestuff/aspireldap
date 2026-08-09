using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// What the server's access control says, and the password path that decides who can bind at
/// all. The app's own bind is the directory's administrator — slapd's rootdn bypasses access
/// control — so an ACL refusal can only be witnessed through a second, unprivileged identity.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class AccessAndPasswordTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task A_bind_without_write_access_gets_access_denied_rather_than_an_exception()
    {
        using var cts = TestCancellation.Source();
        var asAlice = fixture.DirectoryAs(fixture.DnUnder("uid=alice", "ou=people"), "alice-password");

        var result = await asAlice.ModifyEntryAsync(
            fixture.DnUnder("uid=bob", "ou=people"),
            [new LdapAttributeChange(DirectoryAttributeOperation.Replace, "cn", ["Rewritten By Alice"])],
            cts.Token);

        Assert.Equal(LdapOperationStatus.AccessDenied, result.Status);
        Assert.Equal(ResultCode.InsufficientAccessRights, result.ResultCode);
    }

    [Fact]
    public async Task An_unprivileged_bind_can_still_read_what_the_server_lets_it_read()
    {
        using var cts = TestCancellation.Source();
        var asAlice = fixture.DirectoryAs(fixture.DnUnder("uid=alice", "ou=people"), "alice-password");

        var entry = await asAlice.GetEntryAsync(
            fixture.DnUnder("uid=bob", "ou=people"), ["cn"], cts.Token);

        Assert.NotNull(entry);
        Assert.Equal("Bob Brown", Assert.Single(entry.Attributes).Values[0]);
    }

    [Fact]
    public async Task Setting_a_password_lets_the_entry_bind_with_it()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "password-target"), "ou=people");

        var added = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(dn, [
                new LdapNewAttribute("objectClass", ["inetOrgPerson"]),
                new LdapNewAttribute("uid", ["password-target"]),
                new LdapNewAttribute("cn", ["Password Target"]),
                new LdapNewAttribute("sn", ["Target"]),
            ]),
            cts.Token);
        Assert.True(added.Succeeded, added.Message);

        try
        {
            var set = await fixture.Directory.SetPasswordAsync(dn, "chosen-by-the-test", cts.Token);
            Assert.True(set.Succeeded, set.Message);

            // The bind is the assertion: a stored value the server would not accept proves nothing.
            var asTarget = fixture.DirectoryAs(dn, "chosen-by-the-test");
            Assert.NotNull(await asTarget.GetEntryAsync(dn, ["cn"], cts.Token));

            // The server chose the storage scheme, not this layer.
            var stored = await fixture.Directory.GetEntryAsync(dn, ["userPassword"], cts.Token);
            Assert.NotNull(stored);
            Assert.True(Assert.Single(stored.Attributes).IsBinary);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(dn, cts.Token);
        }
    }

    [Fact]
    public async Task Setting_a_password_on_an_entry_that_does_not_exist_reports_a_failure()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SetPasswordAsync(
            fixture.DnUnder("uid=absent", "ou=people"), "irrelevant", cts.Token);

        Assert.False(result.Succeeded);
    }
}

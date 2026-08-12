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
    public async Task Setting_the_bind_identity_password_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();

        // A case/whitespace variant of the bind DN: surviving this mutation is what separates
        // a real DN comparison from string.Equals (#94).
        var variant = fixture.Settings.BindDn.ToUpperInvariant().Replace(",", ", ");

        var result = await fixture.Directory.SetPasswordAsync(variant, "would-brick-the-console", cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
        Assert.Null(result.ResultCode);
        Assert.Contains("bind identity", result.Message);

        // The declared credentials still work: the password genuinely did not change.
        var asAdmin = fixture.DirectoryAs(fixture.Settings.BindDn, fixture.Settings.BindPassword);
        Assert.NotNull(await asAdmin.GetEntryAsync(fixture.BaseDn, ["dc"], cts.Token));
    }

    [Fact]
    public async Task Every_write_that_would_invalidate_the_bind_identity_is_rejected_without_a_round_trip()
    {
        // The #94 class, closed (#136): rename and delete of the identity, rename and
        // subtree-delete of a container holding it, and the password change through the
        // modify door. Each uses a case/whitespace variant so string.Equals cannot pass.
        using var cts = TestCancellation.Source();
        var variant = fixture.Settings.BindDn.ToUpperInvariant().Replace(",", ", ");
        var baseVariant = fixture.BaseDn.ToUpperInvariant().Replace(",", ", ");

        var renamed = await fixture.Directory.RenameEntryAsync(variant, "cn=renamed", cancellationToken: cts.Token);
        Assert.Equal(LdapOperationStatus.InvalidRequest, renamed.Outcome.Status);
        Assert.Null(renamed.Outcome.ResultCode);

        var containerRenamed = await fixture.Directory.RenameEntryAsync(baseVariant, "dc=elsewhere", cancellationToken: cts.Token);
        Assert.Equal(LdapOperationStatus.InvalidRequest, containerRenamed.Outcome.Status);
        Assert.Contains("contains", containerRenamed.Outcome.Message);

        var deleted = await fixture.Directory.DeleteEntryAsync(variant, cts.Token);
        Assert.Equal(LdapOperationStatus.InvalidRequest, deleted.Status);
        Assert.Null(deleted.ResultCode);

        var subtreeDeleted = await fixture.Directory.DeleteEntryAsync(baseVariant, subtree: true, cts.Token);
        Assert.Equal(LdapOperationStatus.InvalidRequest, subtreeDeleted.Status);
        Assert.Contains("contains", subtreeDeleted.Message);

        var passwordModified = await fixture.Directory.ModifyEntryAsync(
            variant,
            [new LdapAttributeChange(DirectoryAttributeOperation.Replace, "userPassword", ["would-brick"])],
            cts.Token);
        Assert.Equal(LdapOperationStatus.InvalidRequest, passwordModified.Status);
        Assert.Null(passwordModified.ResultCode);

        // The declared credentials still work, and the directory is intact.
        var asAdmin = fixture.DirectoryAs(fixture.Settings.BindDn, fixture.Settings.BindPassword);
        Assert.NotNull(await asAdmin.GetEntryAsync(fixture.BaseDn, ["dc"], cts.Token));
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

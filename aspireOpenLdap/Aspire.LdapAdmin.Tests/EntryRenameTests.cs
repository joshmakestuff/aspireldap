using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Renaming and moving entries. The DN the service hands back afterwards is the assertion that
/// matters: it is what the UI navigates to next, and it is built from parsed components rather
/// than by pasting strings together.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class EntryRenameTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Renaming_an_entry_returns_its_new_dn_and_moves_it_there()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("cn", "before"), "ou=groups");
        await AddRoleAsync(dn, "before", cts.Token);

        var result = await fixture.Directory.RenameEntryAsync(dn, Dn.Rdn("cn", "after"), cancellationToken: cts.Token);

        Assert.True(result.Outcome.Succeeded, result.Outcome.Message);
        Assert.Equal(fixture.DnUnder("cn=after", "ou=groups"), result.NewDn);
        Assert.NotNull(await fixture.Directory.GetEntryAsync(result.NewDn!, cancellationToken: cts.Token));
        Assert.Null(await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token));

        await fixture.Directory.DeleteEntryAsync(result.NewDn!, cts.Token);
    }

    [Fact]
    public async Task Moving_an_entry_under_a_new_parent_returns_the_dn_below_that_parent()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("cn", "movable"), "ou=groups");
        await AddRoleAsync(dn, "movable", cts.Token);

        var result = await fixture.Directory.RenameEntryAsync(
            dn, Dn.Rdn("cn", "movable"), fixture.DnUnder("ou=people"), cancellationToken: cts.Token);

        Assert.True(result.Outcome.Succeeded, result.Outcome.Message);
        Assert.Equal(fixture.DnUnder("cn=movable", "ou=people"), result.NewDn);
        Assert.NotNull(await fixture.Directory.GetEntryAsync(result.NewDn!, cancellationToken: cts.Token));

        await fixture.Directory.DeleteEntryAsync(result.NewDn!, cts.Token);
    }

    [Fact]
    public async Task A_new_rdn_whose_value_needs_escaping_comes_back_escaped_in_the_new_dn()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("cn", "plain"), "ou=groups");
        await AddRoleAsync(dn, "plain", cts.Token);

        var result = await fixture.Directory.RenameEntryAsync(
            dn, Dn.Rdn("cn", "Comma, Renamed"), cancellationToken: cts.Token);

        Assert.True(result.Outcome.Succeeded, result.Outcome.Message);
        Assert.Equal(Dn.Combine(@"cn=Comma\, Renamed", fixture.DnUnder("ou=groups")), result.NewDn);
        // The DN it returned is one the directory answers to — the point of returning it at all.
        Assert.NotNull(await fixture.Directory.GetEntryAsync(result.NewDn!, cancellationToken: cts.Token));

        await fixture.Directory.DeleteEntryAsync(result.NewDn!, cts.Token);
    }

    [Fact]
    public async Task A_new_rdn_that_is_more_than_one_rdn_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.RenameEntryAsync(
            fixture.DnUnder("uid=alice", "ou=people"), "cn=a,cn=b", cancellationToken: cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Null(result.NewDn);
    }

    [Fact]
    public async Task A_new_rdn_that_is_not_a_valid_rdn_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.RenameEntryAsync(
            fixture.DnUnder("uid=alice", "ou=people"), "not an rdn", cancellationToken: cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Null(result.NewDn);
    }

    [Fact]
    public async Task Renaming_an_entry_that_does_not_exist_reports_not_found_and_no_new_dn()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.RenameEntryAsync(
            fixture.DnUnder("cn=absent", "ou=groups"), "cn=elsewhere", cancellationToken: cts.Token);

        Assert.Equal(LdapOperationStatus.NotFound, result.Outcome.Status);
        Assert.Null(result.NewDn);
    }

    private async Task AddRoleAsync(string dn, string cn, CancellationToken cancellationToken)
    {
        var added = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(dn, [
                new LdapNewAttribute("objectClass", ["organizationalRole"]),
                new LdapNewAttribute("cn", [cn]),
            ]),
            cancellationToken);
        Assert.True(added.Succeeded, added.Message);
    }
}

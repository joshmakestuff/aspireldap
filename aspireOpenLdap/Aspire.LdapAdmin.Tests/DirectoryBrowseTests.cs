using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Browsing the tree against the seeded directory: what a node's children are, whether each
/// has children of its own, and — the one answer a browser must never get wrong — whether the
/// list it is showing is the whole list.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class DirectoryBrowseTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Children_of_the_base_dn_are_the_seeded_organizational_units()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.GetChildrenAsync(cancellationToken: cts.Token);

        Assert.False(result.Truncated);
        Assert.Equal(
            ["ou=directory", "ou=groups", "ou=hosts", "ou=people"],
            result.Children.Select(c => c.Rdn).Order(StringComparer.Ordinal));
        Assert.All(result.Children, child => Assert.Contains("organizationalUnit", child.ObjectClasses));
    }

    [Fact]
    public async Task A_node_with_entries_beneath_it_is_flagged_as_having_children()
    {
        using var cts = TestCancellation.Source();

        var top = await fixture.Directory.GetChildrenAsync(cancellationToken: cts.Token);
        var people = Assert.Single(top.Children, c => string.Equals(c.Rdn, "ou=people", StringComparison.Ordinal));
        Assert.True(people.HasChildren);

        var leaves = await fixture.Directory.GetChildrenAsync(people.Dn, cancellationToken: cts.Token);
        Assert.Equal(
            ["uid=alice", "uid=bob", "uid=svc-sweeper"],
            leaves.Children.Select(c => c.Rdn).Order(StringComparer.Ordinal));
        Assert.All(leaves.Children, child => Assert.False(child.HasChildren));
    }

    [Fact]
    public async Task Children_past_the_limit_are_reported_as_truncated()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.GetChildrenAsync(
            fixture.DnUnder("ou=directory"), limit: 5, cancellationToken: cts.Token);

        Assert.True(result.Truncated);
        Assert.Equal(5, result.Children.Count);
    }

    [Fact]
    public async Task A_complete_child_list_is_not_flagged_as_truncated()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.GetChildrenAsync(
            fixture.DnUnder("ou=directory"), limit: 100, cancellationToken: cts.Token);

        // The name promises the not-truncated invariant, not the seed census
        // (aspireldap#126): non-empty and within the limit is all the count must be.
        // Children_past_the_limit_are_reported_as_truncated covers the other direction.
        Assert.False(result.Truncated);
        Assert.InRange(result.Children.Count, 1, 100);
    }

    [Fact]
    public async Task A_child_rdn_whose_value_needs_escaping_is_reported_escaped()
    {
        using var cts = TestCancellation.Source();

        // cn=Comma\, Test — the RDN a first-comma split would truncate to "cn=Comma".
        var dn = Dn.Combine(Dn.Rdn("cn", "Comma, Test"), fixture.DnUnder("ou=groups"));
        var created = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(dn, [
                new LdapNewAttribute("objectClass", ["organizationalRole"]),
                new LdapNewAttribute("cn", ["Comma, Test"]),
            ]),
            cts.Token);
        Assert.True(created.Succeeded, created.Message);

        try
        {
            var result = await fixture.Directory.GetChildrenAsync(fixture.DnUnder("ou=groups"), cancellationToken: cts.Token);

            var child = Assert.Single(result.Children, c => c.Dn.Contains("Comma", StringComparison.Ordinal));
            Assert.Equal(@"cn=Comma\, Test", child.Rdn);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(dn, cts.Token);
        }
    }

    [Fact]
    public async Task A_browse_base_that_is_not_a_valid_dn_is_rejected_before_the_server_sees_it()
    {
        using var cts = TestCancellation.Source();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Directory.GetChildrenAsync("not a dn", cancellationToken: cts.Token));
    }
}

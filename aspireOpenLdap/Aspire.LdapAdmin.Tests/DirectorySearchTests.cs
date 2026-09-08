using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Searching the seeded directory: filters and scopes reach the server as written, the
/// requested attribute projection is honoured, and a capped result set says it was capped.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class DirectorySearchTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Search_reads_across_an_ldap_page_boundary()
    {
        using var cts = TestCancellation.Source();
        var result = await fixture.Directory.SearchAsync(new LdapSearchOptions
        {
            BaseDn = fixture.DnUnder("ou=hosts"),
            Filter = "(objectClass=inetOrgPerson)",
            Limit = 502,
            Attributes = ["uid"],
        }, cts.Token);

        Assert.False(result.Truncated);
        Assert.Equal(501, result.Entries.Count);
        Assert.Equal(501, result.Entries.Select(entry => entry.Dn).Distinct().Count());
    }

    [Fact]
    public async Task Matches_past_the_limit_are_reported_as_truncated()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { Filter = "(objectClass=inetOrgPerson)", Limit = 3 },
            cts.Token);

        Assert.True(result.Truncated);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task A_limit_that_exactly_matches_the_result_count_is_not_truncated()
    {
        using var cts = TestCancellation.Source();
        var exact = await fixture.Directory.SearchAsync(new LdapSearchOptions
        {
            BaseDn = fixture.DnUnder("ou=people"),
            Filter = "(|(uid=alice)(uid=bob))",
            Limit = 2,
        }, cts.Token);

        Assert.False(exact.Truncated);
        Assert.Equal(2, exact.Entries.Count);
    }

    [Fact]
    public async Task An_equality_filter_selects_the_one_matching_entry()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(new LdapSearchOptions { Filter = "(uid=alice)" }, cts.Token);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(fixture.DnUnder("uid=alice", "ou=people"), entry.Dn, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_filter_matching_nothing_returns_no_entries_and_no_truncation()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { Filter = "(uid=nobody-at-all)" }, cts.Token);

        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task One_level_scope_returns_only_direct_children_of_the_base()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions
            {
                BaseDn = fixture.DnUnder("ou=people"),
                Filter = "(objectClass=*)",
                Scope = SearchScope.OneLevel,
            },
            cts.Token);

        // The contract is scope, not census: every hit is a DIRECT child
        // of the base — one RDN deeper, still under ou=people — and the base itself is
        // absent. A later typed user must not red this test.
        Assert.NotEmpty(result.Entries);
        var baseDn = fixture.DnUnder("ou=people");
        Assert.All(result.Entries, e =>
        {
            Assert.EndsWith("," + baseDn, e.Dn, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Dn.Parse(baseDn).Count + 1, Dn.Parse(e.Dn).Count);
        });
        Assert.Contains(result.Entries, e =>
            string.Equals(e.Dn, fixture.DnUnder("uid=alice", "ou=people"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Base_scope_returns_the_base_entry_alone()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions
            {
                BaseDn = fixture.DnUnder("ou=people"),
                Filter = "(objectClass=*)",
                Scope = SearchScope.Base,
            },
            cts.Token);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(fixture.DnUnder("ou=people"), entry.Dn, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_requested_attribute_list_narrows_the_projection()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { Filter = "(uid=alice)", Attributes = ["cn"] },
            cts.Token);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(["cn"], entry.Attributes.Select(a => a.Name));
    }

    [Fact]
    public async Task A_search_base_below_the_root_restricts_the_result_set()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { BaseDn = fixture.DnUnder("ou=groups"), Filter = "(objectClass=groupOfNames)" },
            cts.Token);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(fixture.DnUnder("cn=staff", "ou=groups"), entry.Dn, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_search_base_that_is_not_a_valid_dn_is_rejected_as_invalid_input()
    {
        using var cts = TestCancellation.Source();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Directory.SearchAsync(
            new LdapSearchOptions { BaseDn = "not a dn" }, cts.Token));
    }

    [Fact]
    public async Task A_limit_below_one_is_rejected()
    {
        using var cts = TestCancellation.Source();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Directory.SearchAsync(
            new LdapSearchOptions { Limit = 0 }, cts.Token));
    }
}

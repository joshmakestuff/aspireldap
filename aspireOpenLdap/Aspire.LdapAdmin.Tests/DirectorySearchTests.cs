using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
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
    /// <summary>3 typed users (alice, bob, svc-sweeper) + 30 generated people, all inetOrgPerson.</summary>
    private const int SeededPeople = 33;

    [Fact]
    public async Task A_subtree_search_finds_every_seeded_person()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { Filter = "(objectClass=inetOrgPerson)", Limit = 100 },
            cts.Token);

        Assert.False(result.Truncated);
        Assert.Equal(SeededPeople, result.Entries.Count);
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

        var result = await fixture.Directory.SearchAsync(
            new LdapSearchOptions { Filter = "(objectClass=inetOrgPerson)", Limit = SeededPeople },
            cts.Token);

        Assert.False(result.Truncated);
        Assert.Equal(SeededPeople, result.Entries.Count);
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

        // The three typed accounts (alice, bob, svc-sweeper) — none of ou=directory's people.
        Assert.Equal(3, result.Entries.Count);
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
    public async Task A_search_base_that_is_not_a_valid_dn_is_rejected_before_the_server_sees_it()
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

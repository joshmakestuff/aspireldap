using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class SearchResultTableStateTests
{
    [Fact]
    public void Equal_values_keep_fetch_order_when_sort_direction_changes()
    {
        var state = new SearchResultTableState(LdapAdminSortOrder.ServerOrder);
        var entries = new[]
        {
            Entry("uid=first,dc=example,dc=org", name: "Same"),
            Entry("uid=second,dc=example,dc=org", name: "Same"),
            Entry("uid=third,dc=example,dc=org", name: "Alpha"),
        };
        state.SetEntries(entries, "dc=example,dc=org");

        state.SortBy(SearchResultSortColumn.Name);
        Assert.Equal([entries[2].Dn, entries[0].Dn, entries[1].Dn], state.VisibleRows.Select(static row => row.Key));

        state.SortBy(SearchResultSortColumn.Name);
        Assert.Equal([entries[0].Dn, entries[1].Dn, entries[2].Dn], state.VisibleRows.Select(static row => row.Key));
    }

    [Fact]
    public void Missing_values_sort_last_in_both_directions()
    {
        var state = new SearchResultTableState(LdapAdminSortOrder.ServerOrder);
        var missing = Entry("uid=missing,dc=example,dc=org");
        var alpha = Entry("uid=alpha,dc=example,dc=org", mail: "alpha@example.org");
        var zulu = Entry("uid=zulu,dc=example,dc=org", mail: "zulu@example.org");
        state.SetEntries([missing, zulu, alpha], "dc=example,dc=org");

        state.SortBy(SearchResultSortColumn.Mail);
        Assert.Equal([alpha.Dn, zulu.Dn, missing.Dn], state.VisibleRows.Select(static row => row.Key));

        state.SortBy(SearchResultSortColumn.Mail);
        Assert.Equal([zulu.Dn, alpha.Dn, missing.Dn], state.VisibleRows.Select(static row => row.Key));
    }

    [Fact]
    public void Pages_clamp_at_boundaries_and_reset_after_sort_or_new_results()
    {
        var state = new SearchResultTableState(LdapAdminSortOrder.ServerOrder, pageSize: 2);
        var entries = Enumerable.Range(1, 5)
            .Select(index => Entry($"uid={index},dc=example,dc=org", name: $"Name {index}"))
            .ToArray();
        state.SetEntries(entries, "dc=example,dc=org");

        state.GoToPage(99);
        Assert.Equal(3, state.PageNumber);
        Assert.Equal(5, state.FirstVisibleNumber);
        Assert.Equal(5, state.LastVisibleNumber);
        Assert.Single(state.VisibleRows);

        state.GoToPage(-1);
        Assert.Equal(1, state.PageNumber);
        state.GoToPage(2);
        state.SortBy(SearchResultSortColumn.Name);
        Assert.Equal(1, state.PageNumber);

        state.GoToPage(2);
        state.SetEntries(entries[..2], "dc=example,dc=org");
        Assert.Equal(1, state.PageNumber);
        Assert.False(state.HasNextPage);
    }

    [Fact]
    public void Default_sort_is_initial_only_and_user_sort_survives_new_results()
    {
        var state = new SearchResultTableState(LdapAdminSortOrder.Rdn);
        state.SetEntries(
            [Entry("uid=zulu,dc=example,dc=org"), Entry("uid=alpha,dc=example,dc=org")],
            "dc=example,dc=org");
        Assert.Equal("uid=alpha", state.VisibleRows[0].DisplayDn);

        state.SortBy(SearchResultSortColumn.Mail);
        state.SetEntries(
            [Entry("uid=first,dc=example,dc=org", mail: "z@example.org"), Entry("uid=second,dc=example,dc=org", mail: "a@example.org")],
            "dc=example,dc=org");

        Assert.Equal(SearchResultSortColumn.Mail, state.SortColumn);
        Assert.Equal("uid=second", state.VisibleRows[0].DisplayDn);
    }

    internal static LdapEntry Entry(string dn, string? name = null, string? mail = null)
    {
        List<LdapAttributeValues> attributes = [];
        if (name is not null)
        {
            attributes.Add(new LdapAttributeValues("cn", false, [name], LdapValueClassification.Schema));
        }
        if (mail is not null)
        {
            attributes.Add(new LdapAttributeValues("mail", false, [mail], LdapValueClassification.Schema));
        }
        return new LdapEntry(dn, attributes);
    }
}

public sealed class SearchResultsTableComponentTests : TestContext
{
    [Fact]
    public void Sort_headers_toggle_direction_and_report_aria_sort()
    {
        var cut = RenderComponent<SearchResultsTable>(parameters => parameters
            .Add(component => component.Result, new LdapSearchResult(
                [SearchResultTableStateTests.Entry("uid=zulu,dc=example,dc=org", name: "Zulu"),
                 SearchResultTableStateTests.Entry("uid=alpha,dc=example,dc=org", name: "Alpha")],
                Truncated: false))
            .Add(component => component.CommonSuffix, "dc=example,dc=org"));

        Assert.Equal("none", cut.Find("th:nth-child(3)").GetAttribute("aria-sort"));
        cut.Find("th:nth-child(3) button").Click();
        Assert.Equal("ascending", cut.Find("th:nth-child(3)").GetAttribute("aria-sort"));
        Assert.Contains("uid=alpha", cut.Find("tbody tr:first-child").TextContent, StringComparison.Ordinal);

        cut.Find("th:nth-child(3) button").Click();
        Assert.Equal("descending", cut.Find("th:nth-child(3)").GetAttribute("aria-sort"));
        Assert.Contains("uid=zulu", cut.Find("tbody tr:first-child").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Later_pages_keep_truncation_and_show_only_the_bounded_fetched_count()
    {
        var entries = Enumerable.Range(1, 30)
            .Select(index => SearchResultTableStateTests.Entry($"uid={index:D2},dc=example,dc=org"))
            .ToArray();
        var cut = RenderComponent<SearchResultsTable>(parameters => parameters
            .Add(component => component.Result, new LdapSearchResult(entries, Truncated: true))
            .Add(component => component.CommonSuffix, "dc=example,dc=org"));

        cut.Find("nav[aria-label='Search result pages'] button:last-child").Click();

        Assert.Equal(5, cut.FindAll("tbody tr").Count);
        Assert.Contains("Showing 26-30 of 30 fetched entries", cut.Find(".search-result-count").TextContent, StringComparison.Ordinal);
        Assert.Contains("truncated after 30 fetched entries", cut.Find(".search-truncation").TextContent, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2", cut.Find("nav").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "No entry matched.")]
    [InlineData(true, "No entry was returned before truncation.")]
    public void Empty_results_report_zero_fetched_without_overstating_completeness(bool truncated, string message)
    {
        var cut = RenderComponent<SearchResultsTable>(parameters => parameters
            .Add(component => component.Result, new LdapSearchResult([], truncated))
            .Add(component => component.RanFilter, "(uid=missing)"));

        Assert.Contains("Showing 0-0 of 0 fetched entries", cut.Find(".search-result-count").TextContent, StringComparison.Ordinal);
        Assert.Contains(message, cut.Find("p.note").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("table"));
    }
}

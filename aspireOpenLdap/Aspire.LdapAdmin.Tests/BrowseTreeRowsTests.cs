using Aspire.LdapAdmin.Web.Components.Pages;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The rail's row contract under the RDN filter (#121) — pure display logic, no renderer
/// (EntryView precedent). The rule under test: a capped container is never hidden and never
/// silent. A filter cannot prove an unloaded child absent, so the node stays visible and the
/// "search this container" row stays with it; anything less reads as "no such entry", the one
/// wrong answer a directory browser must never give.
/// </summary>
public class BrowseTreeRowsTests
{
    private static Browse.TreeNode Node(
        string rdn, bool capped = false, bool open = false, params Browse.TreeNode[] children) =>
        new()
        {
            Dn = rdn + ",dc=example,dc=org",
            Rdn = rdn,
            HasChildren = children.Length > 0 || capped,
            Open = open,
            Capped = capped,
            Children = children.Length > 0 || capped ? [.. children] : null,
        };

    private static List<Browse.Row> Rows(Browse.TreeNode root, string query = "")
    {
        var rows = new List<Browse.Row>();
        Browse.Walk(root, 0, query, selectedDn: null, rows);
        return rows;
    }

    [Fact]
    public void Filtered_capped_node_with_a_match_emits_the_match_then_the_cap_row()
    {
        var node = Node("ou=hosts", capped: true, open: true,
            Node("uid=host-1"), Node("uid=printer-9"));

        var rows = Rows(node, "host");

        Assert.Equal(["ou=hosts", "uid=host-1", "more entries not shown — search this container"],
            rows.Select(r => r.Label));
        Assert.True(rows[^1].IsCap);
    }

    [Fact]
    public void Filtered_capped_node_with_no_match_stays_visible_closed_with_badge_and_cap_row()
    {
        var node = Node("ou=hosts", capped: true, open: true,
            Node("uid=host-1"), Node("uid=host-2"));

        var rows = Rows(node, "no-such-entry");

        Assert.Equal(2, rows.Count);
        Assert.Equal("ou=hosts", rows[0].Label);
        Assert.Equal("▸", rows[0].Twisty);   // closed: matches were only among the loaded Cap
        Assert.Equal("2+", rows[0].Count);   // the badge says the directory holds more
        Assert.True(rows[1].IsCap);
    }

    [Fact]
    public void A_capped_child_is_never_filtered_out_of_its_parent()
    {
        // Case B of the finding: pre-fix, FilterVisible dropped the whole capped container and
        // the rail collapsed to the root — the literal "no such entry" misread.
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=hosts", capped: true, open: true, Node("uid=host-1")),
            Node("ou=people", capped: false, open: true, Node("uid=alice")));

        var rows = Rows(root, "zzz-matches-nothing-loaded");

        Assert.Contains(rows, r => r.Label == "ou=hosts");
        Assert.Contains(rows, r => r.IsCap && r.Node!.Rdn == "ou=hosts");
        Assert.DoesNotContain(rows, r => r.Label == "ou=people");
    }

    [Fact]
    public void A_filter_matching_the_capped_nodes_own_rdn_shows_node_and_cap_row()
    {
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=hosts", capped: true, open: true, Node("uid=printer-9")));

        var rows = Rows(root, "hosts");

        Assert.Equal(["dc=example", "ou=hosts", "more entries not shown — search this container"],
            rows.Select(r => r.Label));
    }

    [Fact]
    public void A_non_capped_child_with_no_match_stays_hidden()
    {
        // The guard against FilterVisible becoming always-true: only capped nodes earn
        // unconditional visibility.
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=people", capped: false, open: true, Node("uid=alice"), Node("uid=bob")));

        var rows = Rows(root, "carol");

        Assert.Equal(["dc=example"], rows.Select(r => r.Label));
    }

    [Fact]
    public void Nested_capped_nodes_each_emit_their_own_cap_row_at_their_own_depth()
    {
        var root = Node("ou=outer", capped: true, open: true,
            Node("ou=inner", capped: true, open: true, Node("uid=host-1")));

        var rows = Rows(root, "matches-nothing");

        Assert.Equal(["ou=outer", "ou=inner", "more entries not shown — search this container",
            "more entries not shown — search this container"], rows.Select(r => r.Label));
        Assert.Equal("dp2", rows[2].DepthClass); // inner's cap row, under the inner node
        Assert.Equal("dp1", rows[3].DepthClass); // outer's cap row, after its children
        Assert.Equal(2, rows.Where(r => r.IsCap).Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void A_capped_root_with_no_match_renders_exactly_root_and_cap_row()
    {
        var rows = Rows(Node("dc=example", capped: true, open: true, Node("ou=people")), "zzz");

        Assert.Equal(2, rows.Count);
        Assert.Equal("dc=example", rows[0].Label);
        Assert.True(rows[1].IsCap);
    }

    [Fact]
    public void Unfiltered_paging_still_wins_over_the_cap_row()
    {
        var children = Enumerable.Range(1, 20).Select(i => Node($"uid=host-{i}")).ToArray();
        var node = Node("ou=hosts", capped: true, open: true, children);

        var rows = Rows(node);

        // Shown defaults to Page (12): the paging row renders, the cap row waits its turn.
        Assert.Equal(1 + Browse.Page + 1, rows.Count);
        Assert.True(rows[^1].IsMore);
        Assert.Equal("8 more of 20 — show 8", rows[^1].Label);
        Assert.DoesNotContain(rows, r => r.IsCap);
    }

    [Fact]
    public void Unfiltered_fully_shown_capped_node_emits_the_cap_row()
    {
        var node = Node("ou=hosts", capped: true, open: true,
            Node("uid=host-1"), Node("uid=host-2"));

        var rows = Rows(node);

        Assert.Equal(4, rows.Count);
        Assert.True(rows[^1].IsCap);
        Assert.Equal(node.Dn + "::cap", rows[^1].Key);
    }

    [Fact]
    public void Unfiltered_closed_capped_node_keeps_badge_only()
    {
        var node = Node("ou=hosts", capped: true, open: false, Node("uid=host-1"));

        var rows = Rows(node);

        Assert.Single(rows);
        Assert.Equal("1+", rows[0].Count);
    }

    [Fact]
    public void Expanded_mirrors_open_state_without_a_filter()
    {
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=open", capped: false, open: true, Node("uid=alice")),
            Node("ou=closed", capped: false, open: false, Node("uid=bob")));

        var rows = Rows(root);

        Assert.True(rows.Single(r => r.Label == "ou=open").Expanded);
        Assert.False(rows.Single(r => r.Label == "ou=closed").Expanded);
    }

    [Fact]
    public void Expanded_reports_the_rendered_state_for_a_filter_forced_open_ancestor()
    {
        // The keyboard handler and aria-expanded read Row.Expanded, never Node.Open: under
        // a filter an ancestor with a matching descendant renders open while Open is false.
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=people", capped: false, open: false, Node("uid=alice")));

        var rows = Rows(root, "alice");

        var ancestor = rows.Single(r => r.Label == "ou=people");
        Assert.True(ancestor.Expanded);
        Assert.False(ancestor.Node!.Open); // the saved state the filter's clearing restores
        Assert.Equal("▾", ancestor.Twisty);
    }

    [Fact]
    public void Expanded_is_false_for_a_filtered_node_with_no_visible_children()
    {
        var root = Node("dc=example", capped: false, open: true,
            Node("ou=hosts", capped: true, open: true, Node("uid=printer-9")));

        var rows = Rows(root, "hosts");

        // Matches its own RDN but no loaded child matches: renders closed regardless of Open.
        Assert.False(rows.Single(r => r.Label == "ou=hosts").Expanded);
    }
}

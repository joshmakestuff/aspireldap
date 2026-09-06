using System.Collections.Immutable;
using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class BulkEditPlanTests
{
    private const string GroupDn = "cn=team,ou=groups,dc=example,dc=org";

    [Theory]
    [InlineData(BulkAttributeOperation.Add, DirectoryAttributeOperation.Add)]
    [InlineData(BulkAttributeOperation.Replace, DirectoryAttributeOperation.Replace)]
    [InlineData(BulkAttributeOperation.Delete, DirectoryAttributeOperation.Delete)]
    public void Attribute_plan_is_one_immutable_modify_per_selected_dn(
        BulkAttributeOperation requested,
        DirectoryAttributeOperation expected)
    {
        var entries = Entries();
        var values = requested == BulkAttributeOperation.Delete ? ImmutableArray<string>.Empty : ["alpha", "beta"];

        var built = BulkEditPlan.Build(new BulkEditDraft(
            BulkEditOperation.Attribute,
            entries,
            null,
            null,
            requested,
            " description ",
            values));

        var plan = Assert.IsType<BulkEditPlan>(built.Plan);
        Assert.Null(built.Error);
        Assert.Equal(entries.Select(static entry => entry.Dn), plan.Items.Select(static item => item.TargetDn));
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(expected, item.Change.Operation);
            Assert.Equal("description", item.Change.Name);
            Assert.Contains(item.SelectedDn, item.Preview, StringComparison.Ordinal);
        });
        Assert.Contains(requested == BulkAttributeOperation.Delete ? "entire attribute" : "alpha, beta",
            plan.Items[0].Preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BulkEditOperation.AddToGroup, DirectoryAttributeOperation.Add)]
    [InlineData(BulkEditOperation.RemoveFromGroup, DirectoryAttributeOperation.Delete)]
    public void Group_plan_targets_the_group_once_per_selected_dn_with_schema_defined_values(
        BulkEditOperation requested,
        DirectoryAttributeOperation expected)
    {
        var entries = Entries();
        var group = Entry(GroupDn);
        var membership = new GroupMembershipAttribute("member", GroupMemberValueKind.DistinguishedName, Required: true);

        var plan = Assert.IsType<BulkEditPlan>(BulkEditPlan.Build(new BulkEditDraft(
            requested, entries, group, membership, BulkAttributeOperation.Add, string.Empty, [])).Plan);

        Assert.Equal(2, plan.Items.Length);
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(GroupDn, item.TargetDn);
            Assert.Equal(expected, item.Change.Operation);
            Assert.Equal("member", item.Change.Name);
            Assert.Equal([item.SelectedDn], item.Change.Values);
            Assert.Contains($"on {GroupDn}", item.Preview, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Uid_group_plan_names_every_selected_dn_that_has_no_readable_uid()
    {
        var entries = ImmutableArray.Create(
            Entry("cn=first,dc=example,dc=org"),
            Entry("cn=second,dc=example,dc=org"));

        var built = BulkEditPlan.Build(new BulkEditDraft(
            BulkEditOperation.AddToGroup,
            entries,
            Entry(GroupDn),
            new GroupMembershipAttribute("memberUid", GroupMemberValueKind.Uid, Required: false),
            BulkAttributeOperation.Add,
            string.Empty,
            []));

        Assert.Null(built.Plan);
        Assert.Contains(entries[0].Dn, built.Error, StringComparison.Ordinal);
        Assert.Contains(entries[1].Dn, built.Error, StringComparison.Ordinal);
    }

    internal static ImmutableArray<LdapEntry> Entries() =>
        [Entry("uid=alice,dc=example,dc=org", "alice"), Entry("uid=bob,dc=example,dc=org", "bob")];

    internal static LdapEntry Entry(string dn, string? uid = null) => new(
        dn,
        uid is null ? [] : [new LdapAttributeValues("uid", false, [uid], LdapValueClassification.Schema)]);
}

public sealed class BulkEditComponentTests : TestContext
{
    public BulkEditComponentTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Theory]
    [InlineData(BulkAttributeOperation.Add)]
    [InlineData(BulkAttributeOperation.Replace)]
    [InlineData(BulkAttributeOperation.Delete)]
    public async Task Attribute_values_preserve_whitespace_and_empty_lines_through_dispatch(BulkAttributeOperation operation)
    {
        List<BulkEditPlanItem> dispatched = [];
        var cut = RenderComponent<BulkEditDialog>(parameters => parameters.Add(component => component.Model, Model(
            item => { dispatched.Add(item); return Task.FromResult(LdapOperationResult.Ok()); },
            _ => Task.CompletedTask)));
        cut.Find("select").Change(BulkEditOperation.Attribute.ToString());
        cut.Find("select[aria-label='Attribute action']").Change(operation.ToString());
        cut.Find("input[aria-label='Attribute name']").Input("description");
        cut.Find("textarea").Change(" \r\n\t\n leading and trailing \t\n\nquoted \\\";=é\n");
        cut.FindAll("button").Single(button => button.TextContent == "Preview changes").Click();
        Assert.DoesNotContain("entire attribute", cut.Find(".bulk-preview").TextContent, StringComparison.Ordinal);
        await cut.FindAll("button").Single(button => button.TextContent == "Apply changes").ClickAsync(new());

        Assert.Equal(2, dispatched.Count);
        Assert.All(dispatched, item => Assert.Equal(
            new[] { " ", "\t", " leading and trailing \t", "", "quoted \\\";=é", "" }, item.Change.Values));
    }

    [Theory]
    [InlineData(" ", false)]
    [InlineData("\t", false)]
    [InlineData("\n", false)]
    [InlineData("", true)]
    public async Task Only_an_empty_delete_field_removes_the_whole_attribute(string text, bool deleteWholeAttribute)
    {
        List<BulkEditPlanItem> dispatched = [];
        var cut = RenderComponent<BulkEditDialog>(parameters => parameters.Add(component => component.Model, Model(
            item => { dispatched.Add(item); return Task.FromResult(LdapOperationResult.Ok()); },
            _ => Task.CompletedTask)));
        cut.Find("select").Change(BulkEditOperation.Attribute.ToString());
        cut.Find("select[aria-label='Attribute action']").Change(BulkAttributeOperation.Delete.ToString());
        cut.Find("input[aria-label='Attribute name']").Input("description");
        cut.Find("textarea").Change(text);
        cut.FindAll("button").Single(button => button.TextContent == "Preview changes").Click();
        Assert.Equal(deleteWholeAttribute, cut.Find(".bulk-preview").TextContent.Contains("entire attribute", StringComparison.Ordinal));
        await cut.FindAll("button").Single(button => button.TextContent == "Apply changes").ClickAsync(new());
        Assert.Equal(2, dispatched.Count);
        Assert.All(dispatched, item =>
        {
            Assert.Equal(DirectoryAttributeOperation.Delete, item.Change.Operation);
            Assert.Equal(deleteWholeAttribute, item.Change.Values.Count == 0);
        });
    }

    [Fact]
    public void Row_selection_is_dn_keyed_and_survives_pages_and_sorting()
    {
        var entries = Enumerable.Range(1, 30)
            .Select(index => SearchResultTableStateTests.Entry($"uid={index:D2},dc=example,dc=org", name: $"Name {31 - index:D2}"))
            .ToArray();
        HashSet<string> selected = new(StringComparer.Ordinal);
        IRenderedComponent<SearchResultsTable>? cut = null;
        cut = RenderComponent<SearchResultsTable>(parameters => parameters
            .Add(component => component.Result, new LdapSearchResult(entries, Truncated: false))
            .Add(component => component.SelectedDns, selected)
            .Add(component => component.OnSelectionChanged, change =>
            {
                if (change.Selected) selected.Add(change.Entry.Dn); else selected.Remove(change.Entry.Dn);
                cut!.SetParametersAndRender(parameters => parameters.Add(component => component.SelectedDns, selected));
            }));

        cut.Find("input[type=checkbox]").Change(true);
        var firstDn = entries[0].Dn;
        cut.Find("nav[aria-label='Search result pages'] button:last-child").Click();
        cut.Find("input[type=checkbox]").Change(true);
        var secondDn = entries[25].Dn;
        cut.Find("th:nth-child(3) button").Click();

        Assert.Equal(2, selected.Count);
        Assert.Contains(firstDn, selected);
        Assert.Contains(secondDn, selected);
    }

    [Fact]
    public async Task Partial_failure_names_the_dn_and_retry_does_not_reapply_successes()
    {
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var closed = 0;
        BulkEditRunResult? lastRun = null;
        var model = Model(item =>
        {
            calls[item.SelectedDn] = calls.GetValueOrDefault(item.SelectedDn) + 1;
            return Task.FromResult(item.SelectedDn.Contains("bob", StringComparison.Ordinal) && calls[item.SelectedDn] == 1
                ? new LdapOperationResult(LdapOperationStatus.ConstraintViolation, Message: "duplicate value")
                : LdapOperationResult.Ok());
        }, run => { lastRun = run; return Task.CompletedTask; });
        var cut = RenderComponent<BulkEditDialog>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.OnClose, () => closed++));

        SelectAttributePlan(cut);
        await cut.FindAll("button").Single(button => button.TextContent == "Apply changes").ClickAsync(new());

        Assert.Equal(0, closed);
        Assert.NotNull(lastRun);
        Assert.Equal(1, lastRun.FailedCount);
        Assert.Contains("uid=bob", cut.Find(".bulk-outcome[data-status=failed]").TextContent, StringComparison.Ordinal);
        Assert.Contains("duplicate value", cut.Markup, StringComparison.Ordinal);

        await cut.FindAll("button").Single(button => button.TextContent == "Retry failed and pending").ClickAsync(new());

        Assert.Equal(1, closed);
        Assert.Equal(1, calls["uid=alice,dc=example,dc=org"]);
        Assert.Equal(2, calls["uid=bob,dc=example,dc=org"]);
    }

    [Fact]
    public async Task Cancellation_finishes_the_dispatched_item_and_leaves_the_rest_pending()
    {
        var first = new TaskCompletionSource<LdapOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        BulkEditRunResult? lastRun = null;
        var model = Model(_ => ++calls == 1 ? first.Task : Task.FromResult(LdapOperationResult.Ok()),
            run => { lastRun = run; return Task.CompletedTask; });
        var cut = RenderComponent<BulkEditDialog>(parameters => parameters.Add(component => component.Model, model));
        SelectAttributePlan(cut);

        cut.FindAll("button").Single(button => button.TextContent == "Apply changes").Click();
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());
        first.SetResult(LdapOperationResult.Ok());

        cut.WaitForAssertion(() => Assert.Contains("1 completed · 0 failed · 1 pending · cancelled", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, calls);
        Assert.True(lastRun!.Cancelled);
    }

    [Fact]
    public void Closing_An_Incomplete_Run_Leaves_Only_Failed_And_Pending_Entries_Selected()
    {
        var entries = BulkEditPlanTests.Entries();
        var selected = entries.ToDictionary(static entry => entry.Dn, StringComparer.Ordinal);
        var items = entries.Select(entry => new BulkEditPlanItem(
            entry.Dn, entry.Dn,
            new LdapAttributeChange(DirectoryAttributeOperation.Replace, "description", ["value"]))).ToArray();
        var run = new BulkEditRunResult(
        [
            new BulkEditItemOutcome(items[0], BulkEditItemStatus.Succeeded),
            new BulkEditItemOutcome(items[1], BulkEditItemStatus.Failed, "refused"),
        ], Cancelled: false);

        SearchPanel.RetainIncompleteSelection(selected, run);

        Assert.DoesNotContain(items[0].SelectedDn, selected.Keys);
        Assert.Contains(items[1].SelectedDn, selected.Keys);
    }

    [Fact]
    public void Generic_Bulk_Candidates_Exclude_Binary_And_Managed_Membership_Attributes()
    {
        var schema = GroupMembershipSchemaTests.TestSchema;
        var type = schema.FindAttributeType("member")!;
        var group = new LdapEntry("cn=team,dc=example,dc=org",
        [
            new LdapAttributeValues("objectClass", IsBinary: false, ["teamByDn"], LdapValueClassification.Schema),
            new LdapAttributeValues("member", IsBinary: false, ["uid=a,dc=example,dc=org"], LdapValueClassification.Schema),
            new LdapAttributeValues("jpegPhoto", IsBinary: true, ["AA=="], LdapValueClassification.Schema),
        ]);
        var candidates = new[]
        {
            new AttributeGuidance(type, "member", Required: true, SingleValued: false, NoUserModification: false, SyntaxLabel: "DN"),
            new AttributeGuidance(type, "jpegPhoto", Required: false, SingleValued: false, NoUserModification: false, SyntaxLabel: "JPEG"),
            new AttributeGuidance(type, "description", Required: false, SingleValued: false, NoUserModification: false, SyntaxLabel: "text"),
        };

        var filtered = SearchPanel.FilterBulkCandidates(schema, [group], candidates);

        Assert.Equal("description", Assert.Single(filtered).Name);
    }

    [Fact]
    public void Manually_Typed_Unsupported_Attribute_Cannot_Build_A_Bulk_Plan()
    {
        var cut = RenderComponent<BulkEditDialog>(parameters => parameters.Add(component => component.Model, Model(
            _ => Task.FromResult(LdapOperationResult.Ok()), _ => Task.CompletedTask)));
        cut.Find("select").Change(BulkEditOperation.Attribute.ToString());
        cut.Find("input[aria-label='Attribute name']").Input("member");

        cut.FindAll("button").Single(button => button.TextContent == "Preview changes").Click();

        Assert.Contains("available for every selected entry", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview", cut.FindAll(".sec").Select(section => section.TextContent));
    }

    private static BulkEditDialogModel Model(
        Func<BulkEditPlanItem, Task<LdapOperationResult>> apply,
        Func<BulkEditRunResult, Task> finished) => new()
        {
            Entries = BulkEditPlanTests.Entries(),
            AttributeCandidates = [SchemaGuide.Describe(ConsoleTestSchema.Schema, "description", required: false)!],
            SearchGroupsAsync = (_, _) => Task.FromResult(new LdapSearchResult([], Truncated: false)),
            DescribeGroup = _ => [],
            ApplyAsync = apply,
            RunFinishedAsync = finished,
        };

    private static void SelectAttributePlan(IRenderedComponent<BulkEditDialog> cut)
    {
        cut.Find("select").Change(BulkEditOperation.Attribute.ToString());
        cut.Find("input[aria-label='Attribute name']").Input("description");
        cut.Find("textarea").Change("bulk value");
        cut.FindAll("button").Single(button => button.TextContent == "Preview changes").Click();
        var preview = cut.Find(".bulk-preview").TextContent;
        Assert.Contains("uid=alice", preview, StringComparison.Ordinal);
        Assert.Contains("uid=bob", preview, StringComparison.Ordinal);
    }
}

[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BulkEditIntegrationTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Group_plan_applies_each_selected_dn_as_an_independent_modify()
    {
        using var cts = TestCancellation.Source();
        var firstDn = fixture.DnUnder("uid=bulk-plan-first", "ou=people");
        var secondDn = fixture.DnUnder("uid=bulk-plan-second", "ou=people");
        var anchorDn = fixture.DnUnder("uid=bulk-plan-anchor", "ou=people");
        var groupDn = fixture.DnUnder("cn=bulk-plan-group", "ou=groups");
        var people = new[]
        {
            Person(firstDn, "bulk-plan-first"),
            Person(secondDn, "bulk-plan-second"),
            Person(anchorDn, "bulk-plan-anchor"),
        };

        foreach (var person in people)
        {
            Assert.True((await fixture.Directory.AddEntryAsync(person, cts.Token)).Succeeded);
        }
        Assert.True((await fixture.Directory.AddEntryAsync(new LdapNewEntry(groupDn,
        [
            new("objectClass", ["groupOfNames"]),
            new("cn", ["bulk-plan-group"]),
            new("member", [anchorDn]),
        ]), cts.Token)).Succeeded);

        try
        {
            var selected = ImmutableArray.Create(
                BulkEditPlanTests.Entry(firstDn, "bulk-plan-first"),
                BulkEditPlanTests.Entry(secondDn, "bulk-plan-second"));
            var group = new LdapEntry(groupDn,
            [
                new LdapAttributeValues("objectClass", false, ["groupOfNames"], LdapValueClassification.Schema),
            ]);
            var plan = Assert.IsType<BulkEditPlan>(BulkEditPlan.Build(new BulkEditDraft(
                BulkEditOperation.AddToGroup,
                selected,
                group,
                new GroupMembershipAttribute("member", GroupMemberValueKind.DistinguishedName, Required: true),
                BulkAttributeOperation.Add,
                string.Empty,
                [])).Plan);

            foreach (var item in plan.Items)
            {
                var result = await fixture.Directory.ModifyEntryAsync(item.TargetDn, [item.Change], cts.Token);
                Assert.True(result.Succeeded, $"{item.SelectedDn}: {result.Message}");
            }

            var updated = await fixture.Directory.GetEntryAsync(groupDn, ["member"], cts.Token);
            var members = Assert.Single(updated!.Attributes).Values;
            Assert.Contains(firstDn, members, StringComparer.Ordinal);
            Assert.Contains(secondDn, members, StringComparer.Ordinal);
            Assert.Contains(anchorDn, members, StringComparer.Ordinal);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(groupDn, cts.Token);
            foreach (var person in people.Reverse())
            {
                await fixture.Directory.DeleteEntryAsync(person.Dn, cts.Token);
            }
        }
    }

    private static LdapNewEntry Person(string dn, string uid) => new(dn,
    [
        new("objectClass", ["inetOrgPerson"]),
        new("uid", [uid]),
        new("cn", [uid]),
        new("sn", [uid]),
    ]);
}

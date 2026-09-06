using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class GroupMembershipSchemaTests
{
    private static readonly LdifDotNet.Schema.LdapSchema Schema = LdifDotNet.Schema.LdapSchema.ParseSubschema(
        [
            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.3 NAME 'cn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.31 NAME 'member' SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )",
            "( 2.5.4.50 NAME 'uniqueMember' SYNTAX 1.3.6.1.4.1.1466.115.121.1.34 )",
            "( 1.3.6.1.1.1.1.12 NAME 'memberUid' SYNTAX 1.3.6.1.4.1.1466.115.121.1.26 )",
            "( 9.9.1 NAME 'notMember' SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )",
        ],
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 9.9.2 NAME 'teamByDn' SUP top STRUCTURAL MUST ( cn $ member ) )",
            "( 9.9.3 NAME 'teamByUniqueDn' SUP top STRUCTURAL MUST ( cn $ uniqueMember ) )",
            "( 9.9.4 NAME 'teamByUid' SUP top STRUCTURAL MUST cn MAY memberUid )",
            "( 9.9.5 NAME 'ordinaryEntry' SUP top STRUCTURAL MUST cn MAY notMember )",
        ]);

    internal static LdifDotNet.Schema.LdapSchema TestSchema => Schema;

    private static LdapEntry Entry(string objectClass) => new(
        "cn=test,dc=example,dc=org",
        [Text("objectClass", "top", objectClass), Text("cn", "test")]);

    private static LdapAttributeValues Text(string name, params string[] values) =>
        new(name, IsBinary: false, values, LdapValueClassification.Schema);

    [Theory]
    [InlineData("teamByDn", "member", GroupMemberValueKind.DistinguishedName, true)]
    [InlineData("teamByUniqueDn", "uniqueMember", GroupMemberValueKind.DistinguishedName, true)]
    [InlineData("teamByUid", "memberUid", GroupMemberValueKind.Uid, false)]
    public void Detection_Uses_The_Loaded_Class_Definition_Not_A_Class_Name_List(
        string objectClass, string attribute, GroupMemberValueKind kind, bool required)
    {
        var detected = Assert.Single(GroupMembership.Detect(Schema, Entry(objectClass)));

        Assert.Equal(attribute, detected.Name);
        Assert.Equal(kind, detected.ValueKind);
        Assert.Equal(required, detected.Required);
    }

    [Fact]
    public void Detection_Requires_A_Supported_Attribute_And_Expected_Syntax()
    {
        Assert.Empty(GroupMembership.Detect(Schema, Entry("ordinaryEntry")));
        Assert.Empty(GroupMembership.Detect(null, Entry("teamByDn")));
    }

    [Fact]
    public void Dn_Membership_Detects_Equivalent_Stored_Spellings()
    {
        var membership = new GroupMembershipAttribute("member", GroupMemberValueKind.DistinguishedName, Required: true);

        Assert.True(GroupMembership.ContainsEquivalentValue(
            membership, [@"CN=Smith\, Alice,OU=People,DC=Example,DC=Org"],
            @"cn=Smith\2C Alice,ou=people,dc=example,dc=org"));
        Assert.False(GroupMembership.ContainsEquivalentValue(
            new GroupMembershipAttribute("memberUid", GroupMemberValueKind.Uid, Required: false),
            ["Alice"], "alice"));
    }

    [Fact]
    public void Assertion_Escaping_Covers_Every_Rfc4515_Required_Octet()
    {
        Assert.Equal(@"a\2a\28b\29\5c\00", GroupMemberSearch.EscapeAssertionValue("a*(b)\\\0"));
        Assert.Equal(@"(|(uid=*a\2a\28b\29\5c\00*)(cn=*a\2a\28b\29\5c\00*)(mail=*a\2a\28b\29\5c\00*))",
            GroupMemberSearch.Filter("a*(b)\\\0"));
    }
}

public sealed class GroupMembershipComponentTests : TestContext
{
    private const string GroupDn = "cn=team,ou=groups,dc=example,dc=org";
    private static readonly GroupMembershipAttribute Membership =
        new("member", GroupMemberValueKind.DistinguishedName, Required: true);

    private static LdapAttributeValues Text(string name, params string[] values) =>
        new(name, IsBinary: false, values, LdapValueClassification.Schema);

    public GroupMembershipComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new LdapAdminSettings { AttributeValueDisplayCap = 2 });
        Services.AddSingleton(new ConsoleToastService());
        Services.AddSingleton(new ConsoleClipboard(JSInterop.JSRuntime));
    }

    [Fact]
    public async Task Members_Panel_Caps_Expands_And_Removes_The_Exact_Stored_Value()
    {
        string? removed = null;
        var entry = new LdapEntry(GroupDn,
        [
            Text("objectClass", "groupOfNames"),
            Text("member", "uid=A,dc=example,dc=org", "uid=b,dc=example,dc=org", "UID=c,DC=example,DC=org"),
        ]);
        var cut = RenderComponent<GroupMembersPanel>(parameters => parameters
            .Add(p => p.Entry, entry)
            .Add(p => p.Memberships, [Membership])
            .Add(p => p.RemoveMemberAsync, (_, _, value, _) =>
            {
                removed = value;
                return Task.FromResult<string?>(null);
            }));

        Assert.Contains("2 of 3 members", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("tbody tr").Count);
        cut.FindAll("button").Single(button => button.TextContent.Contains("show all", StringComparison.Ordinal)).Click();
        Assert.Equal(3, cut.FindAll("tbody tr").Count);

        await cut.FindAll("button").Single(button => button.GetAttribute("title") == "Remove UID=c,DC=example,DC=org")
            .ClickAsync(new());
        Assert.Equal("UID=c,DC=example,DC=org", removed);
    }

    [Fact]
    public async Task Pending_Removal_Stays_Bound_To_The_Entry_That_Started_It()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? removedFrom = null;
        var first = new LdapEntry(GroupDn, [Text("member", "uid=a,dc=example,dc=org")]);
        var second = new LdapEntry("cn=other,ou=groups,dc=example,dc=org", [Text("member", "uid=b,dc=example,dc=org")]);
        Func<string, GroupMembershipAttribute, string, CancellationToken, Task<string?>> remove = (dn, _, _, _) =>
        {
            removedFrom = dn;
            return pending.Task;
        };
        var cut = RenderComponent<GroupMembersPanel>(parameters => parameters
            .Add(p => p.Entry, first)
            .Add(p => p.Memberships, [Membership])
            .Add(p => p.RemoveMemberAsync, remove));

        var removal = cut.Find("button[title^='Remove']").ClickAsync(new());
        cut.SetParametersAndRender(parameters => parameters
            .Add(p => p.Entry, second)
            .Add(p => p.Memberships, [Membership])
            .Add(p => p.RemoveMemberAsync, remove));
        pending.SetResult("stale failure");
        await removal;

        Assert.Equal(GroupDn, removedFrom);
        Assert.Contains("uid=b", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("stale failure", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_Attribute_Editing_Is_Disabled_For_Managed_Membership()
    {
        var entry = new LdapEntry(GroupDn, [Text("member", "uid=a,dc=example,dc=org"), Text("cn", "team")]);
        var cut = RenderComponent<EntryView>(parameters => parameters
            .Add(p => p.Entry, entry)
            .Add(p => p.OnEdit, _ => { })
            .Add(p => p.ManagedAttributes, new HashSet<string>(["member"], StringComparer.OrdinalIgnoreCase)));

        var managed = cut.FindAll("button").Single(button => button.TextContent == "Members");
        Assert.True(managed.HasAttribute("disabled"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent == "Edit" &&
            button.GetAttribute("title") == "Edit member");
    }

    [Fact]
    public async Task Generic_Ldif_Editing_Refuses_A_Managed_Membership_Replacement()
    {
        var applied = false;
        var entry = new LdapEntry(GroupDn,
        [
            Text("objectClass", "groupOfNames"),
            Text("cn", "team"),
            Text("member", "uid=a,dc=example,dc=org", "uid=b,dc=example,dc=org"),
        ]);
        var cut = RenderComponent<EntryLdifPanel>(parameters => parameters
            .Add(p => p.Entry, entry)
            .Add(p => p.ManagedAttributes, new HashSet<string>(["member"], StringComparer.OrdinalIgnoreCase))
            .Add(p => p.ApplyAsync, _ =>
            {
                applied = true;
                return Task.FromResult<string?>(null);
            }));
        var draft = cut.Find("textarea").GetAttribute("value")!
            .Replace("member: uid=b,dc=example,dc=org\n", string.Empty, StringComparison.Ordinal);

        cut.Find("textarea").Input(draft);
        await cut.Find("button.btn-primary").ClickAsync(new());

        Assert.False(applied);
        Assert.Contains("managed in the Members tab", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_Dialog_Searches_With_A_Bound_And_Adds_A_Selected_Dn()
    {
        string? searched = null;
        string? added = null;
        var entries = Enumerable.Range(1, GroupMemberSearch.ResultLimit)
            .Select(index => new LdapEntry($"uid=user{index},dc=example,dc=org", [Text("uid", $"user{index}")]))
            .ToList();
        var model = new AddGroupMemberDialogModel
        {
            Membership = Membership,
            ExistingValues = new HashSet<string>(StringComparer.Ordinal),
            SearchAsync = (query, _) =>
            {
                searched = query;
                return Task.FromResult(new LdapSearchResult(entries, Truncated: true));
            },
            SaveAsync = (value, _) =>
            {
                added = value;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<AddGroupMemberDialog>(parameters => parameters.Add(p => p.Model, model));

        cut.Find("input[placeholder='name, uid, or email']").Input("alice*)(uid=*)");
        await cut.FindAll("button").Single(button => button.TextContent == "Search").ClickAsync(new());
        Assert.Equal("alice*)(uid=*)", searched);
        Assert.Contains($"truncated at {GroupMemberSearch.ResultLimit}", cut.Markup, StringComparison.Ordinal);

        cut.Find("input[type=radio]").Change(entries[0].Dn);
        await cut.FindAll("button").Single(button => button.TextContent == "Add member").ClickAsync(new());
        Assert.Equal(entries[0].Dn, added);
    }

    [Fact]
    public async Task Add_Dialog_Uses_Typed_Uid_Fallback_And_The_Token_Aware_Cancel_Protocol()
    {
        var pending = new TaskCompletionSource<string?>();
        CancellationToken saveToken = default;
        var model = new AddGroupMemberDialogModel
        {
            Membership = new GroupMembershipAttribute("memberUid", GroupMemberValueKind.Uid, Required: false),
            ExistingValues = new HashSet<string>(StringComparer.Ordinal),
            SearchAsync = (_, _) => Task.FromResult(new LdapSearchResult([], Truncated: false)),
            SaveAsync = (value, token) =>
            {
                Assert.Equal("typed-user", value);
                saveToken = token;
                return pending.Task;
            },
        };
        var cut = RenderComponent<AddGroupMemberDialog>(parameters => parameters.Add(p => p.Model, model));

        cut.FindAll("input.input.mono").Last().Input(" typed-user ");
        cut.FindAll("button").Single(button => button.TextContent == "Add member").Click();
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());

        Assert.True(saveToken.IsCancellationRequested);
        Assert.Contains(cut.FindAll("button.btn-secondary"), button => button.TextContent == "Cancelling");
        pending.SetResult("The server refused the value.");
        cut.WaitForAssertion(() => Assert.Contains("server refused", cut.Find(".bar.err").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_New_Member_Search_Clears_The_Previous_Hidden_Selection()
    {
        var alice = new LdapEntry("uid=alice,dc=example,dc=org", [Text("uid", "alice")]);
        var searches = 0;
        var saved = false;
        var model = new AddGroupMemberDialogModel
        {
            Membership = Membership,
            ExistingValues = new HashSet<string>(StringComparer.Ordinal),
            SearchAsync = (_, _) => Task.FromResult(++searches == 1
                ? new LdapSearchResult([alice], Truncated: false)
                : new LdapSearchResult([], Truncated: false)),
            SaveAsync = (_, _) =>
            {
                saved = true;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<AddGroupMemberDialog>(parameters => parameters.Add(p => p.Model, model));
        var search = cut.FindAll("button").Single(button => button.TextContent == "Search");

        await search.ClickAsync(new());
        cut.Find("input[type=radio]").Change(alice.Dn);
        await search.ClickAsync(new());
        await cut.FindAll("button").Single(button => button.TextContent == "Add member").ClickAsync(new());

        Assert.False(saved);
        Assert.Contains("Select an entry", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
        Assert.Equal("member-search", cut.Find("label[for='member-search']").GetAttribute("for"));
        Assert.Equal("member-value", cut.Find("label[for='member-value']").GetAttribute("for"));
    }

    [Fact]
    public async Task A_Manually_Entered_Equivalent_Dn_Is_Not_Added_Again()
    {
        var saved = false;
        var existing = @"CN=Smith\, Alice,OU=People,DC=Example,DC=Org";
        var model = new AddGroupMemberDialogModel
        {
            Membership = Membership,
            ExistingValues = new HashSet<string>([existing], StringComparer.Ordinal),
            IsExistingValue = value => GroupMembership.ContainsEquivalentValue(Membership, [existing], value),
            SearchAsync = (_, _) => Task.FromResult(new LdapSearchResult([], Truncated: false)),
            SaveAsync = (_, _) =>
            {
                saved = true;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<AddGroupMemberDialog>(parameters => parameters.Add(p => p.Model, model));

        cut.Find("#member-value").Input(@"cn=Smith\2C Alice,ou=people,dc=example,dc=org");
        await cut.FindAll("button").Single(button => button.TextContent == "Add member").ClickAsync(new());

        Assert.False(saved);
        Assert.Contains("already a member", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
    }
}

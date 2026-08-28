using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Component and display-logic tests for the entry detail tabs: the Overview tab's
/// kind-driven field sets and save semantics, the LDIF tab's draft/apply flow, and the
/// entry-semantic stat strip.
/// </summary>
public sealed class EntryTabsComponentTests : TestContext
{
    private const string AliceDn = "uid=alice.chen,ou=people,dc=aspire,dc=dev";

    private static LdapEntry Entry(params LdapAttributeValues[] attributes) =>
        new(AliceDn, attributes);

    private static LdapAttributeValues Text(string name, params string[] values) =>
        new(name, IsBinary: false, values, LdapValueClassification.Schema);

    private static LdapAttributeValues Binary(string name, params string[] base64Values) =>
        new(name, IsBinary: true, base64Values, LdapValueClassification.Schema);

    private static LdapEntry Person(params LdapAttributeValues[] extra) =>
        Entry([Text("objectClass", "inetOrgPerson", "posixAccount"), .. extra]);

    // ---- Kind classifier ----------------------------------------------------------------

    [Theory]
    [InlineData(LdapEntryKind.Person, "inetOrgPerson")]
    [InlineData(LdapEntryKind.Person, "POSIXACCOUNT")]
    [InlineData(LdapEntryKind.Person, "posixAccount", "posixGroup")] // person outranks group
    [InlineData(LdapEntryKind.Group, "groupOfNames")]
    [InlineData(LdapEntryKind.Group, "groupOfUniqueNames")]
    [InlineData(LdapEntryKind.Group, "posixGroup")]
    [InlineData(LdapEntryKind.Container, "organizationalUnit")]
    [InlineData(LdapEntryKind.Container)]
    public void Classify_Follows_The_Handoff_Class_Sets(LdapEntryKind expected, params string[] classes) =>
        Assert.Equal(expected, LdapEntryKinds.Classify(classes));

    // ---- Overview -----------------------------------------------------------------------

    [Fact]
    public void Overview_Renders_The_Person_Field_Set_And_ObjectClass_Chips()
    {
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, Person(Text("cn", "Alice Chen"), Text("uid", "alice.chen"))));

        var labels = cut.FindAll(".field > label").Select(static l => l.TextContent.Trim()).ToList();
        Assert.Contains(labels, static l => l.StartsWith("Login shell", StringComparison.Ordinal));
        Assert.Contains(labels, static l => l.StartsWith("UID number", StringComparison.Ordinal));

        var chips = cut.FindAll(".tag.tag-outline").Select(static c => c.TextContent.Trim()).ToList();
        Assert.Equal(["inetOrgPerson", "posixAccount"], chips);
    }

    [Fact]
    public void Overview_Renders_The_Container_Field_Set_With_The_Real_Name_Attribute()
    {
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, new LdapEntry("ou=people,dc=aspire,dc=dev",
                [Text("objectClass", "organizationalUnit"), Text("ou", "people")])));

        var labels = cut.FindAll(".field > label").Select(static l => l.TextContent.Trim()).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, static l => l.StartsWith("Name", StringComparison.Ordinal) && l.Contains("ou", StringComparison.Ordinal));
    }

    [Fact]
    public void Overview_Makes_MultiValued_And_Binary_Fields_ReadOnly()
    {
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, Person(
                Text("cn", "Alice Chen", "A. Chen"),
                Binary("title", Convert.ToBase64String([1])))));

        var readOnly = cut.FindAll("input[readonly]").ToList();
        Assert.Equal(2, readOnly.Count);
        Assert.All(readOnly, static i =>
            Assert.Contains("Attributes tab", i.GetAttribute("title"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overview_Save_Sends_Replaces_And_Deletes_For_Dirty_Fields_Only()
    {
        IReadOnlyList<LdapAttributeChange>? sent = null;
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, Person(Text("cn", "Alice Chen"), Text("mail", "old@aspire.dev"), Text("title", "temp")))
            .Add(p => p.SaveAsync, changes =>
            {
                sent = changes;
                return Task.FromResult<string?>(null);
            }));

        // cn stays; mail is edited; title is cleared (delete); an absent field cleared stays absent.
        cut.FindAll(".field").First(static f => f.TextContent.Contains("Email", StringComparison.Ordinal))
            .QuerySelector("input")!.Input("alice@aspire.dev");
        cut.FindAll(".field").First(static f => f.TextContent.Contains("Title", StringComparison.Ordinal))
            .QuerySelector("input")!.Input("");
        await cut.Find("button.btn-primary").ClickAsync(new());

        Assert.NotNull(sent);
        Assert.Equal(2, sent.Count);
        var mail = Assert.Single(sent, static c => c.Name == "mail");
        Assert.Equal(DirectoryAttributeOperation.Replace, mail.Operation);
        Assert.Equal(["alice@aspire.dev"], mail.Values);
        var title = Assert.Single(sent, static c => c.Name == "title");
        Assert.Equal(DirectoryAttributeOperation.Delete, title.Operation);
        Assert.Empty(title.Values);
    }

    [Fact]
    public async Task Overview_Shows_A_Save_Failure_Inline_And_Keeps_The_Draft()
    {
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, Person(Text("mail", "old@aspire.dev")))
            .Add(p => p.SaveAsync, _ => Task.FromResult<string?>("Access denied — the server's ACL refused this bind.")));

        cut.FindAll(".field").First(static f => f.TextContent.Contains("Email", StringComparison.Ordinal))
            .QuerySelector("input")!.Input("new@aspire.dev");
        await cut.Find("button.btn-primary").ClickAsync(new());

        Assert.Contains("Access denied", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
        Assert.Equal("new@aspire.dev", cut.FindAll(".field")
            .First(static f => f.TextContent.Contains("Email", StringComparison.Ordinal))
            .QuerySelector("input")!.GetAttribute("value"));
    }

    [Fact]
    public void Overview_Save_Button_Is_Disabled_Until_Something_Changes()
    {
        var cut = RenderComponent<EntryOverview>(parameters => parameters
            .Add(p => p.Entry, Person(Text("mail", "old@aspire.dev")))
            .Add(p => p.SaveAsync, _ => Task.FromResult<string?>(null)));

        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.FindAll(".field").First(static f => f.TextContent.Contains("Email", StringComparison.Ordinal))
            .QuerySelector("input")!.Input("new@aspire.dev");
        Assert.False(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    // ---- LDIF tab -----------------------------------------------------------------------

    private void AddLdifPanelServices()
    {
        Services.AddSingleton(new ConsoleToastService());
        Services.AddSingleton(new ConsoleClipboard(JSInterop.JSRuntime));
    }

    [Fact]
    public void LdifPanel_Renders_The_Entry_As_A_Content_Record()
    {
        AddLdifPanelServices();
        var cut = RenderComponent<EntryLdifPanel>(parameters => parameters
            .Add(p => p.Entry, Person(Text("cn", "Alice Chen")))
            .Add(p => p.ApplyAsync, _ => Task.FromResult<string?>(null)));

        var draft = cut.Find("textarea").GetAttribute("value")!;
        Assert.StartsWith($"dn: {AliceDn}", draft, StringComparison.Ordinal);
        Assert.Contains("objectClass: inetOrgPerson", draft, StringComparison.Ordinal);
        Assert.Contains("cn: Alice Chen", draft, StringComparison.Ordinal);
        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [Fact]
    public async Task LdifPanel_Applies_The_Diff_Of_An_Edited_Draft()
    {
        AddLdifPanelServices();
        IReadOnlyList<LdapAttributeChange>? sent = null;
        var cut = RenderComponent<EntryLdifPanel>(parameters => parameters
            .Add(p => p.Entry, Person(Text("cn", "Alice Chen")))
            .Add(p => p.ApplyAsync, changes =>
            {
                sent = changes;
                return Task.FromResult<string?>(null);
            }));

        var draft = cut.Find("textarea").GetAttribute("value")!.Replace("cn: Alice Chen", "cn: Alice A. Chen", StringComparison.Ordinal);
        cut.Find("textarea").Input(draft);
        await cut.Find("button.btn-primary").ClickAsync(new());

        Assert.NotNull(sent);
        var change = Assert.Single(sent);
        Assert.Equal("cn", change.Name);
        Assert.Equal(["Alice A. Chen"], change.Values);
    }

    [Fact]
    public async Task LdifPanel_Refuses_A_Dn_Edit_Inline()
    {
        AddLdifPanelServices();
        var applied = false;
        var cut = RenderComponent<EntryLdifPanel>(parameters => parameters
            .Add(p => p.Entry, Person(Text("cn", "Alice Chen")))
            .Add(p => p.ApplyAsync, _ =>
            {
                applied = true;
                return Task.FromResult<string?>(null);
            }));

        var draft = cut.Find("textarea").GetAttribute("value")!.Replace("uid=alice.chen", "uid=someone.else", StringComparison.Ordinal);
        cut.Find("textarea").Input(draft);
        await cut.Find("button.btn-primary").ClickAsync(new());

        Assert.False(applied);
        Assert.Contains("DN must match", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
    }

    // ---- Stat strip ---------------------------------------------------------------------

    [Fact]
    public void Stats_For_A_Person_Lead_With_Status_And_Uid_Number()
    {
        var tiles = EntryStats.For(Person(
            Text("uidNumber", "10001"),
            Text("loginShell", "/sbin/nologin"),
            Text("cn", "Alice Chen")));

        Assert.Equal(("no-login", "status"), (tiles.First().Value, tiles.First().Label));
        Assert.Contains(tiles, static t => t is { Value: "10001", Label: "uid number" });
    }

    [Fact]
    public void Stats_For_A_Group_Count_Its_Members()
    {
        var tiles = EntryStats.For(Entry(
            Text("objectClass", "groupOfNames"),
            Text("member", "uid=a,dc=aspire,dc=dev", "uid=b,dc=aspire,dc=dev")));

        Assert.Equal(("2", "members"), (tiles.First().Value, tiles.First().Label));
    }

    [Fact]
    public void Stats_Never_Invent_Tiles_The_Entry_Cannot_Answer()
    {
        // A person with no loginShell/uidNumber, a container: generic counts only.
        var person = EntryStats.For(Person(Text("cn", "Alice Chen")));
        var container = EntryStats.For(Entry(Text("objectClass", "organizationalUnit"), Text("ou", "people")));

        Assert.Equal(["attributes", "values", "object classes"], person.Select(static t => t.Label));
        Assert.Equal(["attributes", "values", "object classes"], container.Select(static t => t.Label));
    }
}

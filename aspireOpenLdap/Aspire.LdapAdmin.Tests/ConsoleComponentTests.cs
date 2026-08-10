using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EntryView = Aspire.LdapAdmin.Web.Components.Directory.EntryView;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Component tests for the Industry-console markup — possible at all because the redesign
/// owns every element (no component-library shadow DOM between the test and the behavior).
/// These assert consumer-visible behavior: what renders, what a click changes, what a save
/// failure keeps on screen. Browser verification in both themes remains a separate,
/// mandatory gate (docs/method.md); these tests do not replace it.
/// </summary>
public sealed class ConsoleComponentTests : TestContext
{
    private static LdapEntry Entry(params LdapAttributeValues[] attributes) =>
        new("uid=alice.chen,ou=people,dc=aspire,dc=dev", attributes);

    private static LdapAttributeValues Text(string name, params string[] values) =>
        new(name, IsBinary: false, values, LdapValueClassification.Schema);

    private void AddSettings(int cap = 20) =>
        Services.AddSingleton(new LdapAdminSettings { AttributeValueDisplayCap = cap });

    [Fact]
    public void EntryView_Renders_Attribute_Rows_Sorted_And_Monospace()
    {
        AddSettings();
        var cut = RenderComponent<EntryView>(parameters => parameters
            .Add(p => p.Entry, Entry(Text("uid", "alice.chen"), Text("cn", "Alice Chen"))));

        var cells = cut.FindAll("td.mono");
        Assert.Contains(cells, c => c.TextContent.Contains("cn", StringComparison.Ordinal));
        // Sorted case-insensitively by name: cn before uid.
        var names = cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent.Trim()).ToList();
        Assert.Equal(2, names.Count);
        Assert.StartsWith("cn", names[0], StringComparison.Ordinal);
        Assert.StartsWith("uid", names[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EntryView_Caps_Values_And_Expands_On_Click()
    {
        AddSettings(cap: 2);
        var values = Enumerable.Range(1, 5).Select(static i => $"value-{i}").ToArray();
        var cut = RenderComponent<EntryView>(parameters => parameters
            .Add(p => p.Entry, Entry(Text("member", values))));

        // Capped: exactly the cap renders, and the cap is stated, never silent.
        Assert.Equal(2, cut.FindAll("td:nth-child(2) div").Count);
        var expand = cut.Find("button.btn-ghost");
        Assert.Contains("Showing 2 of 5 values", expand.TextContent, StringComparison.Ordinal);

        expand.Click();
        Assert.Equal(5, cut.FindAll("td:nth-child(2) div").Count);
    }

    [Fact]
    public void EntryView_Labels_Binary_Values_Instead_Of_Printing_Them()
    {
        AddSettings();
        // "AAAA" decodes to 3 bytes.
        var binary = new LdapAttributeValues(
            "userCertificate", IsBinary: true, ["AAAA"], LdapValueClassification.Schema);
        var cut = RenderComponent<EntryView>(parameters => parameters
            .Add(p => p.Entry, Entry(binary)));

        Assert.Contains("binary", cut.Find("td:first-child").TextContent, StringComparison.Ordinal);
        Assert.Contains("3 bytes", cut.Find("td:nth-child(2) em").TextContent, StringComparison.Ordinal);
        // The base64 transport form hides behind an explicit disclosure, not inline text.
        Assert.Equal("AAAA", cut.Find("details code").TextContent);
    }

    [Fact]
    public void AttributeDialog_Save_Failure_Shows_Inline_And_Keeps_The_Dialog_Open()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closed = false;
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "mail",
            ValuesText = "alice@aspire.dev",
            SaveAsync = _ => Task.FromResult<string?>("Access denied — the server's ACL refused this bind."),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find("button.btn-primary").Click();

        Assert.False(closed);
        Assert.Contains("Access denied", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeDialog_Save_Success_Closes()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closed = false;
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "mail",
            ValuesText = "alice@aspire.dev",
            SaveAsync = _ => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find("button.btn-primary").Click();

        Assert.True(closed);
    }

    [Fact]
    public void NewChildDialog_Previews_The_Composed_Dn_And_Warns_On_A_Pasted_Full_Dn()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new ChildDialogModel
        {
            ParentDn = "ou=directory,dc=example,dc=org",
            SaveAsync = _ => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<NewChildDialog>(parameters => parameters
            .Add(p => p.Model, model));

        // A single RDN previews the DN the dialog would create.
        cut.Find(".field input").Input("uid=jane.doe");
        Assert.Contains("creates uid=jane.doe,ou=directory,dc=example,dc=org",
            cut.Markup, StringComparison.Ordinal);

        // A pasted full DN composes a child under a parent that does not exist — the
        // exact input that produced the server's mystery NoSuchObject. It must warn.
        cut.Find(".field input").Input("uid=jane.doe,ou=directory,dc=example,dc=org");
        Assert.Contains("not a single attribute=value RDN", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleDialog_Escape_And_Backdrop_Click_Cancel()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cancelled = 0;
        var cut = RenderComponent<ConsoleDialog>(parameters => parameters
            .Add(p => p.Title, "Test dialog")
            .Add(p => p.OnCancel, () => { cancelled++; }));

        // Escape arrives through the JS focus trap (a Blazor keydown on the backdrop would
        // round-trip every keystroke), which invokes this JSInvokable.
        await cut.InvokeAsync(() => cut.Instance.CancelFromJs());
        Assert.Equal(1, cancelled);

        cut.Find(".dialog-backdrop").Click();
        Assert.Equal(2, cancelled);
    }

    [Fact]
    public void ConsoleDialog_Wears_The_Blueprint_Frame_And_Dialog_Role()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<ConsoleDialog>(parameters => parameters
            .Add(p => p.Title, "Framed"));

        var panel = cut.Find("[role=dialog]");
        Assert.Equal("true", panel.GetAttribute("aria-modal"));
        // The design system's registration marks are load-bearing (industry-ui skill).
        Assert.Equal(4, cut.FindAll(".blueprint > .corner").Count);
    }
}

/// <summary>Pure display-logic checks for the console (no renderer needed).</summary>
public sealed class ConsoleDisplayLogicTests
{
    [Theory]
    [InlineData(5, 20, false, 5, false)]  // under the cap: everything renders
    [InlineData(25, 20, false, 20, true)] // over the cap: exactly the cap, flagged
    [InlineData(25, 20, true, 25, false)] // explicitly expanded: everything renders
    public void PlanValues_Caps_Exactly_And_Never_Silently(
        int total, int cap, bool expanded, int shown, bool capped)
    {
        var plan = EntryView.PlanValues(total, cap, expanded);
        Assert.Equal(shown, plan.Shown);
        Assert.Equal(total, plan.Total);
        Assert.Equal(capped, plan.Capped);
    }

    [Fact]
    public void CountBadge_States_The_Cap_While_One_Is_In_Effect()
    {
        Assert.Equal("20 of 25 values", EntryView.CountBadge(EntryView.PlanValues(25, 20, expanded: false)));
        Assert.Equal("5 values", EntryView.CountBadge(EntryView.PlanValues(5, 20, expanded: false)));
    }

    [Theory]
    [InlineData("AAAA", "3 bytes")]
    [InlineData("AA==", "1 byte")]
    [InlineData("", "0 bytes")]
    public void DescribeBinary_Sizes_From_Length_Without_Decoding(string base64, string expected) =>
        Assert.Equal(expected, EntryView.DescribeBinary(base64));

    [Fact]
    public void EntryTitle_Prefers_The_Display_Name_Over_The_Rdn_Value()
    {
        // Design reference: the header reads "Alice Chen", not "alice.chen".
        var entry = new LdapEntry("uid=alice.chen,ou=people,dc=aspire,dc=dev",
        [
            new LdapAttributeValues("uid", false, ["alice.chen"], LdapValueClassification.Schema),
            new LdapAttributeValues("cn", false, ["Alice Chen"], LdapValueClassification.Schema),
        ]);
        Assert.Equal("Alice Chen", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Falls_Back_To_The_Rdn_Value_Without_A_Display_Name()
    {
        var entry = new LdapEntry("uid=svc-checkout,ou=services,dc=aspire,dc=dev",
        [
            new LdapAttributeValues("uid", false, ["svc-checkout"], LdapValueClassification.Schema),
        ]);
        Assert.Equal("svc-checkout", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void ConnectionInfo_Carries_Server_And_Bind_But_Never_The_Password()
    {
        var info = ConsoleConnectionInfo.From(
            "Endpoint=ldap://localhost:1389;BaseDN=dc=aspire,dc=dev;BindDN=cn=admin,dc=aspire,dc=dev;BindPassword=s3cret");
        Assert.Equal("ldap://localhost:1389", info.ServerLabel);
        Assert.Equal("cn=admin,dc=aspire,dc=dev", info.BindDn);
        Assert.DoesNotContain("s3cret", info.ServerLabel + info.BindDn, StringComparison.Ordinal);
    }
}

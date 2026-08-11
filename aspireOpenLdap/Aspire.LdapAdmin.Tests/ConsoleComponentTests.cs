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
            .Add(p => p.Entry, Entry(Text("objectClass", "top", "person")))
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
            .Add(p => p.Entry, Entry(Text("objectClass", "top", "person")))
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find("button.btn-primary").Click();

        Assert.True(closed);
    }

    [Fact]
    public void AttributeDialog_With_Schema_Adapts_The_Value_Input_And_Shows_Guidance()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "uidNumber",
            SaveAsync = _ => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Entry, Entry(Text("objectClass", "top", "person", "organizationalPerson", "inetOrgPerson")))
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        // SINGLE-VALUE type: one input, not the multi-line textarea; guidance names it.
        Assert.Contains("single-valued", cut.Find(".hint").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("textarea"));
        Assert.NotEmpty(cut.FindAll(".field input.input.mono"));
    }

    [Fact]
    public void NewEntryWizard_Chains_Classes_Derives_Musts_And_Previews_Ldif()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        LdapNewEntry? saved = null;
        var model = new NewEntryModel
        {
            ParentDn = "ou=people,dc=example,dc=org",
            SaveAsync = entry => { saved = entry; return Task.FromResult<string?>(null); },
        };
        var cut = RenderComponent<NewEntryWizard>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        // Step 1: picking inetOrgPerson chains its whole superior line — an entry cannot
        // be composed without top/person/organizationalPerson.
        cut.FindAll(".picklist button")
            .First(b => b.TextContent.Contains("inetOrgPerson", StringComparison.Ordinal))
            .Click();
        foreach (var chained in (string[])["top", "person", "organizationalPerson", "inetOrgPerson"])
        {
            Assert.Contains(cut.FindAll(".tag"), t => t.TextContent == chained);
        }
        cut.Find("button.btn-primary").Click(); // Continue

        // Step 2: MUST fields derived from the chain (minus objectClass and the RDN
        // attribute, which defaulted to uid); fill the RDN and cn, leave sn empty.
        var fieldLabels = cut.FindAll(".grid2 .field label").Select(l => l.TextContent).ToList();
        Assert.Contains(fieldLabels, l => l.Contains("cn", StringComparison.Ordinal) && l.Contains("must", StringComparison.Ordinal));
        Assert.Contains(fieldLabels, l => l.Contains("sn", StringComparison.Ordinal));
        // The first .frow holds the RDN pair (attribute select + value input); the second
        // holds the optional-attribute picker.
        cut.FindAll(".frow input").First().Input("jtest");
        var cnField = cut.FindAll(".grid2 .field")
            .First(f => f.QuerySelector("label")!.TextContent.Contains("cn", StringComparison.Ordinal));
        cnField.QuerySelector("input")!.Input("J Test");
        cut.Find("button.btn-primary").Click(); // Continue

        // Step 3: the review is real LDIF; the empty MUST (sn) warns but does not block.
        var preview = cut.Find(".pre").TextContent;
        Assert.Contains("dn: uid=jtest,ou=people,dc=example,dc=org", preview, StringComparison.Ordinal);
        Assert.Contains("changetype: add", preview, StringComparison.Ordinal);
        Assert.Contains("objectClass: top", preview, StringComparison.Ordinal);
        Assert.Contains("cn: J Test", preview, StringComparison.Ordinal);
        Assert.Contains("sn", cut.Find(".bar").TextContent, StringComparison.Ordinal);

        cut.Find("button.btn-primary").Click(); // Create entry
        Assert.NotNull(saved);
        Assert.Equal("uid=jtest,ou=people,dc=example,dc=org", saved!.Dn);
    }

    [Fact]
    public void NewEntryWizard_Resets_A_Stale_Rdn_Attribute_After_A_Class_Swap()
    {
        // aspireldap#120: back-and-swap flow. inetOrgPerson defaults the RDN attribute
        // to uid; swapping to person alone removes uid from the choices, so keeping it
        // would compose an RDN the select never offered — a schema violation.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new NewEntryModel
        {
            ParentDn = "ou=people,dc=example,dc=org",
            SaveAsync = _ => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<NewEntryWizard>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        AngleSharp.Dom.IElement PickButton(string name) => cut.FindAll(".picklist button")
            .First(b => b.TextContent.Contains(name, StringComparison.Ordinal));

        PickButton("inetOrgPerson").Click();
        cut.Find("button.btn-primary").Click(); // Continue → step 2, RDN defaults to uid
        Assert.Equal("uid", cut.Find("select.input").GetAttribute("value"));

        cut.FindAll("button.btn-secondary")
            .First(b => b.TextContent == "Back").Click();
        PickButton("inetOrgPerson").Click(); // unpick
        PickButton("person").Click();
        cut.Find("button.btn-primary").Click(); // Continue → step 2 again

        // The stale uid is gone: the field holds what the select shows (cn, the first
        // choice for person), and the composed DN uses it.
        Assert.Equal("cn", cut.Find("select.input").GetAttribute("value"));
        Assert.DoesNotContain(cut.FindAll("select.input option"), o => o.TextContent == "uid");
        cut.FindAll(".frow input").First().Input("J Test");
        Assert.Contains("creates cn=J Test,ou=people,dc=example,dc=org",
            cut.Find(".dnline").TextContent, StringComparison.Ordinal);
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

/// <summary>
/// A small subschema in the server's own publication format (RFC 4512 § 4.2), so the
/// schema-aware tests exercise the same parse path a live server feeds.
/// </summary>
internal static class ConsoleTestSchema
{
    public static readonly LdifDotNet.Schema.LdapSchema Schema = LdifDotNet.Schema.LdapSchema.ParseSubschema(
        [
            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.41 NAME 'name' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.3 NAME ( 'cn' 'commonName' ) SUP name )",
            "( 2.5.4.4 NAME 'sn' SUP name )",
            "( 0.9.2342.19200300.100.1.1 NAME 'uid' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.35 NAME 'userPassword' SYNTAX 1.3.6.1.4.1.1466.115.121.1.40 )",
            "( 2.5.4.13 NAME 'description' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.3.6.1.1.1.1.0 NAME 'uidNumber' SYNTAX 1.3.6.1.4.1.1466.115.121.1.27 SINGLE-VALUE )",
            "( 2.5.18.1 NAME 'createTimestamp' SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        ],
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) MAY ( userPassword $ description ) )",
            "( 2.5.6.7 NAME 'organizationalPerson' SUP person STRUCTURAL )",
            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP organizationalPerson STRUCTURAL MAY ( uid $ uidNumber $ createTimestamp ) )",
        ]);
}

/// <summary>Schema-guide composition checks (#103/#105) — the SUP walking itself is the library's.</summary>
public sealed class SchemaGuideTests
{
    [Fact]
    public void EffectiveSets_Union_Superior_Chains_And_Dedupe_May_Against_Must()
    {
        var (must, may) = SchemaGuide.EffectiveSets(ConsoleTestSchema.Schema, ["inetOrgPerson"]);
        Assert.Contains("objectClass", must, StringComparer.OrdinalIgnoreCase); // from top
        Assert.Contains("cn", must, StringComparer.OrdinalIgnoreCase);          // from person
        Assert.Contains("sn", must, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("uid", may, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("userPassword", may, StringComparer.OrdinalIgnoreCase); // inherited MAY
        Assert.DoesNotContain("cn", may, StringComparer.OrdinalIgnoreCase);     // MUST beats MAY
    }

    [Fact]
    public void WithSuperiors_Chains_The_Whole_Line_Top_First()
    {
        var chained = SchemaGuide.WithSuperiors(ConsoleTestSchema.Schema, ["inetOrgPerson"]);
        Assert.Equal(["top", "person", "organizationalPerson", "inetOrgPerson"], chained);
    }

    [Fact]
    public void AddCandidates_Exclude_ObjectClass_NoUserModification_And_Present_SingleValued()
    {
        var candidates = SchemaGuide.AddCandidates(
            ConsoleTestSchema.Schema,
            ["top", "person", "organizationalPerson", "inetOrgPerson"],
            presentAttributes: ["objectClass", "cn", "sn", "uidNumber"]);

        var names = candidates.Select(static c => c.Name).ToList();
        Assert.DoesNotContain("objectClass", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("createTimestamp", names, StringComparer.OrdinalIgnoreCase); // NO-USER-MODIFICATION
        Assert.DoesNotContain("uidNumber", names, StringComparer.OrdinalIgnoreCase);       // present + SINGLE-VALUE
        Assert.Contains("cn", names, StringComparer.OrdinalIgnoreCase);                    // present but multi-valued
        Assert.Contains("uid", names, StringComparer.OrdinalIgnoreCase);
        // Required-first ordering.
        Assert.True(candidates.First().Required);
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
    public void EntryTitle_Unescapes_The_Rdn_Value_Instead_Of_Splitting_On_Escaped_Commas()
    {
        // aspireldap#122: the RFC 4514 escaped comma is part of the value, not a DN
        // separator — the title must read "Doe, Jane", never "Doe\".
        var entry = new LdapEntry(@"uid=Doe\, Jane,ou=people,dc=aspire,dc=dev", []);
        Assert.Equal("Doe, Jane", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Joins_The_Values_Of_A_Multi_Valued_Rdn()
    {
        // aspireldap#122: a multi-valued RDN yields every value, plus-joined like
        // RelativeDistinguishedName.ToString(), with the attribute types stripped.
        var entry = new LdapEntry("uid=achen+cn=Alice Chen,ou=people,dc=aspire,dc=dev", []);
        Assert.Equal("achen+Alice Chen", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Honors_DisplayName_Like_The_Search_Panel_Does()
    {
        // aspireldap#122: displayName belongs in the chain — SearchPanel's name column
        // already honors it, and the two surfaces must agree on an entry's name.
        var entry = new LdapEntry("uid=alice.chen,ou=people,dc=aspire,dc=dev",
        [
            new LdapAttributeValues("displayName", false, ["Alice C."], LdapValueClassification.Schema),
        ]);
        Assert.Equal("Alice C.", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Prefers_Cn_Over_DisplayName_Matching_The_Search_Panel_Order()
    {
        var entry = new LdapEntry("uid=alice.chen,ou=people,dc=aspire,dc=dev",
        [
            new LdapAttributeValues("displayName", false, ["Alice C."], LdapValueClassification.Schema),
            new LdapAttributeValues("cn", false, ["Alice Chen"], LdapValueClassification.Schema),
        ]);
        Assert.Equal("Alice Chen", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Returns_The_Raw_Dn_When_It_Does_Not_Parse()
    {
        var entry = new LdapEntry("not a dn", []);
        Assert.Equal("not a dn", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
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

using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EntryView = Aspire.LdapAdmin.Web.Components.Directory.EntryView;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Component tests for the Industry-console markup — every element is owned by the app
/// (no component-library shadow DOM between the test and the behavior).
/// These assert consumer-visible behavior: what renders, what a click changes, what a save
/// failure keeps on screen.
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
            Values = ["alice@aspire.dev"],
            Entry = Entry(Text("objectClass", "top", "person")),
            SaveAsync = (_, _) => Task.FromResult<string?>("Access denied — the server's ACL refused this bind."),
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
            Values = ["alice@aspire.dev"],
            Entry = Entry(Text("objectClass", "top", "person")),
            SaveAsync = (_, _) => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find("button.btn-primary").Click();

        Assert.True(closed);
    }

    [Fact]
    public void PasswordDialog_Requires_Nonempty_Exact_Match_Without_Changing_The_Value()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        string? submitted = null;
        var saves = 0;
        var model = new PasswordDialogModel
        {
            Dn = "uid=alice,ou=people,dc=aspire,dc=dev",
            SaveAsync = (password, _) =>
            {
                saves++;
                submitted = password;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<PasswordDialog>(parameters => parameters.Add(p => p.Model, model));
        var fields = cut.FindAll("input[type=password]").ToList();

        Assert.Equal(2, fields.Count);
        Assert.All(fields, field => Assert.Equal("new-password", field.GetAttribute("autocomplete")));

        cut.Find("button.btn-primary").Click();
        Assert.Equal("Enter a password.", cut.Find(".bar.err").TextContent);
        Assert.Equal(0, saves);

        cut.Find("#new-password").Change(" secret ");
        cut.Find("#confirm-password").Change("secret");
        cut.Find("button.btn-primary").Click();
        Assert.Equal("Passwords do not match.", cut.Find(".bar.err").TextContent);
        Assert.Equal(0, saves);

        cut.Find("#confirm-password").Change(" secret ");
        cut.Find("button.btn-primary").Click();
        Assert.Equal(1, saves);
        Assert.Equal(" secret ", submitted);
    }

    [Fact]
    public async Task PasswordDialog_Uses_The_Save_Token_And_Shows_Service_Words_Inline()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var pending = new TaskCompletionSource<string?>();
        CancellationToken saveToken = default;
        const string refusal = "The request is not valid — the target is the console's bind identity; its password cannot be changed from here.";
        var model = new PasswordDialogModel
        {
            Dn = "cn=admin,dc=aspire,dc=dev",
            SaveAsync = (_, token) =>
            {
                saveToken = token;
                return pending.Task;
            },
        };
        var cut = RenderComponent<PasswordDialog>(parameters => parameters.Add(p => p.Model, model));
        var fields = cut.FindAll("input[type=password]").ToList();
        cut.Find("#new-password").Change("not-shown-in-status");
        cut.Find("#confirm-password").Change("not-shown-in-status");
        cut.Find("button.btn-primary").Click();

        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());
        Assert.True(saveToken.IsCancellationRequested);
        pending.SetResult(refusal);

        cut.WaitForAssertion(() => Assert.Equal(refusal, cut.Find(".bar.err").TextContent));
        Assert.DoesNotContain("not-shown-in-status", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeDialog_With_Schema_Adapts_The_Value_Input_And_Shows_Guidance()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "uidNumber",
            Entry = Entry(Text("objectClass", "top", "person", "organizationalPerson", "inetOrgPerson")),
            SaveAsync = (_, _) => Task.FromResult<string?>(null),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        // SINGLE-VALUE still supports multiline content, but not adding a second value.
        Assert.Contains("single-valued", cut.Find(".hint").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("textarea"));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Add value").HasAttribute("disabled"));
    }

    [Fact]
    public void NewEntryWizard_Chains_Classes_Derives_Musts_And_Previews_Ldif()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        LdapNewEntry? saved = null;
        var model = new NewEntryModel
        {
            ParentDn = "ou=people,dc=example,dc=org",
            SaveAsync = (entry, _) => { saved = entry; return Task.FromResult<string?>(null); },
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
        // Back-and-swap flow: inetOrgPerson defaults the RDN attribute
        // to uid; swapping to person alone removes uid from the choices, so keeping it
        // would compose an RDN the select never offered — a schema violation.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new NewEntryModel
        {
            ParentDn = "ou=people,dc=example,dc=org",
            SaveAsync = (_, _) => Task.FromResult<string?>(null),
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
    public async Task ConsoleDialog_Close_Request_Relay_Cancels_When_Idle()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cancelled = 0;
        var cut = RenderComponent<ConsoleDialog>(parameters => parameters
            .Add(p => p.Title, "Test dialog")
            .Add(p => p.OnCancel, () => { cancelled++; }));

        // Escape and backdrop clicks both arrive through console.js as this relay (native
        // close requests; a Blazor keydown would round-trip every keystroke). The real
        // keyboard/pointer paths are browser-probe territory — JS never runs under bUnit.
        await cut.InvokeAsync(() => cut.Instance.CancelFromJs());

        Assert.Equal(1, cancelled);
    }

    [Fact]
    public async Task ConsoleDialog_Busy_Relays_Cancellation_And_Marks_The_Element()
    {
        // Busy close requests reach the owner, which requests cancellation while keeping
        // the component mounted. Native closedby must not suppress those requests.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cancelled = 0;
        var cut = RenderComponent<ConsoleDialog>(parameters => parameters
            .Add(p => p.Title, "Busy dialog")
            .Add(p => p.Busy, true)
            .Add(p => p.OnCancel, () => { cancelled++; }));

        await cut.InvokeAsync(() => cut.Instance.CancelFromJs());

        Assert.Equal(1, cancelled);
        var panel = cut.Find("dialog.wide");
        Assert.Equal("true", panel.GetAttribute("data-busy"));
        Assert.False(panel.HasAttribute("closedby"));
    }

    [Fact]
    public async Task AttributeDialog_InFlight_Save_Cancels_Idempotently_And_Waits_For_Acknowledgement()
    {
        // Cancellation is requested once, but the dialog remains mounted and locked until
        // the delegate acknowledges it with a result.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closed = false;
        var pending = new TaskCompletionSource<string?>();
        CancellationToken saveToken = default;
        var cancellationCallbacks = 0;
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "mail",
            Values = ["alice@aspire.dev"],
            Entry = Entry(Text("objectClass", "top", "person")),
            SaveAsync = (_, token) =>
            {
                saveToken = token;
                token.Register(() => cancellationCallbacks++);
                return pending.Task;
            },
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find("button.btn-primary").Click();

        // In flight: fields and Save stay locked, while Cancel remains available.
        Assert.True(cut.Find("input.input").HasAttribute("disabled"));
        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.False(cut.Find("button.btn-secondary").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());
        Assert.False(closed);
        Assert.True(saveToken.IsCancellationRequested);
        Assert.Equal(1, cancellationCallbacks);
        Assert.Equal("Cancelling", cut.Find("button.btn-secondary").TextContent);

        pending.SetResult("Access denied — the server's ACL refused this bind.");
        cut.WaitForAssertion(() =>
            Assert.Contains("Access denied", cut.Find(".bar.err").TextContent, StringComparison.Ordinal));
        Assert.False(closed);
    }

    [Fact]
    public async Task AttributeDialog_Acknowledged_Success_After_Cancel_Closes_Exactly_Once()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closedCount = 0;
        var pending = new TaskCompletionSource<string?>();
        var model = new AttributeDialogModel
        {
            IsNew = true,
            Name = "mail",
            Values = ["alice@aspire.dev"],
            Entry = Entry(Text("objectClass", "top", "person")),
            SaveAsync = (_, _) => pending.Task,
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnClose, () => { closedCount++; }));

        cut.Find("button.btn-primary").Click();
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleDialog>().Instance.CancelFromJs());
        Assert.Equal("Cancelling", cut.Find("button.btn-secondary").TextContent);
        pending.SetResult(null);

        cut.WaitForAssertion(() => Assert.Equal(1, closedCount));
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public void DeleteDialog_InFlight_Delete_Leaves_Cancel_Enabled_And_Locks_Other_Controls()
    {
        // Flipping the subtree checkbox mid-walk would lie about
        // what the running operation is doing; it locks with the rest of the dialog.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var pending = new TaskCompletionSource<string?>();
        var model = new DeleteDialogModel
        {
            Dn = "uid=alice,ou=people,dc=aspire,dc=dev",
            Subtree = true,
            SaveAsync = (_, _) => pending.Task,
        };
        var cut = RenderComponent<DeleteDialog>(parameters => parameters
            .Add(p => p.Model, model));

        cut.Find("button.btn-primary").Click();

        Assert.False(cut.Find("button.btn-secondary").HasAttribute("disabled"));
        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.True(cut.Find("input[type=checkbox]").HasAttribute("disabled"));

        pending.SetResult(null);
    }

    [Fact]
    public void ConsoleDialog_Wears_The_Blueprint_Frame_On_A_Native_Dialog()
    {
        // Native modal dialogs carry implicit role=dialog/aria-modal — no ARIA attributes
        // to assert; the element itself is the claim. The open attribute must NOT render
        // from Razor (showModal() owns it; a rendered `open` would drop the top layer on
        // the next diff).
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<ConsoleDialog>(parameters => parameters
            .Add(p => p.Title, "Framed"));

        var panel = cut.Find("dialog.wide.blueprint");
        Assert.Equal("Framed", panel.GetAttribute("aria-label"));
        Assert.False(panel.HasAttribute("open"));
        // The four corner blocks are the design system's registration marks on the dialog frame.
        Assert.Equal(4, cut.FindAll(".blueprint > .corner").Count);
    }

    // ---- Rename dialog: guided RDN, validation before submit, hazard prediction ----

    /// <summary>A person entry named by cn, as the shell's OpenRenameDialog would hand over:
    /// RDN prefilled, parent computed, entry snapshotted.</summary>
    private static RenameDialogModel RenameModel(
        Func<RenameDialogModel, CancellationToken, Task<string?>>? save = null,
        params LdapAttributeValues[] attributes)
    {
        const string dn = "cn=Alice Chen,ou=people,dc=aspire,dc=dev";
        var entryAttributes = attributes.Length > 0
            ? attributes
            : new[]
            {
                Text("objectClass", "top", "person", "organizationalPerson", "inetOrgPerson"),
                Text("cn", "Alice Chen"),
                Text("sn", "Chen"),
            };
        return new RenameDialogModel
        {
            Dn = dn,
            CurrentParentDn = "ou=people,dc=aspire,dc=dev",
            Entry = new LdapEntry(dn, entryAttributes),
            RdnAttribute = "cn",
            RdnValue = "Alice Chen",
            SaveAsync = save ?? ((_, _) => Task.FromResult<string?>(null)),
        };
    }

    [Fact]
    public void RenameDialog_Prefills_The_Current_Rdn_Attribute_And_Value()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel())
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        Assert.Equal("cn", cut.Find("select.input").GetAttribute("value"));
        Assert.Equal("Alice Chen", cut.Find(".frow input.input").GetAttribute("value"));
    }

    [Fact]
    public void RenameDialog_Offers_Schema_Attributes_And_Free_Text_Without_A_Schema()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var withSchema = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel())
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        var options = withSchema.FindAll("select.input option").Select(o => o.TextContent).ToList();
        Assert.Contains("uid", options);   // MAY via inetOrgPerson
        Assert.Contains("cn", options);    // MUST via person — and the current attribute
        Assert.Contains("sn", options);
        Assert.DoesNotContain("objectClass", options);

        var withoutSchema = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel()));
        Assert.Empty(withoutSchema.FindAll("select"));
        // Free text degradation: attribute, value and parent are all plain inputs.
        Assert.Equal(3, withoutSchema.FindAll("input.input.mono").Count);
    }

    [Fact]
    public void RenameDialog_Previews_The_Resulting_Dn_For_Rename_In_Place_And_For_A_Move()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel())
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        string Preview() => cut.FindAll(".dnline")
            .Single(d => d.TextContent.Contains("becomes", StringComparison.Ordinal)).TextContent;

        // In place: the current parent completes the DN; the value is escaped by Dn.Rdn.
        cut.Find(".frow input.input").Input(@"Chen, Alice");
        Assert.Contains(@"becomes cn=Chen\, Alice,ou=people,dc=aspire,dc=dev", Preview(), StringComparison.Ordinal);

        // Move: a filled parent replaces the current one in the preview.
        var parentInput = cut.FindAll("input.input.mono").ElementAt(1);
        parentInput.Input("ou=staff,dc=aspire,dc=dev");
        Assert.Contains(@"becomes cn=Chen\, Alice,ou=staff,dc=aspire,dc=dev", Preview(), StringComparison.Ordinal);
    }

    [Fact]
    public void RenameDialog_Rejects_A_Malformed_Parent_Dn_Before_Submit()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var saves = 0;
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel((_, _) => { saves++; return Task.FromResult<string?>(null); }))
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        cut.FindAll("input.input.mono").ElementAt(1).Input("not a dn");
        cut.Find("button.btn-primary").Click();

        Assert.Contains("not a valid DN", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, saves); // the rejection is client-side: no delegate, no round trip
    }

    [Fact]
    public void RenameDialog_Rejects_An_Invalid_Rdn_Attribute_Or_Empty_Value_Before_Submit()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var saves = 0;
        // No schema: the attribute is free text, so an invalid type can be typed at all.
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel((_, _) => { saves++; return Task.FromResult<string?>(null); })));

        cut.FindAll("input.input.mono").First().Change("1bad"); // RFC 4512 descr must start with a letter
        cut.Find("button.btn-primary").Click();
        Assert.Contains("invalid attribute type", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);

        cut.FindAll("input.input.mono").First().Change("cn");
        cut.FindAll("input.input.mono").ElementAt(1).Input("   ");
        cut.Find("button.btn-primary").Click();
        Assert.Contains("Enter a value", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);

        Assert.Equal(0, saves);
    }

    [Fact]
    public void RenameDialog_Unchecks_Delete_Old_Rdn_With_A_Warning_When_It_Would_Violate_Schema()
    {
        // cn is MUST on person and the entry's only cn value is its name: switching the RDN
        // to sn with delete-old-RDN on is a guaranteed objectClassViolation.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, RenameModel())
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        Assert.True(cut.Instance.Model.DeleteOldRdn); // sane default: delete is the common case
        Assert.Empty(cut.FindAll(".bar:not(.err)"));

        cut.Find("select.input").Change("sn");

        Assert.False(cut.Instance.Model.DeleteOldRdn);
        var warning = cut.FindAll(".bar").Single(b => !b.ClassList.Contains("err"));
        Assert.Contains("cn", warning.TextContent, StringComparison.Ordinal);
        Assert.Contains("refused", warning.TextContent, StringComparison.Ordinal);

        // A deliberate re-check is respected: the uncheck fires once per transition.
        cut.Find("input[type=checkbox]").Change(true);
        cut.Find("select.input").Change("uid"); // still hazardous for cn — no new transition
        Assert.True(cut.Instance.Model.DeleteOldRdn);
    }

    [Fact]
    public void RenameDialog_Hands_The_Trimmed_Inputs_To_The_Save_Delegate_And_Closes()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenameDialogModel? saved = null;
        var closed = false;
        var model = RenameModel((m, _) => { saved = m; return Task.FromResult<string?>(null); });
        var cut = RenderComponent<RenameDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, ConsoleTestSchema.Schema)
            .Add(p => p.OnClose, () => { closed = true; }));

        cut.Find(".frow input.input").Input("  Alice Chen-Reyes  ");
        cut.FindAll("input.input.mono").ElementAt(1).Input(" ou=staff,dc=aspire,dc=dev ");
        cut.Find("button.btn-primary").Click();

        Assert.NotNull(saved);
        Assert.Equal("cn", saved!.RdnAttribute);
        Assert.Equal("Alice Chen-Reyes", saved.RdnValue);
        Assert.Equal("ou=staff,dc=aspire,dc=dev", saved.NewParentDn);
        Assert.True(saved.DeleteOldRdn);
        Assert.True(closed);
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

/// <summary>Schema-guide composition checks — the SUP walking itself is the library's.</summary>
public sealed class SchemaGuideTests
{
    [Fact]
    public void Password_Eligibility_Comes_From_Effective_Schema_Attributes_Not_Class_Names()
    {
        var customSchema = LdifDotNet.Schema.LdapSchema.ParseSubschema(
        [
            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.35 NAME 'userPassword' SYNTAX 1.3.6.1.4.1.1466.115.121.1.40 )",
        ],
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 1.3.6.1.4.1.99999.1 NAME 'deviceCredential' SUP top AUXILIARY MAY userPassword )",
            "( 1.3.6.1.4.1.99999.2 NAME 'namedService' SUP top AUXILIARY )",
        ]);

        Assert.True(SchemaGuide.PermitsAttribute(customSchema, ["deviceCredential"], "userPassword"));
        Assert.False(SchemaGuide.PermitsAttribute(customSchema, ["namedService"], "userPassword"));
        Assert.False(SchemaGuide.PermitsAttribute(customSchema, ["unknownClass"], "userPassword"));
        Assert.False(SchemaGuide.PermitsAttribute(null, ["deviceCredential"], "userPassword"));
    }

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

    [Fact]
    public void SharedEditableCandidates_Intersect_Heterogeneous_Entry_Schemas()
    {
        var schema = LdifDotNet.Schema.LdapSchema.ParseSubschema(
        [
            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.3 NAME 'cn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.13 NAME 'description' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 0.9.2342.19200300.100.1.3 NAME 'mail' SYNTAX 1.3.6.1.4.1.1466.115.121.1.26 )",
        ],
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 1.3.6.1.4.1.99999.10 NAME 'mailEntry' SUP top STRUCTURAL MUST cn MAY ( description $ mail ) )",
            "( 1.3.6.1.4.1.99999.11 NAME 'plainEntry' SUP top STRUCTURAL MUST cn MAY description )",
        ]);

        var candidates = SchemaGuide.SharedEditableCandidates(
            schema, new[] { new[] { "mailEntry" }, new[] { "plainEntry" } });

        Assert.Contains(candidates, candidate => candidate.Name.Equals("cn", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => candidate.Name.Equals("description", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, candidate => candidate.Name.Equals("mail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedEditableCandidates_Exclude_Binary_Syntaxes()
    {
        var schema = LdifDotNet.Schema.LdapSchema.ParseSubschema(
        [
            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.3 NAME 'cn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 0.9.2342.19200300.100.1.60 NAME 'jpegPhoto' SYNTAX 1.3.6.1.4.1.1466.115.121.1.28 )",
        ],
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 1.3.6.1.4.1.99999.12 NAME 'photoEntry' SUP top STRUCTURAL MUST cn MAY jpegPhoto )",
        ],
        ["( 1.3.6.1.4.1.1466.115.121.1.28 DESC 'JPEG' X-NOT-HUMAN-READABLE 'TRUE' )"]);

        var candidates = SchemaGuide.SharedEditableCandidates(schema, [new[] { "photoEntry" }]);

        Assert.DoesNotContain(candidates, candidate => candidate.Name.Equals("jpegPhoto", StringComparison.OrdinalIgnoreCase));
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
        // The header reads "Alice Chen", not "alice.chen".
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
        // The RFC 4514 escaped comma is part of the value, not a DN
        // separator — the title must read "Doe, Jane", never "Doe\".
        var entry = new LdapEntry(@"uid=Doe\, Jane,ou=people,dc=aspire,dc=dev", []);
        Assert.Equal("Doe, Jane", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Joins_The_Values_Of_A_Multi_Valued_Rdn()
    {
        // A multi-valued RDN yields every value, plus-joined like
        // RelativeDistinguishedName.ToString(), with the attribute types stripped.
        var entry = new LdapEntry("uid=achen+cn=Alice Chen,ou=people,dc=aspire,dc=dev", []);
        Assert.Equal("achen+Alice Chen", Aspire.LdapAdmin.Web.Components.Pages.Browse.EntryTitle(entry));
    }

    [Fact]
    public void EntryTitle_Honors_DisplayName_Like_The_Search_Panel_Does()
    {
        // displayName belongs in the chain — SearchPanel's name column
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

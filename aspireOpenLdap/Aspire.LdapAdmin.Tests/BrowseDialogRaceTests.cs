using System.DirectoryServices.Protocols;
using System.Reflection;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Aspire.LdapAdmin.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class BrowseDialogRaceTests : TestContext
{
    private const string A = "cn=a,dc=example,dc=org";
    private const string B = "cn=b,dc=example,dc=org";
    private static readonly GroupMembershipAttribute Membership =
        new("member", GroupMemberValueKind.DistinguishedName, Required: true);

    private IRenderedComponent<BrowseHarness> RenderBrowse()
    {
        Services.AddSingleton(new LdapDirectoryService(null!, null!));
        Services.AddSingleton(new LdapSchemaService(null!, null!));
        Services.AddSingleton(new LdapAdminSettings());
        Services.AddSingleton(new ConsoleConnectionInfo("unused", "unused"));
        Services.AddSingleton<ConsoleToastService>();
        Services.AddSingleton<ConsoleClipboard>();
        return RenderComponent<BrowseHarness>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dialog_save_uses_its_opening_entry_after_selection_changes(bool member)
    {
        var cut = RenderBrowse();
        var page = cut.Instance;
        await cut.InvokeAsync(() => page.SelectAsync(B));
        var save = page.OpenDialog(member);
        await cut.InvokeAsync(() => page.SelectAsync(A));
        using var cancellation = new CancellationTokenSource();

        Assert.Null(await cut.InvokeAsync(() => save(cancellation.Token)));

        var write = Assert.Single(page.Writes);
        Assert.Equal(B, write.Dn);
        Assert.Equal(cancellation.Token, write.Token);
        var change = Assert.Single(write.Changes);
        Assert.Equal(member ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Replace, change.Operation);
        Assert.Equal(member ? "member" : "description", change.Name);
        Assert.Equal([member ? "uid=new,dc=example,dc=org" : "B value"], change.Values);
        Assert.Equal(A, page.SelectedDn);
        Assert.Equal([B, A], page.Reads);
    }

    [Theory]
    [InlineData("entry", false)]
    [InlineData("entry", true)]
    [InlineData("attribute", false)]
    [InlineData("attribute", true)]
    [InlineData("add", false)]
    [InlineData("add", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    public async Task Pending_modify_does_not_refresh_over_a_newer_selection(string path, bool returnToA)
    {
        var cut = RenderBrowse();
        var page = cut.Instance;
        await cut.InvokeAsync(() => page.SelectAsync(A));
        var pending = new TaskCompletionSource<LdapOperationResult>();
        page.WriteResult = pending.Task;
        Task<string?> save = null!;
        await cut.InvokeAsync(() => { save = page.SaveAsync(path); });
        Assert.Single(page.Writes);
        Assert.False(save.IsCompleted);
        await cut.InvokeAsync(() => page.SelectAsync(B));
        if (returnToA)
        {
            await cut.InvokeAsync(() => page.SelectAsync(A));
        }
        page.Call("CloseDialogs");
        var nextSave = page.OpenDialog(member: true);

        await cut.InvokeAsync(() => pending.SetResult(LdapOperationResult.Ok()));
        Assert.Null(await save);
        Assert.Equal(returnToA ? A : B, page.SelectedDn);
        Assert.Equal(returnToA ? [A, B, A] : [A, B], page.Reads);

        page.WriteResult = Task.FromResult(LdapOperationResult.Ok());
        Assert.Null(await cut.InvokeAsync(() => nextSave(CancellationToken.None)));
        Assert.Equal(returnToA ? A : B, page.Writes[1].Dn);
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("attribute")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Current_modify_refreshes_on_success_but_not_failure(string path)
    {
        var cut = RenderBrowse();
        var page = cut.Instance;
        await cut.InvokeAsync(() => page.SelectAsync(A));
        page.WriteResult = Task.FromResult(new LdapOperationResult(LdapOperationStatus.AccessDenied));
        Assert.Contains("Access denied", await cut.InvokeAsync(() => page.SaveAsync(path)), StringComparison.Ordinal);
        Assert.Equal([A], page.Reads);
        page.Call("CloseDialogs");
        page.WriteResult = Task.FromResult(LdapOperationResult.Ok());

        Assert.Null(await cut.InvokeAsync(() => page.SaveAsync(path)));
        Assert.Equal([A, A], page.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Attribute_membership_guard_uses_dialog_entry_not_current_selection(bool targetIsGroup)
    {
        var cut = RenderBrowse();
        var page = cut.Instance;
        page.Set("_dialogSchema", GroupMembershipSchemaTests.TestSchema);
        page.Entries[B] = Entry(B, targetIsGroup ? "teamByDn" : "ordinaryEntry");
        page.Entries[A] = Entry(A, targetIsGroup ? "ordinaryEntry" : "teamByDn");
        await cut.InvokeAsync(() => page.SelectAsync(B));
        var save = page.OpenDialog(member: false, attribute: "member");
        await cut.InvokeAsync(() => page.SelectAsync(A));

        var error = await cut.InvokeAsync(() => save(CancellationToken.None));

        if (targetIsGroup)
        {
            Assert.Contains("managed in the Members tab", error, StringComparison.Ordinal);
            Assert.Empty(page.Writes);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(B, Assert.Single(page.Writes).Dn);
        }
    }

    private static LdapEntry Entry(string dn, string objectClass) => new(dn,
        [new LdapAttributeValues("objectClass", false, [objectClass], LdapValueClassification.Schema)]);

    // Keep the real save/open/selection handlers and Blazor dispatcher. Only LDAP I/O,
    // startup queries and unrelated markup are replaced; no server or runtime patching.
    public sealed class BrowseHarness : Browse
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        public IDictionary<string, LdapEntry> Entries { get; } = new Dictionary<string, LdapEntry>(StringComparer.Ordinal)
        {
            [A] = Entry(A, "ordinaryEntry"),
            [B] = Entry(B, "ordinaryEntry"),
        };
        public IList<string> Reads { get; } = new List<string>();
        public IList<(string Dn, IReadOnlyList<LdapAttributeChange> Changes, CancellationToken Token)> Writes { get; } =
            new List<(string Dn, IReadOnlyList<LdapAttributeChange> Changes, CancellationToken Token)>();
        public Task<LdapOperationResult> WriteResult { get; set; } = Task.FromResult(LdapOperationResult.Ok());
        public string? SelectedDn => (string?)typeof(Browse).GetField("_selectedDn", PrivateInstance)!.GetValue(this);

        public void Set(string name, object value) => typeof(Browse).GetField(name, PrivateInstance)!.SetValue(this, value);
        public object? Call(string name, params object[] arguments) =>
            typeof(Browse).GetMethod(name, PrivateInstance)!.Invoke(this, arguments);
        public Task SelectAsync(string dn) => (Task)Call("SelectAsync", dn, false, CancellationToken.None)!;

        public Func<CancellationToken, Task<string?>> OpenDialog(bool member, string attribute = "description")
        {
            if (member)
            {
                Call("OpenAddMemberDialog", Membership);
                var model = (AddGroupMemberDialogModel)typeof(Browse).GetField("_addMemberDialog", PrivateInstance)!.GetValue(this)!;
                return token => model.SaveAsync("uid=new,dc=example,dc=org", token);
            }

            Call("OpenAddAttributeDialog");
            var dialog = (AttributeDialogModel)typeof(Browse).GetField("_attributeDialog", PrivateInstance)!.GetValue(this)!;
            dialog.IsNew = false;
            dialog.Name = attribute;
            dialog.Values = ["B value"];
            return token => dialog.SaveAsync(dialog, token);
        }

        public Task<string?> SaveAsync(string path) => path switch
        {
            "entry" => (Task<string?>)Call("ApplyEntryChangesAsync", (object)new LdapAttributeChange[]
                { new(DirectoryAttributeOperation.Replace, "description", ["value"]) })!,
            "attribute" => OpenDialog(member: false)(CancellationToken.None),
            "add" => OpenDialog(member: true)(CancellationToken.None),
            "remove" => (Task<string?>)Call("RemoveMemberAsync", SelectedDn!, Membership,
                "uid=old,dc=example,dc=org", CancellationToken.None)!,
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

        protected override Task<LdapOperationResult> ModifyEntryAsync(
            string dn, IReadOnlyList<LdapAttributeChange> changes, CancellationToken cancellationToken)
        {
            Writes.Add((dn, changes, cancellationToken));
            return WriteResult;
        }

        protected override Task<LdapEntry?> GetEntryAsync(string dn, CancellationToken cancellationToken)
        {
            Reads.Add(dn);
            return Task.FromResult<LdapEntry?>(Entries[dn]);
        }

        protected override Task OnInitializedAsync() => Task.CompletedTask;
        protected override Task OnParametersSetAsync() => Task.CompletedTask;
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
    }
}

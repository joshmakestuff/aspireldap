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
    private const string Parent = "dc=example,dc=org";
    private const string Created = "cn=new,dc=example,dc=org";
    private const string Moved = "cn=renamed,ou=destination,dc=example,dc=org";
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
    [InlineData("add", false)]
    [InlineData("remove", false)]
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

    private static LdapEntry Entry(string dn, string objectClass) => new(dn,
        [new LdapAttributeValues("objectClass", false, [objectClass], LdapValueClassification.Schema)]);

    [Theory]
    [InlineData("create", false, false)]
    [InlineData("rename", false, false)]
    [InlineData("delete", false, false)]
    [InlineData("cancel-subtree", false, false)]
    // Exercise returning to the same DN and navigation during refresh once each.
    [InlineData("create", true, false)]
    [InlineData("rename", false, true)]
    public async Task Tree_write_refreshes_affected_nodes_without_overwriting_newer_selection(
        string path, bool returnToA, bool delayRefresh)
    {
        var cut = RenderBrowse();
        var page = cut.Instance;
        await cut.InvokeAsync(() => page.SelectAsync(A));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.TreeWriteGate = delayRefresh ? Task.CompletedTask : pending.Task;
        page.RefreshGate = delayRefresh ? pending.Task : Task.CompletedTask;
        Task<string?> save = null!;
        using var cancellation = new CancellationTokenSource();
        await cut.InvokeAsync(() => { save = page.SaveTreeAsync(path, cancellation.Token); });
        Assert.False(save.IsCompleted);
        Assert.Equal(path == "create" ? Created : A, Assert.Single(page.TreeWrites).Dn);
        Assert.Equal(cancellation.Token, page.TreeWrites[0].Token);
        await cut.InvokeAsync(() => page.SelectAsync(B));
        if (returnToA)
        {
            await cut.InvokeAsync(() => page.SelectAsync(A));
        }
        await cut.InvokeAsync(() => pending.SetResult());
        var error = await save;
        Assert.Equal(path == "cancel-subtree", error is not null);
        Assert.Equal(returnToA ? A : B, page.SelectedDn);
        Assert.Equal(returnToA ? [A, B, A] : [A, B], page.Reads);
        Assert.Equal(ExpectedRefreshes(path), page.Refreshes);
    }

    [Theory]
    [InlineData("create", Created)]
    [InlineData("rename", Moved)]
    [InlineData("delete", Parent)]
    [InlineData("cancel-subtree", A)]
    public async Task Tree_write_without_newer_navigation_selects_its_result(string path, string selected)
    {
        var cut = RenderBrowse();
        await cut.InvokeAsync(() => cut.Instance.SelectAsync(A));
        await cut.InvokeAsync(() => cut.Instance.SaveTreeAsync(path, CancellationToken.None));
        Assert.Equal(selected, cut.Instance.SelectedDn);
        Assert.Equal([A, selected], cut.Instance.Reads);
        Assert.Equal(ExpectedRefreshes(path), cut.Instance.Refreshes);
    }

    private static string[] ExpectedRefreshes(string path) => path switch
    {
        "rename" => [Parent, "ou=destination," + Parent],
        "cancel-subtree" => [Parent, A],
        _ => [Parent],
    };

    // Keep the real save/open/selection handlers and Blazor dispatcher. Only LDAP I/O,
    // startup queries and unrelated markup are replaced; no server or runtime patching.
    public sealed class BrowseHarness : Browse
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        public IDictionary<string, LdapEntry> Entries { get; } = new Dictionary<string, LdapEntry>(StringComparer.Ordinal)
        {
            [A] = Entry(A, "ordinaryEntry"),
            [B] = Entry(B, "ordinaryEntry"),
            [Parent] = Entry(Parent, "ordinaryEntry"),
            [Created] = Entry(Created, "ordinaryEntry"),
            [Moved] = Entry(Moved, "ordinaryEntry"),
        };
        public IList<string> Reads { get; } = new List<string>();
        public IList<(string Dn, IReadOnlyList<LdapAttributeChange> Changes, CancellationToken Token)> Writes { get; } =
            new List<(string Dn, IReadOnlyList<LdapAttributeChange> Changes, CancellationToken Token)>();
        public Task<LdapOperationResult> WriteResult { get; set; } = Task.FromResult(LdapOperationResult.Ok());
        public Task TreeWriteGate { get; set; } = Task.CompletedTask;
        public Task RefreshGate { get; set; } = Task.CompletedTask;
        public IList<string> Refreshes { get; } = new List<string>();
        public IList<(string Dn, CancellationToken Token)> TreeWrites { get; } = new List<(string, CancellationToken)>();
        private bool _cancelSubtree;
        public string? SelectedDn => (string?)typeof(Browse).GetField("_selectedDn", PrivateInstance)!.GetValue(this);

        public object? Call(string name, params object[] arguments) =>
            typeof(Browse).GetMethod(name, PrivateInstance)!.Invoke(this, arguments);
        public Task SelectAsync(string dn) => (Task)Call("SelectAsync", dn, false, CancellationToken.None)!;

        public Task<string?> SaveTreeAsync(string path, CancellationToken token)
        {
            _cancelSubtree = path == "cancel-subtree";
            return path switch
            {
                "create" => (Task<string?>)Call("SaveNewEntryAsync", new LdapNewEntry(Created, []), token)!,
                "rename" => (Task<string?>)Call("SaveRenameAsync", new RenameDialogModel
                {
                    Dn = A,
                    CurrentParentDn = Parent,
                    Entry = Entries[A],
                    RdnAttribute = "cn",
                    RdnValue = "renamed",
                    NewParentDn = "ou=destination," + Parent,
                    SaveAsync = (_, _) => throw new NotSupportedException(),
                }, token)!,
                _ => (Task<string?>)Call("ConfirmDeleteAsync", new DeleteDialogModel
                {
                    Dn = A,
                    Subtree = _cancelSubtree,
                    SaveAsync = (_, _) => throw new NotSupportedException(),
                }, token)!,
            };
        }

        protected override async Task<LdapOperationResult> AddEntryAsync(LdapNewEntry entry, CancellationToken token)
        {
            TreeWrites.Add((entry.Dn, token));
            await TreeWriteGate;
            return LdapOperationResult.Ok();
        }

        protected override async Task<LdapRenameResult> RenameEntryAsync(
            string dn, string newRdn, string? newParent, bool deleteOldRdn, CancellationToken token)
        {
            TreeWrites.Add((dn, token));
            Assert.Equal("cn=renamed", newRdn);
            Assert.Equal("ou=destination," + Parent, newParent);
            await TreeWriteGate;
            return new LdapRenameResult(LdapOperationResult.Ok(), Moved);
        }

        protected override async Task<LdapOperationResult> DeleteEntryAsync(string dn, bool subtree, CancellationToken token)
        {
            TreeWrites.Add((dn, token));
            Assert.Equal(_cancelSubtree, subtree);
            await TreeWriteGate;
            return _cancelSubtree ? LdapOperationResult.Cancelled("stopped") : LdapOperationResult.Ok();
        }

        protected override async Task ReloadChildrenAsync(string parentDn, CancellationToken token = default)
        {
            Refreshes.Add(parentDn);
            await RefreshGate;
        }

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

using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class OperationalAttributesPanelTests : TestContext
{
    [Fact]
    public async Task A_late_read_cannot_replace_a_newer_entry_or_expose_edit_actions()
    {
        Services.AddSingleton(new LdapDirectoryService(null!, null!));
        Services.AddSingleton(new LdapAdminSettings());
        var first = new LdapEntry("cn=a,dc=example", []);
        var second = new LdapEntry("cn=b,dc=example", []);
        var cut = RenderComponent<PanelHarness>(parameters => parameters.Add(p => p.Entry, first));
        cut.SetParametersAndRender(parameters => parameters.Add(p => p.Entry, second));
        await cut.InvokeAsync(() => cut.Instance.Reads[second.Dn].SetResult(Operational(second.Dn, "newer-value")));
        cut.WaitForAssertion(() => Assert.Contains("newer-value", cut.Markup, StringComparison.Ordinal));
        var rendered = cut.RenderCount;
        await cut.InvokeAsync(() => cut.Instance.Reads[first.Dn].SetResult(Operational(first.Dn, "stale-value")));
        cut.WaitForState(() => cut.RenderCount > rendered);
        Assert.Contains("newer-value", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-value", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("input,textarea"));
    }

    private static LdapEntry Operational(string dn, string value) => new(dn,
        [new LdapAttributeValues("entryUUID", false, [value], LdapValueClassification.Schema)]);

    public sealed class PanelHarness : OperationalAttributesPanel
    {
        public IDictionary<string, TaskCompletionSource<LdapEntry?>> Reads { get; } =
            new Dictionary<string, TaskCompletionSource<LdapEntry?>>(StringComparer.Ordinal);

        protected override Task<LdapEntry?> ReadAsync(string dn, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<LdapEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Reads.Add(dn, pending);
            return pending.Task;
        }
    }
}

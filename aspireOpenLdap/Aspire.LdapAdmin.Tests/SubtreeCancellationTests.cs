using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class SubtreeCancellationTests(LdapAdminAppHostFixture fixture)
{
    private const int ChildCount = 5;

    private async Task<string> SeedContainerAsync(CancellationToken cancellationToken)
    {
        var parent = fixture.DnUnder(Dn.Rdn("ou", "bulk-del"), "ou=people");
        var addedOu = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(parent, [new("objectClass", ["organizationalUnit"]), new("ou", ["bulk-del"])]),
            cancellationToken);
        Assert.True(addedOu.Succeeded, addedOu.Message);

        for (var i = 0; i < ChildCount; i++)
        {
            var child = Dn.Combine(Dn.Rdn("cn", $"child-{i}"), parent);
            var added = await fixture.Directory.AddEntryAsync(
                new LdapNewEntry(child, [new("objectClass", ["organizationalRole"]), new("cn", [$"child-{i}"])]),
                cancellationToken);
            Assert.True(added.Succeeded, added.Message);
        }
        return parent;
    }

    [Fact]
    public async Task Cancelled_subtree_delete_returns_exact_acknowledged_progress()
    {
        using var setup = TestCancellation.Source();
        var parent = await SeedContainerAsync(setup.Token);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(setup.Token);
            var acknowledgements = 0;

            var deleted = await fixture.Directory.DeleteSubtreeAsync(parent, cancellation.Token, () =>
            {
                if (++acknowledgements == 3)
                {
                    cancellation.Cancel();
                }
            });

            Assert.Equal(LdapOperationStatus.Cancelled, deleted.Status);
            Assert.Equal(3, deleted.DeletedCount);
            Assert.Contains(parent, deleted.Message, StringComparison.Ordinal);

            var remaining = await fixture.Directory.GetChildrenAsync(parent, limit: 100, CancellationToken.None);
            Assert.False(remaining.Truncated);
            Assert.Equal(ChildCount - deleted.DeletedCount, remaining.Children.Count);
        }
        finally
        {
            using var cleanup = TestCancellation.Source();
            await fixture.Directory.DeleteEntryAsync(parent, subtree: true, cleanup.Token);
        }
    }
}

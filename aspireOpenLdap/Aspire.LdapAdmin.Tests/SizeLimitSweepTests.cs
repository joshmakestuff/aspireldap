using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// A server sizelimit must not stop a subtree delete. The AppHost caps
/// uid=svc-sweeper's searches at 10 entries (olcLimits) and grants it write only under
/// ou=bulk-del — the rootdn the other tests bind with is exempt from both limits and ACLs,
/// so this account is the only witness of the sweep-past-sizelimit behavior.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class SizeLimitSweepTests(LdapAdminAppHostFixture fixture)
{
    private const string SweeperPassword = "sweeper-password";
    private const int ChildCount = 25;

    private string SweeperDn => fixture.DnUnder(Dn.Rdn("uid", "svc-sweeper"), "ou=people");

    private LdapDirectoryService Sweeper() => fixture.DirectoryAs(SweeperDn, SweeperPassword);

    /// <summary>Creates ou=bulk-del with more children than the sweeper's sizelimit, as admin.</summary>
    private async Task<string> SeedBulkContainerAsync(CancellationToken cancellationToken)
    {
        var parent = fixture.DnUnder(Dn.Rdn("ou", "bulk-del"), "ou=people");
        var addedOu = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(parent, [new("objectClass", ["organizationalUnit"]), new("ou", ["bulk-del"])]),
            cancellationToken);
        Assert.True(addedOu.Succeeded, addedOu.Message);

        for (var i = 0; i < ChildCount; i++)
        {
            var child = Dn.Combine(Dn.Rdn("cn", $"sweep-{i}"), parent);
            var added = await fixture.Directory.AddEntryAsync(
                new LdapNewEntry(child, [new("objectClass", ["organizationalRole"]), new("cn", [$"sweep-{i}"])]),
                cancellationToken);
            Assert.True(added.Succeeded, added.Message);
        }
        return parent;
    }

    [Fact]
    public async Task Sweeper_listing_is_actually_size_limited()
    {
        // The precondition that keeps the sweep test falsifiable: if the limit never binds,
        // a passing subtree delete proves nothing.
        using var cts = TestCancellation.Source();
        var parent = await SeedBulkContainerAsync(cts.Token);
        try
        {
            var children = await Sweeper().GetChildrenAsync(parent, limit: 100, cts.Token);

            Assert.True(children.Truncated);
            Assert.Equal(10, children.Children.Count); // the olcLimits size=10 the AppHost sets
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(parent, subtree: true, cts.Token);
        }
    }

    [Fact]
    public async Task Subtree_delete_converges_past_the_server_sizelimit()
    {
        // 25 children against a size=10 limit forces at least three
        // size-limited sweeps.
        using var cts = TestCancellation.Source();
        var parent = await SeedBulkContainerAsync(cts.Token);
        try
        {
            var deleted = await Sweeper().DeleteEntryAsync(parent, subtree: true, cts.Token);
            Assert.True(deleted.Succeeded, deleted.Message);

            Assert.Null(await fixture.Directory.GetEntryAsync(parent, cancellationToken: cts.Token));
        }
        finally
        {
            // Admin cleanup only matters when the assertion above failed mid-branch.
            await fixture.Directory.DeleteEntryAsync(parent, subtree: true, cts.Token);
        }
    }

    [Fact]
    public async Task Cancelled_subtree_delete_returns_exact_acknowledged_progress()
    {
        using var setup = TestCancellation.Source();
        var parent = await SeedBulkContainerAsync(setup.Token);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(setup.Token);
            var acknowledgements = 0;

            var deleted = await Sweeper().DeleteSubtreeAsync(parent, cancellation.Token, () =>
            {
                if (++acknowledgements == 3)
                {
                    cancellation.Cancel();
                }
            });

            Assert.Equal(LdapOperationStatus.Cancelled, deleted.Status);
            Assert.Equal(3, deleted.DeletedCount);
            Assert.Contains("stopped before listing children of", deleted.Message, StringComparison.Ordinal);

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

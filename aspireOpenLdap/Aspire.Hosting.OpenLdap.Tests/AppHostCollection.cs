using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Serializes the tests that start a full Aspire AppHost (DCP host, dashboard, OpenLDAP
/// container): multiple AppHosts in one process contend on orchestration host ports and hang.
/// Only these tests need serializing — everything else runs with xunit's default class-level
/// parallelism (the assembly-wide <c>DisableTestParallelization</c> is gone).
/// </summary>
/// <remarks>
/// The collection also carries the shared <see cref="AppHostFixture"/>, so consecutive facts on
/// the same scenario reuse one boot and only one AppHost is ever alive at a time.
/// </remarks>
[CollectionDefinition(AppHostCollection.Name)]
public sealed class AppHostCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "AppHost";
}

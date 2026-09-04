using Aspire.OpenLdap.Testing;
using AspireOpenLdap.TestAppHost;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

[Collection(AppHostCollection.Name)]
public sealed class AppHostCleanupTests(AppHostFixture appHost)
{
    [Fact]
    public async Task Startup_sweep_removes_dead_run_and_preserves_live_and_unowned_containers()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cancellation.Token;
        await appHost.StartAsync(TestAppHostScenarios.Default, cancellationToken);

        var live = await DockerCli.RunAsync(cancellationToken,
            "ps", "--all", "--quiet", "--filter",
            $"label={TestContainerOwnership.RunIdLabel}={TestContainerOwnership.CurrentRunId}");
        Assert.Equal(0, live.ExitCode);
        var liveContainer = Assert.Single(Lines(live.Output));

        var imageResult = await DockerCli.RunAsync(cancellationToken,
            "inspect", "--format={{.Image}}", liveContainer);
        Assert.Equal(0, imageResult.ExitCode);
        var image = Assert.Single(Lines(imageResult.Output));

        using var cleanup = DockerCli.NewScope("orphan-sweep");
        var orphan = cleanup.NewContainer();
        var unowned = cleanup.NewContainer();
        var deadRunId = Guid.NewGuid().ToString("N");

        var createOrphan = await DockerCli.RunAsync(cancellationToken,
            "create", "--name", orphan,
            "--label", $"{TestContainerOwnership.SuiteLabel}={TestContainerOwnership.SuiteLabelValue}",
            "--label", $"{TestContainerOwnership.OwnerProcessIdLabel}={int.MaxValue}",
            "--label", $"{TestContainerOwnership.OwnerProcessStartLabel}=1",
            "--label", $"{TestContainerOwnership.RunIdLabel}={deadRunId}",
            image);
        Assert.True(createOrphan.ExitCode == 0, createOrphan.Output);

        var createUnowned = await DockerCli.RunAsync(cancellationToken,
            "create", "--name", unowned, image);
        Assert.True(createUnowned.ExitCode == 0, createUnowned.Output);

        var removed = await TestContainerOwnership.RemoveOrphansAsync(cancellationToken);

        Assert.True(removed >= 1, "The synthetic dead-run container was not swept.");
        Assert.False(await ContainerExistsAsync(orphan, cancellationToken));
        Assert.True(await ContainerExistsAsync(liveContainer, cancellationToken));
        Assert.True(await ContainerExistsAsync(unowned, cancellationToken));
    }

    private static string[] Lines(string value) => value.Split(
        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<bool> ContainerExistsAsync(
        string container,
        CancellationToken cancellationToken)
    {
        var inspect = await DockerCli.RunAsync(cancellationToken, "inspect", container);
        return inspect.ExitCode == 0;
    }
}

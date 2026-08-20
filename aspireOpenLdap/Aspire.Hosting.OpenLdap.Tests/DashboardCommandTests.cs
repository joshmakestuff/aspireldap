using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Execution-level witnesses for the dashboard commands (#58): runtime awareness
/// (docker vs podman), actionable missing-CLI failures, and kill-on-cancel. Handlers run
/// against a fake <see cref="IContainerCliRunner"/>, so nothing here depends on a
/// developer's global docker/podman state; the one real-container witness lives in
/// <see cref="OverlayAccessControlIntegrationTests"/>.
/// </summary>
public class DashboardCommandTests
{
    private sealed class FakeCliRunner : IContainerCliRunner
    {
        public List<(string FileName, string[] Args)> Calls { get; } = [];

        public Func<string, IReadOnlyList<string>, (int ExitCode, string StdOut, string StdErr)> Handler { get; set; } =
            (_, _) => (0, string.Empty, string.Empty);

        public Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((fileName, [.. arguments]));
            return Task.FromResult(Handler(fileName, arguments));
        }
    }

    private static ServiceProvider BuildServices(
        FakeCliRunner runner, Dictionary<string, string?>? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContainerCliRunner>(runner);
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build());
        return services.BuildServiceProvider();
    }

    // --- Runtime resolution -------------------------------------------------------------

    [Fact]
    public async Task Unconfigured_Runtime_Probes_Docker_First()
    {
        var runner = new FakeCliRunner(); // default handler: every call exits 0
        using var services = BuildServices(runner);

        var (runtime, failure) = await ContainerRuntime
            .ResolveAsync(services, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("docker", runtime);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("docker", call.FileName);
        Assert.Equal(["--version"], call.Args);
    }

    [Fact]
    public async Task Unconfigured_Runtime_Falls_Back_To_Podman_When_Docker_Is_Absent()
    {
        // DCP auto-detects the runtime when nothing is configured, so on a podman-only
        // machine LDAP itself runs fine — the commands must find podman the same way.
        var runner = new FakeCliRunner
        {
            Handler = (file, _) => file == "docker"
                ? (ProcessContainerCliRunner.StartFailureExitCode, "", "not found")
                : (0, "podman version 5", ""),
        };
        using var services = BuildServices(runner);

        var (runtime, failure) = await ContainerRuntime
            .ResolveAsync(services, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("podman", runtime);
    }

    [Fact]
    public async Task Unconfigured_Runtime_Fails_Actionably_When_Nothing_Is_Installed()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (ProcessContainerCliRunner.StartFailureExitCode, "", "not found"),
        };
        using var services = BuildServices(runner);

        var (runtime, failure) = await ContainerRuntime
            .ResolveAsync(services, CancellationToken.None);

        Assert.Null(runtime);
        Assert.NotNull(failure);
        Assert.False(failure.Success);
        Assert.Contains("ASPIRE_CONTAINER_RUNTIME", failure.Message);
    }

    [Fact]
    public async Task Configured_Runtime_Is_Authoritative_And_Never_Probed()
    {
        // Explicit configuration must fail loudly on use, not silently fall back to a probe
        // that picks a different runtime than the user named.
        var runner = new FakeCliRunner();
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman",
        });

        var (runtime, failure) = await ContainerRuntime
            .ResolveAsync(services, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("podman", runtime);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Configured_Runtime_Prefers_The_Dcp_Key_Over_The_Aspire_Selector()
    {
        // The strongly-bound DCP key is what Aspire itself reads first; it must win.
        using var both = BuildServices(new FakeCliRunner(), new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman",
            ["DcpPublisher:ContainerRuntime"] = "docker",
        });
        Assert.Equal("docker", ContainerRuntime.GetConfigured(both));

        using var none = BuildServices(new FakeCliRunner());
        Assert.Null(ContainerRuntime.GetConfigured(none));
    }

    // --- Reset removal core -------------------------------------------------------------

    [Fact]
    public async Task Remove_Runs_Container_Then_Volume_Removal_Through_The_Selected_Runtime()
    {
        var runner = new FakeCliRunner();
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman",
        });

        var failure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
            services, "cid-123", "my-volume", CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal("podman", c.FileName));
        Assert.Equal(["rm", "-f", "cid-123"], runner.Calls[0].Args);
        Assert.Equal(["volume", "rm", "my-volume"], runner.Calls[1].Args);
    }

    [Fact]
    public async Task Remove_Skips_Container_Removal_When_No_Container_Id_Is_Known()
    {
        var runner = new FakeCliRunner();
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "docker", // explicit: keep probing out of this witness
        });

        var failure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
            services, containerId: null, "my-volume", CancellationToken.None);

        Assert.Null(failure);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["volume", "rm", "my-volume"], call.Args);
    }

    [Fact]
    public async Task Remove_Treats_Already_Gone_Container_And_Volume_As_Success()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, args) => args[0] == "rm"
                ? (1, "", "Error: no container with ID cid found: no such container")
                : (1, "", "Error: no such volume"),
        };
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "docker", // explicit: keep probing out of this witness
        });

        var failure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
            services, "cid", "my-volume", CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal(2, runner.Calls.Count); // both steps still attempted
    }

    [Fact]
    public async Task Remove_Reports_Real_Failures_With_The_Runtime_Name()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (2, "", "volume is in use"),
        };
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman",
        });

        var failure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
            services, containerId: null, "my-volume", CancellationToken.None);

        Assert.NotNull(failure);
        Assert.False(failure.Success);
        Assert.Contains("podman volume rm failed (exit 2)", failure.Message);
        Assert.Contains("volume is in use", failure.Message);
    }

    [Fact]
    public async Task Remove_Surfaces_The_Missing_Cli_Guidance_Verbatim()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (ProcessContainerCliRunner.StartFailureExitCode, "",
                "Container runtime 'podman' could not be started: not found."),
        };
        using var services = BuildServices(runner, new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman", // explicitly configured and missing: fail loudly
        });

        var failure = await ContainerRuntime.RemoveContainerAndVolumeAsync(
            services, "cid", "my-volume", CancellationToken.None);

        Assert.NotNull(failure);
        Assert.False(failure.Success);
        Assert.Contains("Container runtime 'podman' could not be started", failure.Message);
    }

    // --- export-ldif handler ------------------------------------------------------------

    private static async Task<(ExecuteCommandResult Result, FakeCliRunner Runner)> ExecuteExportAsync(
        FakeCliRunner runner,
        bool publishContainerId = true,
        string? containerRuntime = null)
    {
        var builder = DistributedApplication.CreateBuilder();
        if (containerRuntime is not null)
        {
            builder.Configuration["ASPIRE_CONTAINER_RUNTIME"] = containerRuntime;
        }
        builder.Services.AddSingleton<IContainerCliRunner>(runner);
        var ldap = builder.AddOpenLdap("ldap");

        await using var app = builder.Build();
        if (publishContainerId)
        {
            var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await notifications.PublishUpdateAsync(ldap.Resource, s => s with
            {
                Properties = s.Properties.Add(new("container.id", "cid-123")),
            });
        }

        var command = ldap.Resource.Annotations.OfType<ResourceCommandAnnotation>()
            .Single(c => c.Name == "export-ldif");
        var result = await command.ExecuteCommand(new ExecuteCommandContext
        {
            // Aspire 13.5.0: Services replaced ServiceProvider (obsolete) and is required.
            Services = app.Services,
            ResourceName = ldap.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            // Neither command reads invocation arguments; the dashboard would pass an empty
            // collection, but its type is not constructible from tests.
            Arguments = null!,
        });
        return (result, runner);
    }

    [Fact]
    public async Task Export_Runs_Slapcat_Through_The_Selected_Runtime_And_Returns_The_Ldif()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (0, "dn: dc=example,dc=org\nobjectClass: organization\n", ""),
        };

        var (result, _) = await ExecuteExportAsync(runner, containerRuntime: "podman");

        Assert.True(result.Success, result.Message);
        var data = Assert.IsType<CommandResultData>(result.Data);
        Assert.Contains("dn: dc=example,dc=org", data.Value?.ToString());

        var call = Assert.Single(runner.Calls);
        Assert.Equal("podman", call.FileName);
        Assert.Equal(["exec", "cid-123", "slapcat", "-b", "dc=example,dc=org"], call.Args);
    }

    [Fact]
    public async Task Export_Without_A_Running_Container_Fails_Without_Spawning_A_Cli()
    {
        var runner = new FakeCliRunner();

        var (result, _) = await ExecuteExportAsync(runner, publishContainerId: false);

        Assert.False(result.Success);
        Assert.Equal("Container is not running.", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Export_Reports_Nonzero_Exit_With_Runtime_And_Stderr()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (3, "", "slapcat: bad suffix"),
        };

        var (result, _) = await ExecuteExportAsync(runner, containerRuntime: "docker");

        Assert.False(result.Success);
        Assert.Contains("slapcat via docker failed (exit 3)", result.Message);
        Assert.Contains("slapcat: bad suffix", result.Message);
    }

    [Fact]
    public async Task Export_Surfaces_The_Missing_Cli_Guidance_Verbatim()
    {
        var runner = new FakeCliRunner
        {
            Handler = (_, _) => (ProcessContainerCliRunner.StartFailureExitCode, "",
                "Container runtime 'podman' could not be started: not found."),
        };

        var (result, _) = await ExecuteExportAsync(runner, containerRuntime: "podman");

        Assert.False(result.Success);
        Assert.Equal("Container runtime 'podman' could not be started: not found.", result.Message);
    }

    // --- Real process runner ------------------------------------------------------------

    [Fact]
    public async Task Process_Runner_Reports_A_Missing_Executable_As_A_Start_Failure()
    {
        var runner = new ProcessContainerCliRunner();

        var (exit, _, stderr) = await runner.RunAsync(
            "definitely-not-a-real-container-cli-x9z", ["ps"], CancellationToken.None);

        Assert.Equal(ProcessContainerCliRunner.StartFailureExitCode, exit);
        Assert.Contains("definitely-not-a-real-container-cli-x9z", stderr);
        Assert.Contains("ASPIRE_CONTAINER_RUNTIME", stderr);
    }

    [Fact]
    public async Task Process_Runner_Kills_The_Whole_Process_Tree_On_Cancellation()
    {
        // Two regressions must fail here: the old implementation abandoned the CLI process
        // entirely (WaitForExitAsync threw, everything kept running), and a Kill() without
        // entireProcessTree would reap the shell but leave its child alive — so the spawned
        // command is a shell PARENT with a real CHILD, and both PIDs must die.
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 60 127.0.0.1" })
            : ("sh", new[] { "-c", "sleep 60; true" }); // '; true' forces sh to fork, not exec

        var parentPid = 0;
        var runner = new ProcessContainerCliRunner { OnProcessStarted = pid => parentPid = pid };
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(fileName, args, cts.Token);

        // Wait for the shell to spawn its child before cancelling, so the tree exists.
        var spawnDeadline = DateTime.UtcNow.AddSeconds(10);
        int? grandchildPid = null;
        while (grandchildPid is null && DateTime.UtcNow < spawnDeadline)
        {
            await Task.Delay(100);
            if (parentPid != 0)
            {
                grandchildPid = await TryGetFirstChildPidAsync(parentPid);
            }
        }
        Assert.NotEqual(0, parentPid);
        Assert.NotNull(grandchildPid);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        await AssertProcessGoneAsync(parentPid, "shell parent");
        await AssertProcessGoneAsync(grandchildPid.Value, "spawned child");
    }

    private static async Task<int?> TryGetFirstChildPidAsync(int parentPid)
    {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("powershell", $"-NoProfile -Command (Get-CimInstance Win32_Process -Filter \"ParentProcessId={parentPid}\").ProcessId")
            : ("ps", $"-o pid= --ppid {parentPid}");
        var psi = new System.Diagnostics.ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var query = System.Diagnostics.Process.Start(psi)!;
        var output = await query.StandardOutput.ReadToEndAsync();
        await query.WaitForExitAsync();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(line, out var pid))
            {
                return pid;
            }
        }
        return null;
    }

    private static async Task AssertProcessGoneAsync(int pid, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // no such process — reaped
            }
            await Task.Delay(100);
        }
        Assert.Fail($"{what} (pid {pid}) still running after cancellation");
    }
}

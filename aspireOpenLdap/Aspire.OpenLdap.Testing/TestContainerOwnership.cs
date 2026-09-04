using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.OpenLdap.Testing;

/// <summary>
/// Marks containers started by the integration-test AppHosts and removes containers whose
/// owning test-host process no longer exists. Manual AppHost runs are never marked.
/// </summary>
public static class TestContainerOwnership
{
    public const string SuiteLabel = "io.github.joshmakestuff.aspireldap.test-suite";
    public const string OwnerProcessIdLabel = "io.github.joshmakestuff.aspireldap.owner-pid";
    public const string OwnerProcessStartLabel = "io.github.joshmakestuff.aspireldap.owner-start-utc-ticks";
    public const string RunIdLabel = "io.github.joshmakestuff.aspireldap.run-id";
    public const string SuiteLabelValue = "apphost-integration";

    private const string ConfigurationPrefix = "AspireOpenLdapTests:ContainerOwner";
    private const string ProcessIdConfigurationKey = ConfigurationPrefix + ":ProcessId";
    private const string ProcessStartConfigurationKey = ConfigurationPrefix + ":ProcessStartUtcTicks";
    private const string RunIdConfigurationKey = ConfigurationPrefix + ":RunId";

    private static readonly TestContainerOwner CurrentOwner = TestContainerOwner.CreateCurrent();

    /// <summary>The unique identifier attached to containers owned by this test-host process.</summary>
    public static string CurrentRunId => CurrentOwner.RunId;

    /// <summary>Arguments that carry this test-host process's identity into an AppHost.</summary>
    public static string[] AppHostArguments =>
    [
        $"--{ProcessIdConfigurationKey}={CurrentOwner.ProcessId.ToString(CultureInfo.InvariantCulture)}",
        $"--{ProcessStartConfigurationKey}={CurrentOwner.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture)}",
        $"--{RunIdConfigurationKey}={CurrentOwner.RunId}",
    ];

    /// <summary>
    /// Adds ownership labels when the AppHost was launched by a test fixture. With no ownership
    /// arguments, as in a developer's manual run, this is a no-op.
    /// </summary>
    public static IResourceBuilder<T> WithTestContainerOwnership<T>(
        this IResourceBuilder<T> builder,
        IConfiguration configuration)
        where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var owner = TestContainerOwner.FromConfiguration(configuration);
        if (owner is null)
        {
            return builder;
        }

        builder.Resource.Annotations.Add(new ContainerRuntimeArgsCallbackAnnotation(args =>
        {
            AddLabel(args, SuiteLabel, SuiteLabelValue);
            AddLabel(args, OwnerProcessIdLabel, owner.ProcessId.ToString(CultureInfo.InvariantCulture));
            AddLabel(args, OwnerProcessStartLabel, owner.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
            AddLabel(args, RunIdLabel, owner.RunId);
        }));

        return builder;
    }

    /// <summary>
    /// Removes containers from dead test-host processes and preserves containers belonging to
    /// any currently-running test host. Returns the number removed.
    /// </summary>
    public static async Task<int> RemoveOrphansAsync(CancellationToken cancellationToken)
    {
        var runtime = ResolveContainerRuntime();
        var list = await RunAsync(runtime, cancellationToken,
            "ps", "--all", "--quiet", "--filter", $"label={SuiteLabel}={SuiteLabelValue}")
            .ConfigureAwait(false);
        EnsureSuccess(list, "list test-owned containers");

        var removed = 0;
        foreach (var containerId in list.StandardOutput.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inspect = await RunAsync(runtime, cancellationToken,
                "inspect", "--format={{json .Config.Labels}}", containerId)
                .ConfigureAwait(false);
            if (inspect.ExitCode != 0)
            {
                if (ContainerDoesNotExist(inspect))
                {
                    continue;
                }

                EnsureSuccess(inspect, $"inspect test-owned container '{containerId}'");
            }

            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(inspect.StandardOutput);
            if (labels is null
                || !labels.TryGetValue(SuiteLabel, out var suite)
                || !string.Equals(suite, SuiteLabelValue, StringComparison.Ordinal))
            {
                continue;
            }

            var owner = TestContainerOwner.FromLabels(labels);
            if (owner is not null && IsProcessInstanceAlive(owner))
            {
                continue;
            }

            var remove = await RunAsync(runtime, cancellationToken,
                "rm", "--force", "--volumes", containerId)
                .ConfigureAwait(false);
            if (remove.ExitCode != 0 && !ContainerDoesNotExist(remove))
            {
                EnsureSuccess(remove, $"remove orphaned test container '{containerId}'");
            }
            else
            {
                removed++;
            }
        }

        return removed;
    }

    private static void AddLabel(IList<object> args, string name, string value)
    {
        args.Add("--label");
        args.Add($"{name}={value}");
    }

    private static bool IsProcessInstanceAlive(TestContainerOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == owner.ProcessStartUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // If the operating system will not reveal another live process's start time, keep
            // its container. A false negative here would disrupt a concurrent test run.
            return true;
        }
    }

    private static string ResolveContainerRuntime()
    {
        var configured = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "docker";
        }

        if (configured.Equals("docker", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("podman", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        throw new InvalidOperationException(
            $"Unsupported ASPIRE_CONTAINER_RUNTIME '{configured}'; expected 'docker' or 'podman'.");
    }

    private static async Task<ContainerRuntimeResult> RunAsync(
        string runtime,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var startInfo = new ProcessStartInfo(runtime)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start container runtime '{runtime}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return new ContainerRuntimeResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill.
        }
    }

    private static void EnsureSuccess(ContainerRuntimeResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container runtime failed to {operation} (exit {result.ExitCode}): " +
                result.StandardError.Trim());
        }
    }

    private static bool ContainerDoesNotExist(ContainerRuntimeResult result) =>
        result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase);

    private sealed record TestContainerOwner(int ProcessId, long ProcessStartUtcTicks, string RunId)
    {
        public static TestContainerOwner CreateCurrent()
        {
            using var process = Process.GetCurrentProcess();
            return new TestContainerOwner(
                Environment.ProcessId,
                process.StartTime.ToUniversalTime().Ticks,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        }

        public static TestContainerOwner? FromConfiguration(IConfiguration configuration)
        {
            var processId = configuration[ProcessIdConfigurationKey];
            var processStart = configuration[ProcessStartConfigurationKey];
            var runId = configuration[RunIdConfigurationKey];
            if (processId is null && processStart is null && runId is null)
            {
                return null;
            }

            if (!int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProcessId)
                || parsedProcessId <= 0
                || !long.TryParse(processStart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProcessStart)
                || parsedProcessStart <= 0
                || !Guid.TryParseExact(runId, "N", out _))
            {
                throw new InvalidOperationException(
                    $"The '{ConfigurationPrefix}' test-container ownership configuration is incomplete or invalid.");
            }

            return new TestContainerOwner(parsedProcessId, parsedProcessStart, runId!);
        }

        public static TestContainerOwner? FromLabels(IReadOnlyDictionary<string, string> labels)
        {
            return labels.TryGetValue(OwnerProcessIdLabel, out var processId)
                && int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProcessId)
                && parsedProcessId > 0
                && labels.TryGetValue(OwnerProcessStartLabel, out var processStart)
                && long.TryParse(processStart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProcessStart)
                && parsedProcessStart > 0
                && labels.TryGetValue(RunIdLabel, out var runId)
                && Guid.TryParseExact(runId, "N", out _)
                    ? new TestContainerOwner(parsedProcessId, parsedProcessStart, runId)
                    : null;
        }
    }

    private sealed record ContainerRuntimeResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

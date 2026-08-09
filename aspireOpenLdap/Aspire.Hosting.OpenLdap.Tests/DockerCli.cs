using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

internal sealed record DockerResult(int ExitCode, string Output);

/// <summary>
/// Shared docker CLI plumbing for the direct-docker integration tests. Previously each test
/// class carried its own copy of these helpers (and its own image tag, so the bundled image
/// was rebuilt once per class).
/// </summary>
internal static class DockerCli
{
    public static async Task<DockerResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A timed-out docker operation must not leave the CLI (and whatever it is
            // attached to) running past the test.
            KillQuiet(process);
            throw;
        }
        return new DockerResult(process.ExitCode, await stdout + Environment.NewLine + await stderr);
    }

    private static void KillQuiet(Process process)
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

    public static void BestEffort(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var process = Process.Start(psi);
            if (process is not null && !process.WaitForExit(30_000))
            {
                KillQuiet(process);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    /// <summary>The in-container LDAP URI every direct-docker probe binds against.</summary>
    public static readonly string InContainerLdapUri = $"ldap://localhost:{OpenLdapResource.DefaultLdapTargetPort}";

    /// <summary>
    /// Creates a scope that owns the docker objects a test makes, removing them on dispose:
    /// containers first, then volumes (a volume cannot be removed while a container holds it).
    /// </summary>
    public static DockerScope NewScope(string prefix) => new(prefix);

    /// <summary>
    /// Polls <c>ldapwhoami</c> until the container serves the public LDAP port. Bounded three
    /// ways so a container that dies during bootstrap fails fast and with evidence rather than
    /// burning the caller's whole budget: the caller's token, an explicit deadline, and a
    /// liveness check that breaks out the moment the container stops running. Failures carry
    /// the last probe output and <c>docker logs</c>, which <c>Dispose</c> would otherwise destroy.
    /// </summary>
    public static async Task WaitForLdapReadyAsync(
        string container,
        string bindDn,
        string password,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            var whoami = await RunAsync(cancellationToken,
                "exec", container, "ldapwhoami",
                "-x", "-H", InContainerLdapUri,
                "-D", bindDn, "-w", password);
            if (whoami.ExitCode == 0)
            {
                return;
            }

            if (!await IsRunningAsync(container, cancellationToken))
            {
                await FailWithLogsAsync(container, "the container stopped running", whoami, cancellationToken);
            }

            if (DateTime.UtcNow >= deadline)
            {
                await FailWithLogsAsync(container, "timed out", whoami, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// True while the container's state is <c>running</c>. An inspect that fails (the container
    /// was removed, or the daemon is unreachable) is reported as not running: either way the
    /// readiness poll can never succeed from here.
    /// </summary>
    public static async Task<bool> IsRunningAsync(string container, CancellationToken cancellationToken)
    {
        var inspect = await RunAsync(cancellationToken, "inspect", "-f", "{{.State.Running}}", container);
        return inspect.ExitCode == 0
            && inspect.Output.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task FailWithLogsAsync(
        string container, string reason, DockerResult lastProbe, CancellationToken cancellationToken)
    {
        // Best effort: the logs are the diagnostic, so a failure to read them must not replace
        // the real failure with a secondary one.
        string logs;
        try
        {
            logs = (await RunAsync(cancellationToken, "logs", "--tail", "200", container)).Output;
        }
        catch (Exception ex)
        {
            logs = $"<docker logs unavailable: {ex.Message}>";
        }

        Assert.Fail(
            $"waiting for LDAP readiness on '{container}' failed: {reason}." + Environment.NewLine +
            $"last ldapwhoami (exit {lastProbe.ExitCode}): {lastProbe.Output}" + Environment.NewLine +
            $"docker logs:{Environment.NewLine}{logs}");
    }

    /// <summary>
    /// CreateTempSubdirectory makes a 0700 dir, but the OpenLDAP container runs as a non-root
    /// user (uid != the test host's) and must traverse the bind-mounted dir and read its files.
    /// Widen perms on Linux accordingly. (Docker Desktop on Windows/macOS exposes mounts as
    /// world-accessible, which hides the problem when running the tests locally.)
    /// </summary>
    public static void WidenPermissionsForContainer(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var file in Directory.GetFiles(dir))
        {
            File.SetUnixFileMode(file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}

/// <summary>
/// Owns the docker containers and volumes a test creates, and removes them on dispose. Each
/// direct-docker test class used to carry its own copy of this list + naming + cleanup, which
/// let the copies diverge (two of the four forgot volumes entirely, so a leaked volume
/// outlived the run).
/// </summary>
internal sealed class DockerScope : IDisposable
{
    private readonly string _prefix;
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];

    public DockerScope(string prefix) => _prefix = prefix;

    /// <summary>Reserves a unique container name owned by this scope.</summary>
    public string NewContainer() => Track(_containers, "container");

    /// <summary>Reserves a unique volume name owned by this scope.</summary>
    public string NewVolume() => Track(_volumes, "vol");

    private string Track(List<string> names, string kind)
    {
        var name = $"aspire-openldap-{_prefix}-{kind}-{Guid.NewGuid():N}";
        names.Add(name);
        return name;
    }

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            DockerCli.BestEffort("rm", "-f", container);
        }
        // Volumes after containers: a volume can't be removed while a container holds it.
        foreach (var volume in _volumes)
        {
            DockerCli.BestEffort("volume", "rm", "-f", volume);
        }
    }
}

/// <summary>
/// The one lock both Docker-using test families share. The AppHost start path (DCP host boot,
/// which builds and starts its own OpenLDAP container) and the direct-docker bundled-image
/// build each hold it for their full duration, so the two can structurally never overlap.
/// Docker Desktop serializes badly under that overlap — a context-metadata lock during a
/// concurrent build once cascaded into 30 misleading failures (#54). Everything after the
/// gated sections (running containers, exec probes) stays parallel.
/// </summary>
internal static class DockerHostGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}

/// <summary>
/// Builds the bundled OpenLDAP image once and shares the tag across all direct-docker
/// integration tests. Tests that are already awaiting a build that fails all receive that
/// one root diagnostic, but the fault is not cached: the next test retries the build, so a
/// transient docker failure cannot cascade a stale error across the rest of the run.
/// </summary>
internal static class BundledImage
{
    public const string Tag = "aspire-openldap-tests";

    private static readonly Lock Sync = new();
    private static Task<string>? _build;

    public static Task<string> GetAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            if (_build is null || (_build.IsCompleted && !_build.IsCompletedSuccessfully))
            {
                _build = BuildOnceAsync();
            }
            return _build.WaitAsync(cancellationToken);
        }
    }

    private static async Task<string> BuildOnceAsync()
    {
        var contextDir = OpenLdapResource.DefaultDockerContextPath;
        Assert.True(Directory.Exists(contextDir), $"bundled docker context not found at {contextDir}");

        // The build owns its lifetime: callers' tokens must not cancel (and thereby poison)
        // the shared task for every concurrently awaiting test.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using (await DockerHostGate.AcquireAsync(cts.Token))
        {
            var build = await DockerCli.RunAsync(cts.Token, "build", "-q", "-t", Tag, contextDir);
            Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");
            return Tag;
        }
    }
}

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
        await process.WaitForExitAsync(cancellationToken);
        return new DockerResult(process.ExitCode, await stdout + Environment.NewLine + await stderr);
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
            process?.WaitForExit(30_000);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    public static async Task WaitForLdapReadyAsync(string container, string bindDn, string password, CancellationToken cancellationToken)
    {
        while (true)
        {
            var whoami = await RunAsync(cancellationToken,
                "exec", container, "ldapwhoami",
                "-x", "-H", "ldap://localhost:1389",
                "-D", bindDn, "-w", password);
            if (whoami.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
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
/// Builds the bundled OpenLDAP image at most once per test run and shares the tag across all
/// direct-docker integration tests. A failed build faults the cached task, so every dependent
/// test fails fast with the same diagnostic instead of retrying the build.
/// </summary>
internal static class BundledImage
{
    public const string Tag = "aspire-openldap-tests";

    private static readonly Lazy<Task<string>> Build = new(BuildOnceAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<string> GetAsync(CancellationToken cancellationToken) => Build.Value.WaitAsync(cancellationToken);

    private static async Task<string> BuildOnceAsync()
    {
        var contextDir = OpenLdapResource.DefaultDockerContextPath;
        Assert.True(Directory.Exists(contextDir), $"bundled docker context not found at {contextDir}");

        // The build owns its lifetime: callers' tokens must not cancel (and thereby poison)
        // the shared cached task for every other test.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var build = await DockerCli.RunAsync(cts.Token, "build", "-q", "-t", Tag, contextDir);
        Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");
        return Tag;
    }
}

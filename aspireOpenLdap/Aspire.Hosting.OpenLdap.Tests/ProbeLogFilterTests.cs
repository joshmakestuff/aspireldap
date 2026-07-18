using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-driven guards for the health-probe log filter (<c>probe_log_filter.sh</c>):
/// <list type="bullet">
/// <item>only wholly-successful sentinel-marked probe blocks are dropped — real traffic,
/// failed probes, and anything withheld when slapd exits must always surface (the
/// fail-open contract),</item>
/// <item>a running container filters live probe-shaped searches out of <c>docker logs</c>
/// while still logging real operations,</item>
/// <item><c>LDAP_LOG_HEALTH_PROBES=yes</c> disables the filter entirely.</item>
/// </list>
/// </summary>
public class ProbeLogFilterTests : IDisposable
{
    private const string AdminDn = "cn=admin,dc=example,dc=org";
    private const string AdminPassword = "probe-filter-test-pw";

    private readonly List<string> _containers = [];

    [Fact]
    public async Task Filter_Drops_Only_Wholly_Successful_Probe_Blocks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // conn=1000: successful probe. conn=1001/1002: real search interleaved with a probe.
        // conn=1003: probe whose search FAILS (err=32). conn=1004: whoami (non-probe shape).
        // conn=1005: probe cut off by slapd exit.
        var fixture =
            "slapd starting\n" +
            "conn=1000 fd=12 ACCEPT from IP=172.17.0.1:39678 (IP=0.0.0.0:1389)\n" +
            "conn=1000 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1000 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" mech=SIMPLE bind_ssf=0 ssf=0\n" +
            "conn=1000 op=0 RESULT tag=97 err=0 qtime=0.000010 etime=0.000212 text=\n" +
            "conn=1000 op=1 SRCH base=\"\" scope=0 deref=0 filter=\"(objectClass=*)\"\n" +
            "conn=1000 op=1 SRCH attr=namingContexts aspire-healthcheck\n" +
            "conn=1000 op=1 SEARCH RESULT tag=101 err=0 qtime=0.000008 etime=0.000135 nentries=1 text=\n" +
            "conn=1000 op=2 UNBIND\n" +
            "conn=1000 fd=12 closed\n" +
            "conn=1001 fd=14 ACCEPT from IP=172.17.0.1:39680 (IP=0.0.0.0:1389)\n" +
            "conn=1002 fd=16 ACCEPT from IP=172.17.0.1:39682 (IP=0.0.0.0:1389)\n" +
            "conn=1001 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1002 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1001 op=0 RESULT tag=97 err=0 text=\n" +
            "conn=1002 op=0 RESULT tag=97 err=0 text=\n" +
            "conn=1001 op=1 SRCH base=\"dc=example,dc=org\" scope=2 deref=0 filter=\"(uid=user01)\"\n" +
            "conn=1002 op=1 SRCH base=\"\" scope=0 deref=0 filter=\"(objectClass=*)\"\n" +
            "conn=1002 op=1 SRCH attr=namingContexts aspire-healthcheck\n" +
            "conn=1001 op=1 SEARCH RESULT tag=101 err=0 nentries=1 text=\n" +
            "conn=1002 op=1 SEARCH RESULT tag=101 err=0 nentries=1 text=\n" +
            "conn=1001 op=2 UNBIND\n" +
            "conn=1002 op=2 UNBIND\n" +
            "conn=1001 fd=14 closed\n" +
            "conn=1002 fd=16 closed\n" +
            "conn=1003 fd=18 ACCEPT from IP=172.17.0.1:39684 (IP=0.0.0.0:1389)\n" +
            "conn=1003 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1003 op=0 RESULT tag=97 err=0 text=\n" +
            "conn=1003 op=1 SRCH base=\"\" scope=0 deref=0 filter=\"(objectClass=*)\"\n" +
            "conn=1003 op=1 SRCH attr=namingContexts aspire-healthcheck\n" +
            "conn=1003 op=1 SEARCH RESULT tag=101 err=32 nentries=0 text=\n" +
            "conn=1003 op=2 UNBIND\n" +
            "conn=1003 fd=18 closed\n" +
            "conn=1004 fd=20 ACCEPT from IP=172.17.0.1:39686 (IP=0.0.0.0:1389)\n" +
            "conn=1004 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1004 op=0 RESULT tag=97 err=0 text=\n" +
            "conn=1004 op=1 EXT oid=1.3.6.1.4.1.4203.1.11.3\n" +
            "conn=1004 op=1 WHOAMI\n" +
            "conn=1004 op=1 RESULT oid= err=0 text=\n" +
            "conn=1004 op=2 UNBIND\n" +
            "conn=1004 fd=20 closed\n" +
            "conn=1005 fd=22 ACCEPT from IP=172.17.0.1:39688 (IP=0.0.0.0:1389)\n" +
            "conn=1005 op=0 BIND dn=\"cn=admin,dc=example,dc=org\" method=128\n" +
            "conn=1005 op=0 RESULT tag=97 err=0 text=\n" +
            "conn=1005 op=1 SRCH base=\"\" scope=0 deref=0 filter=\"(objectClass=*)\"\n" +
            "conn=1005 op=1 SRCH attr=namingContexts aspire-healthcheck\n" +
            "daemon: shutdown requested and initiated.\n";

        var fixtureDir = Directory.CreateTempSubdirectory("aspire-openldap-probefilter-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixtureDir, "fixture.log"), fixture, cts.Token);
            WidenPermissionsForContainer(fixtureDir);

            var run = await DockerAsync(cts.Token,
                "run", "--rm",
                "-v", $"{fixtureDir}:/fixture:ro",
                "--entrypoint", "bash",
                image,
                "-c", "/opt/openldap/scripts/openldap/probe_log_filter.sh < /fixture/fixture.log");
            Assert.True(run.ExitCode == 0, $"filter run failed: {run.Output}");
            var output = run.Output;

            // Successful probes vanish entirely, even interleaved with real traffic.
            Assert.DoesNotContain("conn=1000 ", output);
            Assert.DoesNotContain("conn=1002 ", output);

            // The interleaved real search survives line-for-line.
            Assert.Contains("conn=1001 fd=14 ACCEPT", output);
            Assert.Contains("conn=1001 op=0 BIND", output);
            Assert.Contains("conn=1001 op=1 SRCH base=\"dc=example,dc=org\"", output);
            Assert.Contains("conn=1001 fd=14 closed", output);

            // Fail-open: a probe whose search failed keeps its ENTIRE block, sentinel included.
            Assert.Contains("conn=1003 fd=18 ACCEPT", output);
            Assert.Contains("conn=1003 op=1 SRCH attr=namingContexts aspire-healthcheck", output);
            Assert.Contains("err=32", output);
            Assert.Contains("conn=1003 fd=18 closed", output);

            // Non-probe shapes are flushed and pass through.
            Assert.Contains("conn=1004 op=1 EXT oid=", output);

            // Fail-open: a probe still in flight when slapd exits is drained, not eaten.
            Assert.Contains("conn=1005 op=1 SRCH attr=namingContexts aspire-healthcheck", output);

            // Non-conn lines are untouched.
            Assert.Contains("slapd starting", output);
            Assert.Contains("daemon: shutdown requested", output);
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public async Task Running_Container_Filters_Probe_Searches_But_Logs_Real_Traffic()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, cts.Token);

        // Mimic the AppHost health check exactly: authenticated root-DSE search with the sentinel.
        var probe = await LdapSearchAsync(name, cts.Token,
            "-b", "", "-s", "base", "(objectClass=*)", "namingContexts", "aspire-healthcheck");
        Assert.True(probe.ExitCode == 0, $"probe-mimic search failed: {probe.Output}");

        var real = await LdapSearchAsync(name, cts.Token,
            "-b", "dc=example,dc=org", "(objectClass=organization)");
        Assert.True(real.ExitCode == 0, $"real search failed: {real.Output}");

        // The filter releases/drops blocks as each conn closes; allow a beat for log delivery.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var logs = await DockerAsync(cts.Token, "logs", name);
        // Only assert on the foreground daemon's output — the init-phase slapd (ldapi-only,
        // unfiltered) legitimately logs its own bootstrap operations.
        var daemonLogs = logs.Output[logs.Output.LastIndexOf("** Starting slapd **", StringComparison.Ordinal)..];

        // The probe block is gone: no sentinel, no root-DSE search line.
        Assert.DoesNotContain("aspire-healthcheck", daemonLogs);
        Assert.DoesNotContain("SRCH base=\"\" scope=0", daemonLogs);

        // Real traffic is logged: the search block and the readiness whoamis (which also prove
        // that withheld probe-shaped prefixes get flushed once a conn deviates).
        Assert.Contains("SRCH base=\"dc=example,dc=org\"", daemonLogs);
        Assert.Contains("EXT oid=1.3.6.1.4.1.4203.1.11.3", daemonLogs);
    }

    [Fact]
    public async Task Opt_Out_Env_Keeps_Probe_Lines()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_LOG_HEALTH_PROBES=yes",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, cts.Token);

        var probe = await LdapSearchAsync(name, cts.Token,
            "-b", "", "-s", "base", "(objectClass=*)", "namingContexts", "aspire-healthcheck");
        Assert.True(probe.ExitCode == 0, $"probe-mimic search failed: {probe.Output}");

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var logs = await DockerAsync(cts.Token, "logs", name);
        Assert.Contains("aspire-healthcheck", logs.Output);
    }

    private static Task<DockerResult> LdapSearchAsync(string container, CancellationToken ct, params string[] searchArgs)
    {
        string[] baseArgs =
        [
            "exec", container, "ldapsearch",
            "-x", "-H", "ldap://localhost:1389",
            "-D", AdminDn, "-w", AdminPassword,
        ];
        return DockerAsync(ct, [.. baseArgs, .. searchArgs]);
    }

    private static async Task WaitForLdapReadyAsync(string container, CancellationToken ct)
    {
        while (true)
        {
            var whoami = await DockerAsync(ct,
                "exec", container, "ldapwhoami",
                "-x", "-H", "ldap://localhost:1389",
                "-D", AdminDn, "-w", AdminPassword);
            if (whoami.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static async Task<string> BuildBundledImageAsync(CancellationToken cancellationToken)
    {
        var contextDir = OpenLdapResource.DefaultDockerContextPath;
        Assert.True(Directory.Exists(contextDir), $"bundled docker context not found at {contextDir}");

        const string tag = "aspire-openldap-probefiltertests";
        var build = await DockerAsync(cancellationToken, "build", "-q", "-t", tag, contextDir);
        Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");
        return tag;
    }

    private string NewContainer()
    {
        var name = $"aspire-openldap-probefiltertest-{Guid.NewGuid():N}";
        _containers.Add(name);
        return name;
    }

    private static void WidenPermissionsForContainer(string dir)
    {
        // The container runs as a non-root user and must traverse/read the bind-mounted fixture.
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

    private sealed record DockerResult(int ExitCode, string Output);

    private static async Task<DockerResult> DockerAsync(CancellationToken cancellationToken, params string[] args)
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

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            try
            {
                var psi = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("rm");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(container);
                using var process = Process.Start(psi);
                process?.WaitForExit(30_000);
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }
        }
    }
}

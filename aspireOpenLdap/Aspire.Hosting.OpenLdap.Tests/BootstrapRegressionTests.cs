using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-driven regressions for container bootstrap semantics:
/// <list type="bullet">
/// <item><c>LDAP_ACCESSLOG_ADMIN_PASSWORD</c> is honored (it used to be silently overwritten
/// by the <c>LDAP_ACCESSLOG_PASSWORD</c> alias's default, leaving the known default password
/// active on the access-log database),</item>
/// <item>default-tree user passwords are stored hashed, not as recoverable cleartext,</item>
/// <item>a <c>c=</c> root longer than two characters fails validation up front instead of
/// dying mid-bootstrap on an opaque olcSuffix syntax error,</item>
/// <item>RFC 4514 hex escapes in the root RDN value (<c>\2C</c>) are decoded, not mangled
/// into literal text.</item>
/// </list>
/// </summary>
public class BootstrapRegressionTests : IDisposable
{
    private const string AdminPassword = "bootstrap-regression-pw";

    private readonly List<string> _containers = [];

    [Fact]
    public async Task Accesslog_Admin_Password_Resolution_Honors_Canonical_And_Alias()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // (env args, expected resolved password)
        (string[] Env, string Expected)[] cases =
        [
            (["-e", "LDAP_ACCESSLOG_ADMIN_PASSWORD=canonical-pw"], "canonical-pw"),
            (["-e", "LDAP_ACCESSLOG_PASSWORD=alias-pw"], "alias-pw"),
            (["-e", "LDAP_ACCESSLOG_ADMIN_PASSWORD=canonical-pw", "-e", "LDAP_ACCESSLOG_PASSWORD=alias-pw"], "canonical-pw"),
            ([], "accesspassword"),
        ];

        foreach (var (env, expected) in cases)
        {
            var run = await DockerAsync(cts.Token,
            [
                "run", "--rm", .. env, "--entrypoint", "bash", image,
                "-c", ". /opt/openldap/scripts/libopenldap.sh && eval \"$(ldap_env)\" && printf 'resolved=%s' \"$LDAP_ACCESSLOG_ADMIN_PASSWORD\"",
            ]);
            Assert.True(run.ExitCode == 0, $"env resolution run failed: {run.Output}");
            Assert.Contains($"resolved={expected}", run.Output);
        }
    }

    [Fact]
    public async Task Accesslog_Bind_Uses_Configured_Admin_Password()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        const string configured = "review-fix-accesslog-pw";
        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_ENABLE_ACCESSLOG=yes",
            "-e", $"LDAP_ACCESSLOG_ADMIN_PASSWORD={configured}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

        var goodBind = await DockerAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=admin,cn=accesslog", "-w", configured);
        Assert.True(goodBind.ExitCode == 0, $"configured accesslog password was rejected: {goodBind.Output}");

        var defaultBind = await DockerAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=admin,cn=accesslog", "-w", "accesspassword");
        Assert.True(defaultBind.ExitCode != 0, "the default accesslog password must not remain active");
    }

    [Fact]
    public async Task Default_Tree_Passwords_Are_Hashed_And_Still_Bindable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

        var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", "dc=example,dc=org");
        Assert.True(slapcat.ExitCode == 0, $"slapcat failed: {slapcat.Output}");

        // "e1NTSEF9" is base64("{SSHA}"): every stored userPassword must be hashed, and the
        // former recoverable-cleartext values (base64("bitnami1"/"bitnami2")) must be gone.
        Assert.Contains("userPassword:: e1NTSEF9", slapcat.Output);
        Assert.DoesNotContain("Yml0bmFtaTE=", slapcat.Output);
        Assert.DoesNotContain("Yml0bmFtaTI=", slapcat.Output);

        // Hashing must not break the advertised default credentials.
        var userBind = await DockerAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=user01,ou=users,dc=example,dc=org", "-w", "bitnami1");
        Assert.True(userBind.ExitCode == 0, $"default user bind failed after hashing: {userBind.Output}");
    }

    [Fact]
    public async Task Oversized_Country_Root_Fails_Validation_Before_Bootstrap()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var run = await DockerAsync(cts.Token,
            "run", "--rm",
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_ROOT=c=USA",
            image);
        Assert.True(run.ExitCode != 0, "a three-letter c= root must fail container validation");
        Assert.Contains("two-character country code", run.Output);
        // It must fail at validation, not die later on the opaque slapd schema error.
        Assert.DoesNotContain("invalid per syntax", run.Output);
    }

    [Fact]
    public async Task Two_Letter_Country_Root_Bootstraps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_ROOT=c=US",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,c=US", AdminPassword, cts.Token);

        var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", "c=US");
        Assert.True(slapcat.ExitCode == 0, $"slapcat failed: {slapcat.Output}");
        Assert.Contains("objectClass: country", slapcat.Output);
        Assert.Contains("c: US", slapcat.Output);
    }

    [Fact]
    public async Task Escaped_Root_Rdn_Value_Is_Decoded_Not_Mangled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        // \2C is the RFC 4514 hex escape for a comma inside an RDN value.
        const string root = "o=Acme\\2C Inc.";
        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", $"LDAP_ROOT={root}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, $"cn=admin,{root}", AdminPassword, cts.Token);

        var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", root);
        Assert.True(slapcat.ExitCode == 0, $"slapcat failed: {slapcat.Output}");

        // The naming value decodes to "Acme, Inc."; the pre-fix behavior emitted a second,
        // mangled attribute value with the backslash stripped ("Acme2C Inc.").
        Assert.Contains("o: Acme, Inc.", slapcat.Output);
        Assert.DoesNotContain("o: Acme2C", slapcat.Output);
    }

    private static async Task WaitForLdapReadyAsync(string container, string bindDn, string password, CancellationToken ct)
    {
        while (true)
        {
            var whoami = await DockerAsync(ct,
                "exec", container, "ldapwhoami",
                "-x", "-H", "ldap://localhost:1389",
                "-D", bindDn, "-w", password);
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

        const string tag = "aspire-openldap-bootstraptests";
        var build = await DockerAsync(cancellationToken, "build", "-q", "-t", tag, contextDir);
        Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");
        return tag;
    }

    private string NewContainer()
    {
        var name = $"aspire-openldap-bootstraptest-{Guid.NewGuid():N}";
        _containers.Add(name);
        return name;
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

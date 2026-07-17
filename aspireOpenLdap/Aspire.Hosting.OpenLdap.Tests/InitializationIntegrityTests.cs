using System.Diagnostics;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Container-level regression guards for initialization integrity, driven through the docker
/// CLI directly because they assert on container EXIT behavior and same-volume restarts —
/// states the Aspire testing harness cannot easily reach:
/// <list type="bullet">
/// <item>a mid-seed failure must fail the start with actionable diagnostics (and never leak
/// the bind password into logs),</item>
/// <item>restarting over the partially-initialized volume must be refused (completion-marker
/// gate) rather than served as "persisted data",</item>
/// <item>admin passwords must be hashed byte-exactly (no shell word-splitting/globbing).</item>
/// </list>
/// </summary>
public class InitializationIntegrityTests : IDisposable
{
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];

    [Fact]
    public async Task Failed_Seed_Fails_Loudly_And_Restart_Over_Partial_Data_Is_Refused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var seedDir = Directory.CreateTempSubdirectory("aspire-openldap-badseed-").FullName;
        try
        {
            // Valid root entry, then an entry whose parent OU does not exist: ldapadd commits
            // the first and rejects the second, leaving a non-empty MDB directory behind.
            await File.WriteAllTextAsync(
                Path.Combine(seedDir, "00-bad.ldif"),
                "dn: dc=example,dc=org\n" +
                "objectClass: dcObject\n" +
                "objectClass: organization\n" +
                "dc: example\n" +
                "o: Example\n" +
                "\n" +
                "dn: uid=orphan,ou=missing,dc=example,dc=org\n" +
                "objectClass: inetOrgPerson\n" +
                "uid: orphan\n" +
                "cn: Orphan\n" +
                "sn: User\n",
                cts.Token);
            WidenPermissionsForContainer(seedDir);

            var name = NewName("container");
            var volume = NewName("volume");
            const string password = "test-admin-password";

            var first = await DockerAsync(cts.Token,
                "run", "--name", name,
                "-v", $"{volume}:/data/openldap",
                "-v", $"{seedDir}:/ldifs:ro",
                "-e", $"LDAP_ADMIN_PASSWORD={password}",
                image);

            Assert.NotEqual(0, first.ExitCode);
            // The failing entry and the server's diagnostic must be in the normal log (no
            // BITNAMI_DEBUG re-run required), with the bind password redacted.
            Assert.Contains("00-bad.ldif", first.Output);
            Assert.Contains("No such object", first.Output);
            Assert.Contains("[redacted]", first.Output);
            Assert.DoesNotContain(password, first.Output);

            var restart = await DockerAsync(cts.Token, "start", "-a", name);

            Assert.NotEqual(0, restart.ExitCode);
            Assert.Contains("completed-initialization marker", restart.Output);
            // The partial dataset must never be served: refusal happens before slapd starts.
            Assert.DoesNotContain("Starting slapd", restart.Output);
        }
        finally
        {
            Directory.Delete(seedDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("  lead  in  trail  ")]
    [InlineData("glob*?[a-z]  chars")]
    [InlineData("quo\"te'semi;Ünïcode")]
    public async Task Admin_Password_Bytes_Are_Preserved_Exactly(string password)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var name = NewName("container");
        _ = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={password}",
            image);

        // Wait for init to finish, then bind with the EXACT caller-supplied bytes. Before the
        // printf fix, unquoted expansion collapsed whitespace / expanded globs before hashing,
        // so this bind failed with err=49 even though the env round-tripped the value.
        var deadline = DateTime.UtcNow.AddMinutes(5);
        DockerResult whoami;
        do
        {
            whoami = await DockerAsync(cts.Token,
                "exec", "-e", $"PROBE_PW={password}", name,
                "bash", "-c", "ldapwhoami -H ldapi:/// -D \"cn=admin,dc=example,dc=org\" -w \"$PROBE_PW\"");
            if (whoami.ExitCode == 0)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        } while (DateTime.UtcNow < deadline);

        Assert.True(whoami.ExitCode == 0, $"bind with exact password bytes failed: {whoami.Output}");
        Assert.Contains("dn:cn=admin,dc=example,dc=org", whoami.Output);
    }

    [Fact]
    public async Task Country_Base_Dn_Initializes_With_Country_Root_Entry()
    {
        // c=US is a valid suffix that previously killed the container at "Creating LDAP
        // default tree": the generated root entry assumed dcObject/organization and never
        // emitted the naming c attribute (F04).
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var name = NewName("container");
        const string password = "test-admin-password";
        _ = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", "LDAP_ROOT=c=US",
            "-e", $"LDAP_ADMIN_PASSWORD={password}",
            image);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        DockerResult search;
        do
        {
            search = await DockerAsync(cts.Token,
                "exec", name,
                "ldapsearch", "-x", "-H", "ldapi:///", "-D", "cn=admin,c=US", "-w", password,
                "-b", "c=US", "-s", "base", "(objectClass=*)");
            if (search.ExitCode == 0)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        } while (DateTime.UtcNow < deadline);

        Assert.True(search.ExitCode == 0, $"admin bind/search under c=US failed: {search.Output}");
        Assert.Contains("objectClass: country", search.Output);
        Assert.Contains("c: US", search.Output);
    }

    [Fact]
    public async Task Invalid_Dn_Inputs_Are_Rejected_Before_Bootstrap()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // Unsupported root naming attribute: fail at validation, never reach tree creation.
        var badRoot = await DockerAsync(cts.Token,
            "run", "--name", NewName("container"),
            "-e", "LDAP_ROOT=ou=nope,dc=example,dc=org",
            "-e", "LDAP_ADMIN_PASSWORD=t",
            image);
        Assert.NotEqual(0, badRoot.ExitCode);
        Assert.Contains("LDAP_ROOT must begin with a dc=, o= or c= component", badRoot.Output);
        Assert.DoesNotContain("Starting slapd", badRoot.Output);

        // DN-special characters in the admin username can never bind consistently (the
        // container composes cn={username},{root} verbatim): fail at validation.
        var badUser = await DockerAsync(cts.Token,
            "run", "--name", NewName("container"),
            "-e", "LDAP_ADMIN_USERNAME=Doe, John",
            "-e", "LDAP_ADMIN_PASSWORD=t",
            image);
        Assert.NotEqual(0, badUser.ExitCode);
        Assert.Contains("LDAP_ADMIN_USERNAME must not contain DN special characters", badUser.Output);
        Assert.DoesNotContain("Starting slapd", badUser.Output);
    }

    private async Task<string> BuildBundledImageAsync(CancellationToken cancellationToken)
    {
        var contextDir = OpenLdapResource.DefaultDockerContextPath;
        Assert.True(Directory.Exists(contextDir), $"bundled docker context not found at {contextDir}");

        const string tag = "aspire-openldap-inittests";
        var build = await DockerAsync(cancellationToken, "build", "-q", "-t", tag, contextDir);
        Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");
        return tag;
    }

    private string NewName(string kind)
    {
        var name = $"aspire-openldap-inittest-{kind}-{Guid.NewGuid():N}";
        (kind == "volume" ? _volumes : _containers).Add(name);
        return name;
    }

    private static void WidenPermissionsForContainer(string dir)
    {
        // The container runs as a non-root user and must traverse/read the bind-mounted seed
        // (see LargeSeedHealthGatingTests for the full rationale).
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
            RunQuiet("rm", "-f", container);
        }
        foreach (var volume in _volumes)
        {
            RunQuiet("volume", "rm", "-f", volume);
        }

        static void RunQuiet(params string[] args)
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
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}

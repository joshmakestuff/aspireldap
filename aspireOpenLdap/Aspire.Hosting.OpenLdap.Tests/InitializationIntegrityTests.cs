using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
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
[Trait("Category", "Integration")]
public class InitializationIntegrityTests : IDisposable
{
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];

    [Fact]
    public async Task Failed_Seed_Fails_Loudly_And_Restart_Over_Partial_Data_Is_Refused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);

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
            DockerCli.WidenPermissionsForContainer(seedDir);

            var name = NewName("container");
            var volume = NewName("volume");
            const string password = "test-admin-password";

            var first = await DockerCli.RunAsync(cts.Token,
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

            var restart = await DockerCli.RunAsync(cts.Token, "start", "-a", name);

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

    [Fact]
    public async Task Admin_Password_Bytes_Are_Preserved_Exactly()
    {
        // One composite password covering every byte-mangling class the printf fix guards:
        // leading/trailing and interior whitespace runs, glob characters, both quote kinds,
        // a semicolon, and non-ASCII — one container start instead of three.
        const string password = "  lead  glob*?[a-z]  quo\"te'semi;Ünïcode  ";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);

        var name = NewName("container");
        _ = await DockerCli.RunAsync(cts.Token,
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
            whoami = await DockerCli.RunAsync(cts.Token,
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
        var image = await BundledImage.GetAsync(cts.Token);

        var name = NewName("container");
        const string password = "test-admin-password";
        _ = await DockerCli.RunAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", "LDAP_ROOT=c=US",
            "-e", $"LDAP_ADMIN_PASSWORD={password}",
            image);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        DockerResult search;
        do
        {
            search = await DockerCli.RunAsync(cts.Token,
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
    public async Task Seeded_User_Password_Is_Hashed_At_Rest_And_Binds_With_Cleartext()
    {
        // F05: the typed seed generator stores userPassword as {SSHA}. Prove end-to-end that
        // (a) the seeded user can still bind with the original cleartext password and
        // (b) the directory holds only the hash at rest.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);

        const string userPassword = "user-s3cret!";
        var resource = new OpenLdapResource(
            "ldap", "dc=example,dc=org", "admin",
            new ParameterResource("pw", _ => "unused", secret: true));
        var model = new LdapSeedModel();
        model.Users.Add(new SeedUserEntry("user01", userPassword, null, "User One", "One", null));
        var ldif = LdapSeedLdifGenerator.Generate(resource, model);

        var seedDir = Directory.CreateTempSubdirectory("aspire-openldap-hashseed-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(seedDir, "00-seed.ldif"), ldif, cts.Token);
            DockerCli.WidenPermissionsForContainer(seedDir);

            var name = NewName("container");
            _ = await DockerCli.RunAsync(cts.Token,
                "run", "-d", "--name", name,
                "-v", $"{seedDir}:/ldifs:ro",
                "-e", "LDAP_ADMIN_PASSWORD=test-admin-password",
                image);

            var deadline = DateTime.UtcNow.AddMinutes(5);
            DockerResult whoami;
            do
            {
                whoami = await DockerCli.RunAsync(cts.Token,
                    "exec", "-e", $"PROBE_PW={userPassword}", name,
                    "bash", "-c", "ldapwhoami -x -H ldapi:/// -D \"uid=user01,dc=example,dc=org\" -w \"$PROBE_PW\"");
                if (whoami.ExitCode == 0)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            } while (DateTime.UtcNow < deadline);

            Assert.True(whoami.ExitCode == 0, $"seeded user bind failed: {whoami.Output}");
            Assert.Contains("dn:uid=user01,dc=example,dc=org", whoami.Output);

            // At rest, only the salted hash: slapcat must show {SSHA} and never the cleartext.
            var slapcat = await DockerCli.RunAsync(cts.Token, "exec", name, "slapcat", "-b", "dc=example,dc=org");
            Assert.Equal(0, slapcat.ExitCode);
            // slapcat may emit the value plainly or base64-encoded ("e1NTSEF9" is base64 of
            // a "{SSHA}"-prefixed value); either way it must be the hash.
            Assert.True(
                slapcat.Output.Contains("userPassword:: e1NTSEF9", StringComparison.Ordinal)
                    || slapcat.Output.Contains("userPassword: {SSHA}", StringComparison.Ordinal),
                $"expected {{SSHA}}-hashed userPassword at rest, got: {slapcat.Output}");
            Assert.DoesNotContain(userPassword, slapcat.Output);
            Assert.DoesNotContain(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(userPassword)),
                slapcat.Output);
        }
        finally
        {
            Directory.Delete(seedDir, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_Dn_Inputs_Are_Rejected_Before_Bootstrap()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);

        // Unsupported root naming attribute: fail at validation, never reach tree creation.
        var badRoot = await DockerCli.RunAsync(cts.Token,
            "run", "--name", NewName("container"),
            "-e", "LDAP_ROOT=ou=nope,dc=example,dc=org",
            "-e", "LDAP_ADMIN_PASSWORD=t",
            image);
        Assert.NotEqual(0, badRoot.ExitCode);
        Assert.Contains("LDAP_ROOT must begin with a dc=, o= or c= component", badRoot.Output);
        Assert.DoesNotContain("Starting slapd", badRoot.Output);

        // DN-special characters in the admin username can never bind consistently (the
        // container composes cn={username},{root} verbatim): fail at validation.
        var badUser = await DockerCli.RunAsync(cts.Token,
            "run", "--name", NewName("container"),
            "-e", "LDAP_ADMIN_USERNAME=Doe, John",
            "-e", "LDAP_ADMIN_PASSWORD=t",
            image);
        Assert.NotEqual(0, badUser.ExitCode);
        Assert.Contains("LDAP_ADMIN_USERNAME must not contain DN special characters", badUser.Output);
        Assert.DoesNotContain("Starting slapd", badUser.Output);
    }

    private string NewName(string kind)
    {
        var name = $"aspire-openldap-inittest-{kind}-{Guid.NewGuid():N}";
        (kind == "volume" ? _volumes : _containers).Add(name);
        return name;
    }

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            DockerCli.BestEffort("rm", "-f", container);
        }
        foreach (var volume in _volumes)
        {
            DockerCli.BestEffort("volume", "rm", "-f", volume);
        }
    }
}

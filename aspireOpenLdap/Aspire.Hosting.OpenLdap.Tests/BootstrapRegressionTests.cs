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
    private readonly List<string> _volumes = [];

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

            // The deprecation signal must fire exactly when the alias is what resolved.
            if (expected == "alias-pw")
            {
                Assert.Contains("deprecated", run.Output);
            }
            else
            {
                Assert.DoesNotContain("deprecated", run.Output);
            }
        }
    }

    [Fact]
    public async Task Stale_Alias_File_Is_Ignored_When_Canonical_Is_Set()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        const string resolve =
            ". /opt/openldap/scripts/libopenldap.sh && eval \"$(ldap_env)\" && printf 'resolved=%s' \"$LDAP_ACCESSLOG_ADMIN_PASSWORD\"";

        // Migration path: the canonical setting was added while an obsolete alias _FILE
        // reference (whose secret is no longer mounted) is still present. The alias is not
        // the selected credential source, so the stale file must be ignored, not fatal.
        var migration = await DockerAsync(cts.Token,
            "run", "--rm",
            "-e", "LDAP_ACCESSLOG_ADMIN_PASSWORD=canonical-pw",
            "-e", "LDAP_ACCESSLOG_PASSWORD_FILE=/run/secrets/obsolete-missing",
            "--entrypoint", "bash", image, "-c", resolve);
        Assert.True(migration.ExitCode == 0, $"stale alias _FILE must not abort when canonical is set: {migration.Output}");
        Assert.Contains("resolved=canonical-pw", migration.Output);
        Assert.Contains("Ignoring LDAP_ACCESSLOG_PASSWORD", migration.Output);

        // But when the alias IS the selected source (no canonical), an unreadable file stays
        // fail-closed.
        var selected = await DockerAsync(cts.Token,
            "run", "--rm",
            "-e", "LDAP_ACCESSLOG_PASSWORD_FILE=/run/secrets/obsolete-missing",
            "--entrypoint", "bash", image, "-c", resolve);
        Assert.True(selected.ExitCode != 0, "an unreadable alias _FILE that is the selected source must fail closed");
        Assert.Contains("not readable", selected.Output);
    }

    [Theory]
    [InlineData("LDAP_ACCESSLOG_ADMIN_PASSWORD_FILE")]
    [InlineData("LDAP_ACCESSLOG_PASSWORD_FILE")]
    public async Task Accesslog_File_Secret_Feeds_The_Resolved_Password(string fileVariable)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // The secret file is created inside the container (a bind mount would work too, but
        // this keeps the test independent of host-path translation quirks).
        var run = await DockerAsync(cts.Token,
            "run", "--rm", "--entrypoint", "bash", image,
            "-c", $"printf '%s' 'file-secret-pw' > /tmp/secret && export {fileVariable}=/tmp/secret && " +
                  ". /opt/openldap/scripts/libopenldap.sh && eval \"$(ldap_env)\" && " +
                  "printf 'resolved=%s' \"$LDAP_ACCESSLOG_ADMIN_PASSWORD\"");
        Assert.True(run.ExitCode == 0, $"file-secret resolution run failed: {run.Output}");
        Assert.Contains("resolved=file-secret-pw", run.Output);
    }

    [Fact]
    public async Task File_Secret_Feeds_The_Admin_Password_End_To_End()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        const string secretPassword = "file-admin-pw-123";
        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name, "--entrypoint", "bash", image,
            "-c", $"printf '%s' '{secretPassword}' > /tmp/adminpw && " +
                  "export LDAP_ADMIN_PASSWORD_FILE=/tmp/adminpw && " +
                  "exec /opt/openldap/scripts/openldap/entrypoint.sh /opt/openldap/scripts/openldap/run.sh");
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", secretPassword, cts.Token);

        // The default must be dead — the secret really replaced it.
        var defaultBind = await DockerAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=admin,dc=example,dc=org", "-w", "adminpassword");
        Assert.True(defaultBind.ExitCode != 0, "the default admin password must not remain active");
    }

    [Fact]
    public async Task Unreadable_File_Secret_Refuses_To_Start()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // Fail-closed contract: an explicitly configured secret file that can't be read must
        // abort startup, not boot with the well-known default password.
        var run = await DockerAsync(cts.Token,
            "run", "--rm",
            "-e", "LDAP_ADMIN_PASSWORD_FILE=/run/secrets/nonexistent",
            image);
        Assert.True(run.ExitCode != 0, "an unreadable _FILE secret must abort startup");
        Assert.Contains("not readable", run.Output);
        Assert.Contains("refusing to fall back", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prehashed_Default_Tree_Password_Passes_Through()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // Generate a real {SSHA} hash with the image's own slappasswd, then seed with it.
        const string cleartext = "prehash-known-pw";
        var hashRun = await DockerAsync(cts.Token,
            "run", "--rm", "--entrypoint", "slappasswd", image, "-s", cleartext);
        Assert.True(hashRun.ExitCode == 0, $"slappasswd failed: {hashRun.Output}");
        var hash = hashRun.Output.Trim();
        Assert.StartsWith("{SSHA}", hash);

        var name = NewContainer();
        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_USERS=svc01",
            "-e", $"LDAP_PASSWORDS={hash}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

        // Stored verbatim (RFC 3112 scheme-prefix passthrough, matching the .NET seed path) —
        // NOT re-hashed into a hash of the literal hash text.
        var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", "dc=example,dc=org");
        var storedBase64 = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(hash));
        Assert.Contains($"userPassword:: {storedBase64}", slapcat.Output);

        var bind = await DockerAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=svc01,ou=users,dc=example,dc=org", "-w", cleartext);
        Assert.True(bind.ExitCode == 0, $"bind with the original cleartext failed: {bind.Output}");
    }

    [Fact]
    public async Task Custom_Ldifs_Load_And_Suppress_The_Default_Tree()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var ldifDir = Directory.CreateTempSubdirectory("aspire-openldap-customldif-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(ldifDir, "10-custom.ldif"),
                "dn: dc=example,dc=org\n" +
                "objectClass: dcObject\n" +
                "objectClass: organization\n" +
                "dc: example\n" +
                "o: example\n" +
                "\n" +
                "dn: ou=custom,dc=example,dc=org\n" +
                "objectClass: organizationalUnit\n" +
                "ou: custom\n", cts.Token);
            WidenPermissionsForContainer(ldifDir);

            var name = NewContainer();
            var run = await DockerAsync(cts.Token,
                "run", "-d", "--name", name,
                "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
                "-v", $"{ldifDir}:/ldifs:ro",
                image);
            Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
            await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

            var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", "dc=example,dc=org");
            // The mounted entries loaded, and the built-in user01/user02 tree was suppressed —
            // the documented "custom LDIFs replace the default tree" behavior.
            Assert.Contains("ou=custom", slapcat.Output);
            Assert.DoesNotContain("cn=user01", slapcat.Output);

            var logs = await DockerAsync(cts.Token, "logs", name);
            Assert.Contains("Ignoring LDAP_USERS", logs.Output);
        }
        finally
        {
            Directory.Delete(ldifDir, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_Volume_Restart_Preserves_Data()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();
        var volume = NewVolume();

        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-v", $"{volume}:/data/openldap",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

        var add = await DockerAsync(cts.Token,
            "exec", name, "bash", "-c",
            "printf 'dn: cn=persisted,ou=users,dc=example,dc=org\\nobjectClass: inetOrgPerson\\ncn: persisted\\nsn: marker\\n' > /tmp/e.ldif && " +
            $"ldapadd -x -H ldapi:/// -D cn=admin,dc=example,dc=org -w {AdminPassword} -f /tmp/e.ldif");
        Assert.True(add.ExitCode == 0, $"ldapadd failed: {add.Output}");

        _ = await DockerAsync(cts.Token, "stop", name);
        _ = await DockerAsync(cts.Token, "start", name);
        await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

        // The second boot must reuse the volume (no re-init) and still serve the entry.
        var search = await DockerAsync(cts.Token,
            "exec", name, "ldapsearch", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=admin,dc=example,dc=org", "-w", AdminPassword,
            "-b", "dc=example,dc=org", "(cn=persisted)", "dn");
        Assert.Contains("dn: cn=persisted,ou=users,dc=example,dc=org", search.Output);

        var logs = await DockerAsync(cts.Token, "logs", name);
        Assert.Contains("Using persisted data", logs.Output);
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
    public async Task Escaped_Semicolon_Root_Bootstraps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);
        var name = NewContainer();

        // '\;' is the RFC 4514-escaped form the model validation accepts (#35 rejects only
        // the raw unescaped ';'); it must bootstrap and decode to a literal ';' in the value.
        const string root = "o=Acme\\; Inc.";
        var run = await DockerAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", $"LDAP_ROOT={root}",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await WaitForLdapReadyAsync(name, $"cn=admin,{root}", AdminPassword, cts.Token);

        var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", root);
        Assert.True(slapcat.ExitCode == 0, $"slapcat failed: {slapcat.Output}");
        Assert.Contains("o: Acme; Inc.", slapcat.Output);
    }

    [Theory]
    [InlineData("LDAP_USER_OU=a\nb", "must not contain line breaks")]
    [InlineData("LDAP_SUFFIX=dc=x\ndc=y", "must not contain line breaks")]
    [InlineData("LDAP_TLS_VERIFY_CLIENTS=bogus", "must be one of")]
    public async Task Invalid_Config_Env_Is_Rejected_Before_Bootstrap(string envAssignment, string expectedFragment)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // Line-break-bearing values would smuggle extra LDIF lines into a privileged
        // cn=config apply; enum-invalid TLS client verification would misconfigure slapd.
        // All must die at validation, before any LDIF is generated.
        var run = await DockerAsync(cts.Token,
            "run", "--rm", "-e", envAssignment, image);
        Assert.True(run.ExitCode != 0, $"'{envAssignment}' must fail container validation");
        Assert.Contains(expectedFragment, run.Output);
    }

    [Fact]
    public async Task Unescaper_Preserves_A_Dangling_Backslash()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        // A dangling trailing backslash is invalid RFC 4514; it must stay literal (so slapd
        // rejects the DN loudly) rather than being silently swallowed.
        var run = await DockerAsync(cts.Token,
            "run", "--rm", "--entrypoint", "bash", image,
            "-c", ". /opt/openldap/scripts/libopenldap.sh && printf '[%s]' \"$(ldap_unescape_rdn_value 'Acme\\')\"");
        Assert.True(run.ExitCode == 0, $"unescape run failed: {run.Output}");
        Assert.Contains("[Acme\\]", run.Output);
    }

    [Theory]
    [InlineData("LDAP_SYNCPROV_CHECKPOINT=7 7", "7 7")]     // canonical spelling
    [InlineData("LDAP_SYNCPROV_CHECKPPOINT=9 9", "9 9")]    // legacy double-P alias
    [InlineData("LDAP_ENABLE_SYNCPROV=no", "100 10")]       // neither → default
    public async Task Syncprov_Checkpoint_Spelling_Resolves(string envAssignment, string expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var run = await DockerAsync(cts.Token,
            "run", "--rm", "-e", envAssignment, "--entrypoint", "bash", image,
            "-c", ". /opt/openldap/scripts/libopenldap.sh && eval \"$(ldap_env)\" && printf 'checkpoint=[%s]' \"$LDAP_SYNCPROV_CHECKPOINT\"");
        Assert.True(run.ExitCode == 0, $"resolution run failed: {run.Output}");
        Assert.Contains($"checkpoint=[{expected}]", run.Output);
    }

    [Fact]
    public async Task Custom_Ldif_Continue_On_Error_Skips_Rejects_But_Default_Fails_Loud()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BuildBundledImageAsync(cts.Token);

        var ldifDir = Directory.CreateTempSubdirectory("aspire-openldap-continueonerror-").FullName;
        try
        {
            // Middle entry is rejected (its parent dc=missing does not exist); the last entry
            // is valid and must load when continue-on-error is enabled.
            await File.WriteAllTextAsync(Path.Combine(ldifDir, "10-mixed.ldif"),
                "dn: dc=example,dc=org\n" +
                "objectClass: dcObject\n" +
                "objectClass: organization\n" +
                "dc: example\n" +
                "o: example\n" +
                "\n" +
                "dn: ou=bad,dc=missing,dc=org\n" +
                "objectClass: organizationalUnit\n" +
                "ou: bad\n" +
                "\n" +
                "dn: ou=good,dc=example,dc=org\n" +
                "objectClass: organizationalUnit\n" +
                "ou: good\n", cts.Token);
            WidenPermissionsForContainer(ldifDir);

            // Default (fail-loud): a rejected entry aborts initialization.
            var strict = await DockerAsync(cts.Token,
                "run", "--rm",
                "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
                "-v", $"{ldifDir}:/ldifs:ro",
                image);
            Assert.True(strict.ExitCode != 0, "a rejected custom-LDIF entry must abort by default");

            // Opt-in continue-on-error: rejects are skipped, later entries still load.
            var name = NewContainer();
            var lenient = await DockerAsync(cts.Token,
                "run", "-d", "--name", name,
                "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
                "-e", "LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR=yes",
                "-v", $"{ldifDir}:/ldifs:ro",
                image);
            Assert.True(lenient.ExitCode == 0, $"docker run failed: {lenient.Output}");
            await WaitForLdapReadyAsync(name, "cn=admin,dc=example,dc=org", AdminPassword, cts.Token);

            var slapcat = await DockerAsync(cts.Token, "exec", name, "slapcat", "-b", "dc=example,dc=org");
            Assert.Contains("ou=good", slapcat.Output);
            Assert.DoesNotContain("ou=bad", slapcat.Output);
        }
        finally
        {
            Directory.Delete(ldifDir, recursive: true);
        }
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

    private string NewVolume()
    {
        var name = $"aspire-openldap-bootstraptest-vol-{Guid.NewGuid():N}";
        _volumes.Add(name);
        return name;
    }

    private static void WidenPermissionsForContainer(string dir)
    {
        // The container runs as a non-root user and must traverse/read the bind-mounted dir.
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
            BestEffortDocker("rm", "-f", container);
        }
        // Volumes after containers: a volume can't be removed while a container holds it.
        foreach (var volume in _volumes)
        {
            BestEffortDocker("volume", "rm", "-f", volume);
        }
    }

    private static void BestEffortDocker(params string[] args)
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
            // Best-effort cleanup.
        }
    }
}

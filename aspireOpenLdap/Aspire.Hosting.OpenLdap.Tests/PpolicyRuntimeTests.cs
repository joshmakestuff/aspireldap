using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// One container running the ppolicy bootstrap with both sub-toggles on, shared by every
/// witness in <see cref="PpolicyRuntimeTests"/>. The witnesses used to be welded into a single
/// mega-fact purely to avoid paying a second container start; a fixture buys the same saving
/// without hiding later assertions behind an earlier failure.
/// </summary>
public sealed class PpolicyContainerFixture : IAsyncLifetime
{
    public const string AdminPassword = "ppolicy-runtime-pw";
    public const string AdminDn = "cn=admin,dc=example,dc=org";

    private readonly DockerScope _scope = DockerCli.NewScope("ppolicytest");

    /// <summary>Name of the running container. Empty until <see cref="InitializeAsync"/> runs.</summary>
    public string Container { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);

        Container = _scope.NewContainer();
        var run = await DockerCli.RunAsync(cts.Token,
            "run", "-d", "--name", Container,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_CONFIGURE_PPOLICY=yes",
            "-e", "LDAP_PPOLICY_HASH_CLEARTEXT=yes",
            "-e", "LDAP_PPOLICY_USE_LOCKOUT=yes",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await DockerCli.WaitForLdapReadyAsync(Container, AdminDn, AdminPassword, cts.Token);
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Docker-driven runtime witnesses for the ppolicy bootstrap path (issue #38):
/// <c>ldap_configure_ppolicy</c> performs privileged cn=config applies that previously had no
/// container-level coverage. The facts assert both that the configuration landed in cn=config
/// AND that the overlay actually changes server behavior (password hashing, lockout).
/// (The sibling syncprov bootstrap path was removed from the image instead of tested — see #53.)
/// </summary>
/// <remarks>
/// Each behavioral witness touches a different default-tree account (user01 for hashing,
/// user02 for lockout) and each sets up whatever it needs, so the facts are order-independent
/// over the shared container.
/// </remarks>
[Trait("Category", "Integration")]
public class PpolicyRuntimeTests(PpolicyContainerFixture fixture) : IClassFixture<PpolicyContainerFixture>
{
    private const string HashUserDn = "cn=user01,ou=users,dc=example,dc=org";
    private const string LockoutUserDn = "cn=user02,ou=users,dc=example,dc=org";

    [Fact]
    public async Task Overlay_And_Both_Toggles_Land_In_Config()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // The overlay entry and BOTH sub-toggles (each a separate privileged ldapmodify in
        // ldap_configure_ppolicy) must have landed in cn=config. Attribute names come back in
        // slapd's canonical casing, not the spelling the bootstrap wrote — compare
        // case-insensitively (the syncprov coverage hit exactly this before its removal).
        var config = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "ldapsearch", "-Q", "-Y", "EXTERNAL", "-H", "ldapi:///",
            "-b", OpenLdapResource.MdbDatabaseDn, "(objectClass=olcPPolicyConfig)");
        Assert.True(config.ExitCode == 0, $"cn=config ppolicy search failed: {config.Output}");
        Assert.Contains("olcPPolicyHashCleartext: TRUE", config.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("olcPPolicyUseLockout: TRUE", config.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HashCleartext_Stores_A_Modified_Password_Hashed_And_Still_Binds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // hash_cleartext: a cleartext userPassword modify must be stored hashed, yet still
        // verify on bind. Credentials are single-quoted so a future password/DN with
        // shell-special characters cannot word-split or expand.
        var replace = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "bash", "-c",
            $"printf 'dn: {HashUserDn}\\nchangetype: modify\\nreplace: userPassword\\nuserPassword: cleartext-pw-42\\n' | " +
            $"ldapmodify -x -H ldapi:/// -D '{PpolicyContainerFixture.AdminDn}' -w '{PpolicyContainerFixture.AdminPassword}'");
        Assert.True(replace.ExitCode == 0, $"cleartext password modify failed: {replace.Output}");

        // "e1NTSEF9" is base64("{SSHA}"). RFC 2849 does not force base64 for a printable
        // value, so accept either encoding the client may emit (same hedge as
        // InitializationIntegrityTests).
        var stored = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "ldapsearch", "-x", "-H", "ldapi:///",
            "-D", PpolicyContainerFixture.AdminDn, "-w", PpolicyContainerFixture.AdminPassword,
            "-b", HashUserDn, "-s", "base", "userPassword");
        Assert.True(stored.ExitCode == 0, $"userPassword read-back failed: {stored.Output}");
        Assert.True(
            stored.Output.Contains("userPassword:: e1NTSEF9", StringComparison.Ordinal)
            || stored.Output.Contains("userPassword: {SSHA}", StringComparison.Ordinal),
            $"replaced password must be stored {{SSHA}}-hashed: {stored.Output}");

        var hashedBind = await BindAsync(HashUserDn, "cleartext-pw-42", cts.Token);
        Assert.True(hashedBind.ExitCode == 0, $"bind against the overlay-hashed password failed: {hashedBind.Output}");
    }

    [Fact]
    public async Task UseLockout_Locks_The_Account_After_MaxFailure_Failures()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // The bootstrap only installs the overlay; give it a default policy (lock after 2
        // failures) the way an operator would, then prove the overlay enforces it. Two
        // separate applies so a failure names the step that broke.
        var policyEntries = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "bash", "-c",
            "printf 'dn: ou=policies,dc=example,dc=org\\nobjectClass: organizationalUnit\\nou: policies\\n\\n" +
            "dn: cn=default,ou=policies,dc=example,dc=org\\nobjectClass: organizationalRole\\nobjectClass: pwdPolicy\\ncn: default\\npwdAttribute: userPassword\\npwdLockout: TRUE\\npwdMaxFailure: 2\\n' | " +
            $"ldapadd -x -H ldapi:/// -D '{PpolicyContainerFixture.AdminDn}' -w '{PpolicyContainerFixture.AdminPassword}'");
        Assert.True(policyEntries.ExitCode == 0, $"pwdPolicy entry add failed: {policyEntries.Output}");

        var policyDefault = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "bash", "-c",
            "printf 'dn: olcOverlay={0}ppolicy," + OpenLdapResource.MdbDatabaseDn +
            "\\nchangetype: modify\\nadd: olcPPolicyDefault\\nolcPPolicyDefault: cn=default,ou=policies,dc=example,dc=org\\n' | " +
            "ldapmodify -Q -Y EXTERNAL -H ldapi:///");
        Assert.True(policyDefault.ExitCode == 0, $"olcPPolicyDefault modify failed: {policyDefault.Output}");

        // Sanity: the account binds fine before any failures.
        var before = await BindAsync(LockoutUserDn, "bitnami2", cts.Token);
        Assert.True(before.ExitCode == 0, $"pre-lockout bind must succeed: {before.Output}");

        // Each failed attempt must be an LDAP credential rejection — a dead container or a
        // docker exec error also exits non-zero and would pass a bare exit-code check
        // vacuously, witnessing nothing about ppolicy.
        for (var i = 0; i < 2; i++)
        {
            var failed = await BindAsync(LockoutUserDn, "wrong-password", cts.Token);
            Assert.True(failed.ExitCode != 0, $"a wrong-password bind must fail: {failed.Output}");
            Assert.Contains("Invalid credentials", failed.Output);
        }

        // After pwdMaxFailure failures the CORRECT password must now be rejected, and the
        // overlay must have stamped the lockout marker.
        var locked = await BindAsync(LockoutUserDn, "bitnami2", cts.Token);
        Assert.True(locked.ExitCode != 0, $"the locked account must reject its correct password: {locked.Output}");
        Assert.Contains("Invalid credentials", locked.Output);

        var marker = await DockerCli.RunAsync(cts.Token,
            "exec", fixture.Container, "ldapsearch", "-x", "-H", "ldapi:///",
            "-D", PpolicyContainerFixture.AdminDn, "-w", PpolicyContainerFixture.AdminPassword,
            "-b", LockoutUserDn, "-s", "base", "pwdAccountLockedTime");
        Assert.True(marker.ExitCode == 0, $"lockout marker read-back failed: {marker.Output}");
        Assert.Contains("pwdAccountLockedTime:", marker.Output);
    }

    private Task<DockerResult> BindAsync(string bindDn, string password, CancellationToken cancellationToken)
        => DockerCli.RunAsync(cancellationToken,
            "exec", fixture.Container, "ldapwhoami", "-x", "-H", DockerCli.InContainerLdapUri,
            "-D", bindDn, "-w", password);
}

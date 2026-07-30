using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Docker-driven runtime witnesses for the ppolicy bootstrap path (issue #38):
/// <c>ldap_configure_ppolicy</c> performs privileged cn=config applies that previously had no
/// container-level coverage. The test asserts both that the configuration landed in cn=config
/// AND that the overlay actually changes server behavior (password hashing, lockout).
/// (The sibling syncprov bootstrap path was removed from the image instead of tested — see #53.)
/// </summary>
[Trait("Category", "Integration")]
public class PpolicyRuntimeTests : IDisposable
{
    private const string AdminPassword = "ppolicy-runtime-pw";
    private const string AdminDn = "cn=admin,dc=example,dc=org";

    private readonly List<string> _containers = [];

    [Fact]
    public async Task Ppolicy_Overlay_Applies_With_Toggles_And_Enforces_Hashing_And_Lockout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var image = await BundledImage.GetAsync(cts.Token);
        var name = NewContainer();

        var run = await DockerCli.RunAsync(cts.Token,
            "run", "-d", "--name", name,
            "-e", $"LDAP_ADMIN_PASSWORD={AdminPassword}",
            "-e", "LDAP_CONFIGURE_PPOLICY=yes",
            "-e", "LDAP_PPOLICY_HASH_CLEARTEXT=yes",
            "-e", "LDAP_PPOLICY_USE_LOCKOUT=yes",
            image);
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        await DockerCli.WaitForLdapReadyAsync(name, AdminDn, AdminPassword, cts.Token);

        // The overlay entry and BOTH sub-toggles (each a separate privileged ldapmodify in
        // ldap_configure_ppolicy) must have landed in cn=config. Attribute names come back in
        // slapd's canonical casing, not the spelling the bootstrap wrote — compare
        // case-insensitively (the syncprov coverage hit exactly this before its removal).
        var config = await DockerCli.RunAsync(cts.Token,
            "exec", name, "ldapsearch", "-Q", "-Y", "EXTERNAL", "-H", "ldapi:///",
            "-b", "olcDatabase={2}mdb,cn=config", "(objectClass=olcPPolicyConfig)");
        Assert.True(config.ExitCode == 0, $"cn=config ppolicy search failed: {config.Output}");
        Assert.Contains("olcPPolicyHashCleartext: TRUE", config.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("olcPPolicyUseLockout: TRUE", config.Output, StringComparison.OrdinalIgnoreCase);

        // Behavioral witness 1 — hash_cleartext: a cleartext userPassword modify must be
        // stored hashed, yet still verify on bind. Credentials are single-quoted so a future
        // password/DN with shell-special characters cannot word-split or expand.
        var replace = await DockerCli.RunAsync(cts.Token,
            "exec", name, "bash", "-c",
            "printf 'dn: cn=user01,ou=users,dc=example,dc=org\\nchangetype: modify\\nreplace: userPassword\\nuserPassword: cleartext-pw-42\\n' | " +
            $"ldapmodify -x -H ldapi:/// -D '{AdminDn}' -w '{AdminPassword}'");
        Assert.True(replace.ExitCode == 0, $"cleartext password modify failed: {replace.Output}");

        // "e1NTSEF9" is base64("{SSHA}"). RFC 2849 does not force base64 for a printable
        // value, so accept either encoding the client may emit (same hedge as
        // InitializationIntegrityTests).
        var stored = await DockerCli.RunAsync(cts.Token,
            "exec", name, "ldapsearch", "-x", "-H", "ldapi:///",
            "-D", AdminDn, "-w", AdminPassword,
            "-b", "cn=user01,ou=users,dc=example,dc=org", "-s", "base", "userPassword");
        Assert.True(stored.ExitCode == 0, $"userPassword read-back failed: {stored.Output}");
        Assert.True(
            stored.Output.Contains("userPassword:: e1NTSEF9", StringComparison.Ordinal)
            || stored.Output.Contains("userPassword: {SSHA}", StringComparison.Ordinal),
            $"replaced password must be stored {{SSHA}}-hashed: {stored.Output}");

        var hashedBind = await DockerCli.RunAsync(cts.Token,
            "exec", name, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=user01,ou=users,dc=example,dc=org", "-w", "cleartext-pw-42");
        Assert.True(hashedBind.ExitCode == 0, $"bind against the overlay-hashed password failed: {hashedBind.Output}");

        // Behavioral witness 2 — lockout. The bootstrap only installs the overlay; give it a
        // default policy (lock after 2 failures) the way an operator would, then prove the
        // overlay enforces it. Two separate applies so a failure names the step that broke.
        var policyEntries = await DockerCli.RunAsync(cts.Token,
            "exec", name, "bash", "-c",
            "printf 'dn: ou=policies,dc=example,dc=org\\nobjectClass: organizationalUnit\\nou: policies\\n\\n" +
            "dn: cn=default,ou=policies,dc=example,dc=org\\nobjectClass: organizationalRole\\nobjectClass: pwdPolicy\\ncn: default\\npwdAttribute: userPassword\\npwdLockout: TRUE\\npwdMaxFailure: 2\\n' | " +
            $"ldapadd -x -H ldapi:/// -D '{AdminDn}' -w '{AdminPassword}'");
        Assert.True(policyEntries.ExitCode == 0, $"pwdPolicy entry add failed: {policyEntries.Output}");

        var policyDefault = await DockerCli.RunAsync(cts.Token,
            "exec", name, "bash", "-c",
            "printf 'dn: olcOverlay={0}ppolicy,olcDatabase={2}mdb,cn=config\\nchangetype: modify\\nadd: olcPPolicyDefault\\nolcPPolicyDefault: cn=default,ou=policies,dc=example,dc=org\\n' | " +
            "ldapmodify -Q -Y EXTERNAL -H ldapi:///");
        Assert.True(policyDefault.ExitCode == 0, $"olcPPolicyDefault modify failed: {policyDefault.Output}");

        // Sanity: the account binds fine before any failures.
        var before = await UserBindAsync(name, "bitnami2", cts.Token);
        Assert.True(before.ExitCode == 0, $"pre-lockout bind must succeed: {before.Output}");

        // Each failed attempt must be an LDAP credential rejection — a dead container or a
        // docker exec error also exits non-zero and would pass a bare exit-code check
        // vacuously, witnessing nothing about ppolicy.
        for (var i = 0; i < 2; i++)
        {
            var failed = await UserBindAsync(name, "wrong-password", cts.Token);
            Assert.True(failed.ExitCode != 0, $"a wrong-password bind must fail: {failed.Output}");
            Assert.Contains("Invalid credentials", failed.Output);
        }

        // After pwdMaxFailure failures the CORRECT password must now be rejected, and the
        // overlay must have stamped the lockout marker.
        var locked = await UserBindAsync(name, "bitnami2", cts.Token);
        Assert.True(locked.ExitCode != 0, $"the locked account must reject its correct password: {locked.Output}");
        Assert.Contains("Invalid credentials", locked.Output);

        var marker = await DockerCli.RunAsync(cts.Token,
            "exec", name, "ldapsearch", "-x", "-H", "ldapi:///",
            "-D", AdminDn, "-w", AdminPassword,
            "-b", "cn=user02,ou=users,dc=example,dc=org", "-s", "base", "pwdAccountLockedTime");
        Assert.True(marker.ExitCode == 0, $"lockout marker read-back failed: {marker.Output}");
        Assert.Contains("pwdAccountLockedTime:", marker.Output);
    }

    private static Task<DockerResult> UserBindAsync(string container, string password, CancellationToken cancellationToken)
        => DockerCli.RunAsync(cancellationToken,
            "exec", container, "ldapwhoami", "-x", "-H", "ldap://localhost:1389",
            "-D", "cn=user02,ou=users,dc=example,dc=org", "-w", password);

    private string NewContainer()
    {
        var name = $"aspire-openldap-ppolicytest-{Guid.NewGuid():N}";
        _containers.Add(name);
        return name;
    }

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            DockerCli.BestEffort("rm", "-f", container);
        }
    }
}

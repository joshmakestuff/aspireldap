using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireOpenLdap.TestAppHost;
using LdifDotNet;

var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("openldap");

// Exactly one scenario runs, selected by --OpenLdap:Scenario=<name>. An unknown name is a
// test-authoring bug, so fail loudly here instead of silently running the default scenario
// and letting the caller's assertions report something unrelated.
var scenario = builder.Configuration[TestAppHostScenarios.ScenarioKey];
if (string.IsNullOrWhiteSpace(scenario))
{
    scenario = TestAppHostScenarios.Default;
}

switch (scenario)
{
    case TestAppHostScenarios.Default:
        // Plain AddOpenLdap: the default smoke scenario.
        break;

    case TestAppHostScenarios.LargeSeed:
    {
        var seedDir = builder.Configuration[TestAppHostScenarios.SeedDirKey];
        if (string.IsNullOrWhiteSpace(seedDir))
        {
            throw new InvalidOperationException(
                $"Scenario '{TestAppHostScenarios.LargeSeed}' requires --{TestAppHostScenarios.SeedDirKey}=<path>.");
        }
        ldap.WithSeedData(seedDir);
        break;
    }

    case TestAppHostScenarios.Tls:
        // Generated CA + required LDAPS, so the health check and client connect through the
        // real TLS trust paths.
        ldap.WithTls().WithRequiredTls();
        break;

    case TestAppHostScenarios.TlsOptional:
        // TLS enabled but NOT required: LDAPS is served alongside plain LDAP — the mode
        // WithTls() alone configures.
        ldap.WithTls();
        break;

    case TestAppHostScenarios.ConfigWitness:
    {
        // Overlay + access-control scenario: a memberof overlay over a typed seed, an extra
        // raw-record subtree, and a complete access policy, so integration tests can witness
        // the privileged cn=config apply paths against a live slapd (issue #38).
        // The access rules define the FULL policy — verified empirically against the bundled
        // image: the mdb database ships with no olcAccess, and the moment one rule exists
        // slapd's implicit final rule is "to * by * none" (unmatched targets AND rules
        // exhausted via "by * break" are both denied, including the auth access simple binds
        // need on userPassword). Rule order matters: the attrs=userPassword auth rule comes
        // FIRST so binds keep working even for entries inside the restricted subtree, whose
        // own rule would otherwise shadow it.
        const string baseDn = "dc=example,dc=org";
        ldap.WithOrganizationalUnit("users")
            .WithOrganizationalUnit("groups")
            .WithOrganizationalUnit("secret")
            .WithUser("svc", "svc-password", ou: "users")
            .WithUser("alice", "alice-password", ou: "users")
            .WithGroup("devs", ["svc", "alice"], ou: "groups")
            .WithOverlay(OpenLdapOverlay.MemberOf("groupOfNames", "member"))
            .WithSeedRecords(new LdifContentRecord($"cn=classified,ou=secret,{baseDn}",
                new LdifAttribute("objectClass", "organizationalRole"),
                new LdifAttribute("cn", "classified")))
            .WithAccessControl(
                """to attrs=userPassword by anonymous auth by self write by * none""",
                $"""to dn.subtree="ou=secret,{baseDn}" by dn.exact="uid=svc,ou=users,{baseDn}" read by * none""",
                """to * by users read by * none""");
        break;
    }

    case TestAppHostScenarios.FakeData:
        // Generated people/groups plus one bindable typed user, so integration tests can
        // witness the deferred fake-data materialization (OnBeforeResourceStarted hook)
        // against a live slapd.
        ldap.WithFakeDirectory(people: 5, groups: 2, seed: 1)
            .WithUser("svc", "svc-password", ou: "people");
        break;

    default:
        throw new InvalidOperationException($"Unknown --{TestAppHostScenarios.ScenarioKey} value '{scenario}'.");
}

builder.Build().Run();

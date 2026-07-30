using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using LdifDotNet;

var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("openldap");

// Optional large-seed scenario, driven by tests via --OpenLdap:SeedDir=<path>.
// Left unset for the default smoke test, which exercises a plain AddOpenLdap.
var seedDir = builder.Configuration["OpenLdap:SeedDir"];
if (!string.IsNullOrWhiteSpace(seedDir))
{
    ldap.WithSeedData(seedDir);
}

// Optional TLS scenario, driven by tests via --OpenLdap:Tls=true: generated CA + required
// LDAPS, so the health check and client connect through the real TLS trust paths.
if (string.Equals(builder.Configuration["OpenLdap:Tls"], "true", StringComparison.OrdinalIgnoreCase))
{
    ldap.WithTls().WithRequiredTls();
}

// TLS enabled but NOT required (--OpenLdap:TlsOptional=true): LDAPS is served alongside
// plain LDAP — the mode WithTls() alone configures.
if (string.Equals(builder.Configuration["OpenLdap:TlsOptional"], "true", StringComparison.OrdinalIgnoreCase))
{
    ldap.WithTls();
}

// Overlay + access-control scenario (--OpenLdap:ConfigWitness=true): a memberof overlay over
// a typed seed, an extra raw-record subtree, and a complete access policy, so integration
// tests can witness the privileged cn=config apply paths against a live slapd (issue #38).
// The access rules define the FULL policy — verified empirically against the bundled image:
// the mdb database ships with no olcAccess, and the moment one rule exists slapd's implicit
// final rule is "to * by * none" (unmatched targets AND rules exhausted via "by * break" are
// both denied, including the auth access simple binds need on userPassword). Rule order
// matters: the attrs=userPassword auth rule comes FIRST so binds keep working even for
// entries inside the restricted subtree, whose own rule would otherwise shadow it.
if (string.Equals(builder.Configuration["OpenLdap:ConfigWitness"], "true", StringComparison.OrdinalIgnoreCase))
{
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
}

builder.Build().Run();

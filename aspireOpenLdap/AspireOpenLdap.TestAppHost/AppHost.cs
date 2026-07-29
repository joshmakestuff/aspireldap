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
// The access rules define the FULL policy: once any olcAccess exists on the database, slapd's
// implicit default flips from "read for all" to "deny unmatched" — so binds need the
// userPassword auth rule and reads need the final catch-all.
if (string.Equals(builder.Configuration["OpenLdap:ConfigWitness"], "true", StringComparison.OrdinalIgnoreCase))
{
    ldap.WithOrganizationalUnit("users")
        .WithOrganizationalUnit("groups")
        .WithOrganizationalUnit("secret")
        .WithUser("svc", "svc-password", ou: "users")
        .WithUser("alice", "alice-password", ou: "users")
        .WithGroup("devs", ["svc", "alice"], ou: "groups")
        .WithOverlay(OpenLdapOverlay.MemberOf("groupOfNames", "member"))
        .WithSeedRecords(new LdifContentRecord("cn=classified,ou=secret,dc=example,dc=org",
            new LdifAttribute("objectClass", "organizationalRole"),
            new LdifAttribute("cn", "classified")))
        .WithAccessControl(
            """to dn.subtree="ou=secret,dc=example,dc=org" by dn.exact="uid=svc,ou=users,dc=example,dc=org" read by * none""",
            """to attrs=userPassword by anonymous auth by self write by * none""",
            """to * by users read by * none""");
}

builder.Build().Run();

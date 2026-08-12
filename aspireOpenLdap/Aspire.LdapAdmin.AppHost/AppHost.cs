using Aspire.Hosting;

// Test AppHost for the admin: an OpenLDAP resource seeded with a small directory the service
// integration tests browse, search, read and rewrite, plus the admin web host wired to it by
// connection string. The WithLdapAdmin() delivery model lands in phase 3 (#78); until then the
// admin is referenced here as an ordinary project resource.
var builder = DistributedApplication.CreateBuilder(args);

const string baseDn = "dc=example,dc=org";

var ldap = builder.AddOpenLdap("openldap")
    // Typed tree: two OUs the tests own outright. ou=people carries the bindable accounts —
    // the admin service itself binds with the AppHost's admin credentials, but the ACL tests
    // need a second, unprivileged identity, and only WithUser produces one (generated fake
    // people have no userPassword). Their passwords are fixed rather than generated so a test
    // can build a connection string for them without the AppHost handing one over.
    .WithOrganizationalUnit("people")
    .WithOrganizationalUnit("groups")
    .WithUser("alice", "alice-password", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org")
    .WithUser("bob", "bob-password", ou: "people", cn: "Bob Brown", sn: "Brown", mail: "bob@example.org")
    // The sizelimit witness (#118): a non-root account that CAN delete, but only under
    // ou=bulk-del, and whose searches slapd caps at 10 entries — the rootdn is exempt from
    // both limits and ACLs, so only this bind can observe the sweep-past-sizelimit behavior.
    .WithUser("svc-sweeper", "sweeper-password", ou: "people", cn: "Sweep Service", sn: "Service")
    .WithGroup("staff", ["alice", "bob"], ou: "groups")
    // Fake people under their own auto-declared ou=directory: a node with more children than
    // the truncation tests' limits, seeded so the count is the same on every run.
    .WithFakePeople(30, ou: "directory", seed: 81)
    // The handoff-scale large container (#115): far past the tree's Cap of 48, so the rail's
    // "search this container" row renders against a real directory (#121's live witness).
    // Seeded for run-to-run determinism like ou=directory.
    .WithFakePeople(2000, ou: "hosts", seed: 82)
    // The COMPLETE access policy (the moment any olcAccess rule exists, slapd's implicit
    // final rule is "to * by * none" — see WithAccessControl docs). It must reproduce the
    // semantics AccessAndPasswordTests witnessed under the built-in default: binds work
    // (anonymous auth on userPassword), self may change its password, authenticated users
    // read everything, and non-admin writes elsewhere are refused.
    .WithAccessControl(
        // FIRST, so no subtree rule can shadow the auth access binds need.
        """to attrs=userPassword by anonymous auth by self write by * none""",
        // Deleting ou=bulk-del itself needs write on the PARENT's children pseudo-attribute;
        // dn.subtree below cannot grant that, ou=people sits outside it.
        $"""to dn.exact="ou=people,{baseDn}" attrs=children by dn.exact="uid=svc-sweeper,ou=people,{baseDn}" write by * none""",
        // A rule without attrs covers all attributes including the entry/children
        // pseudo-attributes, which is what add/delete require.
        $"""to dn.subtree="ou=bulk-del,ou=people,{baseDn}" by dn.exact="uid=svc-sweeper,ou=people,{baseDn}" write by users read by * none""",
        """to * by users read by * none""")
    // size=10 makes the #118 scenario cheap: ~25 children under ou=bulk-del already exceed
    // the limit svc-sweeper's listings run under.
    .WithLimits($"""dn.exact="uid=svc-sweeper,ou=people,{baseDn}" size=10""");

builder.AddProject<Projects.Aspire_LdapAdmin_Web>("ldapadmin")
    // Same configuration contract WithLdapAdmin() sets on the packaged container: the admin
    // host reads LdapAdmin:ConnectionName and binds with that connection string.
    .WithEnvironment("LdapAdmin__ConnectionName", "openldap")
    // The options contract (#98), mirrored for dev runs: any LdapAdmin__* value set on the
    // AppHost's own environment (LdapAdmin__Theme=Dark aspire start ...) passes through to
    // the admin host; unset values fall back to the admin host's own defaults.
    .WithEnvironment(context =>
    {
        foreach (var key in (string[])["Theme", "DefaultSearchLimit", "DefaultSortOrder", "AttributeValueDisplayCap"])
        {
            if (builder.Configuration[$"LdapAdmin:{key}"] is { Length: > 0 } value)
            {
                context.EnvironmentVariables[$"LdapAdmin__{key}"] = value;
            }
        }
    })
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

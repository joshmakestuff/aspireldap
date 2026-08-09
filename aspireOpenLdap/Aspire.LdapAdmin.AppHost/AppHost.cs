using Aspire.Hosting;

// Test AppHost for the admin: an OpenLDAP resource seeded with a small directory the service
// integration tests browse, search, read and rewrite, plus the admin web host wired to it by
// connection string. The WithLdapAdmin() delivery model lands in phase 3 (#78); until then the
// admin is referenced here as an ordinary project resource.
var builder = DistributedApplication.CreateBuilder(args);

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
    .WithGroup("staff", ["alice", "bob"], ou: "groups")
    // Fake people under their own auto-declared ou=directory: a node with more children than
    // the truncation tests' limits, seeded so the count is the same on every run.
    .WithFakePeople(30, ou: "directory", seed: 81);

builder.AddProject<Projects.Aspire_LdapAdmin_Web>("ldapadmin")
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// OpenLDAP directory seeded with two well-known users plus generated fake data: 25 fake
// inetOrgPerson entries in 4 fake groups (ou=people/ou=groups are auto-declared). The fixed
// seed makes the generated data identical on every run. (The hosting integration also
// supports TLS via WithTls()/WithRequiredTls(), data persistence via WithDataVolume(),
// custom schemas, overlays, and more — kept off here so the sample is plain and portable.)
var ldap = builder.AddOpenLdap("openldap")
    .WithFakeDirectory(seed: 20260806)
    .WithUser("alice", password: "alice-pw", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org")
    .WithUser("bob", password: "bob-pw", ou: "people", cn: "Bob Brown", sn: "Brown", mail: "bob@example.org")
    .WithGroup("developers", members: ["alice", "bob"], ou: "groups")
    .WithPhpLdapAdmin();

// The API consumes the directory through the instrumented OpenLdapClient.
builder.AddProject<Projects.AspireOpenLdap_Api>("api")
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// OpenLDAP directory seeded with a couple of people and a group. (The hosting integration
// also supports TLS via WithTls()/WithRequiredTls(), data persistence via WithDataVolume(),
// custom schemas, overlays, and more — kept off here so the sample is plain and portable.)
var ldap = builder.AddOpenLdap("openldap")
    .WithOrganizationalUnit("people")
    .WithOrganizationalUnit("groups")
    .WithUser("alice", password: "alice-pw", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org")
    .WithUser("bob", password: "bob-pw", ou: "people", cn: "Bob Brown", sn: "Brown", mail: "bob@example.org")
    .WithGroup("developers", members: ["alice", "bob"], ou: "groups")
    .WithPhpLdapAdmin();

// The API consumes the directory through the instrumented OpenLdapClient.
builder.AddProject<Projects.AspireOpenLdap_Api>("api")
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

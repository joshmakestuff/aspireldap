using Aspire.Hosting;
using LdifDotNet.Generator;

var builder = DistributedApplication.CreateBuilder(args);

// Realistic fake directory data (LdifDotNet.Generator, powered by Bogus). The fixed seed
// makes the generated people and groups identical on every run.
var generator = new LdifGenerator(new LdifGeneratorOptions { Seed = 20260806 });
var people = generator.People(25, "ou=people,dc=example,dc=org");
var groups = generator.Groups(4, "ou=groups,dc=example,dc=org", people);

// OpenLDAP directory seeded with two well-known users plus the generated entries. (The
// hosting integration also supports TLS via WithTls()/WithRequiredTls(), data persistence
// via WithDataVolume(), custom schemas, overlays, and more — kept off here so the sample
// is plain and portable.)
var ldap = builder.AddOpenLdap("openldap")
    .WithOrganizationalUnit("people")
    .WithOrganizationalUnit("groups")
    .WithUser("alice", password: "alice-pw", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org")
    .WithUser("bob", password: "bob-pw", ou: "people", cn: "Bob Brown", sn: "Brown", mail: "bob@example.org")
    .WithGroup("developers", members: ["alice", "bob"], ou: "groups")
    .WithSeedRecords(people)
    .WithSeedRecords(groups)
    .WithPhpLdapAdmin();

// The API consumes the directory through the instrumented OpenLdapClient.
builder.AddProject<Projects.AspireOpenLdap_Api>("api")
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

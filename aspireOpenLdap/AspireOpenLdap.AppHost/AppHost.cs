using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddOpenLdap("openldap")
    .WithDataVolume()
    .WithTls()
    .WithRequiredTls()
    .WithOpenTelemetry()
    .WithOrganizationalUnit("people")
    .WithOrganizationalUnit("groups")
    .WithUser("alice", password: "alice-pw", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org")
    .WithUser("bob", password: "bob-pw", ou: "people", cn: "Bob Brown", sn: "Brown")
    .WithGroup("developers", members: ["alice", "bob"], ou: "groups")
    .WithPhpLdapAdmin();

builder.Build().Run();

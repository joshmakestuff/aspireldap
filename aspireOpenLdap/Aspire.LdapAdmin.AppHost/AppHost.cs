using Aspire.Hosting;

// Phase 0 skeleton: an OpenLDAP resource and the admin web host wired to it by connection
// string. The WithLdapAdmin() delivery model lands in phase 3 (#78); until then the admin is
// referenced here as an ordinary project resource.
var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("openldap");

builder.AddProject<Projects.Aspire_LdapAdmin_Web>("ldapadmin")
    .WithReference(ldap)
    .WaitFor(ldap);

builder.Build().Run();

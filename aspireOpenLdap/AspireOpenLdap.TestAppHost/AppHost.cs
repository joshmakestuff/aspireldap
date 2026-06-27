using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("openldap");

// Optional large-seed scenario, driven by tests via --OpenLdap:SeedDir=<path>.
// Left unset for the default smoke test, which exercises a plain AddOpenLdap.
var seedDir = builder.Configuration["OpenLdap:SeedDir"];
if (!string.IsNullOrWhiteSpace(seedDir))
{
    ldap.WithSeedData(seedDir);
}

builder.Build().Run();

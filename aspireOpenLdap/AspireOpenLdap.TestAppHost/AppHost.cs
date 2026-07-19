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

builder.Build().Run();

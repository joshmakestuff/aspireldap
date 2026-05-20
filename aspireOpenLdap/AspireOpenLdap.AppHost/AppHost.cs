using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddOpenLdap("openldap")
    .WithTls()
    .WithRequiredTls()
    .WithPhpLdapAdmin();

builder.Build().Run();

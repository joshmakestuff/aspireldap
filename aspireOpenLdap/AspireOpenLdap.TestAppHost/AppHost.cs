using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddOpenLdap("openldap");

builder.Build().Run();

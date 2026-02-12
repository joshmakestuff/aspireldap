using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

builder
    .AddOpenLdap("openldap")
    .WithLifetime(ContainerLifetime.Persistent);

builder.Build().Run();

# AspireLdap

.NET Aspire integrations for [OpenLDAP](https://www.openldap.org/) — run a real LDAP directory as part of your local Aspire app, and connect to it from your services with first-class DI, configuration, and health checks.

The OpenLDAP container image is **built locally from a Dockerfile bundled in the hosting package**, so no external image registry is required.

## Packages

| Package | Install in | Purpose |
| --- | --- | --- |
| [`JoshMakeStuff.Aspire.Hosting.OpenLdap`](https://www.nuget.org/packages/JoshMakeStuff.Aspire.Hosting.OpenLdap) | AppHost | Adds an OpenLDAP container resource (TLS, seeding, schemas, overlays, health checks, phpLDAPadmin sidecar). |
| [`JoshMakeStuff.Aspire.OpenLdap`](https://www.nuget.org/packages/JoshMakeStuff.Aspire.OpenLdap) | Service project | Registers an `LdapConnection` wired to the resource's connection string, with a health check. |

```sh
# in the AppHost project
dotnet add package JoshMakeStuff.Aspire.Hosting.OpenLdap

# in the service project that talks to LDAP
dotnet add package JoshMakeStuff.Aspire.OpenLdap
```

> The packages keep the `Aspire.Hosting` / `Microsoft.Extensions.Hosting` API namespaces, so the extension methods appear exactly where you'd expect. Only the NuGet package IDs carry the `JoshMakeStuff.` prefix (the `Aspire.*` prefix is reserved on nuget.org).

## Quick start

**AppHost:**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("ldap");

builder.AddProject<Projects.MyApi>("api")
       .WithReference(ldap);

builder.Build().Run();
```

**Service project:**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddOpenLdapClient("ldap");

var app = builder.Build();

app.MapGet("/whoami", (LdapConnection conn) =>
{
    var who = (ExtendedResponse)conn.SendRequest(new ExtendedRequest("1.3.6.1.4.1.4203.1.11.3"));
    return Results.Text(Encoding.UTF8.GetString(who.ResponseValue ?? []));
});

app.Run();
```

See each package's README for the full API: [hosting](aspireOpenLdap/Aspire.Hosting.OpenLdap/README.md) · [client](aspireOpenLdap/Aspire.OpenLdap/README.md).

## Building

```sh
cd aspireOpenLdap
dotnet build AspireOpenLdap.slnx
```

Running the sample AppHost or the tests requires a running Docker daemon (the OpenLDAP image is built on first run).

On **Linux**, the client and the hosting health check use `System.DirectoryServices.Protocols`, which loads the native `libldap-2.5.so.0` — present by default on Windows but not on most Linux distros (especially those shipping OpenLDAP 2.6, e.g. Ubuntu 24.04+ and Fedora). See [Requirements on Linux](aspireOpenLdap/Aspire.OpenLdap/README.md#requirements-on-linux) for the one-time `libldap` install/symlink steps.

## License

[MIT](LICENSE). Icon from [iconoir](https://iconoir.com/), MIT licensed.

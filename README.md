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

## Examples

A runnable end-to-end sample lives in [`examples/`](examples/): an AppHost running OpenLDAP plus a minimal Web API that queries it through the instrumented `OpenLdapClient`. Run it and watch `LDAP search` spans (nested under each HTTP request) and `db.client.operation.duration` metrics appear in the Aspire dashboard:

```sh
aspire run --apphost examples/AspireOpenLdap.AppHost/AspireOpenLdap.AppHost.csproj
```

See [examples/README.md](examples/README.md) for the walkthrough.

## Building

```sh
cd aspireOpenLdap
dotnet build AspireOpenLdap.slnx
```

Running the sample AppHost or the tests requires a running Docker daemon (the OpenLDAP image is built on first run).

On **Linux**, the client and the hosting health check use `System.DirectoryServices.Protocols`, which needs the native `libldap` client library installed (any of the 2.4/2.5/2.6 sonames — the integrations resolve whichever your distro ships automatically). See [Requirements on Linux](aspireOpenLdap/Aspire.OpenLdap/README.md#requirements-on-linux).

## License

The .NET libraries (`aspireOpenLdap/`, `examples/`) are [MIT](LICENSE). The bundled OpenLDAP container sources (`openldap/`) are derived from the Bitnami OpenLDAP container and are [Apache-2.0](openldap/LICENSE) — see [openldap/NOTICE](openldap/NOTICE). The `JoshMakeStuff.Aspire.Hosting.OpenLdap` package, which ships both, is licensed `MIT AND Apache-2.0`. Icon from [iconoir](https://iconoir.com/), MIT licensed.

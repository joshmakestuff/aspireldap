# Aspire.Hosting.OpenLdap

A [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) hosting integration that adds an OpenLDAP container resource to your AppHost. The container is built from a Dockerfile bundled with this package, so no external image registry is required.

Pairs with [`Aspire.OpenLdap`](https://www.nuget.org/packages/Aspire.OpenLdap), the client integration installed in your service projects.

## Install

```sh
dotnet add package Aspire.Hosting.OpenLdap
```

Install this package in your **AppHost** project.

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("ldap");

builder.AddProject<Projects.MyApi>("api")
       .WithReference(ldap);

builder.Build().Run();
```

`AddOpenLdap` returns an `IResourceBuilder<OpenLdapResource>` that exposes:

- `WithDataVolume(...)` / `WithDataBindMount(...)` — persist directory data across runs.
- `WithSchema(...)` / `WithSchemas(...)` — apply custom LDIF schemas.
- `WithSeedData(...)` / `WithCustomLdifsBindMount(...)` — seed entries on first start.
- `WithAnonymousBinding(...)` — allow unauthenticated binds.
- `WithTls(...)` / `WithRequiredTls(...)` — enable LDAPS; auto-generates a self-signed cert if you don't supply one.
- `WithPhpLdapAdmin(...)` — add a sibling phpLDAPadmin UI container.

A health check that hits the LDAP root DSE is registered automatically.

## Connection string

The resource publishes a connection string in this shape:

```
Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=<secret>
```

When `WithRequiredTls()` is used the scheme switches to `ldaps://` and `CaCertFile=...` is appended.

## Notes

- The admin password is auto-generated and surfaced in the Aspire dashboard as a secret parameter named `{name}-password`. Pass your own via the `adminPassword` parameter to override.
- Container endpoints default to `1389` (LDAP) and `1636` (LDAPS) on the host.

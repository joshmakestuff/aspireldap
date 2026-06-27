# JoshMakeStuff.Aspire.Hosting.OpenLdap

A [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) hosting integration that adds an OpenLDAP container resource to your AppHost. The container is built from a Dockerfile bundled with this package, so no external image registry is required.

Pairs with [`JoshMakeStuff.Aspire.OpenLdap`](https://www.nuget.org/packages/JoshMakeStuff.Aspire.OpenLdap), the client integration installed in your service projects.

> The package ID carries the `JoshMakeStuff.` prefix because `Aspire.*` is reserved on nuget.org. The API namespace is still `Aspire.Hosting`, so `AddOpenLdap(...)` resolves without any extra `using`.

## Install

```sh
dotnet add package JoshMakeStuff.Aspire.Hosting.OpenLdap
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

## Requirements on Linux

The automatically-registered health check uses `System.DirectoryServices.Protocols`, which on Linux P/Invokes the native OpenLDAP client library. (On Windows it uses the built-in `wldap32.dll` and needs nothing extra.) The runtime loads **`libldap-2.5.so.0`** — this is still true on .NET 10 ([dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676)) — so the library must be present under that name:

```sh
# Debian/Ubuntu that ship OpenLDAP 2.5 (e.g. 22.04)
sudo apt-get install -y libldap-2.5-0

# Distros that ship OpenLDAP 2.6 (Ubuntu 24.04+, Fedora, Alpine 3.20+):
# install the client libs, then symlink the 2.6 sonames to the 2.5 names.
# Path is /usr/lib/x86_64-linux-gnu on Debian/Ubuntu, /usr/lib64 on Fedora.
# Confirm what you have with:  ldconfig -p | grep -E 'libldap|liblber'
sudo ln -sf .../libldap-2.6.so.0 .../libldap-2.5.so.0
sudo ln -sf .../liblber-2.6.so.0 .../liblber-2.5.so.0
sudo ldconfig
```

Without this you'll see `Unable to load shared library 'libldap-2.5.so.0'` and the resource never reports healthy.

## Notes

- The admin password is auto-generated and surfaced in the Aspire dashboard as a secret parameter named `{name}-password`. Pass your own via the `adminPassword` parameter to override.
- Container endpoints default to `1389` (LDAP) and `1636` (LDAPS) on the host.

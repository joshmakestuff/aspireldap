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

- `WithDataVolume(...)` / `WithDataBindMount(...)` — persist directory data across runs. The default volume name is scoped to the AppHost (`{apphost}-{hash}-{resource}-data`); pass an explicit name to share a volume across AppHosts.
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

TLS trust is platform-specific: on Windows the health check validates the server certificate through a managed callback; on Linux the CA is trusted natively by `libldap` (via an OpenSSL hash-named certificate directory), which always validates the hostname — so the `disableHealthCheckHostnameValidation` opt-out is rejected there, and custom server certificates must name `localhost`/`127.0.0.1`. On macOS the server-side TLS requirement is relaxed and the health check uses plain LDAP, because Apple's `LDAP.framework` cannot trust a custom CA from managed code.

Settings applied through the builder (TLS enforcement, seeds, schemas, ACLs, anonymous-bind policy) take effect on **first initialization only**. A persisted data volume keeps the configuration it was initialized with — use the resource's "Reset data volume" dashboard command to reinitialize with current settings. If initialization fails partway (for example on a rejected seed entry), the container refuses to restart over the partial data until the volume is reset; the original failure is printed in the logs of the failing run.

## Requirements on Linux

The automatically-registered health check uses `System.DirectoryServices.Protocols`, which on Linux P/Invokes the native OpenLDAP client library. (On Windows it uses the built-in `wldap32.dll` and needs nothing extra.) The runtime hardcodes a load of **`libldap-2.5.so.0`** — still true on .NET 10 ([dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676)) — but modern distros (Ubuntu 24.04+, Fedora, Alpine 3.20+) ship the upstream soname `libldap.so.2` instead.

**`AddOpenLdap` handles this automatically**: it registers a resolver that probes the sonames distros actually ship (`libldap-2.5.so.0`, `libldap.so.2`, `libldap-2.6.so.0`, `libldap-2.4.so.2`). The AppHost machine only needs the OpenLDAP client library installed — no symlinks:

```sh
sudo apt-get install -y libldap2      # Ubuntu 24.04+ (22.04: libldap-2.5-0)
sudo dnf install -y openldap          # Fedora
```

Without the library installed you'll see `Unable to load shared library 'libldap-2.5.so.0'` and the resource never reports healthy.

## Notes

- The admin password is auto-generated and surfaced in the Aspire dashboard as a secret parameter named `{name}-password`. Pass your own via the `adminPassword` parameter to override.
- Host ports are allocated dynamically by Aspire (the container listens on `1389`/`1636` internally), so multiple AppHosts can run side by side. Pin fixed host ports with `.WithLdapPort(1389)` / `.WithLdapsPort(1636)` when an external tool needs a stable address.

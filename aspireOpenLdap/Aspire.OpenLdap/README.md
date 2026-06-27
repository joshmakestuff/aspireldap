# JoshMakeStuff.Aspire.OpenLdap

A [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) client integration for OpenLDAP. Registers an `LdapConnection` (from `System.DirectoryServices.Protocols`) in DI, wired to the connection string published by the [`JoshMakeStuff.Aspire.Hosting.OpenLdap`](https://www.nuget.org/packages/JoshMakeStuff.Aspire.Hosting.OpenLdap) resource.

> The package ID carries the `JoshMakeStuff.` prefix because `Aspire.*` is reserved on nuget.org. The API namespace is still `Microsoft.Extensions.Hosting`, so `AddOpenLdapClient(...)` resolves without any extra `using`.

## Install

```sh
dotnet add package JoshMakeStuff.Aspire.OpenLdap
```

Install this package in the **service project** that talks to LDAP (not the AppHost).

## Usage

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

The `connectionName` (`"ldap"` above) must match the resource name passed to `AddOpenLdap(...)` in your AppHost.

## What gets registered

- `OpenLdapClientFactory` (singleton) — parses the connection string, applies settings, and creates `LdapConnection` instances.
- `LdapConnection` (transient) — resolved from the factory.
- A health check named `openldap_{connectionName}` that performs a root-DSE search (disable with `settings.DisableHealthChecks = true`).

## Multiple directories (keyed)

To connect to more than one OpenLDAP resource, register each with `AddKeyedOpenLdapClient` — the connection name doubles as the DI service key:

```csharp
builder.AddKeyedOpenLdapClient("corp");
builder.AddKeyedOpenLdapClient("partners");

app.MapGet("/corp", ([FromKeyedServices("corp")] LdapConnection conn) => /* ... */);
```

This registers `OpenLdapClientFactory` and `LdapConnection` as keyed services under the name, plus a health check named `openldap_{name}`.

## Configuration

Bind from `Aspire:OpenLdap` in configuration, or pass a callback:

```csharp
builder.AddOpenLdapClient("ldap", settings =>
{
    settings.DisableHealthChecks = false;
    // settings.ConnectionString — overrides the resolved connection string
});
```

The connection string is normally provided automatically by Aspire via `ConnectionStrings:{connectionName}`.

## Requirements on Linux

`LdapConnection` comes from `System.DirectoryServices.Protocols`, which on Linux P/Invokes the native OpenLDAP client library. (On Windows it uses the built-in `wldap32.dll` and needs nothing extra.) The runtime loads **`libldap-2.5.so.0`** — still true on .NET 10 ([dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676)) — so the library must be present under that name:

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

Without this you'll see `Unable to load shared library 'libldap-2.5.so.0'` at the first LDAP call.

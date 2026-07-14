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
- `OpenLdapClient` (transient) — an instrumented wrapper over `LdapConnection`; use it to get OpenTelemetry traces/metrics (see [Telemetry](#telemetry)).
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

## Telemetry

Operations issued through **`OpenLdapClient`** (resolve it from DI and call `Send` / `SendAsync` instead of using the raw `LdapConnection`) emit OpenTelemetry traces and metrics under the source/meter name **`Aspire.OpenLdap`**. `AddOpenLdapClient` registers the source and meter with the app's OpenTelemetry pipeline automatically, so they flow to whatever exporter you've configured (e.g. via Aspire's `AddServiceDefaults`).

```csharp
builder.AddOpenLdapClient("ldap");

app.MapGet("/users", (OpenLdapClient ldap) =>
{
    var resp = (SearchResponse)ldap.Send(
        new SearchRequest("ou=users,dc=example,dc=org", "(objectClass=person)", SearchScope.Subtree, null));
    return Results.Ok(resp.Entries.Count);
});
```

- **Traces** — one span per operation named `LDAP <op>` (e.g. `LDAP search`), kind `Client`, with attributes `db.system.name=openldap`, `db.operation.name`, `server.address`, `server.port`, `db.response.status_code` / `db.ldap.result_code`, and for searches `db.ldap.scope`, `db.ldap.entries_returned`, `db.ldap.controls` (control OIDs), `db.ldap.paged`.
- **Metrics** — a histogram `db.client.operation.duration` (seconds) tagged by operation, server, and result/error.
- **Privacy** — search filters, DNs, entry attributes, control values, and paging-cookie bytes are **never** recorded.
- **Disabling** — set `settings.DisableTracing` and/or `settings.DisableMetrics` (or `Aspire:OpenLdap:DisableTracing` in configuration).
- **Note** — only `OpenLdapClient` is instrumented; the raw `LdapConnection` and the `OpenLdapClient.Connection` escape hatch are not.

For a runnable end-to-end demo (AppHost + Web API + dashboard), see the [`examples/`](../../examples/) folder.

## Requirements on Linux

`LdapConnection` comes from `System.DirectoryServices.Protocols`, which on Linux P/Invokes the native OpenLDAP client library. (On Windows it uses the built-in `wldap32.dll` and needs nothing extra.) The runtime hardcodes a load of **`libldap-2.5.so.0`** — still true on .NET 10 ([dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676)) — but modern distros (Ubuntu 24.04+, Fedora, Alpine 3.20+) ship the upstream soname `libldap.so.2` instead.

**`AddOpenLdapClient` / `AddKeyedOpenLdapClient` handle this automatically**: they register a resolver that probes the sonames distros actually ship (`libldap-2.5.so.0`, `libldap.so.2`, `libldap-2.6.so.0`, `libldap-2.4.so.2`). You only need the OpenLDAP client library installed — no symlinks:

```sh
sudo apt-get install -y libldap2      # Ubuntu 24.04+ (22.04: libldap-2.5-0)
sudo dnf install -y openldap          # Fedora
sudo apk add libldap                  # Alpine
```

The automatic resolution only applies when you register through the `Add*` methods. If you use `System.DirectoryServices.Protocols` directly elsewhere in your app before calling them, symlink the shipped soname to the 2.5 name as a fallback (confirm what you have with `ldconfig -p | grep -E 'libldap|liblber'`):

```sh
# Path is /usr/lib/x86_64-linux-gnu on Debian/Ubuntu, /usr/lib64 on Fedora.
sudo ln -sf .../libldap.so.2 .../libldap-2.5.so.0
sudo ldconfig
```

Without either, you'll see `Unable to load shared library 'libldap-2.5.so.0'` at the first LDAP call.

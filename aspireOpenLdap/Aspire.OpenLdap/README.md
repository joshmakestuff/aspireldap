# Aspire.OpenLdap

A [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) client integration for OpenLDAP. Registers an `LdapConnection` (from `System.DirectoryServices.Protocols`) in DI, wired to the connection string published by the [`Aspire.Hosting.OpenLdap`](https://www.nuget.org/packages/Aspire.Hosting.OpenLdap) resource.

## Install

```sh
dotnet add package Aspire.OpenLdap
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

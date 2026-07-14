# Examples

A runnable, end-to-end sample for the AspireLdap integrations.

## What it shows

An Aspire **AppHost** runs an OpenLDAP container (seeded with a couple of people and a group) plus phpLDAPadmin, and a minimal **Web API** consumes the directory through the **instrumented [`OpenLdapClient`](../aspireOpenLdap/Aspire.OpenLdap/README.md#telemetry)**.

It demonstrates:

- **Hosting** — `AddOpenLdap("openldap")` with seed data (`WithUser`/`WithGroup`/`WithOrganizationalUnit`) in the AppHost.
- **Client** — `AddOpenLdapClient("openldap")` in the API, resolving `OpenLdapClient` and calling `Send(...)`.
- **Client OpenTelemetry** — every LDAP operation emits an `LDAP search` span (nested under the incoming HTTP request span) and a `db.client.operation.duration` metric on the `Aspire.OpenLdap` source/meter, all visible in the Aspire dashboard.

## Run it

Requires Docker (the OpenLDAP image is built on first run).

```sh
aspire run --apphost examples/AspireOpenLdap.AppHost/AspireOpenLdap.AppHost.csproj
```

Then:

1. Open the **dashboard** (the URL is printed on start).
2. Call the API's **`GET /users`** endpoint — find its URL on the dashboard's **Resources** page. It returns the seeded users (alice, bob).
3. In the dashboard, open **Traces** → each `GET /users` trace contains a nested `LDAP search` span. Open **Metrics** → the `api` resource → `db.client.operation.duration`.

## Projects

| Project | Role |
| --- | --- |
| `AspireOpenLdap.AppHost` | Aspire AppHost — runs OpenLDAP, the API, and phpLDAPadmin |
| `AspireOpenLdap.Api` | Minimal Web API that queries the directory via `OpenLdapClient` |
| `AspireOpenLdap.ServiceDefaults` | Standard Aspire service defaults (OpenTelemetry export, health checks) |

> **Linux note:** the client uses `System.DirectoryServices.Protocols`, which needs the native `libldap` library installed at runtime (any of the 2.4/2.5/2.6 sonames — `AddOpenLdapClient` resolves whichever your distro ships automatically). See [Requirements on Linux](../aspireOpenLdap/Aspire.OpenLdap/README.md#requirements-on-linux).

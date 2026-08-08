---
name: aspire-openldap
description: Use the JoshMakeStuff.Aspire.Hosting.OpenLdap and JoshMakeStuff.Aspire.OpenLdap NuGet packages — add an OpenLDAP container to a .NET Aspire AppHost, seed it with users/groups/LDIF/fake data, and query it from services via the instrumented OpenLdapClient.
---

# Aspire OpenLDAP integration — agent reference

Two NuGet packages, installed in different projects:

| Package | Install into | Namespace | Entry point |
| ------- | ------------ | --------- | ----------- |
| `JoshMakeStuff.Aspire.Hosting.OpenLdap` | the **AppHost** project | `Aspire.Hosting` | `builder.AddOpenLdap("name")` |
| `JoshMakeStuff.Aspire.OpenLdap` | each **service** that talks to LDAP | `Microsoft.Extensions.Hosting` | `builder.AddOpenLdapClient("name")` |

The `JoshMakeStuff.` prefix exists only because `Aspire.*` is reserved on nuget.org; namespaces match first-party Aspire integrations, so no extra `using` is needed. The OpenLDAP container is built from a Dockerfile bundled in the package — no registry pull.

## Minimal end-to-end

```csharp
// AppHost/AppHost.cs
var builder = DistributedApplication.CreateBuilder(args);

var ldap = builder.AddOpenLdap("ldap")           // base DN dc=example,dc=org, admin user "admin", generated password
    .WithOrganizationalUnit("people")
    .WithUser("alice", password: "alice-pw", ou: "people", cn: "Alice Anderson", sn: "Anderson", mail: "alice@example.org");

builder.AddProject<Projects.MyApi>("api")
    .WithReference(ldap)                          // injects ConnectionStrings:ldap
    .WaitFor(ldap);

builder.Build().Run();
```

```csharp
// Service/Program.cs
builder.AddOpenLdapClient("ldap");                // name must match AddOpenLdap resource name

app.MapGet("/users", (OpenLdapClient ldap) =>
{
    var resp = (SearchResponse)ldap.Send(new SearchRequest(
        "dc=example,dc=org", "(objectClass=inetOrgPerson)", SearchScope.Subtree, "uid", "cn", "mail"));
    return resp.Entries.Cast<SearchResultEntry>().Select(e => e.DistinguishedName);
});
```

Prefer `OpenLdapClient` (`Send`/`SendAsync` over `System.DirectoryServices.Protocols` requests) — it is the instrumented wrapper (OpenTelemetry source/meter `Aspire.OpenLdap`). The raw `LdapConnection` is also registered (transient) but emits no telemetry.

## Hosting API surface (all on `IResourceBuilder<OpenLdapResource>`)

Identity/endpoints: `WithBaseDn(string)` (default `dc=example,dc=org`), `WithAdminUsername(string)` (default `admin`), `WithLdapPort(int)`, `WithLdapsPort(int)` (host ports are dynamic by default; container listens on 1389/1636).

Storage: `WithDataVolume(string? name = null, bool isReadOnly = false)`, `WithDataBindMount(string source, ...)`. A persisted volume keeps the config it was **first initialized** with — TLS, seeds, schemas, ACLs applied later require the resource's "Reset data volume" dashboard command.

Seeding (four routes, combinable except as noted):
1. **Typed tree** — `WithOrganizationalUnit(string name)`, `WithUser(string uid, string password, string? ou = null, string? cn = null, string? sn = null, string? mail = null)`, `WithGroup(string cn, IEnumerable<string> members, string? ou = null)`. Validated before start (duplicates, undeclared OU/member refs, name charset `[A-Za-z0-9._-]+`). Passwords stored `{SSHA}`-hashed; a value already carrying `{SCHEME}` passes through verbatim. Group members are uids of declared users, or literal DNs if they contain `=`.
2. **Fake data** — `WithFakePeople(int count, string ou = "people", int? seed = null)`, `WithFakeGroups(int count, string ou = "groups", int? seed = null)`, `WithFakeDirectory(int people = 25, int groups = 4, int? seed = null)` (the one-liner = people + groups). Realistic `inetOrgPerson`/`groupOfNames` entries via the bundled `LdifDotNet.Generator` — no extra package install. Every person carries `uid, cn, sn, givenName, displayName, mail, telephoneNumber, title, employeeNumber, l` (verified against 0.7.0); groups carry `cn`, `description`, and ≥1 `member` DNs drawn from the generated people. OUs are auto-declared (do not also call `WithOrganizationalUnit` for them). Same seed + same `LdifDotNet.Generator` version = identical data, per call; null seed = fresh data each run. `WithFakeGroups` requires a preceding `WithFakePeople` (member pool). Parent DNs derive from `WithBaseDn` automatically.
3. **LdifDotNet records** — `WithSeedRecords(params IEnumerable<LdifDotNet.LdifRecord> records)`. Accumulates across calls; loads after the typed tree. Use for custom objectClasses/attributes. Also the escape hatch for raw `LdifGenerator` output when you need custom parent DNs:

```csharp
var gen = new LdifGenerator(new LdifGeneratorOptions { Seed = 42 });      // Seed => deterministic
var people = gen.People(25, "ou=staff,o=acme");                           // any parent DN you own
ldap.WithSeedRecords(people);                                             // parent entries must exist
```

4. **Your own LDIF files** — `WithSeedData(string ldifFileOrDirectory, bool continueOnError = false)`; paths resolve against the AppHost project dir. **Do not pass a directory while also using routes 1–3** — the directory mounts over `/ldifs` and collides with the generated seed files.

Generated fake people have no `userPassword` — they are searchable data, not bindable accounts; use `WithUser` for accounts tests bind as.

```csharp
builder.AddOpenLdap("ldap")
    .WithFakeDirectory(seed: 42)                       // 25 people in ou=people, 4 groups in ou=groups
    .WithUser("alice", "alice-pw", ou: "people");      // a bindable account alongside the fake data
```

Schema/config: `WithSchema(string ldifFile)`, `WithSchemas(string directory)`, `WithDefaultSchemas(bool)`, `WithExtraSchemas(params string[])`, `WithOverlay(OpenLdapOverlay)` (e.g. `OpenLdapOverlay.MemberOf("groupOfNames", "member")`), `WithAccessControl(params string[] rules)` (full `olcAccess` bodies without the `{N}` prefix; slapd appends an implicit `to * by * none`).

TLS: `WithTls()` (self-signed, cached under `obj/`), `WithTls(serverCertFile, serverKeyFile, caCertFile, ...)`, `WithRequiredTls()` (LDAPS-only; connection string switches to `ldaps://` + `CaCertFile=`). macOS: server TLS requirement is relaxed and the health check uses plain LDAP (Apple's LDAP.framework can't trust a custom CA from managed code).

Misc: `WithAnonymousBinding(bool)`, `WithLogLevel(OpenLdapLogLevel)`, `WithHealthCheckProbeLogging(bool)`, `WithPhpLdapAdmin(...)` (admin UI sidecar). Admin password: auto-generated parameter `{name}-password`; override via `AddOpenLdap(name, adminPassword: someParameter)`.

## Client API surface

`AddOpenLdapClient(string connectionName, Action<OpenLdapClientSettings>? configure = null)` and `AddKeyedOpenLdapClient(...)` (service key = name, for multiple directories). Registers: `OpenLdapClientFactory` (singleton), `LdapConnection` (transient), `OpenLdapClient` (transient), health check `openldap_{name}` (root-DSE search).

`OpenLdapClientSettings`: `ConnectionString`, `DisableHealthChecks`, `DisableTracing`, `DisableMetrics`, `TrustConnectionStringCaCertificate` (default true), `DisableTlsHostnameValidation` (Windows-only relaxation; rejected on Linux), `Timeout` (default 30 s). Config section `Aspire:OpenLdap`.

Connection string shape (published by the resource, consumed by the client):

```text
Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=<secret>[;CaCertFile=<path>]
```

Read the base DN from `OpenLdapConnectionStringBuilder.Parse(connectionString).BaseDn` instead of hardcoding it.

Writing a connection string from your own inputs (env vars, config, a sidecar contract): build the object and call `Build()` — never assemble the string by hand, and never re-implement the quoting.

```csharp
var connectionString = new OpenLdapConnectionStringBuilder
{
    Endpoint = new Uri($"ldap://{host}:{port}"),
    BaseDn = baseDn,
    BindDn = bindDn,
    BindPassword = password,   // quoted correctly whatever it contains
}.Build();
```

`Build()` is a named method, not a `ToString()` override, because the object holds a password — an override would leak it into any log line or interpolation that mentions the instance. It rejects the same endpoints `Parse` rejects (path, query, user info, fragment, non-`ldap(s)` scheme), so it can never emit a string the parser refuses.

## Gotchas agents hit

- **Linux native dependency**: `System.DirectoryServices.Protocols` P/Invokes `libldap-2.5.so.0`; both packages auto-probe modern sonames, but the OS package must be installed (`apt-get install libldap2` / `dnf install openldap` / `apk add libldap`). Symptom: `Unable to load shared library 'libldap-2.5.so.0'`.
- **First-init-only config** with `WithDataVolume`: changed seeds/TLS/ACLs don't apply to an existing volume; use the "Reset data volume" dashboard command.
- **Health check on the AppHost machine** also needs the libldap OS package on Linux.
- Search filters, DNs, and attributes are never recorded in telemetry (privacy by design) — don't look for them in spans.
- Verify a running app with the Aspire CLI: `aspire wait <resource>`, `aspire logs <resource>`, `aspire describe --format Json`.

## Where this file lives

Both packages ship this file in the nupkg root and as `skills/SKILL.md`, so after restore it is at `~/.nuget/packages/<package-id>/<version>/AGENTS.md`. For best results, copy it (or add a pointer) into the consuming repo's `AGENTS.md` / `CLAUDE.md` / `.claude/skills/aspire-openldap/SKILL.md`.

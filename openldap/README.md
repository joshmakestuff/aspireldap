# OpenLDAP Docker Image

OpenLDAP 2.6 container image based on stock Debian 13 (Trixie) packages, which ship slapd 2.6.x. The Dockerfile asserts the installed `slapd -VV` version at build time, so the advertised and actual versions cannot drift apart. Forked from the Bitnami OpenLDAP container and rewritten to eliminate external binary dependencies.

## Quick Start

Build and run directly:

```bash
docker build -t openldap ./2.6/debian-13
docker run --rm -p 1389:1389 -p 1636:1636 openldap
```

Test connectivity:

```bash
ldapsearch -x -H ldap://localhost:1389 -b "" -s base "(objectClass=*)" +
```

## Key Differences from Bitnami

| Feature | Bitnami | This Image |
|---------|---------|------------|
| Base image | `bitnami/minideb:bookworm` | `debian:trixie-slim` |
| OpenLDAP source | Pre-compiled binary from `downloads.bitnami.com` | `apt-get install slapd ldap-utils` |
| Binary paths | `/opt/bitnami/openldap/` | `/usr/sbin/`, `/usr/bin/` |
| Config path | `/opt/bitnami/openldap/etc/` | `/etc/ldap/` |
| Schema path | `/opt/bitnami/openldap/etc/schema/` | `/etc/ldap/schema/` |
| Module path | `/opt/bitnami/openldap/libexec/openldap/` | `/usr/lib/ldap/` |
| Data volume | `/bitnami/openldap` | `/data/openldap` |
| Daemon user | `slapd` (UID 1001) | `openldap` (created by apt) |
| Script path | `/opt/bitnami/scripts/` | `/opt/openldap/scripts/` |

## Environment Variables

### Core Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_PORT_NUMBER` | `1389` | LDAP listen port |
| `LDAP_LDAPS_PORT_NUMBER` | `1636` | LDAPS listen port |
| `LDAP_ROOT` | `dc=example,dc=org` | Base DN / suffix |
| `LDAP_ADMIN_USERNAME` | `admin` | Admin CN component (admin DN = `cn={username},{root}`) |
| `LDAP_ADMIN_PASSWORD` | `adminpassword` | Admin password |
| `LDAP_LOGLEVEL` | `256` | slapd debug log level |
| `LDAP_LOG_HEALTH_PROBES` | `no` | Log Aspire health-check probe connections (sentinel-marked, wholly-successful probe blocks are filtered from the stats log by default; failed probes always log in full) |
| `LDAP_ULIMIT_NOFILES` | `1024` | File descriptor limit |
| `LDAP_ALLOW_ANON_BINDING` | `yes` | Allow anonymous LDAP binds |
| `LDAP_PASSWORD_HASH` | `{SSHA}` | Password hashing algorithm |

### User / Tree Creation

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_USERS` | `user01,user02` | Comma-separated user names to create |
| `LDAP_PASSWORDS` | `bitnami1,bitnami2` | Matching passwords (same count as users) |
| `LDAP_USER_OU` | `users` | Organizational unit for users |
| `LDAP_GROUP_OU` | `groups` | Organizational unit for groups |
| `LDAP_GROUP` | `readers` | Group name to create |
| `LDAP_SKIP_DEFAULT_TREE` | `no` | Skip creating default DIT entries |

### Schema

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_ADD_SCHEMAS` | `yes` | Load extra schemas |
| `LDAP_EXTRA_SCHEMAS` | `cosine,inetorgperson,nis` | Comma-separated schema names |
| `LDAP_CUSTOM_SCHEMA_FILE` | `/schema/custom.ldif` | Single custom schema LDIF |
| `LDAP_CUSTOM_SCHEMA_DIR` | `/schemas` | Directory of custom schema LDIFs |

### Config Admin

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_CONFIG_ADMIN_ENABLED` | `no` | Enable cn=config admin credentials |
| `LDAP_CONFIG_ADMIN_USERNAME` | `admin` | Config admin CN |
| `LDAP_CONFIG_ADMIN_PASSWORD` | `configpassword` | Config admin password |

### TLS

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_ENABLE_TLS` | `no` | Enable TLS |
| `LDAP_REQUIRE_TLS` | `no` | Require TLS for all connections |
| `LDAP_TLS_CERT_FILE` | | X.509 certificate path |
| `LDAP_TLS_KEY_FILE` | | Private key path |
| `LDAP_TLS_CA_FILE` | | CA certificate path |
| `LDAP_TLS_VERIFY_CLIENTS` | `never` | Client certificate verification |
| `LDAP_TLS_DH_PARAMS_FILE` | | DH params file |

### Overlays

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_CONFIGURE_PPOLICY` | `no` | Enable ppolicy overlay |
| `LDAP_PPOLICY_USE_LOCKOUT` | `no` | Account lockout |
| `LDAP_PPOLICY_HASH_CLEARTEXT` | `no` | Auto-hash cleartext passwords |
| `LDAP_ENABLE_ACCESSLOG` | `no` | Enable access log overlay |
| `LDAP_ACCESSLOG_DB` | `cn=accesslog` | Access-log database suffix |
| `LDAP_ACCESSLOG_ADMIN_USERNAME` | `admin` | Access-log admin `cn` (root DN is `cn=<name>,<suffix>`) |
| `LDAP_ACCESSLOG_ADMIN_PASSWORD` | `accesspassword` | Access-log admin password (`LDAP_ACCESSLOG_PASSWORD` is a deprecated alias) |
| `LDAP_ACCESSLOG_LOGOPS` | `writes` | Operation classes to log (`olcAccessLogOps`) |
| `LDAP_ACCESSLOG_LOGSUCCESS` | `TRUE` | Log successful operations only |
| `LDAP_ACCESSLOG_LOGPURGE` | `07+00:00 01+00:00` | Purge age/interval (`olcAccessLogPurge`) |
| `LDAP_ACCESSLOG_LOGOLD` | `(objectClass=*)` | Filter for logging old entry attributes |
| `LDAP_ACCESSLOG_LOGOLDATTR` | `objectClass` | Attributes logged from old entries |
| `LDAP_ENABLE_SYNCPROV` | `no` | Enable sync provider overlay |
| `LDAP_SYNCPROV_CHECKPOINT` | `100 10` | Syncprov checkpoint ops/minutes (`LDAP_SYNCPROV_CHECKPPOINT` accepted as a legacy alias) |
| `LDAP_SYNCPROV_SESSIONLOG` | `100` | Syncprov session log size |

### Other

| Variable | Default | Description |
|----------|---------|-------------|
| `LDAP_SUFFIX` | `$LDAP_ROOT` | Database suffix (`olcSuffix`) when it must differ from the root entry DN |
| `LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR` | `no` | Load custom LDIFs with `ldapadd -c` (skip rejected entries instead of aborting) |
| `BITNAMI_DEBUG` | `false` | Verbose bootstrap logging (shows suppressed command output) |

### Docker Secrets

These password variables support a `_FILE` suffix that reads the value from a file (e.g. a
Docker secret): `LDAP_ADMIN_PASSWORD_FILE`, `LDAP_CONFIG_ADMIN_PASSWORD_FILE`, and
`LDAP_ACCESSLOG_ADMIN_PASSWORD_FILE` (plus the deprecated `LDAP_ACCESSLOG_PASSWORD_FILE`
alias). Set `LDAP_ADMIN_PASSWORD_FILE=/run/secrets/ldap_password` and the container reads the
password from that file. If a configured `_FILE` path is missing or unreadable the container
**refuses to start** — it never falls back to the default password.

## Bootstrapping

### Custom LDIF Files

Mount LDIF files to `/ldifs` (the `LDAP_CUSTOM_LDIF_DIR`). They are loaded alphabetically via `ldapadd` after the server starts. When custom LDIFs are present, the default tree (users/groups) is **not** created.

```bash
docker run -v ./my-ldifs:/ldifs:ro -p 1389:1389 openldap
```

### Custom Init Scripts

Mount `.sh` scripts to `/docker-entrypoint-initdb.d/`. They run once after initialization and a marker file prevents re-execution on restart.

## Data Persistence

Mount a volume at `/data/openldap`:

```bash
docker run -v openldap_data:/data/openldap -p 1389:1389 openldap
```

The directory contains:
- `data/` — MDB database files
- `slapd.d/` — OLC (cn=config) configuration database

On subsequent starts, if `/data/openldap/data/` is non-empty, initialization is skipped and persisted data is used.

## Aspire Integration

This image is designed to be used with the `Aspire.Hosting.OpenLdap` hosting integration:

```csharp
var openldap = builder.AddOpenLdap("openldap");
```

See the `aspireOpenLdap/` directory for the full Aspire hosting library.

## License

Apache-2.0. Originally forked from [Bitnami OpenLDAP](https://github.com/bitnami/containers/tree/main/bitnami/openldap).

# Changelog

## Unreleased

### Fixed

Findings from a fourth (2026-07-19) hardening review, run with the lens playbook extracted
from the first three reviews (#34–#37):

- **An unreadable `*_FILE` secret now refuses to start the container** (#34). A configured
  but missing/unreadable secret file (typo'd path, absent mount) used to log one WARN and
  boot with the well-known default password, which the init marker then persisted into the
  data volume. The README's Docker-secrets section now enumerates the exact supported `_FILE`
  variables instead of claiming "any password variable".
- **`WithBaseDn` rejects unescaped `;` in DN values** (#35). RFC 4514 requires `;` escaped;
  slapd rejects the unescaped form as a database suffix, so it previously passed model
  validation and died mid-bootstrap with the opaque `olcSuffix: value #0 invalid per syntax`.
  String-level guard until LdifDotNet enforces this in `Dn.Parse` (ldifdotnet#43); `\;`
  remains accepted.
- **Round-4 low-severity sweep** (#36): pre-hashed `{SCHEME}` values in `LDAP_PASSWORDS` now
  pass through instead of being double-hashed (matching the typed-seed rule); the shell RDN
  unescaper preserves a dangling trailing backslash instead of silently dropping it; the
  container's line-break validation now also covers `LDAP_SUFFIX`, the user/group OU and
  group names, and the accesslog DB/username (all interpolated into privileged cn=config
  LDIF), and `LDAP_TLS_VERIFY_CLIENTS` is enum-checked; health checks report only the
  exception type, never the exception object (LDAP diagnostics can embed directory data —
  same rule as telemetry); `DisableTlsHostnameValidation` in a configuration where it cannot
  apply now throws instead of being silently ignored; `LDAP_SYNCPROV_CHECKPOINT` is the
  canonical spelling (the historical `CHECKPPOINT` double-P typo remains a fallback alias).

Findings from a third (2026-07-19) adversarial code review (R1–R3, B1–B4):

- **`LDAP_ACCESSLOG_ADMIN_PASSWORD` was silently ignored** (B1, high). The env block
  unconditionally rebuilt it from the differently-named `LDAP_ACCESSLOG_PASSWORD`, so a
  configured access-log admin password was reset to the known default `accesspassword` — the
  access-log database (potentially sensitive query/write history) stayed readable with a
  documented default even when the user believed the password was changed.
  `LDAP_ACCESSLOG_ADMIN_PASSWORD` is now canonical (matching its `_FILE` Docker-secret name);
  `LDAP_ACCESSLOG_PASSWORD` remains a deprecated alias honored only when the canonical
  variable is absent, in plain and `_FILE` form. Both are now documented.
- **DN validation and container bootstrap now agree on root-RDN semantics** (R1).
  `WithBaseDn("c=USA")` passed model validation but died mid-bootstrap on an opaque
  `olcSuffix: value #0 invalid per syntax` (the country attribute takes a two-character
  Country String); both the model validation and the container's own validation now reject
  non-two-letter `c=` roots up front. Hex escapes in the root RDN value (`o=Acme\2C Inc.`)
  were unescaped by stripping the backslash — emitting a mangled second naming value
  (`o: Acme2C Inc.`) alongside the server-derived correct one; the bootstrap now decodes
  RFC 4514 escapes properly (hex pairs, UTF-8 octet sequences, escaped separators).
- **Default-tree user passwords are now stored hashed** (R2). The built-in
  `LDAP_USERS`/`LDAP_PASSWORDS` tree loaded `userPassword` values verbatim, so a default
  `slapcat` showed base64-recoverable cleartext (`LDAP_PASSWORD_HASH` only governs the
  password-modify extended operation, not `ldapadd`). They are hashed with `slappasswd`
  (`{SSHA}`) like the admin and typed-seed passwords; cleartext binds still work.
- **A configured but missing/unloadable client CA certificate now fails closed** (B2).
  `CaCertFile` pointing at a nonexistent or non-PEM file silently fell back to the platform
  trust store — the effective TLS trust policy could differ from configuration with only a
  generic handshake error (or none) as a symptom. `CreateConnection()` now throws an
  actionable error naming the path; opting out of custom trust remains explicit via
  `TrustConnectionStringCaCertificate = false`. The connection is also disposed if trust
  configuration fails partway.
- **Published manifests now quote the bind password expression** (B3). The manifest path
  substitutes the password parameter at deployment time — after quoting used to happen — so
  a deployed password containing `;` or edge whitespace corrupted the connection string even
  though it worked locally. The manifest expression now carries the connection-string quotes.
  Known limit: embedded double quotes cannot be escaped at deploy time and fail loudly at
  client parse time.
- **Telemetry no longer records exception messages** (B4). Failure spans called
  `Activity.AddException`, whose event includes the exception message — LDAP server
  diagnostics can embed DNs and other directory data, contradicting the integration's no-PII
  telemetry contract. Failures now record only the sanitized `error.type` (exception type
  name), matching the metric tags.
- **The probe log filter no longer drops an unterminated final log line** (R3). When slapd
  died mid-line, the EOF read path discarded the partial line it had consumed, losing the
  tail of the fatal message — contradicting the filter's fail-open contract. The fragment is
  now flushed with the rest of the withheld output.

### Added

- **Runtime witnesses for previously untested modes and claims** (#37): TLS enabled without
  `WithRequiredTls` (plain LDAP and LDAPS served side by side), custom-LDIF loading with
  default-tree suppression, restart over a completed data volume preserving data, `_FILE`
  secret resolution (plain, alias, unreadable-refusal, and end-to-end admin bind), the
  accesslog deprecation warning, and the phpLDAPadmin image pin. The image README now
  documents the previously implemented-but-undocumented env vars (`LDAP_SUFFIX`, accesslog
  and syncprov tuning, `LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR`, `BITNAMI_DEBUG`).
- **Health-check probe traffic no longer floods the container log** (#31). The Aspire health
  check polls continuously, and at the default `stats` log level each probe emitted a ~7-line
  `conn=N` block — drowning real activity in the dashboard's console view. The container now
  pipes slapd's log through a sentinel-aware filter that drops each probe's block. The probe
  marks itself twice — the `aspire-healthcheck` sentinel attribute (logged on the `SRCH attr=`
  line) and a no-op `(cn=aspire-healthcheck)` branch in its search filter (logged on the
  `SRCH base=` line) — and either marker classifies the connection, on root-DSE searches only.
  The filter is strictly fail-open: a block is discarded only after the connection completed as
  a wholly-successful probe (marker present, every result `err=0`, clean unbind and close); any
  deviation — a nonzero result, an unexpected operation, slapd exiting mid-probe — flushes the
  withheld lines verbatim, and a crashed filter falls back to a passthrough `cat` so slapd
  never loses its stderr. Restore probe logging with `WithHealthCheckProbeLogging()`
  (`LDAP_LOG_HEALTH_PROBES=yes` standalone).
- **phpLDAPadmin's health check no longer generates LDAP query noise.** `WithPhpLdapAdmin`
  health-checked the login page, which performs a real admin bind + root-DSE query on every
  render — a continuous, un-filterable stream of `conn=N` blocks in the LDAP container's log.
  The health check now polls the static `/robots.txt` (verified served without touching LDAP).
  Behavior note: the admin container's health state no longer implies end-to-end LDAP
  connectivity — that remains covered by the LDAP resource's own health check, which the admin
  container `WaitFor`s.
- **phpLDAPadmin errors now surface in the container log.** The image's Laravel app logs to
  a file inside the container by default, so LDAP failures — unreachable server, bad admin
  bind credentials — produced a 500 page with nothing in the dashboard console.
  `WithPhpLdapAdmin` now sets `LOG_CHANNEL=stderr` and `LOG_LEVEL=info` (both overridable
  via the configure callback): connection and bind failures log as `ERROR`, login attempts
  as a one-line `INFO`, while the app's per-page-render `DEBUG` dumps stay suppressed.
- `WithLogLevel(OpenLdapLogLevel)` — typed control over slapd's debug log level
  (`LDAP_LOGLEVEL`), previously not settable from the AppHost. Flags map to slapd's
  documented bits (`Stats` is the container default); undefined bits are rejected at the
  fluent call.

## 0.5.0-preview.1 — 2026-07-18

Fixes from a second (2026-07-17) adversarial code review, findings F01–F08, plus adoption of
the [LdifDotNet](https://github.com/joshmakestuff/ldifdotnet) library for LDIF generation and
RFC 4514 DN handling (which unblocked F04 and F05).

### Breaking / behavior changes

- **Base DN and admin username are validated at model construction** (F04). `AddOpenLdap` /
  `WithBaseDn` / `WithAdminUsername` now fail in the AppHost — before Docker starts — instead
  of producing a broken DN or a mid-bootstrap container death: the base DN must be a
  well-formed RFC 4514 DN with no control characters and a `dc=`, `o=`, or `c=` leading RDN;
  the admin username must not contain characters that require DN escaping (`, + " \ < > ;`, a
  leading `#`/space, a trailing space) since the container composes `cn={username},{baseDn}`
  verbatim. The container's own `ldap_validate` enforces the same rules for standalone use,
  closing the newline-into-privileged-LDIF injection class.
- **Malformed base DNs are no longer silently mis-split.** Root-entry derivation now parses
  the base DN escape-aware (`Dn.Parse`): `o=Acme\, Inc.,c=US` no longer splits mid-value, and
  extracted values are unescaped. `c=` roots are newly supported (root entry
  `objectClass: country`) in both the typed seed generator and the container's default tree;
  previously they killed the container at "Creating LDAP default tree".
- **Seeded user passwords are stored hashed, not cleartext** (F05). `WithUser(...)` passwords
  are written to the generated LDIF as `{SSHA}` (salted SHA-1, verified natively by slapd), so
  the directory never holds the cleartext at rest — visible via `slapcat`, backups, or reads of
  `userPassword`. Binds with the original password keep working. Values already carrying an
  RFC 3112 scheme prefix (`{SSHA}...`, `{CRYPT}...`) are stored verbatim, so pre-hashed data
  migrates unchanged. Anything that read the cleartext back out of `userPassword` must now
  bind to verify instead.

- **Custom-CA LDAPS now works on Linux** (F01). The client integration and the AppHost health
  check previously threw `LdapException` before the first request on Linux; they now configure
  libldap trust natively (`TrustedCertificatesDirectory` + `StartNewTlsSessionContext`) via an
  OpenSSL hash-named CA directory staged automatically. Consequences: on Linux, hostname
  validation is always on (the `disableHealthCheckHostnameValidation` /
  `DisableTlsHostnameValidation` opt-outs now throw there), and on macOS the client fails fast
  with guidance instead of an opaque native error when asked to trust a connection-string CA.
- **The container refuses to start over a partially-initialized data directory** (F02). A
  completion marker (`.init_complete`) is written only after every init step succeeds; existing
  data without it fails startup with reset instructions instead of silently serving partial
  data with TLS/ACL/anonymous-bind configuration never applied. Volumes initialized by older
  image versions lack the marker — reset them or create the marker manually as the error
  message describes.
- Out-of-range `WithLdapPort`/`WithLdapsPort` values fail at the fluent call; connection-string
  endpoints with URI user-info or fragments are rejected at parse time.

### Fixed

- Admin passwords with repeated/leading/trailing whitespace or glob characters are hashed
  byte-exactly (F03; unquoted shell expansion previously altered the value before hashing).
- Failed init commands (rejected seed entries, schema/config errors) now log the failing file,
  command, and server diagnostic — with bind passwords redacted (F06).
- Health checks honor cancellation (surfaced as cancellation, not "unhealthy"), return promptly
  instead of blocking out the LDAP timeout, and dispose the per-probe CA certificate (F07).
- The generated-certificate cache validates the full set (CA parses, key matches the server
  certificate, chain, validity windows, SANs) before reuse, and writes files atomically (F08).

### Changed

- LDIF generation and DN handling are now backed by **LdifDotNet 0.3.0** (#23, #30): the
  hand-maintained `LdifEncoder` (RFC 2849) and `DnEscaper` (RFC 4514) were deleted in favor of
  `LdifWriter` and the `Dn` API. The admin bind DN is composed once (escaped) and reused by the
  connection string, health check, dashboard command, and phpLDAPadmin — previously four
  unescaped string interpolations.
- Adopted `Meziantou.Analyzer` across `aspireOpenLdap/` (dev-only); fixed the issues it found,
  including a misleading `ArgumentException` parameter name and a regex without a match timeout.
- Package READMEs document per-platform TLS trust behavior and seed-once/reset-volume semantics.

## 0.4.0-preview.1 — 2026-07-17

Fixes stemming from a 2026-07 adversarial code review (findings referenced as F01–F14 in PR #15).

### Breaking changes

- **The container now really runs OpenLDAP 2.6** (2.6.10, Debian 13 "trixie" base). Previous
  releases advertised 2.6 but shipped 2.5.13 from Debian 12. The Dockerfile now asserts the
  `slapd` version at build time so this cannot drift again. The bundled build-context path
  changed from `openldap/2.6/debian-12` to `openldap/2.6/debian-13`.
- **Host ports are now dynamically allocated** (proxied endpoints) instead of fixed
  1389/1636, so multiple AppHosts can run concurrently. Pin the old behavior with
  `.WithLdapPort(1389)` / `.WithLdapsPort(1636)`.
- **Connection strings now quote values** containing `;`, `"`, or leading/trailing
  whitespace (embedded quotes doubled). Consumers parsing the connection string with
  `OpenLdapConnectionStringBuilder.Parse` are unaffected; hand-rolled parsers may be.
- **LDAPS certificate validation now also checks the hostname** when trusting the
  connection-string CA — a certificate from the right CA for the wrong host is rejected.
  Opt-outs: `WithTls(..., disableHealthCheckHostnameValidation: true)` (health check),
  `DisableTlsHostnameValidation` (client settings).
- **phpLDAPadmin image is pinned** to `2.3.11` instead of `latest`.
- Hosting package license expression is now `MIT AND Apache-2.0`, accurately covering the
  bundled Bitnami-derived container sources (see `THIRD-PARTY-NOTICES.txt` in the package).

### Fixed

- `WithTls(serverCertFile, serverKeyFile, caCertFile)` now bind-mounts each file at its fixed
  container path — arbitrary host filenames work, and missing files fail at model construction.
- Generated seed LDIF now base64-encodes non-safe values per RFC 2849 and escapes DN
  components per RFC 4514 — international names, spaces, colons, and newlines are safe.
- `WithPhpLdapAdmin()` no longer freezes the parent's base DN / admin username / TLS state at
  call time; fluent order no longer matters.
- Custom schema loading stops `slapd` before running the offline `slapadd` tool.
- Release tags are now tested (full Docker-backed suite + example build) before publishing;
  OIDC publish permissions are scoped to the publish job.

### Changed

- Dependencies moved to the current stable net10 servicing baseline
  (`System.DirectoryServices.Protocols` 10.0.10, `Microsoft.Extensions.*` 10.0.10); Dependabot
  keeps them fresh. NuGet packages now include XML documentation. CI verifies formatting.

## 0.3.0-preview.1 — 2026-07-14

- Default data volume names are scoped to the AppHost (#11, breaking).
- libldap soname auto-resolution on Linux (#8/#13).
- Runnable end-to-end example with client OpenTelemetry (#10).

## 0.2.x / 0.1.x

- Initial previews on nuget.org.

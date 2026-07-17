# Changelog

## Unreleased

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

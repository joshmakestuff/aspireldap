# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately via
[GitHub Security Advisories](https://github.com/joshmakestuff/aspireldap/security/advisories/new)
rather than opening a public issue. You should receive a response within a week.

## Scope and threat model

AspireLdap is a **local development and testing** integration. The defaults are tuned for
developer convenience, and several features are explicitly not production-grade:

- **Self-signed TLS** (`WithTls()` with no arguments) generates a local CA and server
  certificate on disk under the AppHost's `obj/` directory.
- **The dashboard "Show admin password" command** reveals the admin secret to whoever can
  see the dashboard.
- **Anonymous binding** is enabled by default in the container image
  (`LDAP_ALLOW_ANON_BINDING=yes`), matching common dev-server behavior.
- **phpLDAPadmin** (`WithPhpLdapAdmin()`) connects with `LDAPTLS_REQCERT=never` when TLS is
  required, because the self-signed CA is not trusted inside that container.
- The TLS hostname-validation opt-outs (`disableHealthCheckHostnameValidation`,
  `DisableTlsHostnameValidation`) exist for local certificates only.

Do not expose the OpenLDAP container or the phpLDAPadmin UI beyond your development machine.
Reports about the above behaving as documented are appreciated but will generally be treated
as documentation issues rather than vulnerabilities; anything that undermines the security of
a consumer's *own* code or data (e.g. injection through generated LDIF, credential leakage
into telemetry) is firmly in scope.

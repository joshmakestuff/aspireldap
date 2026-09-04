# AspireLdap

This is the active product repository. It contains the Aspire OpenLDAP hosting and client
packages plus the LdapAdmin application.

## Commands

- Build the integration: `dotnet build aspireOpenLdap/AspireOpenLdap.slnx`
- Run the example AppHost: `aspire run --apphost examples/AspireOpenLdap.AppHost/AspireOpenLdap.AppHost.csproj`
- Use the Aspire workflow skills for AppHost lifecycle, diagnostics, or deployment work.

## Boundaries

- Use `LdifDotNet` for DNs, LDIF, schema, and fake data; do not duplicate those APIs here.
- Use the in-repo `Aspire.OpenLdap` APIs for connections, TLS, connection strings, and
  telemetry. See `aspireOpenLdap/AGENTS.md` for the supported surface and gotchas.
- LdapAdmin is an Aspire development tool, not a general-purpose LDAP administrator. It
  is server-rendered Blazor with InteractiveServer; UI components call services directly.
  Do not add a WASM client, a shared DTO/Contracts project, or a parallel REST dependency
  for the UI.
- LdapAdmin ships as an internal payload of the hosting package and is built locally by
  Aspire. It has no independent release lifecycle.
- The archived standalone `AspireLdapAdmin` application is historical context, not a
  compatibility target.
- LdapAdmin UI work uses owned Razor markup and the vendored Industry stylesheet. Use
  design tokens for colors, typography, spacing, radii, and shadows; verify both themes
  in a browser. Do not reintroduce Fluent UI Blazor or another component-library
  stylesheet.
- Do not add a published admin image, an admin NuGet package, or login UX unless the
  product scope is explicitly changed.
- Keep the schema view read-only. Put user-facing defaults on `WithLdapAdmin()` options,
  not in a settings page.
- Change notifications are a server capability. Do not add a second LDAP client stack
  for a first-party RFC 4533 subscription API without an explicit product decision.

## Workflow

- Use one GitHub issue per unit of work. Keep status and unresolved suspicions in the
  issue rather than adding planning documents to the repository.
- Do not create design, planning, status, findings, or decision documents unless the user
  explicitly requests one.
- Treat `artifacts/`, `.playwright/`, `.playwright-cli/`, and `working/` as disposable
  local output unless a task explicitly asks to preserve an artifact.
- Finish code changes with the narrowest relevant build or test, then broaden only when
  the change warrants it.

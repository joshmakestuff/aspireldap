# Contributing to AspireLdap

Thanks for your interest! Issues and pull requests are welcome.

## Repository layout

- `aspireOpenLdap/` — the two NuGet libraries (`Aspire.Hosting.OpenLdap` hosting integration, `Aspire.OpenLdap` client), their tests, and the test AppHost.
- `openldap/` — the bundled OpenLDAP container build context (Dockerfile + init scripts, Apache-2.0, Bitnami-derived). Shipped inside the hosting package as contentFiles.
- `examples/` — a runnable end-to-end sample AppHost + API.

## Building and testing

Prerequisites: .NET 10 SDK and Docker (the integration tests run a real OpenLDAP container).

```bash
dotnet build aspireOpenLdap/AspireOpenLdap.slnx --configuration Release
dotnet test  aspireOpenLdap/AspireOpenLdap.slnx --configuration Release
dotnet build examples/AspireOpenLdap.Examples.slnx --configuration Release
```

The first test run builds the bundled Docker image and is slow; later runs reuse it. Fast
model/unit tests can be run without Docker:

```bash
dotnet test aspireOpenLdap/AspireOpenLdap.slnx --filter "FullyQualifiedName!~Becomes_Healthy&FullyQualifiedName!~LargeSeed&FullyQualifiedName!~Instrumentation&FullyQualifiedName!~ClientTelemetry"
```

## Pull request expectations

- Run `dotnet format aspireOpenLdap/AspireOpenLdap.slnx` before pushing — CI verifies formatting.
- Every behavioral fix needs a regression test.
- If a change claims something about versions or capabilities (e.g. "runs OpenLDAP 2.6"),
  verify it empirically and, where possible, assert it in the build or tests so it can't drift.
- Changes to packaging/content files: run `dotnet pack` and inspect the produced `.nupkg`.
- Public API changes to either library should be called out explicitly in the PR description.

## Licensing

Contributions to the .NET libraries are accepted under MIT; contributions to `openldap/`
are accepted under Apache-2.0 (see `openldap/NOTICE`).

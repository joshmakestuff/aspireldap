# AspireLdap Code Review Report

Date: 2026-07-16  
Repository state reviewed: commit `514ec53` (`chore: bump version to 0.3.0-preview.1`)

## Purpose

This document records a maintainer-focused review of AspireLdap and is intended to be a durable handoff for future contributors or coding agents. It covers correctness, security, Aspire behavior, testing, packaging, dependency hygiene, CI, documentation, and open-source readiness.

No source changes were made as part of the review. The tracked worktree was clean when the review finished.

## Executive summary

AspireLdap is a strong first open-source project. Its package split is coherent, the public API follows Aspire conventions, the code is readable, and the Docker-backed regression tests provide meaningful assurance. Particularly good choices include secret parameter handling, a non-root OpenLDAP container, PII-conscious telemetry, SourceLink packages, and OIDC-based NuGet publishing.

The project should remain marked as preview until the high-priority findings below are resolved. The most important issues are:

1. The image advertised as OpenLDAP 2.6 actually runs OpenLDAP 2.5.13.
2. Apache-2.0-labelled container sources are shipped in packages declared only as MIT.
3. Generated LDIF and connection strings do not safely encode arbitrary values.
4. The custom TLS overload ignores the supplied filenames, and custom-CA validation does not verify the server hostname.
5. Default fixed LDAP ports collide with other AspireLdap instances.
6. Tagged release commits are published without running the tests or building the examples.

## Review scope

The review inspected:

- Both NuGet library projects and their public APIs
- The Aspire test AppHost and runnable example AppHost
- OpenLDAP container construction and bootstrap scripts
- Unit and Docker-backed integration tests
- NuGet packaging and generated package contents
- GitHub Actions build and publishing workflow
- Dependency versions and vulnerability metadata
- Repository documentation and open-source project hygiene

## Verification performed

The following commands completed successfully unless otherwise noted:

```powershell
dotnet build aspireOpenLdap/AspireOpenLdap.slnx --configuration Release
dotnet build examples/AspireOpenLdap.Examples.slnx --configuration Release
dotnet test aspireOpenLdap/AspireOpenLdap.slnx --configuration Release --no-build
dotnet pack aspireOpenLdap/AspireOpenLdap.slnx --configuration Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet list aspireOpenLdap/AspireOpenLdap.slnx package --vulnerable --include-transitive
dotnet list aspireOpenLdap/AspireOpenLdap.slnx package --outdated
dotnet format aspireOpenLdap/AspireOpenLdap.slnx --verify-no-changes --no-restore --severity info
```

Results:

- Main solution build: succeeded with 0 warnings and 0 errors.
- Example solution build: succeeded with 0 warnings and 0 errors.
- Tests: 20/20 passed in approximately 76 seconds.
- NuGet packaging: both `.nupkg` and `.snupkg` packages were created successfully.
- Vulnerability query: no known vulnerable direct or transitive NuGet packages were reported.
- Formatting verification: failed on whitespace in `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapOverlay.cs`, primarily around lines 43-55. It also reported informational IDE and performance suggestions that are not currently enforced by the build.

The Docker-backed test output showed the bundled server identifying itself as:

```text
slapd 2.5.13+dfsg-5
```

The tests also encountered a real port collision with an already-running AspireLdap instance on `127.0.0.1:1389`.

## Findings

### F01 — Advertised OpenLDAP version does not match the installed server

Severity: High  
Area: Correctness, release integrity

Evidence:

- `openldap/2.6/debian-12/Dockerfile:1-9` describes and labels the image as OpenLDAP 2.6.
- `openldap/2.6/debian-12/Dockerfile:17-25` installs `slapd` from the stock Debian Bookworm repository.
- `openldap/README.md:3` calls the image OpenLDAP 2.6.
- Runtime integration-test logs reported OpenLDAP 2.5.13.

Impact:

Consumers may rely on features, fixes, or security expectations associated with OpenLDAP 2.6 while actually running 2.5.13. The image directory, image tag, labels, and documentation are therefore misleading.

Recommended action:

- Either install and verify a genuine OpenLDAP 2.6 server, or rename the image path/tag/documentation to 2.5.
- Add a CI assertion that runs `slapd -VV` inside the built image and checks the expected version.

Acceptance criteria:

- The installed server version matches the version in the Docker path, image tag, OCI label, and README.
- CI fails if the version drifts.

### F02 — Bundled-source licensing and package metadata are inconsistent

Severity: High  
Area: Open-source compliance, packaging

Evidence:

- `openldap/2.6/debian-12/Dockerfile:3` declares `SPDX-License-Identifier: Apache-2.0`.
- The scripts under `openldap/2.6/debian-12/rootfs/opt/openldap/scripts/` also declare Apache-2.0.
- `openldap/README.md:142-144` says the image is Apache-2.0 and derived from Bitnami.
- The root `LICENSE` contains only the MIT license.
- `aspireOpenLdap/Directory.Build.props:8` declares `PackageLicenseExpression` as MIT.
- The generated hosting `.nupkg` includes the Apache-labelled Dockerfile and scripts but no Apache license or notice file.

Impact:

The package tells consumers it is MIT-only while distributing sources marked Apache-2.0. This should be resolved before another release. This report identifies the inconsistency; it is not legal advice.

Recommended action:

- Confirm the provenance and licensing requirements of the Bitnami-derived files.
- Include the appropriate Apache license and any required notices in the repository and hosting package.
- Clearly document which portions are MIT and which are Apache-2.0, or relicense/rewrite only where legally permitted.

Acceptance criteria:

- Repository and NuGet metadata accurately describe all bundled content.
- Required license and notice files are present in the generated package.

### F03 — Generated LDIF does not escape or encode values

Severity: High  
Area: Correctness, input safety

Evidence:

- `aspireOpenLdap/Aspire.Hosting.OpenLdap/Seeding/LdapSeedLdifGenerator.cs:22-60` appends DNs, `cn`, `sn`, `mail`, passwords, and member DNs directly into LDIF.
- `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResourceBuilderExtensions.cs:592-612` accepts arbitrary non-empty user values.
- `WithBaseDn` and `WithAdminUsername` validate only that values are not blank.
- `LdapSeedValidator` restricts OU, UID, and group names but does not encode the other fields.

Impact:

Newlines can inject additional attributes or entries. Leading spaces, colons, and some Unicode or binary values require LDIF base64 encoding. Valid real-world names may produce invalid LDIF, while hostile values can alter the generated document structure.

Recommended action:

- Implement an RFC 2849-compatible LDIF value encoder.
- Validate or correctly escape DN components according to RFC 4514.
- Apply encoding consistently to all generated values, including root entries and literal group DNs.
- Add unit tests covering newlines, leading/trailing spaces, colons, Unicode, commas, backslashes, and empty optional values.

Acceptance criteria:

- Generated LDIF loads successfully for representative international and special-character data.
- User-controlled values cannot add LDIF lines or entries.

### F04 — Connection-string parsing cannot represent arbitrary credentials

Severity: Medium-high  
Area: Correctness, configuration

Evidence:

- `aspireOpenLdap/Aspire.OpenLdap/OpenLdapConnectionStringBuilder.cs:31-42` splits the connection string unconditionally on semicolons.
- `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResource.cs:118-130` emits values without quoting or escaping.

Impact:

A caller-supplied password such as `abc;def` is parsed as multiple segments and corrupts the connection string. Similar ambiguity exists for other values containing connection-string delimiters.

Recommended action:

- Use a standard quoted key/value connection-string serializer and parser, or define and implement explicit escaping rules.
- Reject duplicate keys, unknown endpoint schemes, missing hosts, invalid ports, and inappropriate URI paths/query strings.
- Add round-trip tests for delimiters, quotes, whitespace, Unicode, and empty values.

Acceptance criteria:

- Every emitted connection string can be parsed back into exactly the original values.
- Arbitrary generated or caller-supplied passwords work correctly.

### F05 — Custom TLS filenames are ignored

Severity: High  
Area: Correctness, TLS

Evidence:

- `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResourceBuilderExtensions.cs:968-990` accepts three file paths but passes only their common directory to `ApplyTls`.
- `ApplyTls` at lines 1029-1042 always configures `/tls/server.crt`, `/tls/server.key`, and `/tls/ca.crt`.

Impact:

Valid calls using names such as `certificate.pem`, `private-key.pem`, and `root.pem` fail because those files are never mounted at the paths advertised to OpenLDAP.

Recommended action:

- Validate that all three files exist.
- Bind-mount each supplied file directly to its fixed container path, rather than mounting only the directory.
- Use platform-appropriate path comparison if a common-directory constraint is retained.
- Add a Docker-backed test using deliberately non-default filenames.

Acceptance criteria:

- Custom TLS works regardless of the host filenames.
- Missing or mismatched files fail during AppHost model construction with actionable messages.

### F06 — Custom-CA verification does not validate the server hostname

Severity: Medium-high  
Area: TLS security

Evidence:

- `aspireOpenLdap/Aspire.OpenLdap/OpenLdapClientFactory.cs:76-84` builds a chain to the custom root and sets `X509VerificationFlags.IgnoreInvalidName`.
- The hosting health check uses equivalent logic in `OpenLdapHealthCheck.cs:130-138`.

Impact:

Any server certificate chaining to the supplied CA can be accepted, even when its subject alternative names do not match the LDAP endpoint. This is especially significant when users provide a reusable organizational CA rather than the per-resource generated CA.

Recommended action:

- Validate the certificate SAN/CN against the endpoint host in addition to building the custom-root chain.
- Avoid `IgnoreInvalidName` except behind an explicit local-development opt-out.
- Add tests proving that a correctly chained certificate for the wrong host is rejected.

Acceptance criteria:

- Chain and hostname must both validate by default.
- Any insecure development override is explicit and clearly documented.

### F07 — Default fixed ports collide with other instances

Severity: Medium-high  
Area: Aspire orchestration, developer experience

Evidence:

- `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResourceBuilderExtensions.cs:53-54` creates both endpoints with `isProxied: false`.
- `aspireOpenLdap/Aspire.Hosting.OpenLdap/README.md:67` documents fixed host ports 1389 and 1636.
- XML comments on `WithLdapPort` and `WithLdapsPort` say Aspire allocates random ports by default, contradicting the package README and runtime behavior.
- The integration tests encountered `Bind for 127.0.0.1:1389 failed: port is already allocated` while another sample instance was running.

Aspire API inspection confirmed that proxied endpoints can use a different public port from the resource's internal target port.

Impact:

Two AspireLdap AppHosts cannot reliably run at the same time without explicit port customization. Tests and local development can accidentally connect to the wrong existing LDAP server after a bind failure.

Recommended action:

- Use Aspire-managed proxied endpoints with dynamically allocated host ports by default.
- Keep `WithLdapPort` and `WithLdapsPort` as explicit fixed-port opt-ins.
- Add an integration test that runs two independent AppHosts or two LDAP resources simultaneously.

Acceptance criteria:

- Two default AspireLdap instances can run concurrently.
- Documentation and XML comments agree on allocation behavior.

### F08 — `WithPhpLdapAdmin` is sensitive to fluent call order

Severity: Medium  
Area: Public API behavior

Evidence:

- `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResourceBuilderExtensions.cs:904-940` eagerly captures the parent name, base DN, admin username, and `TlsRequired` state.

Impact:

For example, `.WithPhpLdapAdmin().WithTls().WithRequiredTls()` leaves phpLDAPadmin configured for plain LDAP and stale parent settings. Similar behavior occurs if the base DN or admin username changes after adding the sidecar.

Recommended action:

- Resolve parent values lazily through environment callbacks where possible.
- Make TLS-sidecar configuration update when required TLS is enabled later, or enforce and document a strict ordering contract.
- Add permutation tests for the supported fluent calls.

Acceptance criteria:

- Semantically equivalent fluent call orders produce equivalent resource models, or unsupported orders fail immediately with clear guidance.

### F09 — Release tags skip validation before publishing

Severity: Medium-high  
Area: CI/CD, supply chain

Evidence:

- `.github/workflows/build.yml:44-55` skips both tests and the example build when the ref is a tag.
- The same tagged job packs and pushes NuGet packages at lines 57-92.

Impact:

The exact commit being published is not tested by the publishing workflow. A tag can point to an untested or non-master commit.

Recommended action:

- Run unit, integration, example-build, and package-validation steps for tags before publishing.
- If runtime is a concern, separate fast checks and Docker-backed checks into jobs and make publishing depend on both.
- Restrict `id-token: write` to the publishing job rather than granting it to all build and pull-request executions.

Acceptance criteria:

- Publishing cannot run unless the exact tagged commit passes all required checks.

### F10 — Dependency versions are inconsistent with the target framework

Severity: Medium  
Area: Maintenance, compatibility

Evidence:

- `aspireOpenLdap/Directory.Packages.props` references `Microsoft.Extensions.Diagnostics.HealthChecks` `10.0.0-preview.5.25277.114` in net10 packages.
- It references `System.DirectoryServices.Protocols` 9.0.5 while targeting net10.
- A stable-version query on 2026-07-16 reported 10.0.10 for both packages.
- Other direct Microsoft.Extensions dependencies were at 10.0.0 rather than current 10.0.x servicing releases.
- No known vulnerable packages were reported by NuGet vulnerability metadata.

Recommended action:

- Update to a coherent, stable net10 dependency baseline and rerun the Linux native-library and TLS tests.
- Add Dependabot or Renovate with grouped .NET/Aspire updates.
- Consider NuGet lock files for CI reproducibility if appropriate for the repository's release model.

Acceptance criteria:

- Published packages do not depend on obsolete previews when stable equivalents exist.
- Automated dependency update PRs exercise the full test suite.

### F11 — Custom schema loading uses `slapadd` while slapd is running

Severity: Medium  
Area: Container initialization, data integrity

Evidence:

- `openldap/2.6/debian-12/rootfs/opt/openldap/scripts/libopenldap.sh:514-532` invokes `slapadd` against the online configuration directory and only stops slapd afterward.

Impact:

Offline database tools should not modify an actively used configuration database. The current order risks inconsistent configuration or failures dependent on timing and platform behavior.

Recommended action:

- Stop slapd before invoking `slapadd` for custom schemas.
- Restart it only after all schema files have loaded successfully.
- Add an integration test that loads multiple dependent custom schemas.

Acceptance criteria:

- No offline OpenLDAP database tool runs against a live database.

### F12 — Quality gates do not yet match the apparent build quality

Severity: Medium  
Area: Maintainability, package quality

Evidence:

- Normal builds report zero warnings, but analyzer and formatting enforcement is not configured centrally.
- `dotnet format --verify-no-changes` fails in `OpenLdapOverlay.cs:43-55`.
- The generated NuGet packages contain DLLs but no XML documentation files, despite extensive public XML comments in the source.
- Package validation and public API compatibility baselines are not enabled.

Recommended action:

- Enable XML documentation generation for both public packages and include the XML files in the packages.
- Add a formatting check to CI.
- Configure an intentional analyzer baseline, suppressing namespace-layout diagnostics where the Aspire-compatible namespaces are deliberate.
- Enable warnings-as-errors for CI after establishing the baseline.
- Add NuGet package validation and a public API compatibility mechanism before 1.0.

Acceptance criteria:

- Formatting verification passes locally and in CI.
- NuGet packages include XML documentation.
- API-breaking changes are detected automatically.

### F13 — The test suite does not cover most of the public surface

Severity: Medium  
Area: Testing

Existing strengths:

- A real OpenLDAP container is used.
- The large-seed health-gating regression is valuable.
- Telemetry includes privacy-focused unit tests and a real integration test.
- The Linux native resolver is exercised in CI.

Important gaps:

- Generated LDIF and validation edge cases
- Connection-string parsing and round trips
- Generated and custom TLS
- Certificate hostname rejection
- Resource endpoint annotations and port allocation
- `WithPhpLdapAdmin` ordering and TLS behavior
- Overlays and access-control generation
- Custom schemas and seed failure diagnostics
- Data-volume persistence and reset behavior
- DI registration, including multiple keyed clients
- Consumption of the generated `.nupkg` from a clean temporary project

Recommended action:

- Separate fast model/unit tests from Docker-backed integration tests.
- Add focused tests for each public fluent method.
- Add a packaged-consumer smoke test so content-file and build-output behavior is verified from the artifact users actually install.

### F14 — Reproducibility and repository hygiene improvements

Severity: Low to medium  
Area: Maintenance, contributor experience

Items:

- `aspireOpenLdap/Aspire.Hosting.OpenLdap/PhpLdapAdminResource.cs:8-9` uses `phpldapadmin/phpldapadmin:latest`. Pin a tested version or digest and update it through automation.
- `openldap/2.6/debian-12/Dockerfile:5` uses a floating Debian tag and unpinned apt packages. Choose an explicit update policy and test it regularly.
- `.aspire/settings.json:2` points to the nonexistent `aspireOpenLdap/AspireOpenLdap.AppHost/AspireOpenLdap.AppHost.csproj`, while `aspire.config.json` correctly points to the example AppHost. Remove or correct the stale settings file.
- Add `CONTRIBUTING.md`, `SECURITY.md`, a changelog, a code of conduct, issue templates, and pull-request guidance.
- Consider documenting which features are intended only for local development, especially self-signed TLS, password-reveal commands, anonymous binding, and phpLDAPadmin.

## Recommended implementation sequence

### Phase 1 — Release integrity

- [ ] Resolve F01: actual OpenLDAP version versus advertised version.
- [ ] Resolve F02: bundled-source licensing and package metadata.
- [ ] Prevent publishing until the exact tag passes validation (F09).

### Phase 2 — Correctness and security

- [ ] Implement LDIF/DN encoding and tests (F03).
- [ ] Replace connection-string parsing with a round-trippable implementation (F04).
- [ ] Fix custom TLS file mounting (F05).
- [ ] Add certificate hostname validation (F06).
- [ ] Make default endpoints dynamically allocated (F07).
- [ ] Remove fluent-order sensitivity (F08).
- [ ] Correct custom-schema lifecycle ordering (F11).

### Phase 3 — Regression coverage

- [ ] Add fast unit/model tests for every public builder method.
- [ ] Add Docker-backed TLS, schema, overlay, ACL, persistence, and multi-instance tests.
- [ ] Add a clean packaged-consumer smoke test.

### Phase 4 — Stable-release engineering

- [ ] Move dependencies to a coherent stable baseline (F10).
- [ ] Add formatting, analyzers, XML documentation, package validation, and API compatibility checks (F12).
- [ ] Pin or automate external image/base-image updates.
- [ ] Add contributor and security documentation.

## Suggested agent task boundaries

The following tasks can be assigned independently, but agents should avoid editing the same central files concurrently:

1. **Version and container task** — F01 and F11; primarily `openldap/`.
2. **Licensing task** — F02; repository/package metadata and bundled license files. Human confirmation may be required.
3. **Serialization task** — F03 and F04; seed generator, validator, resource connection string, parser, and tests.
4. **TLS task** — F05 and F06; hosting TLS setup, client factory, health check, and integration tests.
5. **Aspire resource-model task** — F07 and F08; hosting extension methods and model tests.
6. **CI/package task** — F09, F10, and F12; workflow, central package versions, package validation, and documentation generation.
7. **Contributor-experience task** — F13 and F14; test organization, repository documentation, templates, and stale config cleanup.

When assigning work, ask each agent to:

- Preserve unrelated user changes.
- Add regression tests for every behavioral fix.
- Run the main and example builds.
- Run fast tests first, then the Docker-backed suite.
- Inspect the generated NuGet package when changing packaging or content files.
- Avoid stopping or deleting pre-existing Docker containers or volumes that may belong to the user.

## Definition of readiness for a stable release

A stable release should, at minimum, satisfy all of the following:

- The advertised and runtime OpenLDAP versions match.
- All bundled-source licensing is accurately represented in the repository and package.
- LDIF, DNs, and connection strings safely round-trip arbitrary supported values.
- Custom and generated TLS pass positive and negative hostname-validation tests.
- Multiple default AppHosts can run concurrently without port conflicts.
- Every tagged artifact is built and tested from the exact published commit.
- Packages depend only on intentional stable dependencies.
- Formatting, analyzers, XML documentation, package validation, and API compatibility checks pass.
- The generated NuGet packages are tested from a clean consumer project.
- Contributor and vulnerability-reporting guidance is available.


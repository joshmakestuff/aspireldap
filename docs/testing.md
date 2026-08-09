# Testing strategy: what each tier protects

This repository's suite is not one pool of tests. It is three tiers with different powers and
different blind spots, plus two measurements (coverage, mutation) that each answer a *different*
question and are easy to misread as answering the same one.

Read this before quoting a coverage percentage or adding a test "to raise a number".

## The three tiers

| Tier | How it runs | What it can prove | What it cannot prove |
|---|---|---|---|
| **Fast** (`Category!=Integration&Category!=CleanConsumer`) | In-process, no Docker. The whole tier is a few seconds. | Pure/deterministic contracts: connection-string parse+quote round-trips, DN and seed-model validation, certificate hostname matching, LDIF generation, AppHost *model* shape (env vars, mounts, annotations, endpoints). | Anything that depends on slapd, on a real TLS handshake, or on the native LDAP library actually being loadable. A model assertion proves we *asked* for a mount, never that the container honored it. |
| **Direct Docker** (`Category=Integration`, driven through `DockerCli`) | Runs the bundled image directly, asserting on container exit codes, logs, and same-volume restarts. | Bootstrap/init semantics the Aspire harness cannot reach: failed-seed exit behavior, the partial-init completion-marker gate, password hashing byte-exactness, probe-log filtering, `LDAP_ROOT` validation. | Anything about how the Aspire resource model wires up — this tier bypasses it. |
| **Full AppHost** (`Category=Integration`, via `AppHostFixture`) | Starts the TestAppHost through `Aspire.Hosting.Testing`, waits for health, then talks LDAP to the running server. | The consumer-facing path end to end: published connection string, health gating (including large-seed gating), LDAPS through the real client factory, telemetry through DI registration, fake-data materialization, overlay/ACL applies actually changing server behavior. | Container-internal failure modes (that is the direct-Docker tier), and anything the hosted CI runner's OS cannot start. |
| **Clean consumer** (`Category=CleanConsumer`, `CleanConsumerPackTests`) | Packs the hosting package, restores it into a scaffolded consumer AppHost in an isolated temp workspace (local feed + nuget.org only), and runs `AddOpenLdap(...).WithLdapAdmin()` end to end. | The *packed artifact* boundary (#82): every path resolves from package-delivered assets — missing packaged files, checkout-relative paths, and hosting-API/payload drift all fail here and nowhere else. | Nothing about behavioral depth — the AppHost tier owns that. It boots one strict scenario (required TLS) and verifies startup, `/health`, and the admin→LDAP round trip. |

`AppHostCollection` serializes the AppHost tier: more than one AppHost alive in a process
contends on orchestration ports and hangs.

## Coverage: what the number means, and what it structurally cannot mean

Collected by the fast tier only, filtered to first-party shipped assemblies by
[`aspireOpenLdap/coverage.runsettings`](../aspireOpenLdap/coverage.runsettings), merged across
test projects, and published as a CI artifact plus a job summary.

- **Denominator:** `Aspire.OpenLdap`, `Aspire.Hosting.OpenLdap`, `Aspire.LdapAdmin.Core`,
  `Aspire.LdapAdmin.Web`. Framework/Aspire packages and the test AppHosts are excluded. CI
  asserts this with an allow-list, so a filter regression that lets framework source back into
  the denominator fails the build rather than quietly inflating (or deflating) the percentage.
  The unfiltered collector aggregate was ~11.7%, essentially measuring the Aspire dependency
  graph.
- **Scope:** the fast tier, in-process.
- **The structural limitation:** Docker containers and child AppHost processes execute *outside*
  the collector. Everything the two integration tiers prove is invisible to this metric. A type
  whose real work happens inside the container (`OpenLdapHealthCheck` is the clearest example)
  scores low no matter how thoroughly it is witnessed. **A low first-party line number is not
  evidence that something is untested, and a high one is not evidence that it works.**
- **No threshold is enforced**, deliberately. A percentage gate rewards executing lines over
  protecting contracts, and the cheapest way to move this particular number would be to start a
  container inside the fast tier — which is exactly the thing that must not happen. Contract
  protection is enforced by mutation testing instead.

Reproduce locally from `aspireOpenLdap/`:

```bash
dotnet test AspireOpenLdap.slnx -c Release --filter "Category!=Integration&Category!=CleanConsumer" \
  --collect "XPlat Code Coverage" --settings coverage.runsettings \
  --results-directory ../artifacts/test-results
dotnet tool restore
dotnet reportgenerator "-reports:../artifacts/test-results/*/coverage.cobertura.xml" \
  "-targetdir:../artifacts/coverage-report" "-reporttypes:Cobertura;TextSummary"
```

Merging matters: each test project emits its own cobertura file, and a project that exercises
none of the included assemblies emits a 0%-of-everything file. Quoting a single file is wrong.

## Mutation testing: scope, thresholds, survivor policy

Coverage says a line ran. Mutation asks whether anything would have *noticed* if that line were
wrong. It runs against a deliberately small set of boundaries — configured in
[`stryker-config.client.json`](../aspireOpenLdap/stryker-config.client.json) and
[`stryker-config.hosting.json`](../aspireOpenLdap/stryker-config.hosting.json):

| Boundary | File |
|---|---|
| Connection-string parsing | `Aspire.OpenLdap/OpenLdapConnectionStringBuilder.cs` |
| Connection-string quoting | `Shared/ConnectionStringQuoting.cs` |
| Certificate hostname validation | `Shared/OpenLdapCertificateValidation.cs` |
| DN / admin-username validation | `Aspire.Hosting.OpenLdap/OpenLdapDnValidation.cs` |
| Seed-model validation | `Aspire.Hosting.OpenLdap/Seeding/LdapSeedValidator.cs` |
| LDIF generation | `Aspire.Hosting.OpenLdap/Seeding/LdapSeedLdifGenerator.cs` |

### Scope rules (these are the point, not an optimization)

- **Deterministic pure code only.** Every mutant must be killable by an assertion about a
  contract, not by a container refusing to start.
- **`Category!=Integration&Category!=CleanConsumer` is enforced in the config.** A mutation
  run never starts Docker (nor packs and boots a consumer AppHost). Both legs finish in about
  two minutes.
- **Nothing container-touching, nothing model-wiring, nothing crossing the native LDAP
  boundary** is mutated. Those are witnessed by the integration tiers, where "the mutant died"
  would mean "a container failed", which is not a contract statement.

### Thresholds

`break` is set from the **measured** baseline, less a two-point margin for run-to-run timeout
jitter — not from a round number someone liked. It is a ratchet: raise it when the score rises;
never lower it to make a run pass.

Baselines at the time of writing (Windows, .NET 10.0.301):

| Leg | Score | Per file |
|---|---|---|
| client (`break: 88`) | **90.59%** | `OpenLdapConnectionStringBuilder` 89.6%, `ConnectionStringQuoting` 90.9%, `OpenLdapCertificateValidation` 96.4% |
| hosting (`break: 89`) | **91.07%** | `OpenLdapDnValidation` 92.9%, `LdapSeedLdifGenerator` 94.4%, `LdapSeedValidator` 87.8% |

### Survivor policy

Every surviving mutant must be dispositioned in one of three ways. "It survived and the score is
still above the threshold" is not a disposition.

1. **Kill it.** The default. A survivor usually means a real contract has no assertion. The
   surviving mutants in the first run of this configuration were exactly that, and they found
   two genuine gaps: `LdapSeedValidator`'s entire rejection surface (duplicate names, undeclared
   references, empty passwords, the "did you mean" hint) had no fast test at all, and the
   connection-string tests asserted only `FormatException` — so mutants that made the parser
   reject input *for the wrong reason* survived. Both are now asserted by message.
2. **Accept it as equivalent, in writing.** A mutant that cannot change observable behavior. It
   must be named here with the argument:
   - *Exception message text* (`"…"` → `""`). Accepted where the fragment is explanatory prose
     (e.g. the middle sentence of the admin-username rejection, which explains *why* rather than
     what to do). **Not** accepted where the message is the contract — the offending value, the
     rejected character set, the remedy, and the platform-limitation messages users are told to
     act on are all asserted by substring.
   - *`OpenLdapConnectionStringBuilder` loop-bound arithmetic in `ParsePairs`.* Several
     `i < len` → `i <= len` mutants are masked by the immediately following
     `if (i >= len) break;` guard.
   - *`ResolveUserDn`'s `First` → `FirstOrDefault`.* `Validate` runs before `Generate` and
     guarantees the uid exists; the difference is unobservable in a valid pipeline.
   - *The `break` in `RootEntry`'s `o=` search.* Only observable with two `o=` components in one
     base DN, which resolves to the same first match either way.
   - *`LevenshteinDistance` internals and the suggestion threshold.* This is hint *quality*, not
     correctness: a worse suggestion is still a correct rejection. The rejection itself, and
     which of the three hint shapes is produced, are asserted.
3. **Accept it as unreachable defence-in-depth, in writing.** Code guarded by an earlier check
   that cannot be bypassed through any public entry point — currently
   `LdapSeedLdifGenerator.RootEntry`'s unsupported-root `throw`, which `OpenLdapResource`'s
   constructor makes unreachable (the reachable form of that rule *is* asserted, at
   construction), and `MatchesDnsName`'s empty-pattern guard, which `X509SubjectAlternativeNameBuilder`
   gives no way to produce a certificate for.

Reproduce locally from `aspireOpenLdap/`:

```bash
dotnet tool restore
dotnet stryker -f stryker-config.client.json  -O ../artifacts/stryker/client
dotnet stryker -f stryker-config.hosting.json -O ../artifacts/stryker/hosting
```

## Cross-platform witnesses, and where they are reduced

The client stack sits on a **different native LDAP implementation per OS**, so a Linux-only CI
run leaves real code paths unwitnessed.

| Platform | CI | Covers | Reduction |
|---|---|---|---|
| Linux (`ubuntu-latest`) | Fast + both integration tiers + examples build | Everything, including `libldap` trust via a hash-named CA directory. The runner ships only `libldap.so.2`, which is what proves the `DllImportResolver` works on a 2.6-only distro. | — |
| Windows (`windows-latest`) | Fast suite | `wldap32` paths and the managed `VerifyServerCertificate` callback. | **No integration tier.** Hosted Windows runners run the Windows container engine and cannot start the Linux OpenLDAP image, so the single integration command is not achievable on hosted runners at all. Closing it requires a self-hosted Windows runner with Docker Desktop in Linux-container mode (see #54 for the orchestration failure observed there). |
| macOS | Not run in CI | — | Apple's `LDAP.framework` supports neither the managed verification callback nor OpenSSL-style trust options, so **client-side custom CA trust is refused up front**. |

macOS reductions are **asserted, never silently skipped**. Where a test would otherwise return
early on macOS, it first pins the `PlatformNotSupportedException` and its actionable message,
then writes an explicit `LIMITATION (macOS): …` line to the test output naming what did not run
(`TlsIntegrationTests`, `OpenLdapClientFactoryTests`). xunit 2.9 has no dynamic skip, so the
reduction is recorded in the log rather than marked "skipped" — but no macOS test path reaches a
green result having asserted nothing.

## Adding a test: which tier?

- Can the contract be stated as "given this input, this output/rejection"? **Fast tier**, and
  assert the *specific* message or shape, not just the exception type.
- Does it depend on slapd's actual behavior, on a real handshake, or on the native library?
  **Integration tier** — and reuse an existing `AppHostFixture` scenario or the shared
  `BundledImage` build rather than adding a container start.
- Never add a container start to move a coverage percentage. That trade is always bad: it slows
  the suite, it does not make the contract safer, and the resulting number is still not
  measuring what it appears to measure.

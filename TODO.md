# OpenLDAP server-side OTel — pick-up notes

This branch (`feat/openldap-otel-server-logs`) holds in-flight work to surface
slapd operations as OpenTelemetry spans in the Aspire dashboard. The current
commit on this branch is an **experimental AppHost-side log parser that needs
to be deleted** — we hit a delivery dead-end and decided to pivot. This doc
captures everything a fresh session needs to start over with the right design.

## TL;DR — where we landed

- **Goal:** Surface every LDAP operation (BIND/SRCH/ADD/MOD/DEL/CMP) slapd handles
  as a span in the Aspire dashboard's Traces tab, filtered to exclude the
  Aspire-injected healthcheck noise.
- **Primary consumer:** Shibboleth IdP (Java/SAML). A .NET Graph→LDAP sync app
  is hypothetical, so server-side observability beats client-side wrapping for now.
- **Approved approach:** A **sidecar OTel Collector container** added as a sibling
  Aspire resource by `WithOpenTelemetry()`. The collector tails slapd's log file
  via a shared volume, parses with `recombine` + regex_parser + OTTL, filters
  healthchecks, and ships OTLP back to the dashboard.
- **Rejected approaches and why:**
  - *AppHost-side log parser* — implementation works (regex parsing, BIND deferral,
    span emission via `ActivitySource`) but can't deliver to the dashboard. The
    `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` env var (`https://localhost:21068`) is
    stale config; no process actually listens there in `--isolated` mode. DCP
    chooses dynamic ports we can't discover from the AppHost process.
  - *AppHost-side structured logs* via `ResourceLoggerService.GetLogger(resource)` —
    would work, but loses the Traces tab. Acceptable fallback if the sidecar
    feels too heavy later.
  - *OTel Collector path with our own discovery code* — couldn't find the live
    dashboard port from the AppHost. **Solved by practical-otel** (see below).

## Already shipped on `master`

Commit `b30ceba` — **healthcheck sentinel attribute**: both healthchecks
(`OpenLdapHealthCheck.cs` and `OpenLdapClientExtensions.cs`'s client check) now
request a synthetic `aspire-healthcheck` attribute in the SearchRequest's
`attributeList`. slapd logs the attribute list verbatim. Downstream parsers
(the collector we're about to build) filter records by exact match on that
string. This change is independent of the parser approach and ships regardless
of which delivery mechanism wins.

## Code to throw away on this branch

The AppHost-side parser experiment created these files. **Delete all of them**
when starting the sidecar work:

```
aspireOpenLdap/Aspire.Hosting.OpenLdap/Tracing/OpenLdapDiagnostics.cs
aspireOpenLdap/Aspire.Hosting.OpenLdap/Tracing/OpenLdapLogParser.cs
aspireOpenLdap/Aspire.Hosting.OpenLdap/Tracing/OpenLdapLogParserHost.cs
aspireOpenLdap/Aspire.Hosting.OpenLdap/Tracing/OpenTelemetryDiagnosticsListener.cs
```

Revert from `aspireOpenLdap/Aspire.Hosting.OpenLdap/Aspire.Hosting.OpenLdap.csproj`:

- `<NoWarn>$(NoWarn);NU1902</NoWarn>` (the NU1902 suppression in PropertyGroup)
- Three `<PackageReference>` entries: `OpenTelemetry.Extensions.Hosting`,
  `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Console`

Revert from `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResourceBuilderExtensions.cs`:

- The `using OpenTelemetry.Resources;` and `using OpenTelemetry.Trace;` lines
- The `WithOpenTelemetry()` method (we'll rewrite it for the sidecar)
- `EnsureOpenTelemetryRegistered` helper + the `OpenLdapTracingMarker` private class
- `using Microsoft.Extensions.DependencyInjection.Extensions;` if unused after
- `using Microsoft.Extensions.Hosting;` if unused after

Revert from `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapResource.cs`:

- The `OpenTelemetryEnabled` internal property

Revert from `aspireOpenLdap/AspireOpenLdap.AppHost/AppHost.cs`:

- The `.WithOpenTelemetry()` call (will come back when the sidecar lands)

The `WithDataVolume()` call in AppHost.cs is fine to keep — it was already
added to demo the `reset-data-volume` command on master.

## What we learned that's worth keeping

These insights apply to the collector's YAML config — they're not throwaway:

1. **slapd's log format includes a thread ID after the timestamp.**
   Lines look like:

   ```
   6a14cb9c.25847722 0x7d5283fff6c0 conn=1000 op=0 BIND dn="cn=admin,dc=example,dc=org" method=128
   ```

   Don't anchor regex on the prefix — start at `\bconn=`. The thread ID and
   timestamp format vary across slapd versions.

2. **A single LDAP operation spans multiple lines.** Example for SRCH:

   ```
   conn=N op=M SRCH base="..." scope=2 deref=0 filter="..."
   conn=N op=M SRCH attr=mail cn aspire-healthcheck
   conn=N op=M SEARCH RESULT tag=101 err=0 nentries=1 text=
   ```

   The `recombine` operator in the OTel Collector's filelog receiver is built
   for exactly this: merge lines sharing `conn=N op=M` until the terminating
   `RESULT` (or `SEARCH RESULT`) line arrives, emit one record.

3. **BIND completes before the sentinel-bearing SRCH on the same connection.**
   Each healthcheck connection does: BIND → BIND result → SRCH → SRCH attr line
   (this is where the `aspire-healthcheck` sentinel appears) → SRCH result →
   UNBIND. If you filter ops one at a time, the BIND span slips through before
   you know the connection is a probe.

   **Fix in OTTL:** filter by *connection* not by *operation*. Buffer per-conn
   and drop the whole connection's ops if any op carries the sentinel. The
   `groupbyattrs` processor or an OTTL stateful transform can do this. Worst
   case, filter post-hoc by tagging spans with `aspire.healthcheck=true` and
   suppressing in a `filter` processor.

4. **slapd writes to stderr by default.** To get logs into a file the collector
   can tail, tweak the entrypoint at
   `openldap/2.6/debian-12/rootfs/opt/openldap/scripts/openldap/run.sh` (or
   similar — check what actually exists). Tee stderr to
   `/var/log/slapd/slapd.log`. Mount that path on a named volume that the
   collector also mounts read-only.

5. **slapd's `olcLogLevel` defaults to `stats` (256) in this container** —
   that gives us the `conn=N op=M ...` format we need. The existing
   `libopenldap.sh` doesn't override it. Document this in the
   `WithOpenTelemetry()` XML doc as a dependency; don't enforce in code.

6. **Tag semantic conventions we already drafted** (for OTTL `set` statements):

   ```
   db.system               = "ldap"
   ldap.operation          = bind|srch|add|mod|del|cmp
   ldap.dn                 = <bind DN, for BIND>
   ldap.base_dn            = <search base, for SRCH>
   ldap.scope              = base|onelevel|subtree|children
   ldap.bind_mech          = SIMPLE|SASL|...
   ldap.result_code        = <int>
   ldap.entries_returned   = <int, SRCH only>
   ldap.connection_id      = <slapd's conn=N>
   ldap.message_id         = <slapd's op=M>
   ```

   Set span status to Error when `ldap.result_code != 0`.

## The practical-otel project

URL: <https://github.com/practical-otel/opentelemetry-aspire-collector>

This project solved the dashboard-OTLP-discovery problem before we did.
**The trick we're borrowing:**

```csharp
// From their CollectorExtensions.cs
var url = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]
    ?? builder.Configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]
    ?? "http://localhost:18889";

// Then rewrite localhost → host.docker.internal so the container can reach it
var hostName = configuration["AppHost:ContainerHostname"] ?? "host.docker.internal";
var dashboardOtlpEndpoint = url.Replace("localhost", hostName, ...);

// Pass into container via env var; YAML references ${env:ASPIRE_ENDPOINT}
.WithEnvironment("ASPIRE_ENDPOINT", dashboardOtlpEndpoint)
.WithEnvironment("ASPIRE_API_KEY", builder.Configuration["AppHost:OtlpApiKey"]);
```

The collector's YAML then exports to `${env:ASPIRE_ENDPOINT}` with the
`x-otlp-api-key` header. **This is the piece I couldn't figure out on my own** —
reading from `IConfiguration` (not `Environment.GetEnvironmentVariable`) AND
rewriting `localhost` for container reachability.

**Compatibility notes (their project is on Aspire 9.4 / .NET 8; we're on 13.3.5 / .NET 10):**

| Their code uses | Status in 13.3.4 | Action |
|---|---|---|
| `IDistributedApplicationLifecycleHook` (for `WithAppForwarding`) | Obsolete; use Eventing API | **Skip — we don't need `WithAppForwarding`** |
| `TryAddLifecycleHook<T>()` | Exists, marked obsolete | Skip (same reason) |
| `builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]` | Works | Use as-is |
| `ReplaceLocalhostWithContainerHost` | Pure string manipulation | Use as-is |
| `appModel.GetProjectResources()` | Still on `ProjectResourceExtensions` | Not needed |
| `WithEndpoint(targetPort, name, scheme)` | Same signature | Use as-is |
| `DevCertHostingExtensions` (their dev-cert helper) | Their own | Skip — we don't expose HTTPS OTLP receiver |

**What to vendor (≈50–80 lines):**

- Their `AddOpenTelemetryCollector` logic — **rename to something
  integration-specific like `AddOpenLdapLogsCollector` or keep it private**, to
  avoid colliding if the user later adopts the upstream package.
- `ReplaceLocalhostWithContainerHost` (3 lines).
- Inline what we need from `OpenTelemetryCollectorSettings` / `CollectorResource`
  instead of vendoring those whole. We only need one endpoint and one image.

**What to skip:**

- `EnvironmentVariableHook` + `WithAppForwarding` — uses the obsolete lifecycle
  API and we don't need the redirection (we're not making other resources
  forward through this collector; it only tails slapd's file).
- `DevCertHostingExtensions` — we don't host an HTTPS receiver.

License is MIT, vendoring is fine. Drop a `[NOTICE]` or attribution comment in
the file we adapt from.

## The plan (todos at end of session)

1. **Strip the AppHost-side parser code** (file list under "Code to throw away"
   above). Single commit, restores the branch to a clean base for the sidecar
   work. Build should be green afterward.
2. **Vendor the slimmed-down collector helper** — adapted from practical-otel's
   `CollectorExtensions.cs` and `ReplaceLocalhostWithContainerHost`. Sized for
   our slapd-log use case. Lives in
   `aspireOpenLdap/Aspire.Hosting.OpenLdap/Tracing/` (rename the folder
   `Otel/` if you prefer).
3. **Author `otelcol-openldap.yaml`** — bundled via `contentFiles` similar to
   the Dockerfile. Should contain:
   - `filelog` receiver tailing `/var/log/slapd/slapd.log`
   - `recombine` operator joining `conn=N op=M ...` lines on the
     `RESULT`/`SEARCH RESULT` terminator
   - `regex_parser` extracting the fields listed in the "Tag semantic
     conventions" section above
   - Healthcheck filter — drop records whose attribute list contains
     `aspire-healthcheck`
   - OTTL transform to set span name, kind, status, and the `ldap.*` /
     `db.system` tags
   - `otlp/aspire` exporter pointing at `${env:ASPIRE_ENDPOINT}` with
     `x-otlp-api-key: ${env:ASPIRE_API_KEY}` and `tls.insecure: true`
   - service pipeline wiring (`traces` only, no metrics/logs in v1)
   Place at `openldap/2.6/debian-12/otelcol-openldap.yaml` so it ships in the
   same content-files batch as the Dockerfile.
4. **Tweak slapd entrypoint** to tee stderr to `/var/log/slapd/slapd.log`. Make
   this conditional on the log directory existing (so the entrypoint still
   works without the sidecar). Look at
   `openldap/2.6/debian-12/rootfs/opt/openldap/scripts/openldap/run.sh` first.
5. **Wire `WithOpenTelemetry()`** to:
   - Add a shared named volume (e.g. `{name}-slapd-logs`) on the openldap
     container at `/var/log/slapd/`
   - Add a sibling container resource (`{name}-otel`) running
     `otel/opentelemetry-collector-contrib` (pin the version)
   - Bind-mount the YAML config and the shared volume (read-only) into the
     collector
   - Use `WithParentRelationship(builder)` so the dashboard groups it under
     openldap
   - Set `ASPIRE_ENDPOINT` and `ASPIRE_API_KEY` env vars on the collector using
     the borrowed discovery + localhost rewrite
6. **Verify end-to-end:**
   - `aspire start --apphost aspireOpenLdap/AspireOpenLdap.AppHost --isolated`
   - `aspire wait openldap`
   - Drive a few `ldapsearch` calls via
     `docker exec -e LDAPTLS_REQCERT=never <cid> ldapsearch -x -H ldaps://localhost:1636 -D "cn=admin,dc=example,dc=org" -w "$pw" -b "dc=example,dc=org" "(uid=alice)" dn`
   - Confirm via `aspire otel traces -n 20 --format Table` that LDAP spans
     appear with the expected tags
   - Confirm no healthcheck spans appear (Aspire's healthcheck still fires
     every ~5s)

## Where things live (current branch state, before stripping)

- Hosting integration: `aspireOpenLdap/Aspire.Hosting.OpenLdap/`
- Client package: `aspireOpenLdap/Aspire.OpenLdap/`
- Sample AppHost: `aspireOpenLdap/AspireOpenLdap.AppHost/AppHost.cs`
- Bundled Dockerfile + slapd init scripts: `openldap/2.6/debian-12/`
- Healthcheck (with sentinel):
  `aspireOpenLdap/Aspire.Hosting.OpenLdap/OpenLdapHealthCheck.cs:67`
- Healthcheck (client-side, with sentinel):
  `aspireOpenLdap/Aspire.OpenLdap/OpenLdapClientExtensions.cs:78`

## Verification gotchas

- `aspire wait <name>` can report "healthy at 0.0s" against a stale state if
  you don't actually start a fresh AppHost first. Use `aspire ps` to confirm
  PID and dashboard URL are current before issuing follow-up commands.
- After each restart, the openldap-data volume persists by default (we added
  `WithDataVolume()` on master). When iterating on the slapd entrypoint
  tweaks, run `docker volume rm openldap-data` between restarts so init
  re-runs and you see fresh slapd config behavior.
- The Aspire dashboard's traces query (`aspire otel traces`) batches —
  give the collector 3–5 seconds after driving operations before checking.

## Open questions to resolve while building

- **Pin the collector image version** — `otel/opentelemetry-collector-contrib`
  ships frequently. Pick a known-good tag (around 0.110.x at the time of this
  doc; check current stable when starting).
- **Connection-scoped healthcheck filter in OTTL** — investigate whether
  `groupbyattrs` processor or a stateful OTTL transform handles "drop all
  records for a connection if any carries the sentinel". If not, fall back
  to filtering only the SRCH (the BIND from a healthcheck connection will
  produce a span; cost is one extra BIND span per healthcheck tick, which
  isn't terrible but isn't ideal).
- **Whether to also collect slapd metrics** via the `cn=monitor` overlay later.
  Not in scope for v1.

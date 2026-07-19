using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Health check that performs an authenticated root DSE query against the OpenLDAP server
/// using the resource's admin credentials. Uses LDAPS when the resource requires TLS.
/// </summary>
/// <remarks>
/// macOS path: <see cref="OpenLdapResourceBuilderExtensions.WithRequiredTls"/> skips the
/// server-side <c>LDAP_REQUIRE_TLS</c> enforcement on macOS, so the health check connects
/// to the plain LDAP port with the admin bind. See that method's remarks for the full
/// rationale (Apple's <c>LDAP.framework</c> can't trust our self-signed CA from managed
/// code). Linux trusts the CA natively via <c>TrustedCertificatesDirectory</c> — see the
/// TLS branch in <see cref="CheckHealthAsync"/>.
/// </remarks>
internal sealed class OpenLdapHealthCheck(OpenLdapResource resource) : IHealthCheck
{
    // LDAP result code for invalid credentials (RFC 4511).
    private const int InvalidCredentialsResultCode = 49;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var useTls = resource.TlsRequired && !OperatingSystem.IsMacOS();

        try
        {
            var endpointName = useTls
                ? OpenLdapResource.LdapsEndpointName
                : OpenLdapResource.LdapEndpointName;

            var allocatedEndpoint = resource.GetEndpoint(endpointName);
            if (allocatedEndpoint is null || !allocatedEndpoint.IsAllocated)
            {
                return HealthCheckResult.Unhealthy($"LDAP endpoint '{endpointName}' is not allocated.");
            }

            var password = await resource.AdminPasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admin password parameter resolved to null.");

            var bindDn = resource.AdminBindDn;

            // Root DSE query: base DN = "", scope = Base. The probe marks itself twice so the
            // container's log filter (probe_log_filter.sh) can identify it unambiguously:
            // the "aspire-healthcheck" sentinel attribute (slapd logs the attribute list
            // verbatim; slapd returns nothing for it since it's not a real attribute), and the
            // "(cn=aspire-healthcheck)" branch of the filter (semantically a no-op alongside
            // (objectClass=*), logged by slapd on the SRCH line — value case-normalized, hence
            // the lowercase token).
            var request = new SearchRequest(
                distinguishedName: "",
                ldapFilter: "(|(objectClass=*)(cn=aspire-healthcheck))",
                searchScope: SearchScope.Base,
                attributeList: ["namingContexts", "aspire-healthcheck"]);

            // On the Linux native-trust path, dial an IP literal: libldap checks the cert
            // against the peer address's reverse-DNS name (not the dialed name), which on
            // typical NSS stacks resolves 127.0.0.1 to the machine hostname and can never
            // match. IP dials are validated against the cert's IP SANs instead.
            var dialHost = useTls && !OperatingSystem.IsWindows() && resource.CaCertHostPath is not null
                ? Aspire.Hosting.OpenLdap.OpenLdapUnixTlsTrust.ResolveDialHost(allocatedEndpoint.Host)
                : allocatedEndpoint.Host;

            var connection = new LdapConnection(
                new LdapDirectoryIdentifier(dialHost, allocatedEndpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindDn, password),
                Timeout = TimeSpan.FromSeconds(5),
            };
            X509Certificate2? ca = null;
            try
            {
                connection.SessionOptions.ProtocolVersion = 3;

                if (useTls)
                {
                    connection.SessionOptions.SecureSocketLayer = true;
                    if (resource.CaCertHostPath is { } caPath)
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            ca = Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.LoadPemCertificate(caPath);
                            // Chain to the resource's CA AND match the host we dial, unless the user
                            // opted out (custom certificates that don't name localhost).
                            var expectedHost = resource.TlsHostnameValidationDisabled ? null : allocatedEndpoint.Host;
                            var trustedRoot = ca;
                            connection.SessionOptions.VerifyServerCertificate = (_, serverCert) =>
                                Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.ValidateAgainstCustomRoot(serverCert, trustedRoot, expectedHost);
                        }
                        else
                        {
                            // Linux (macOS never reaches here — useTls excludes it): libldap throws
                            // from the VerifyServerCertificate setter, so trust the CA natively via
                            // an OpenSSL hash-named directory. libldap also validates the hostname;
                            // the generated certificates name localhost/127.0.0.1, and
                            // WithTlsCertificates rejects the hostname-validation opt-out on Linux.
                            connection.SessionOptions.TrustedCertificatesDirectory =
                                Aspire.Hosting.OpenLdap.OpenLdapUnixTlsTrust.EnsureTrustDirectory(caPath);
                            connection.SessionOptions.StartNewTlsSessionContext();
                        }
                    }
                }
            }
            catch
            {
                // Setup failed before the probe task below could take ownership.
                ca?.Dispose();
                connection.Dispose();
                throw;
            }

            // SendRequest is synchronous and cannot observe the token once dispatched, so the
            // probe task owns the connection (and CA callback certificate) and disposes them
            // when the request finishes — while WaitAsync lets THIS method return promptly on
            // cancellation instead of blocking out the LDAP timeout. The 5-second connection
            // timeout bounds the orphaned probe.
            var response = await Task.Run(
                () =>
                {
                    using (connection)
                    using (ca)
                    {
                        return (SearchResponse)connection.SendRequest(request);
                    }
                },
                CancellationToken.None)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.ResultCode == ResultCode.Success && response.Entries.Count > 0)
            {
                return HealthCheckResult.Healthy("LDAP root DSE query succeeded.");
            }

            return HealthCheckResult.Unhealthy(
                $"LDAP root DSE query returned unexpected result: {response.ResultCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the caller's shutdown/timeout, not LDAP unhealthiness.
            throw;
        }
        // Only the exception TYPE is reported below, never the exception object: LDAP
        // diagnostics can embed server-supplied directory data (DNs, matched values), and
        // health reporters log HealthCheckResult exceptions — same no-PII rule as telemetry.
        catch (LdapException ex) when (ex.ErrorCode == InvalidCredentialsResultCode)
        {
            return HealthCheckResult.Unhealthy(DescribeAuthFailure());
        }
        catch (LdapException ex)
        {
            return HealthCheckResult.Unhealthy($"LDAP connection failed ({ex.GetType().Name}, error {ex.ErrorCode}).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Unexpected error during LDAP health check ({ex.GetType().Name}).");
        }
    }

    private string DescribeAuthFailure()
    {
        // A persistent data mount that predates this run keeps whatever admin password the
        // directory was first initialized with — the container skips LDAP_ADMIN_PASSWORD when
        // it finds existing data — so the current credentials fail forever with err=49.
        var mounts = resource.Annotations.OfType<ContainerMountAnnotation>()
            .Where(m => string.Equals(m.Target, OpenLdapResource.DataPath, StringComparison.Ordinal))
            .ToList();

        if (mounts.Any(m => m.Type == ContainerMountType.Volume))
        {
            return "LDAP authentication failed. The data volume may predate this run and hold a " +
                "different admin password — the resource's \"Reset data volume\" command " +
                "reinitializes it with the current credentials.";
        }

        if (mounts.Count > 0)
        {
            return "LDAP authentication failed. The data bind mount may predate this run and hold " +
                "a different admin password — clear the host directory to reinitialize with the " +
                "current credentials.";
        }

        return "LDAP authentication failed.";
    }

}

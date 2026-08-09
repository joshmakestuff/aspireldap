using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Construction of the LdapAdmin sidecar container: the docker build from the packaged build
/// context, the connection-string contract the admin host reads, the TLS trust material, and the
/// health/wait wiring. See <see cref="OpenLdapResourceBuilderExtensions.WithLdapAdmin"/> for the
/// user-facing contract.
/// </summary>
internal static class LdapAdminBuilder
{
    /// <summary>
    /// Config key (env form <c>LdapAdmin__ConnectionName</c>) telling the admin host which
    /// connection string to bind with. The dev AppHost mirrors the same contract.
    /// </summary>
    internal const string ConnectionNameEnvironmentVariable = "LdapAdmin__ConnectionName";

    internal static void Add(
        IResourceBuilder<OpenLdapResource> builder,
        Action<IResourceBuilder<LdapAdminResource>>? configureContainer,
        string? containerName)
    {
        EnsurePackagedPayload(LdapAdminResource.DefaultDockerContextPath);

        var parent = builder.Resource;
        var adminName = containerName ?? $"{parent.Name}-ldapadmin";
        var adminResource = new LdapAdminResource(adminName, parent);

        var admin = builder.ApplicationBuilder
            .AddResource(adminResource)
            // Publish-time image name only; the local build tag is content-hash-addressed.
            .WithImage(LdapAdminResource.DefaultImageName, LdapAdminResource.DefaultImageTag)
            .WithDockerfile(LdapAdminResource.DefaultDockerContextPath, LdapAdminResource.DefaultDockerfilePath)
            .WithHttpEndpoint(targetPort: LdapAdminResource.ContainerHttpPort, name: LdapAdminResource.HttpEndpointName)
            // All parent-derived settings resolve when the admin container starts, so fluent
            // calls chained on the parent AFTER WithLdapAdmin (WithBaseDn, WithAdminUsername,
            // WithTls().WithRequiredTls()) still take effect here.
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[ConnectionNameEnvironmentVariable] = parent.Name;
                context.EnvironmentVariables[$"ConnectionStrings__{parent.Name}"] =
                    BuildContainerConnectionString(parent);

                if (parent.TlsRequired)
                {
                    // LDAPS is encrypted but the admin does NOT verify the server certificate,
                    // matching the phpLDAPadmin sidecar. Verification cannot work here: libldap
                    // validates an IP-literal dial against the certificate's IP SANs, and the
                    // container's dynamically-assigned network address cannot be in the
                    // certificate; libldap offers no trust-this-CA-but-skip-hostname mode.
                    context.EnvironmentVariables["LDAPTLS_REQCERT"] = "never";
                }
            })
            // Health = the admin host's /health endpoint, which runs the root-DSE LDAP health
            // check registered by AddOpenLdapClient — so "healthy" on the dashboard means the
            // admin serves HTTP AND can bind to the directory with the AppHost credentials.
            .WithHttpHealthCheck(path: LdapAdminResource.HealthPath, statusCode: 200, endpointName: LdapAdminResource.HttpEndpointName)
            .WaitFor(builder);

        configureContainer?.Invoke(admin);
    }

    /// <summary>
    /// The connection string the admin binds with, addressed over the Aspire-managed container
    /// network: the parent by resource name on its container target port, with the AppHost admin
    /// credentials (no login exists by decision). When LDAPS is required the scheme and port
    /// switch; no CaCertFile is emitted — see the LDAPTLS_REQCERT comment above. Evaluated
    /// lazily so late fluent overrides apply.
    /// </summary>
    private static ReferenceExpression BuildContainerConnectionString(OpenLdapResource parent)
    {
        var scheme = parent.TlsRequired ? "ldaps" : "ldap";
        var port = (parent.TlsRequired
            ? OpenLdapResource.DefaultLdapsTargetPort
            : OpenLdapResource.DefaultLdapTargetPort).ToString(CultureInfo.InvariantCulture);
        var baseDn = ConnectionStringQuoting.Quote(parent.BaseDn);
        var bindDn = ConnectionStringQuoting.Quote(parent.AdminBindDn);
        var password = new QuotedParameterValue(parent.AdminPasswordParameter);

        return ReferenceExpression.Create(
            $"Endpoint={scheme}://{parent.Name}:{port};BaseDN={baseDn};BindDN={bindDn};BindPassword={password}");
    }

    /// <summary>
    /// The packaged build context is the only supported source — never the AspireLdap source
    /// checkout (#78/#82). Fail at the fluent call with an actionable message rather than later
    /// inside the docker build with a missing-file error.
    /// </summary>
    private static void EnsurePackagedPayload(string contextPath)
    {
        var dockerfile = Path.Combine(contextPath, LdapAdminResource.DefaultDockerfilePath);
        var entryAssembly = Path.Combine(
            contextPath, LdapAdminResource.PayloadRelativePath, LdapAdminResource.PayloadEntryAssembly);
        if (!File.Exists(dockerfile) || !File.Exists(entryAssembly))
        {
            throw new DistributedApplicationException(
                $"The LdapAdmin container build context was not found at '{contextPath}'. " +
                "The admin payload ships inside the JoshMakeStuff.Aspire.Hosting.OpenLdap NuGet package " +
                "(contentFiles) and lands in the AppHost build output on restore, so WithLdapAdmin() requires " +
                "the AppHost to consume the package as a PackageReference. AppHosts that project-reference " +
                "this repo run the admin as a project resource (AddProject) instead.");
        }
    }

}

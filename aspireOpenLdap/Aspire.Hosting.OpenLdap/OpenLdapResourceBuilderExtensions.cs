using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

public static class OpenLdapResourceBuilderExtensions
{
    /// <summary>
    /// Adds an OpenLDAP container resource built from the local Dockerfile.
    /// </summary>
    /// <param name="dockerContextPath">
    /// Docker build context. Relative paths are resolved against the AppHost project directory
    /// (Aspire's <c>IDistributedApplicationBuilder.AppHostDirectory</c>), not the runtime working directory.
    /// </param>
    /// <param name="adminPassword">
    /// Optional parameter resource backing the admin password. When omitted, a 22-character random
    /// password is auto-generated via <see cref="ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter"/>
    /// and surfaced in the Aspire dashboard as a secret parameter named <c>{name}-password</c>.
    /// </param>
    public static IResourceBuilder<OpenLdapResource> AddOpenLdap(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? ldapPort = null,
        int? ldapsPort = null,
        string dockerContextPath = OpenLdapResource.DefaultDockerContextPath,
        string dockerfilePath = OpenLdapResource.DefaultDockerfilePath,
        string ldapRoot = OpenLdapResource.DefaultLdapRoot,
        string adminUsername = OpenLdapResource.DefaultAdminUsername,
        IResourceBuilder<ParameterResource>? adminPassword = null,
        string users = OpenLdapResource.DefaultUsers,
        string userPasswords = OpenLdapResource.DefaultUserPasswords)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerContextPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerfilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldapRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(users);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPasswords);

        var passwordParameter = adminPassword?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var resource = new OpenLdapResource(
            name,
            ldapRoot: ldapRoot,
            adminUsername: adminUsername,
            adminPasswordParameter: passwordParameter);

        var openLdap = builder
            .AddResource(resource)
            // Sets the publish-time image name. The local docker build tag is content-hash-addressed
            // by Aspire's WithDockerfile and not affected by this call.
            .WithImage(OpenLdapResource.DefaultImageName, OpenLdapResource.DefaultImageTag)
            .WithDockerfile(dockerContextPath, dockerfilePath)
            .WithEndpoint(port: ldapPort, targetPort: OpenLdapResource.DefaultLdapTargetPort, name: OpenLdapResource.LdapEndpointName, isProxied: false)
            .WithEndpoint(port: ldapsPort, targetPort: OpenLdapResource.DefaultLdapsTargetPort, name: OpenLdapResource.LdapsEndpointName, isProxied: false)
            .WithEnvironment("LDAP_ROOT", ldapRoot)
            .WithEnvironment("LDAP_ADMIN_USERNAME", adminUsername)
            .WithEnvironment("LDAP_USERS", users)
            .WithEnvironment("LDAP_PASSWORDS", userPasswords)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_ADMIN_PASSWORD"] = passwordParameter;
            });

        // Register LDAP root DSE health check
        var healthCheckName = $"openldap-{name}";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckName,
            sp => new OpenLdapHealthCheck(resource),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));

        openLdap.WithHealthCheck(healthCheckName);

        return openLdap;
    }

    /// <summary>
    /// Adds a named data volume for the OpenLDAP data directory.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithDataVolume(
        this IResourceBuilder<OpenLdapResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var volumeName = name ?? $"{builder.Resource.Name}-data";
        return builder.WithVolume(volumeName, OpenLdapResource.DataPath, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the OpenLDAP data directory.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithDataBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, OpenLdapResource.DataPath, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for custom LDIF files loaded during initialization.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithCustomLdifsBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, "/ldifs", isReadOnly);
    }

    /// <summary>
    /// Enables anonymous LDAP binding on the container.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithAnonymousBinding(
        this IResourceBuilder<OpenLdapResource> builder,
        bool allow = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment("LDAP_ALLOW_ANON_BINDING", allow ? "yes" : "no");
    }

    private const string ContainerTlsDir = "/tls";
    private const string ContainerServerCertPath = "/tls/server.crt";
    private const string ContainerServerKeyPath = "/tls/server.key";
    private const string ContainerCaCertPath = "/tls/ca.crt";

    /// <summary>
    /// Enables TLS using an auto-generated self-signed CA and server certificate.
    /// Certificates are cached under <c>{AppHostDir}/obj/aspire-openldap-certs/{name}/</c> and
    /// regenerated only when missing or near expiry.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithTls(
        this IResourceBuilder<OpenLdapResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;
        var appHostDir = builder.ApplicationBuilder.AppHostDirectory;
        var generated = OpenLdapCertificateGenerator.EnsureCertificates(appHostDir, resource.Name);

        return ApplyTls(builder, generated.Directory, generated.CaCertPath);
    }

    /// <summary>
    /// Enables TLS using caller-provided certificates. All three paths must point to PEM files
    /// inside a single directory that is bind-mounted read-only at <c>/tls</c>.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithTls(
        this IResourceBuilder<OpenLdapResource> builder,
        string serverCertFile,
        string serverKeyFile,
        string caCertFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCertFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKeyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(caCertFile);

        var certDir = Path.GetDirectoryName(Path.GetFullPath(serverCertFile))
            ?? throw new DistributedApplicationException($"Could not resolve directory for '{serverCertFile}'.");
        var keyDir = Path.GetDirectoryName(Path.GetFullPath(serverKeyFile));
        var caDir = Path.GetDirectoryName(Path.GetFullPath(caCertFile));
        if (!string.Equals(certDir, keyDir, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(certDir, caDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new DistributedApplicationException(
                "serverCertFile, serverKeyFile and caCertFile must all live in the same directory (it is bind-mounted into the container).");
        }

        return ApplyTls(builder, certDir, Path.GetFullPath(caCertFile));
    }

    /// <summary>
    /// Requires TLS for all LDAP connections. Switches the connection string scheme to <c>ldaps://</c>.
    /// Must be chained after <c>WithTls(...)</c>.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithRequiredTls(
        this IResourceBuilder<OpenLdapResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Resource.TlsEnabled)
        {
            throw new DistributedApplicationException(
                "WithRequiredTls() must be called after WithTls(...).");
        }

        builder.Resource.TlsRequired = true;
        return builder.WithEnvironment("LDAP_REQUIRE_TLS", "yes");
    }

    private static IResourceBuilder<OpenLdapResource> ApplyTls(
        IResourceBuilder<OpenLdapResource> builder,
        string hostCertDir,
        string caCertHostPath)
    {
        builder.Resource.TlsEnabled = true;
        builder.Resource.CaCertHostPath = caCertHostPath;

        return builder
            .WithBindMount(hostCertDir, ContainerTlsDir, isReadOnly: true)
            .WithEnvironment("LDAP_ENABLE_TLS", "yes")
            .WithEnvironment("LDAP_TLS_CERT_FILE", ContainerServerCertPath)
            .WithEnvironment("LDAP_TLS_KEY_FILE", ContainerServerKeyPath)
            .WithEnvironment("LDAP_TLS_CA_FILE", ContainerCaCertPath);
    }
}

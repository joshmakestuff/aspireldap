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
    /// Adds a named data volume for the OpenLDAP data directory (<c>/data/openldap</c>).
    /// On subsequent starts, the container detects existing data and skips reinitialization
    /// (including re-applying seed LDIFs), making startup fast even with large seed data.
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
    /// Bind-mounts a host directory at the OpenLDAP data path (<c>/data/openldap</c>).
    /// Same reinit-skipping behavior as <see cref="WithDataVolume"/>.
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
    /// Adds a custom LDAP schema from a single LDIF file. The file is mounted into the container
    /// and loaded via <c>slapadd</c> during initialization.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithSchema(
        this IResourceBuilder<OpenLdapResource> builder,
        string ldifFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldifFile);

        var fullPath = Path.GetFullPath(ldifFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Schema LDIF file not found: {fullPath}", fullPath);
        }

        return builder.WithBindMount(fullPath, "/schema/custom.ldif", isReadOnly: true);
    }

    /// <summary>
    /// Adds a directory of custom LDAP schema LDIF files. Files with the <c>.ldif</c> extension
    /// are loaded alphabetically via <c>slapadd</c> during initialization.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> WithSchemas(
        this IResourceBuilder<OpenLdapResource> builder,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Schema directory not found: {fullPath}");
        }

        return builder.WithBindMount(fullPath, "/schemas", isReadOnly: true);
    }

    /// <summary>
    /// Seeds the directory with one or more LDIF files loaded via <c>ldapadd</c> after
    /// initialization completes. Accepts either a single LDIF file or a directory of LDIF files.
    /// </summary>
    /// <remarks>
    /// When seed data is present the container's default tree (the <c>LDAP_USERS</c>/<c>LDAP_PASSWORDS</c>
    /// users) is NOT created — your seed becomes the entire initial dataset. Pair with
    /// <see cref="WithDataVolume"/> to amortize the cost of large seeds across restarts.
    /// </remarks>
    public static IResourceBuilder<OpenLdapResource> WithSeedData(
        this IResourceBuilder<OpenLdapResource> builder,
        string ldifFileOrDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ldifFileOrDirectory);

        var fullPath = Path.GetFullPath(ldifFileOrDirectory);

        if (Directory.Exists(fullPath))
        {
            return builder.WithBindMount(fullPath, "/ldifs", isReadOnly: true);
        }

        if (File.Exists(fullPath))
        {
            var fileName = Path.GetFileName(fullPath);
            return builder.WithBindMount(fullPath, $"/ldifs/{fileName}", isReadOnly: true);
        }

        throw new FileNotFoundException(
            $"Seed data path not found: {fullPath}", fullPath);
    }

    /// <summary>
    /// Adds a bind mount for custom LDIF files loaded during initialization.
    /// </summary>
    [Obsolete("Use WithSeedData(...) instead. This method will be removed in a future release.")]
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
    /// Adds a phpLDAPadmin web UI container that targets this OpenLDAP resource.
    /// The admin container connects to the parent over the Aspire-managed container network.
    /// </summary>
    /// <param name="builder">The parent OpenLDAP builder.</param>
    /// <param name="configureContainer">Optional callback to further configure the admin container.</param>
    /// <param name="containerName">Override the admin resource name. Defaults to <c>{parent}-admin</c>.</param>
    /// <returns>The parent OpenLDAP builder (admin runs alongside as a sibling resource).</returns>
    public static IResourceBuilder<OpenLdapResource> WithPhpLdapAdmin(
        this IResourceBuilder<OpenLdapResource> builder,
        Action<IResourceBuilder<PhpLdapAdminResource>>? configureContainer = null,
        string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var parent = builder.Resource;
        var adminName = containerName ?? $"{parent.Name}-admin";
        var adminResource = new PhpLdapAdminResource(adminName, parent);

        // Inside the container network the admin connects to the parent by resource name.
        // If TLS is required we point at the LDAPS target port; otherwise plain LDAP.
        var ldapHost = parent.Name;
        var ldapPort = parent.TlsRequired
            ? OpenLdapResource.DefaultLdapsTargetPort
            : OpenLdapResource.DefaultLdapTargetPort;

        var admin = builder.ApplicationBuilder
            .AddResource(adminResource)
            .WithImage(PhpLdapAdminResource.DefaultImageName, PhpLdapAdminResource.DefaultImageTag)
            .WithHttpEndpoint(targetPort: PhpLdapAdminResource.ContainerHttpPort, name: PhpLdapAdminResource.HttpEndpointName)
            .WithEnvironment("LDAP_HOST", ldapHost)
            .WithEnvironment("LDAP_PORT", ldapPort.ToString())
            .WithEnvironment("LDAP_BASE_DN", parent.LdapRoot)
            .WithEnvironment("LDAP_USERNAME", $"cn={parent.AdminUsername},{parent.LdapRoot}")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_PASSWORD"] = parent.AdminPasswordParameter;
            })
            .WaitFor(builder);

        if (parent.TlsRequired)
        {
            // Use the image's preconfigured 'ldaps' connection (use_ssl=true). Self-signed CA isn't
            // trusted inside the admin container so disable libldap's cert verification for local dev.
            admin.WithEnvironment("LDAP_CONNECTION", "ldaps")
                 .WithEnvironment("LDAP_SSL", "true")
                 .WithEnvironment("LDAPTLS_REQCERT", "never");
        }

        configureContainer?.Invoke(admin);
        return builder;
    }

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

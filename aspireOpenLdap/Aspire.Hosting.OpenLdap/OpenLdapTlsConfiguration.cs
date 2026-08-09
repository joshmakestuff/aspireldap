using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// TLS material handling: where the certificate, key, and CA land inside the container, how
/// caller-supplied files are validated and mounted, and the environment the image reads.
/// Certificate generation itself lives in <see cref="OpenLdapCertificateGenerator"/>.
/// </summary>
internal static class OpenLdapTlsConfiguration
{
    private const string ContainerTlsDir = "/tls";
    private const string ContainerServerCertPath = "/tls/server.crt";
    private const string ContainerServerKeyPath = "/tls/server.key";
    private const string ContainerCaCertPath = "/tls/ca.crt";

    /// <summary>
    /// Enables TLS from the auto-generated self-signed CA and server certificate, mounting the
    /// whole cache directory. See <see cref="OpenLdapResourceBuilderExtensions.WithTls(IResourceBuilder{OpenLdapResource})"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> EnableGeneratedTls(
        IResourceBuilder<OpenLdapResource> builder)
    {
        var resource = builder.Resource;
        var appHostDir = builder.ApplicationBuilder.AppHostDirectory;
        var generated = OpenLdapCertificateGenerator.EnsureCertificates(appHostDir, resource.Name);

        builder.WithBindMount(generated.Directory, ContainerTlsDir, isReadOnly: true);
        return ApplyTlsEnvironment(builder, generated.CaCertPath);
    }

    /// <summary>
    /// Enables TLS from caller-provided PEM files, each mounted at its fixed container path so
    /// the host files can live anywhere and use any names. See
    /// <see cref="OpenLdapResourceBuilderExtensions.WithTls(IResourceBuilder{OpenLdapResource}, string, string, string, bool)"/>.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> EnableProvidedTls(
        IResourceBuilder<OpenLdapResource> builder,
        string serverCertFile,
        string serverKeyFile,
        string caCertFile,
        bool disableHealthCheckHostnameValidation)
    {
        builder.Resource.TlsHostnameValidationDisabled = disableHealthCheckHostnameValidation;

        var certPath = RequireTlsFile(builder, serverCertFile, "server certificate");
        var keyPath = RequireTlsFile(builder, serverKeyFile, "server private key");
        var caPath = RequireTlsFile(builder, caCertFile, "CA certificate");

        builder
            .WithBindMount(certPath, ContainerServerCertPath, isReadOnly: true)
            .WithBindMount(keyPath, ContainerServerKeyPath, isReadOnly: true)
            .WithBindMount(caPath, ContainerCaCertPath, isReadOnly: true);

        return ApplyTlsEnvironment(builder, caPath);
    }

    /// <summary>
    /// Switches the resource to TLS-required. See
    /// <see cref="OpenLdapResourceBuilderExtensions.WithRequiredTls"/> for the macOS carve-out.
    /// </summary>
    internal static IResourceBuilder<OpenLdapResource> RequireTls(
        IResourceBuilder<OpenLdapResource> builder)
    {
        if (!builder.Resource.TlsEnabled)
        {
            throw new DistributedApplicationException(
                "WithRequiredTls() must be called after WithTls(...).");
        }

        builder.Resource.TlsRequired = true;
        if (OperatingSystem.IsMacOS())
        {
            return builder;
        }
        return builder.WithEnvironment("LDAP_REQUIRE_TLS", "yes");
    }

    private static string RequireTlsFile(
        IResourceBuilder<OpenLdapResource> builder, string path, string description)
    {
        var fullPath = OpenLdapMounts.ResolveAppHostRelativePath(builder, path);
        if (!File.Exists(fullPath))
        {
            throw new DistributedApplicationException(
                $"TLS {description} file not found: {fullPath}");
        }
        return fullPath;
    }

    private static IResourceBuilder<OpenLdapResource> ApplyTlsEnvironment(
        IResourceBuilder<OpenLdapResource> builder,
        string caCertHostPath)
    {
        builder.Resource.TlsEnabled = true;
        builder.Resource.CaCertHostPath = caCertHostPath;

        return builder
            .WithEnvironment("LDAP_ENABLE_TLS", "yes")
            .WithEnvironment("LDAP_TLS_CERT_FILE", ContainerServerCertPath)
            .WithEnvironment("LDAP_TLS_KEY_FILE", ContainerServerKeyPath)
            .WithEnvironment("LDAP_TLS_CA_FILE", ContainerCaCertPath);
    }
}

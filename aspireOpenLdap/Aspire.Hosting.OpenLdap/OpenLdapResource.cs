using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an OpenLDAP container resource built from a local Dockerfile.
/// </summary>
public sealed class OpenLdapResource : ContainerResource, IResourceWithConnectionString
{
    internal const string LdapEndpointName = "ldap";
    internal const string LdapsEndpointName = "ldaps";
    internal const int DefaultLdapTargetPort = 1389;
    internal const int DefaultLdapsTargetPort = 1636;
    internal const string DefaultImageName = "aspire-openldap";
    internal const string DefaultImageTag = "2.6";
    /// <summary>
    /// Relative path of the docker build context inside the consumer's build output.
    /// The context (Dockerfile + rootfs scripts) is shipped as contentFiles in the
    /// Aspire.Hosting.OpenLdap nupkg and copied here at build time.
    /// </summary>
    internal const string DefaultDockerContextRelativePath = "openldap/2.6/debian-12";
    internal const string DefaultDockerfilePath = "Dockerfile";

    /// <summary>
    /// Absolute default docker build context path, resolved against the AppHost's
    /// build output (where the bundled Dockerfile/scripts land via contentFiles).
    /// </summary>
    internal static string DefaultDockerContextPath { get; } =
        Path.Combine(AppContext.BaseDirectory, DefaultDockerContextRelativePath);
    internal const string DefaultAdminUsername = "admin";
    internal const string DefaultBaseDn = "dc=example,dc=org";
    internal const string DataPath = "/data/openldap";

    private EndpointReference? _ldapEndpoint;
    private EndpointReference? _ldapsEndpoint;

    public OpenLdapResource(
        string name,
        string baseDn,
        string adminUsername,
        ParameterResource adminPasswordParameter)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(adminPasswordParameter);
        BaseDn = baseDn;
        AdminUsername = adminUsername;
        AdminPasswordParameter = adminPasswordParameter;
    }

    /// <summary>The base DN / suffix (e.g. <c>dc=example,dc=org</c>). Override with <c>WithBaseDn(...)</c>.</summary>
    public string BaseDn { get; internal set; }

    /// <summary>The admin username (CN component). Admin DN = <c>cn={AdminUsername},{BaseDn}</c>. Override with <c>WithAdminUsername(...)</c>.</summary>
    public string AdminUsername { get; internal set; }

    /// <summary>Parameter resource backing the admin password. Auto-generated when the caller does not supply one.</summary>
    public ParameterResource AdminPasswordParameter { get; }

    /// <summary>True when TLS has been enabled via <c>WithTls</c>.</summary>
    internal bool TlsEnabled { get; set; }

    /// <summary>True when LDAPS is required (<c>WithRequiredTls</c>). Switches the connection string scheme to <c>ldaps://</c>.</summary>
    internal bool TlsRequired { get; set; }

    /// <summary>Host filesystem path to the CA certificate (PEM) trusted by the server, when TLS is enabled.</summary>
    internal string? CaCertHostPath { get; set; }

    /// <summary>
    /// Accumulated seed declarations (OUs, users, groups) emitted as a generated LDIF
    /// at <c>BeforeResourceStarted</c> time. Null until the first seed builder call.
    /// </summary>
    internal LdapSeedModel? SeedModel { get; set; }

    /// <summary>Host filesystem path of the generated seed LDIF. Set alongside <see cref="SeedModel"/>.</summary>
    internal string? SeedFilePath { get; set; }

    /// <summary>True when <c>WithOpenTelemetry()</c> has been called. Drives the slapd log parser.</summary>
    internal bool OpenTelemetryEnabled { get; set; }

    public EndpointReference LdapEndpoint =>
        _ldapEndpoint ??= new EndpointReference(this, LdapEndpointName);

    public EndpointReference LdapsEndpoint =>
        _ldapsEndpoint ??= new EndpointReference(this, LdapsEndpointName);

    /// <summary>
    /// Connection string in the format:
    /// Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=secret
    /// When TLS is required the scheme switches to <c>ldaps://</c> and <c>CaCertFile=</c> is appended.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            var scheme = TlsRequired ? "ldaps" : "ldap";
            var endpoint = TlsRequired ? LdapsEndpoint : LdapEndpoint;
            var caSuffix = TlsEnabled && CaCertHostPath is not null
                ? $";CaCertFile={CaCertHostPath}"
                : string.Empty;

            return ReferenceExpression.Create(
                $"Endpoint={scheme}://{endpoint.Property(EndpointProperty.HostAndPort)};BaseDN={BaseDn};BindDN=cn={AdminUsername},{BaseDn};BindPassword={AdminPasswordParameter}{caSuffix}");
        }
    }
}

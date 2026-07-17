using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;
using ConnectionStringQuoting = Aspire.Hosting.OpenLdap.ConnectionStringQuoting;

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
    internal const string DefaultDockerContextRelativePath = "openldap/2.6/debian-13";
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

    /// <summary>Container path of the generated overlay LDIF the init script applies online (ldapadd).</summary>
    internal const string GeneratedOverlayContainerPath = "/overlays.ldif";

    /// <summary>Container path of the generated access-control LDIF the init script applies online (ldapmodify).</summary>
    internal const string GeneratedAccessContainerPath = "/access.ldif";

    /// <summary>
    /// cn=config DN of the main data database. The bootstrap slapd.ldif defines databases in a
    /// fixed order (frontend={-1}, config={0}, monitor={1}, mdb={2}), so the mdb index is stable.
    /// Overlays are attached here.
    /// </summary>
    internal const string MdbDatabaseDn = "olcDatabase={2}mdb,cn=config";

    private EndpointReference? _ldapEndpoint;
    private EndpointReference? _ldapsEndpoint;

    /// <summary>
    /// Creates the resource. Use <c>builder.AddOpenLdap(...)</c> rather than constructing directly.
    /// </summary>
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

    /// <summary>
    /// The admin bind DN, <c>cn={AdminUsername},{BaseDn}</c>, composed through the RFC 4514 DN
    /// builder so a username (or base DN) containing a comma or other special character escapes
    /// correctly instead of silently producing a broken DN.
    /// </summary>
    internal string AdminBindDn => Dn.Combine(Dn.Rdn("cn", AdminUsername), BaseDn);

    /// <summary>Parameter resource backing the admin password. Auto-generated when the caller does not supply one.</summary>
    public ParameterResource AdminPasswordParameter { get; }

    /// <summary>True when TLS has been enabled via <c>WithTls</c>.</summary>
    internal bool TlsEnabled { get; set; }

    /// <summary>True when LDAPS is required (<c>WithRequiredTls</c>). Switches the connection string scheme to <c>ldaps://</c>.</summary>
    internal bool TlsRequired { get; set; }

    /// <summary>Host filesystem path to the CA certificate (PEM) trusted by the server, when TLS is enabled.</summary>
    internal string? CaCertHostPath { get; set; }

    /// <summary>
    /// True when the health check should skip certificate hostname validation — an explicit
    /// local-dev opt-out for caller-provided certificates that don't name the health-check host.
    /// </summary>
    internal bool TlsHostnameValidationDisabled { get; set; }

    /// <summary>
    /// Accumulated seed declarations (OUs, users, groups) emitted as a generated LDIF
    /// at <c>BeforeResourceStarted</c> time. Null until the first seed builder call.
    /// </summary>
    internal LdapSeedModel? SeedModel { get; set; }

    /// <summary>Host filesystem path of the generated seed LDIF. Set alongside <see cref="SeedModel"/>.</summary>
    internal string? SeedFilePath { get; set; }

    /// <summary>
    /// Raw LDIF records declared via <c>WithSeedRecords(...)</c>, written as a generated LDIF at
    /// <c>BeforeResourceStarted</c> time and loaded after the typed seed. Null until the first call.
    /// </summary>
    internal List<LdifRecord>? SeedRecords { get; set; }

    /// <summary>Host filesystem path of the generated record-seed LDIF. Set alongside <see cref="SeedRecords"/>.</summary>
    internal string? SeedRecordsFilePath { get; set; }

    /// <summary>Opt-in overlays declared via <c>WithOverlay(...)</c>, emitted as cn=config at start. Null until the first call.</summary>
    internal List<OpenLdapOverlay>? Overlays { get; set; }

    /// <summary>Host filesystem path of the generated overlay LDIF. Set alongside <see cref="Overlays"/>.</summary>
    internal string? OverlayFilePath { get; set; }

    /// <summary>
    /// Access-control rules declared via <c>WithAccessControl(...)</c> — each a full <c>olcAccess</c>
    /// rule body (without the <c>{N}</c> ordering prefix). Emitted as an <c>olcAccess</c> modify on the
    /// mdb database and applied online at start. Null until the first call.
    /// </summary>
    internal List<string>? AccessRules { get; set; }

    /// <summary>Host filesystem path of the generated access-control LDIF. Set alongside <see cref="AccessRules"/>.</summary>
    internal string? AccessFilePath { get; set; }

    /// <summary>Reference to the plain LDAP endpoint (container port 1389).</summary>
    public EndpointReference LdapEndpoint =>
        _ldapEndpoint ??= new EndpointReference(this, LdapEndpointName);

    /// <summary>Reference to the LDAPS endpoint (container port 1636).</summary>
    public EndpointReference LdapsEndpoint =>
        _ldapsEndpoint ??= new EndpointReference(this, LdapsEndpointName);

    /// <summary>
    /// Connection string in the format:
    /// Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=secret
    /// When TLS is required the scheme switches to <c>ldaps://</c> and <c>CaCertFile=</c> is appended.
    /// Values containing semicolons, quotes, or leading/trailing whitespace are double-quoted
    /// (embedded quotes doubled) so any password or DN round-trips through the client parser.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            var scheme = TlsRequired ? "ldaps" : "ldap";
            var endpoint = TlsRequired ? LdapsEndpoint : LdapEndpoint;
            var baseDn = ConnectionStringQuoting.Quote(BaseDn);
            var bindDn = ConnectionStringQuoting.Quote(AdminBindDn);
            var password = new QuotedParameterValue(AdminPasswordParameter);
            var caSuffix = TlsEnabled && CaCertHostPath is not null
                ? $";CaCertFile={ConnectionStringQuoting.Quote(CaCertHostPath)}"
                : string.Empty;

            return ReferenceExpression.Create(
                $"Endpoint={scheme}://{endpoint.Property(EndpointProperty.HostAndPort)};BaseDN={baseDn};BindDN={bindDn};BindPassword={password}{caSuffix}");
        }
    }
}

/// <summary>
/// Wraps a parameter so its resolved value is connection-string-quoted at the moment Aspire
/// evaluates the expression — the only point where a runtime secret's content is knowable.
/// </summary>
internal sealed class QuotedParameterValue(ParameterResource parameter) : IValueProvider, IManifestExpressionProvider
{
    public string ValueExpression => parameter.ValueExpression;

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : ConnectionStringQuoting.Quote(value);
    }
}

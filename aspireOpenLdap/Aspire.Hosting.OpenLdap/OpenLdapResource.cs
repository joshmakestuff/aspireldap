using Aspire.Hosting.ApplicationModel;

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
    internal const string DefaultDockerContextPath = "../../openldap/2.6/debian-12";
    internal const string DefaultDockerfilePath = "Dockerfile";
    internal const string DefaultAdminUsername = "admin";
    internal const string DefaultAdminPassword = "adminpassword";
    internal const string DefaultLdapRoot = "dc=example,dc=org";
    internal const string DefaultUsers = "user01,user02";
    internal const string DefaultUserPasswords = "password1,password2";
    internal const string DataPath = "/data/openldap";

    private EndpointReference? _ldapEndpoint;
    private EndpointReference? _ldapsEndpoint;

    public OpenLdapResource(
        string name,
        string ldapRoot,
        string adminUsername,
        ParameterResource? adminPasswordParameter = null,
        string? adminPassword = null)
        : base(name)
    {
        LdapRoot = ldapRoot;
        AdminUsername = adminUsername;
        AdminPasswordParameter = adminPasswordParameter;
        AdminPassword = adminPassword;
    }

    /// <summary>The base DN / suffix (e.g. dc=example,dc=org).</summary>
    public string LdapRoot { get; }

    /// <summary>The admin username (CN component). Admin DN = cn={AdminUsername},{LdapRoot}.</summary>
    public string AdminUsername { get; }

    /// <summary>Optional parameter resource for the admin password (supports Aspire secrets).</summary>
    public ParameterResource? AdminPasswordParameter { get; }

    /// <summary>Literal admin password (used when AdminPasswordParameter is null).</summary>
    internal string? AdminPassword { get; }

    public EndpointReference LdapEndpoint =>
        _ldapEndpoint ??= new EndpointReference(this, LdapEndpointName);

    public EndpointReference LdapsEndpoint =>
        _ldapsEndpoint ??= new EndpointReference(this, LdapsEndpointName);

    /// <summary>
    /// Connection string in the format:
    /// Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=secret
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (AdminPasswordParameter is not null)
            {
                return ReferenceExpression.Create(
                    $"Endpoint=ldap://{LdapEndpoint.Property(EndpointProperty.HostAndPort)};BaseDN={LdapRoot};BindDN=cn={AdminUsername},{LdapRoot};BindPassword={AdminPasswordParameter}");
            }

            return ReferenceExpression.Create(
                $"Endpoint=ldap://{LdapEndpoint.Property(EndpointProperty.HostAndPort)};BaseDN={LdapRoot};BindDN=cn={AdminUsername},{LdapRoot};BindPassword={AdminPassword ?? DefaultAdminPassword}");
        }
    }
}

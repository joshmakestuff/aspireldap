using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

public sealed class OpenLdapResource(string name, ParameterResource? adminPasswordParameter = null)
    : ContainerResource(name), IResourceWithConnectionString
{
    internal const string LdapEndpointName = "ldap";
    internal const string LdapsEndpointName = "ldaps";
    internal const int DefaultLdapTargetPort = 1389;
    internal const int DefaultLdapsTargetPort = 1636;
    internal const string DefaultImageName = "bitnamilegacy/openldap";
    internal const string DefaultImageTag = "2.6";
    internal const string DefaultDockerContextPath = "../../openldap/2.6/debian-12";
    internal const string DefaultDockerfilePath = "Dockerfile";
    internal const string DefaultAdminUsername = "admin";
    internal const string DefaultAdminPassword = "adminpassword";
    internal const string DefaultUsers = "user01,user02";
    internal const string DefaultUserPasswords = "password1,password2";
    internal const string DataPath = "/bitnami/openldap";

    private EndpointReference? _ldapEndpoint;
    private EndpointReference? _ldapsEndpoint;

    public ParameterResource? AdminPasswordParameter { get; } = adminPasswordParameter;

    public EndpointReference LdapEndpoint => _ldapEndpoint ??= new EndpointReference(this, LdapEndpointName);

    public EndpointReference LdapsEndpoint => _ldapsEndpoint ??= new EndpointReference(this, LdapsEndpointName);

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"ldap://{LdapEndpoint.Property(EndpointProperty.HostAndPort)}");
}

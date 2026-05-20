namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a phpLDAPadmin web UI container attached to an <see cref="OpenLdapResource"/>.
/// </summary>
public sealed class PhpLdapAdminResource : ContainerResource
{
    internal const string DefaultImageName = "phpldapadmin/phpldapadmin";
    internal const string DefaultImageTag = "latest";
    internal const int ContainerHttpPort = 8080;
    internal const string HttpEndpointName = "http";

    public PhpLdapAdminResource(string name, OpenLdapResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;
    }

    /// <summary>The OpenLDAP resource this admin UI targets.</summary>
    public OpenLdapResource Parent { get; }
}

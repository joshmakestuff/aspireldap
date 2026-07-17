namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a phpLDAPadmin web UI container attached to an <see cref="OpenLdapResource"/>.
/// </summary>
public sealed class PhpLdapAdminResource : ContainerResource
{
    internal const string DefaultImageName = "phpldapadmin/phpldapadmin";
    // Pinned so a surprise upstream release can't break consumers; bump deliberately after
    // testing (2.3.11 = latest stable as of 2026-07). Override via WithImageTag if needed.
    internal const string DefaultImageTag = "2.3.11";
    internal const int ContainerHttpPort = 8080;
    internal const string HttpEndpointName = "http";

    /// <summary>
    /// Creates the resource. Use <c>WithPhpLdapAdmin(...)</c> on an OpenLDAP builder rather than
    /// constructing directly.
    /// </summary>
    public PhpLdapAdminResource(string name, OpenLdapResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;
    }

    /// <summary>The OpenLDAP resource this admin UI targets.</summary>
    public OpenLdapResource Parent { get; }
}

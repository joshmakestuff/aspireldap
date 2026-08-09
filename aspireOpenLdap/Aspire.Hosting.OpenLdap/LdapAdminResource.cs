namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the LdapAdmin web UI container attached to an <see cref="OpenLdapResource"/>.
/// The container is built locally from a build context bundled inside the
/// JoshMakeStuff.Aspire.Hosting.OpenLdap package (the pack-time-published admin app plus a
/// Dockerfile) — there is no registry-published image and no separate admin package, by decision.
/// </summary>
public sealed class LdapAdminResource : ContainerResource
{
    // Publish-time image name/tag only: the local docker build tag is content-hash-addressed
    // by Aspire's WithDockerfile, same as the parent OpenLDAP resource.
    internal const string DefaultImageName = "aspire-ldapadmin";
    internal const string DefaultImageTag = "1.0";
    internal const int ContainerHttpPort = 8080;
    internal const string HttpEndpointName = "http";
    internal const string HealthPath = "/health";

    /// <summary>
    /// Relative path of the admin docker build context inside the consumer's build output.
    /// The context (Dockerfile + published web app under <see cref="PayloadRelativePath"/>) is
    /// shipped as contentFiles in the nupkg and copied here at build time.
    /// </summary>
    internal const string DefaultDockerContextRelativePath = "ldapadmin";
    internal const string DefaultDockerfilePath = "Dockerfile";

    /// <summary>Subdirectory of the build context holding the published Aspire.LdapAdmin.Web output.</summary>
    internal const string PayloadRelativePath = "app";

    /// <summary>The published app's entry assembly — used to verify the payload is present.</summary>
    internal const string PayloadEntryAssembly = "Aspire.LdapAdmin.Web.dll";

    /// <summary>
    /// Absolute default docker build context path, resolved against the AppHost's build output
    /// (where the bundled Dockerfile + payload land via contentFiles). Packaged assets only —
    /// never the AspireLdap source checkout.
    /// </summary>
    internal static string DefaultDockerContextPath { get; } =
        Path.Combine(AppContext.BaseDirectory, DefaultDockerContextRelativePath);

    /// <summary>
    /// Creates the resource. Use <c>WithLdapAdmin(...)</c> on an OpenLDAP builder rather than
    /// constructing directly.
    /// </summary>
    public LdapAdminResource(string name, OpenLdapResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;
    }

    /// <summary>The OpenLDAP resource this admin UI targets.</summary>
    public OpenLdapResource Parent { get; }
}

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

public static class OpenLdapResourceBuilderExtensions
{
    /// <summary>
    /// Adds an OpenLDAP container resource built from the local Dockerfile.
    /// </summary>
    public static IResourceBuilder<OpenLdapResource> AddOpenLdap(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? ldapPort = null,
        int? ldapsPort = null,
        string dockerContextPath = OpenLdapResource.DefaultDockerContextPath,
        string dockerfilePath = OpenLdapResource.DefaultDockerfilePath,
        string ldapRoot = OpenLdapResource.DefaultLdapRoot,
        string adminUsername = OpenLdapResource.DefaultAdminUsername,
        string? adminPassword = null,
        IResourceBuilder<ParameterResource>? adminPasswordParameter = null,
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

        if (adminPasswordParameter is not null && adminPassword is not null)
        {
            throw new DistributedApplicationException(
                "Specify either adminPassword or adminPasswordParameter, but not both.");
        }

        var resource = new OpenLdapResource(
            name,
            ldapRoot: ldapRoot,
            adminUsername: adminUsername,
            adminPasswordParameter: adminPasswordParameter?.Resource,
            adminPassword: adminPassword);

        var openLdap = builder
            .AddResource(resource)
            .WithImage(OpenLdapResource.DefaultImageName, OpenLdapResource.DefaultImageTag)
            .WithDockerfile(dockerContextPath, dockerfilePath)
            .WithEndpoint(port: ldapPort, targetPort: OpenLdapResource.DefaultLdapTargetPort, name: OpenLdapResource.LdapEndpointName, isProxied: false)
            .WithEndpoint(port: ldapsPort, targetPort: OpenLdapResource.DefaultLdapsTargetPort, name: OpenLdapResource.LdapsEndpointName, isProxied: false)
            .WithEnvironment("LDAP_ROOT", ldapRoot)
            .WithEnvironment("LDAP_ADMIN_USERNAME", adminUsername)
            .WithEnvironment("LDAP_USERS", users)
            .WithEnvironment("LDAP_PASSWORDS", userPasswords);

        if (adminPasswordParameter is not null)
        {
            openLdap.WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_ADMIN_PASSWORD"] = adminPasswordParameter.Resource;
            });
        }
        else
        {
            openLdap.WithEnvironment("LDAP_ADMIN_PASSWORD", adminPassword ?? OpenLdapResource.DefaultAdminPassword);
        }

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
}

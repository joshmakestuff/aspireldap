using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

public static class OpenLdapResourceBuilderExtensions
{
    public static IResourceBuilder<OpenLdapResource> AddOpenLdap(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? ldapPort = null,
        int? ldapsPort = null,
        string imageName = OpenLdapResource.DefaultImageName,
        string imageTag = OpenLdapResource.DefaultImageTag,
        string adminUsername = OpenLdapResource.DefaultAdminUsername,
        string? adminPassword = null,
        IResourceBuilder<ParameterResource>? adminPasswordParameter = null,
        string users = OpenLdapResource.DefaultUsers,
        string userPasswords = OpenLdapResource.DefaultUserPasswords)
    {
        ValidateCommonArguments(builder, name, adminUsername, users, userPasswords);
        ValidateAdminPasswordArguments(adminPassword, adminPasswordParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageTag);

        var openLdap = builder
            .AddResource(new OpenLdapResource(name, adminPasswordParameter?.Resource))
            .WithImage(imageName, imageTag);

        return ConfigureCommon(openLdap, ldapPort, ldapsPort, adminUsername, adminPassword, adminPasswordParameter, users, userPasswords);
    }

    public static IResourceBuilder<OpenLdapResource> AddOpenLdapFromDockerProject(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? ldapPort = null,
        int? ldapsPort = null,
        string dockerContextPath = OpenLdapResource.DefaultDockerContextPath,
        string dockerfilePath = OpenLdapResource.DefaultDockerfilePath,
        string adminUsername = OpenLdapResource.DefaultAdminUsername,
        string? adminPassword = null,
        IResourceBuilder<ParameterResource>? adminPasswordParameter = null,
        string users = OpenLdapResource.DefaultUsers,
        string userPasswords = OpenLdapResource.DefaultUserPasswords)
    {
        ValidateCommonArguments(builder, name, adminUsername, users, userPasswords);
        ValidateAdminPasswordArguments(adminPassword, adminPasswordParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerContextPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerfilePath);

        var openLdap = builder
            .AddResource(new OpenLdapResource(name, adminPasswordParameter?.Resource))
            .WithImage(name, "latest")
            .WithDockerfile(dockerContextPath, dockerfilePath);

        return ConfigureCommon(openLdap, ldapPort, ldapsPort, adminUsername, adminPassword, adminPasswordParameter, users, userPasswords);
    }

    public static IResourceBuilder<OpenLdapResource> WithDataVolume(
        this IResourceBuilder<OpenLdapResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var volumeName = name ?? $"{builder.Resource.Name}-data";
        return builder.WithVolume(volumeName, OpenLdapResource.DataPath, isReadOnly);
    }

    public static IResourceBuilder<OpenLdapResource> WithDataBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, OpenLdapResource.DataPath, isReadOnly);
    }

    public static IResourceBuilder<OpenLdapResource> WithCustomLdifsBindMount(
        this IResourceBuilder<OpenLdapResource> builder,
        string source,
        bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return builder.WithBindMount(source, "/ldifs", isReadOnly);
    }

    private static IResourceBuilder<OpenLdapResource> ConfigureCommon(
        IResourceBuilder<OpenLdapResource> openLdap,
        int? ldapPort,
        int? ldapsPort,
        string adminUsername,
        string? adminPassword,
        IResourceBuilder<ParameterResource>? adminPasswordParameter,
        string users,
        string userPasswords)
    {
        openLdap
            .WithEndpoint(port: ldapPort, targetPort: OpenLdapResource.DefaultLdapTargetPort, name: OpenLdapResource.LdapEndpointName)
            .WithEndpoint(port: ldapsPort, targetPort: OpenLdapResource.DefaultLdapsTargetPort, name: OpenLdapResource.LdapsEndpointName)
            .WithEnvironment("LDAP_ADMIN_USERNAME", adminUsername)
            .WithEnvironment("LDAP_USERS", users)
            .WithEnvironment("LDAP_PASSWORDS", userPasswords)
            .WithDataVolume();

        if (adminPasswordParameter is not null)
        {
            openLdap.WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_ADMIN_PASSWORD"] = adminPasswordParameter.Resource;
            });

            return openLdap;
        }

        openLdap.WithEnvironment("LDAP_ADMIN_PASSWORD", adminPassword ?? OpenLdapResource.DefaultAdminPassword);
        return openLdap;
    }

    private static void ValidateCommonArguments(
        IDistributedApplicationBuilder builder,
        string name,
        string adminUsername,
        string users,
        string userPasswords)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(users);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPasswords);
    }

    private static void ValidateAdminPasswordArguments(
        string? adminPassword,
        IResourceBuilder<ParameterResource>? adminPasswordParameter)
    {
        if (adminPasswordParameter is not null && adminPassword is not null)
        {
            throw new DistributedApplicationException("Specify either adminPassword or adminPasswordParameter, but not both.");
        }
    }
}

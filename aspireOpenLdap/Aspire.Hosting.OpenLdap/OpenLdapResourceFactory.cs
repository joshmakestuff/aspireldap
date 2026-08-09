using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Builds the OpenLDAP container resource itself: services the integration needs, the resource
/// and its image/endpoints, the late-bound environment, the health check, and the dashboard
/// wiring. Everything the fluent overrides layer on afterwards lives in the other collaborators.
/// </summary>
internal static class OpenLdapResourceFactory
{
    internal static IResourceBuilder<OpenLdapResource> Create(
        IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource>? adminPassword)
    {
        // On Linux distros shipping OpenLDAP 2.6+ the runtime's hardcoded libldap-2.5 load
        // fails; register the soname fallback resolver so the health check's LdapConnection
        // works without a hand-made symlink.
        OpenLdapNativeLibraryResolver.EnsureRegistered();

        // Dashboard commands shell out to the container CLI through this abstraction; TryAdd
        // so tests (or advanced users) can substitute a runner before AddOpenLdap.
        builder.Services.TryAddSingleton<IContainerCliRunner, ProcessContainerCliRunner>();

        var passwordParameter = adminPassword?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var resource = new OpenLdapResource(
            name,
            baseDn: OpenLdapResource.DefaultBaseDn,
            adminUsername: OpenLdapResource.DefaultAdminUsername,
            adminPasswordParameter: passwordParameter);

        var openLdap = builder
            .AddResource(resource)
            // Sets the publish-time image name. The local docker build tag is content-hash-addressed
            // by Aspire's WithDockerfile and not affected by this call.
            .WithImage(OpenLdapResource.DefaultImageName, OpenLdapResource.DefaultImageTag)
            .WithDockerfile(OpenLdapResource.DefaultDockerContextPath, OpenLdapResource.DefaultDockerfilePath)
            // Proxied endpoints: Aspire allocates a free host port per run, so multiple
            // AppHosts (or multiple LDAP resources) never collide. Pin a fixed host port
            // via WithLdapPort / WithLdapsPort when a stable address is needed.
            .WithEndpoint(targetPort: OpenLdapResource.DefaultLdapTargetPort, name: OpenLdapResource.LdapEndpointName)
            .WithEndpoint(targetPort: OpenLdapResource.DefaultLdapsTargetPort, name: OpenLdapResource.LdapsEndpointName)
            // Late-binding env values so fluent overrides (e.g. WithBaseDn) take effect when the container starts.
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["LDAP_ROOT"] = resource.BaseDn;
                context.EnvironmentVariables["LDAP_ADMIN_USERNAME"] = resource.AdminUsername;
                context.EnvironmentVariables["LDAP_ADMIN_PASSWORD"] = passwordParameter;
            });

        RegisterHealthCheck(builder, openLdap, resource, name);
        OpenLdapDashboardCommands.Register(openLdap);
        ConfigureEndpointUrls(openLdap, resource);

        return openLdap;
    }

    /// <summary>Registers the LDAP root DSE health check and attaches it to the resource.</summary>
    private static void RegisterHealthCheck(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<OpenLdapResource> openLdap,
        OpenLdapResource resource,
        string name)
    {
        var healthCheckName = $"openldap-{name}";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckName,
            sp => new OpenLdapHealthCheck(resource),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));

        openLdap.WithHealthCheck(healthCheckName);
    }

    /// <summary>
    /// Surfaces the base DN next to the endpoint URL on the dashboard so users don't have to
    /// click through env vars. Lambdas read resource.BaseDn lazily, so WithBaseDn(...) overrides
    /// are picked up.
    /// </summary>
    private static void ConfigureEndpointUrls(
        IResourceBuilder<OpenLdapResource> openLdap, OpenLdapResource resource)
    {
        openLdap
            .WithUrlForEndpoint(OpenLdapResource.LdapEndpointName, url =>
            {
                url.DisplayText = $"ldap (base={resource.BaseDn})";
            })
            .WithUrlForEndpoint(OpenLdapResource.LdapsEndpointName, url =>
            {
                url.DisplayText = $"ldaps (base={resource.BaseDn})";
            });
    }

    /// <summary>
    /// Pins the host port of one of the resource's endpoints. Validates here so a bad value
    /// fails at the fluent call rather than later inside Aspire's endpoint allocation with a
    /// less attributable error.
    /// </summary>
    internal static void SetEndpointPort(
        IResourceBuilder<OpenLdapResource> builder, string endpointName, int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var annotation = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new DistributedApplicationException(
                $"Endpoint '{endpointName}' not found on OpenLDAP resource '{builder.Resource.Name}'.");
        annotation.Port = port;
    }
}

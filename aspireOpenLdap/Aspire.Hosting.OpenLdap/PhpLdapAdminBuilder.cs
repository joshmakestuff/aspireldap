using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Construction of the phpLDAPadmin sidecar container and everything it needs to reach the
/// parent OpenLDAP resource over the Aspire-managed container network. See
/// <see cref="OpenLdapResourceBuilderExtensions.WithPhpLdapAdmin"/> for the user-facing contract.
/// </summary>
internal static class PhpLdapAdminBuilder
{
    internal static void Add(
        IResourceBuilder<OpenLdapResource> builder,
        Action<IResourceBuilder<PhpLdapAdminResource>>? configureContainer,
        string? containerName,
        string? loginObjectClass)
    {
        var parent = builder.Resource;
        var adminName = containerName ?? $"{parent.Name}-admin";
        var adminResource = new PhpLdapAdminResource(adminName, parent);

        var admin = builder.ApplicationBuilder
            .AddResource(adminResource)
            .WithImage(PhpLdapAdminResource.DefaultImageName, PhpLdapAdminResource.DefaultImageTag)
            .WithHttpEndpoint(targetPort: PhpLdapAdminResource.ContainerHttpPort, name: PhpLdapAdminResource.HttpEndpointName)
            .WithEnvironment("LDAP_LOGIN_OBJECTCLASS", loginObjectClass ?? "inetOrgPerson")
            // The image is a Laravel app whose default log channel is a file inside the
            // container ('daily'), so LDAP failures — unreachable server, bad admin bind —
            // never reach the container log or the dashboard console. Route the app log to
            // stderr, at 'info' so failures (ERROR) and login attempts (INFO) surface while
            // the per-page-render DEBUG dumps (full root-DSE etc.) stay suppressed.
            .WithEnvironment("LOG_CHANNEL", "stderr")
            .WithEnvironment("LOG_LEVEL", "info")
            // All parent-derived settings resolve when the admin container starts, so fluent
            // calls chained on the parent AFTER WithPhpLdapAdmin (WithBaseDn, WithAdminUsername,
            // WithTls().WithRequiredTls()) still take effect here.
            .WithEnvironment(context =>
            {
                // Inside the container network the admin connects to the parent by resource name.
                // If TLS is required we point at the LDAPS target port; otherwise plain LDAP.
                context.EnvironmentVariables["LDAP_HOST"] = parent.Name;
                context.EnvironmentVariables["LDAP_PORT"] = (parent.TlsRequired
                    ? OpenLdapResource.DefaultLdapsTargetPort
                    : OpenLdapResource.DefaultLdapTargetPort).ToString(CultureInfo.InvariantCulture);
                context.EnvironmentVariables["LDAP_BASE_DN"] = parent.BaseDn;
                context.EnvironmentVariables["LDAP_USERNAME"] = parent.AdminBindDn;
                context.EnvironmentVariables["LDAP_PASSWORD"] = parent.AdminPasswordParameter;

                if (parent.TlsRequired)
                {
                    // Use the image's preconfigured 'ldaps' connection (use_ssl=true). Self-signed
                    // CA isn't trusted inside the admin container so disable libldap's cert
                    // verification for local dev.
                    context.EnvironmentVariables["LDAP_CONNECTION"] = "ldaps";
                    context.EnvironmentVariables["LDAP_SSL"] = "true";
                    context.EnvironmentVariables["LDAPTLS_REQCERT"] = "never";
                }
            })
            // Deliberately a static asset, not the login page: the login page performs a real
            // admin bind + root-DSE query on every render, so health-polling it flooded the
            // LDAP container's log with un-filterable query noise (#31). LDAP connectivity is
            // covered by the parent resource's own health check plus WaitFor below; this check
            // only proves the admin container serves HTTP.
            .WithHttpHealthCheck(path: "/robots.txt", statusCode: 200, endpointName: PhpLdapAdminResource.HttpEndpointName)
            .WaitFor(builder);

        configureContainer?.Invoke(admin);
    }
}

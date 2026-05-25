using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods to register an OpenLDAP client backed by an Aspire-emitted connection string.
/// </summary>
public static class OpenLdapClientExtensions
{
    private const string DefaultConfigSectionName = "Aspire:OpenLdap";

    /// <summary>
    /// Registers an <see cref="OpenLdapClientFactory"/> (singleton) and a transient
    /// <see cref="LdapConnection"/> resolved from it. By default also registers a health check.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="connectionName">The connection string name (e.g. the Aspire resource name).</param>
    /// <param name="configureSettings">Optional callback to tweak settings.</param>
    public static IHostApplicationBuilder AddOpenLdapClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<OpenLdapClientSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var settings = new OpenLdapClientSettings();
        builder.Configuration.GetSection(DefaultConfigSectionName).Bind(settings);
        settings.ConnectionString ??= builder.Configuration.GetConnectionString(connectionName);
        configureSettings?.Invoke(settings);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                $"No connection string found for '{connectionName}'. " +
                $"Set ConnectionStrings:{connectionName} or {DefaultConfigSectionName}:ConnectionString.");
        }

        var parsed = OpenLdapConnectionStringBuilder.Parse(settings.ConnectionString);
        var factory = new OpenLdapClientFactory(parsed, settings);

        builder.Services.TryAddSingleton(factory);
        builder.Services.TryAddTransient(sp => sp.GetRequiredService<OpenLdapClientFactory>().CreateConnection());

        if (!settings.DisableHealthChecks)
        {
            builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                $"openldap_{connectionName}",
                sp => new OpenLdapClientHealthCheck(sp.GetRequiredService<OpenLdapClientFactory>()),
                failureStatus: HealthStatus.Unhealthy,
                tags: null));
        }

        return builder;
    }
}

internal sealed class OpenLdapClientHealthCheck(OpenLdapClientFactory factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = factory.CreateConnection();
            // "aspire-healthcheck" sentinel — see OpenLdapHealthCheck for why.
            var request = new SearchRequest(
                distinguishedName: "",
                ldapFilter: "(objectClass=*)",
                searchScope: SearchScope.Base,
                attributeList: ["namingContexts", "aspire-healthcheck"]);

            var response = await Task.Run(
                () => (SearchResponse)connection.SendRequest(request),
                cancellationToken).ConfigureAwait(false);

            return response.ResultCode == ResultCode.Success
                ? HealthCheckResult.Healthy("LDAP root DSE query succeeded.")
                : HealthCheckResult.Unhealthy($"Unexpected LDAP result code: {response.ResultCode}");
        }
        catch (LdapException ex)
        {
            return HealthCheckResult.Unhealthy("LDAP connection failed.", ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Unexpected error during LDAP health check.", ex);
        }
    }
}

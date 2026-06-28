using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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

        return AddOpenLdapClientCore(builder, connectionName, serviceKey: null, configureSettings);
    }

    /// <summary>
    /// Registers a keyed <see cref="OpenLdapClientFactory"/> (singleton) and a keyed transient
    /// <see cref="LdapConnection"/> under the service key <paramref name="name"/>, allowing multiple
    /// OpenLDAP directories in one app. Resolve with <c>[FromKeyedServices(name)]</c> or
    /// <c>GetRequiredKeyedService&lt;LdapConnection&gt;(name)</c>. By default also registers a health check.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="name">The connection string name, also used as the DI service key.</param>
    /// <param name="configureSettings">Optional callback to tweak settings.</param>
    public static IHostApplicationBuilder AddKeyedOpenLdapClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<OpenLdapClientSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return AddOpenLdapClientCore(builder, connectionName: name, serviceKey: name, configureSettings);
    }

    private static IHostApplicationBuilder AddOpenLdapClientCore(
        IHostApplicationBuilder builder,
        string connectionName,
        string? serviceKey,
        Action<OpenLdapClientSettings>? configureSettings)
    {
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

        if (serviceKey is null)
        {
            builder.Services.TryAddSingleton(factory);
            builder.Services.TryAddTransient(sp => sp.GetRequiredService<OpenLdapClientFactory>().CreateConnection());
            builder.Services.TryAddTransient(sp =>
                new OpenLdapClient(sp.GetRequiredService<OpenLdapClientFactory>().CreateConnection(), settings, parsed));
        }
        else
        {
            builder.Services.TryAddKeyedSingleton(serviceKey, factory);
            builder.Services.TryAddKeyedTransient<LdapConnection>(
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<OpenLdapClientFactory>(key).CreateConnection());
            builder.Services.TryAddKeyedTransient<OpenLdapClient>(
                serviceKey,
                (sp, key) => new OpenLdapClient(
                    sp.GetRequiredKeyedService<OpenLdapClientFactory>(key).CreateConnection(), settings, parsed));
        }

        // Register the Aspire.OpenLdap activity source / meter with the app's OpenTelemetry
        // pipeline so spans and metrics from OpenLdapClient flow to whatever exporter the app
        // configured (e.g. via Aspire's AddServiceDefaults). AddOpenTelemetry is additive and
        // idempotent — no exporter is registered here.
        if (!settings.DisableTracing || !settings.DisableMetrics)
        {
            var openTelemetry = builder.Services.AddOpenTelemetry();
            if (!settings.DisableTracing)
            {
                openTelemetry.WithTracing(tracing => tracing.AddSource(OpenLdapInstrumentation.Name));
            }
            if (!settings.DisableMetrics)
            {
                openTelemetry.WithMetrics(metrics => metrics.AddMeter(OpenLdapInstrumentation.Name));
            }
        }

        if (!settings.DisableHealthChecks)
        {
            builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                serviceKey is null ? $"openldap_{connectionName}" : $"openldap_{serviceKey}",
                sp => new OpenLdapClientHealthCheck(
                    serviceKey is null
                        ? sp.GetRequiredService<OpenLdapClientFactory>()
                        : sp.GetRequiredKeyedService<OpenLdapClientFactory>(serviceKey)),
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

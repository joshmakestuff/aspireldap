namespace Aspire.OpenLdap;

/// <summary>
/// Configuration for an OpenLDAP client registered by <c>AddOpenLdapClient</c>.
/// Bound from <c>Aspire:OpenLdap</c> by default.
/// </summary>
public sealed class OpenLdapClientSettings
{
    /// <summary>The connection string emitted by the Aspire OpenLDAP hosting resource.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Disables the LDAP health check registration when true.</summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Disables OpenTelemetry tracing (the <c>Aspire.OpenLdap</c> activity source) for operations
    /// issued through <see cref="OpenLdapClient"/> when true.
    /// </summary>
    public bool DisableTracing { get; set; }

    /// <summary>
    /// Disables OpenTelemetry metrics (the <c>Aspire.OpenLdap</c> meter) for operations issued
    /// through <see cref="OpenLdapClient"/> when true.
    /// </summary>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// When true (default), trust the CA file referenced by the connection string's
    /// <c>CaCertFile</c> value for LDAPS connections. Set false to use the system trust store only.
    /// </summary>
    public bool TrustConnectionStringCaCertificate { get; set; } = true;

    /// <summary>Connection timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

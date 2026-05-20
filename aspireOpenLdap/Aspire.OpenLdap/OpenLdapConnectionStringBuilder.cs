namespace Aspire.OpenLdap;

/// <summary>
/// Parses an OpenLDAP connection string emitted by <c>Aspire.Hosting.OpenLdap</c>.
/// Expected format:
/// <code>
/// Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=secret;CaCertFile=/path/to/ca.crt
/// </code>
/// <c>CaCertFile</c> is optional and only present when TLS is enabled on the server.
/// </summary>
public sealed class OpenLdapConnectionStringBuilder
{
    private const string EndpointKey = "Endpoint";
    private const string BaseDnKey = "BaseDN";
    private const string BindDnKey = "BindDN";
    private const string BindPasswordKey = "BindPassword";
    private const string CaCertFileKey = "CaCertFile";

    public required Uri Endpoint { get; init; }
    public required string BaseDn { get; init; }
    public required string BindDn { get; init; }
    public required string BindPassword { get; init; }
    public string? CaCertFile { get; init; }

    public bool UsesLdaps => string.Equals(Endpoint.Scheme, "ldaps", StringComparison.OrdinalIgnoreCase);

    public static OpenLdapConnectionStringBuilder Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                throw new FormatException($"Malformed connection-string segment: '{segment}'.");
            }
            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            pairs[key] = value;
        }

        if (!pairs.TryGetValue(EndpointKey, out var endpointRaw))
        {
            throw new FormatException($"Connection string is missing the required '{EndpointKey}' key.");
        }
        if (!pairs.TryGetValue(BaseDnKey, out var baseDn))
        {
            throw new FormatException($"Connection string is missing the required '{BaseDnKey}' key.");
        }
        if (!pairs.TryGetValue(BindDnKey, out var bindDn))
        {
            throw new FormatException($"Connection string is missing the required '{BindDnKey}' key.");
        }
        if (!pairs.TryGetValue(BindPasswordKey, out var bindPassword))
        {
            throw new FormatException($"Connection string is missing the required '{BindPasswordKey}' key.");
        }

        if (!Uri.TryCreate(endpointRaw, UriKind.Absolute, out var endpoint))
        {
            throw new FormatException($"'{EndpointKey}' is not a valid absolute URI: '{endpointRaw}'.");
        }

        pairs.TryGetValue(CaCertFileKey, out var caCertFile);

        return new OpenLdapConnectionStringBuilder
        {
            Endpoint = endpoint,
            BaseDn = baseDn,
            BindDn = bindDn,
            BindPassword = bindPassword,
            CaCertFile = string.IsNullOrWhiteSpace(caCertFile) ? null : caCertFile,
        };
    }
}

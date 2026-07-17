using System.Text;

namespace Aspire.OpenLdap;

/// <summary>
/// Parses an OpenLDAP connection string emitted by <c>Aspire.Hosting.OpenLdap</c>.
/// Expected format:
/// <code>
/// Endpoint=ldap://host:port;BaseDN=dc=example,dc=org;BindDN=cn=admin,dc=example,dc=org;BindPassword=secret;CaCertFile=/path/to/ca.crt
/// </code>
/// Values containing semicolons, double quotes, or leading/trailing whitespace are double-quoted
/// with embedded quotes doubled (<c>BindPassword="ab;c""d"</c>), so arbitrary passwords and DNs
/// round-trip. <c>CaCertFile</c> is optional and only present when TLS is enabled on the server.
/// </summary>
public sealed class OpenLdapConnectionStringBuilder
{
    private const string EndpointKey = "Endpoint";
    private const string BaseDnKey = "BaseDN";
    private const string BindDnKey = "BindDN";
    private const string BindPasswordKey = "BindPassword";
    private const string CaCertFileKey = "CaCertFile";

    /// <summary>The LDAP server endpoint (<c>ldap://host:port</c> or <c>ldaps://host:port</c>).</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The directory's base DN / suffix (e.g. <c>dc=example,dc=org</c>).</summary>
    public required string BaseDn { get; init; }

    /// <summary>The DN used to bind (e.g. <c>cn=admin,dc=example,dc=org</c>).</summary>
    public required string BindDn { get; init; }

    /// <summary>The password for <see cref="BindDn"/>.</summary>
    public required string BindPassword { get; init; }

    /// <summary>Path to a PEM CA certificate to trust for LDAPS; null when TLS is not enabled on the server.</summary>
    public string? CaCertFile { get; init; }

    /// <summary>True when <see cref="Endpoint"/> uses the <c>ldaps</c> scheme.</summary>
    public bool UsesLdaps => string.Equals(Endpoint.Scheme, "ldaps", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a connection string in the format documented on this class.
    /// </summary>
    /// <exception cref="FormatException">
    /// A required key is missing, a key is duplicated, the endpoint is not a valid
    /// <c>ldap</c>/<c>ldaps</c> host URI, or a quoted value is malformed.
    /// </exception>
    public static OpenLdapConnectionStringBuilder Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var pairs = ParsePairs(connectionString);

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

        var endpoint = ParseEndpoint(endpointRaw);

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

    private static Uri ParseEndpoint(string endpointRaw)
    {
        if (!Uri.TryCreate(endpointRaw, UriKind.Absolute, out var endpoint))
        {
            throw new FormatException($"'{EndpointKey}' is not a valid absolute URI: '{endpointRaw}'.");
        }
        if (endpoint.Scheme is not ("ldap" or "ldaps"))
        {
            throw new FormatException($"'{EndpointKey}' scheme must be 'ldap' or 'ldaps', got '{endpoint.Scheme}'.");
        }
        if (string.IsNullOrEmpty(endpoint.Host))
        {
            throw new FormatException($"'{EndpointKey}' must include a host: '{endpointRaw}'.");
        }
        if (endpoint.Port <= 0)
        {
            throw new FormatException($"'{EndpointKey}' must include an explicit port: '{endpointRaw}'.");
        }
        if (endpoint.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(endpoint.Query))
        {
            throw new FormatException($"'{EndpointKey}' must not contain a path or query: '{endpointRaw}'.");
        }
        // LdapDirectoryIdentifier only uses host and port; silently ignoring user-info or a
        // fragment would hide a configuration mistake, so reject them too.
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new FormatException($"'{EndpointKey}' must not contain user info or a fragment: '{endpointRaw}'.");
        }
        return endpoint;
    }

    // Splits "Key=Value;Key2=Value2" pairs. An unquoted value runs to the next ';' and is
    // trimmed; a value starting with '"' runs to the closing quote, may contain ';' and
    // doubled quotes (""), and is taken verbatim.
    private static Dictionary<string, string> ParsePairs(string connectionString)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < connectionString.Length)
        {
            // Skip separators and stray whitespace between pairs.
            while (i < connectionString.Length && (connectionString[i] == ';' || char.IsWhiteSpace(connectionString[i])))
            {
                i++;
            }
            if (i >= connectionString.Length)
            {
                break;
            }

            var eq = connectionString.IndexOf('=', i);
            var nextSemi = connectionString.IndexOf(';', i);
            if (eq < 0 || (nextSemi >= 0 && nextSemi < eq))
            {
                var segment = connectionString[i..(nextSemi < 0 ? connectionString.Length : nextSemi)];
                throw new FormatException($"Malformed connection-string segment: '{segment.Trim()}'.");
            }

            var key = connectionString[i..eq].Trim();
            if (key.Length == 0)
            {
                throw new FormatException("Connection string contains a pair with an empty key.");
            }
            i = eq + 1;

            while (i < connectionString.Length && connectionString[i] == ' ')
            {
                i++;
            }

            string value;
            if (i < connectionString.Length && connectionString[i] == '"')
            {
                (value, i) = ReadQuotedValue(connectionString, i);
            }
            else
            {
                var end = connectionString.IndexOf(';', i);
                if (end < 0)
                {
                    end = connectionString.Length;
                }
                value = connectionString[i..end].Trim();
                i = end;
            }

            if (!pairs.TryAdd(key, value))
            {
                throw new FormatException($"Connection string contains duplicate key '{key}'.");
            }
        }
        return pairs;
    }

    private static (string Value, int Next) ReadQuotedValue(string s, int openQuote)
    {
        var sb = new StringBuilder();
        var i = openQuote + 1;
        while (true)
        {
            if (i >= s.Length)
            {
                throw new FormatException("Connection string contains an unterminated quoted value.");
            }
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }
                i++;
                break;
            }
            sb.Append(s[i]);
            i++;
        }

        // Only whitespace may follow a closing quote before the next ';' or end of string.
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
        if (i < s.Length && s[i] != ';')
        {
            throw new FormatException("Connection string has unexpected characters after a quoted value.");
        }
        return (sb.ToString(), i);
    }
}

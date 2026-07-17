using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.OpenLdap;

/// <summary>
/// Builds <see cref="LdapConnection"/> instances configured from parsed connection-string settings.
/// </summary>
public sealed class OpenLdapClientFactory
{
    private readonly OpenLdapConnectionStringBuilder _connectionString;
    private readonly OpenLdapClientSettings _settings;
    private readonly Lazy<X509Certificate2?> _caCertificate;

    /// <summary>Creates a factory for the given parsed connection string and client settings.</summary>
    public OpenLdapClientFactory(OpenLdapConnectionStringBuilder connectionString, OpenLdapClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(settings);
        _connectionString = connectionString;
        _settings = settings;
        _caCertificate = new Lazy<X509Certificate2?>(LoadCaCertificate);
    }

    /// <summary>The parsed connection-string settings this factory was created with.</summary>
    public OpenLdapConnectionStringBuilder ConnectionString => _connectionString;

    /// <summary>Creates a new <see cref="LdapConnection"/>. Caller owns disposal.</summary>
    public LdapConnection CreateConnection()
    {
        var endpoint = _connectionString.Endpoint;

        // On the Linux native-trust path, dial an IP literal: libldap checks the cert against
        // the peer address's reverse-DNS name (not the dialed name), which on typical NSS
        // stacks resolves 127.0.0.1 to the machine hostname and can never match. IP dials are
        // validated against the cert's IP SANs instead. The Windows callback below still
        // validates against the ORIGINAL endpoint host.
        var usesLinuxNativeTrust = OperatingSystem.IsLinux()
            && _connectionString.UsesLdaps
            && _settings.TrustConnectionStringCaCertificate
            && _caCertificate.Value is not null;
        var dialHost = usesLinuxNativeTrust
            ? OpenLdapUnixTlsTrust.ResolveDialHost(endpoint.Host)
            : endpoint.Host;

        var identifier = new LdapDirectoryIdentifier(
            dialHost,
            endpoint.Port,
            fullyQualifiedDnsHostName: false,
            connectionless: false);

        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(_connectionString.BindDn, _connectionString.BindPassword),
            Timeout = _settings.Timeout,
        };
        connection.SessionOptions.ProtocolVersion = 3;

        if (_connectionString.UsesLdaps)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            if (_settings.TrustConnectionStringCaCertificate && _caCertificate.Value is { } ca)
            {
                ConfigureCustomTrust(connection, ca, endpoint.Host);
            }
        }

        return connection;
    }

    /// <summary>
    /// Trusts the connection string's CA for this connection. The mechanism is per-platform:
    /// Windows wldap32 supports a managed verification callback; Linux libldap rejects that
    /// callback before the first request (the setter itself throws), so trust is configured
    /// natively via an OpenSSL hash-named CA directory instead; macOS LDAP.framework supports
    /// neither, so custom trust is refused up front with an actionable message.
    /// </summary>
    private void ConfigureCustomTrust(LdapConnection connection, X509Certificate2 ca, string host)
    {
        if (OperatingSystem.IsWindows())
        {
            // Chain must reach the connection string's CA, and the certificate must name the
            // host we dialed — unless the caller explicitly opted out of hostname validation.
            var expectedHost = _settings.DisableTlsHostnameValidation ? null : host;
            connection.SessionOptions.VerifyServerCertificate = (_, serverCert) =>
                OpenLdapCertificateValidation.ValidateAgainstCustomRoot(serverCert, ca, expectedHost);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            if (_settings.DisableTlsHostnameValidation)
            {
                throw new PlatformNotSupportedException(
                    "DisableTlsHostnameValidation is not supported on Linux: libldap validates the " +
                    "server hostname natively and offers no hostname-only opt-out. Use a server " +
                    "certificate that names the endpoint host, or run on Windows.");
            }

            // libldap validates the chain (against the staged CA directory) and the hostname
            // natively during the handshake; no managed callback is involved.
            connection.SessionOptions.TrustedCertificatesDirectory =
                OpenLdapUnixTlsTrust.EnsureTrustDirectory(_connectionString.CaCertFile!);
            connection.SessionOptions.StartNewTlsSessionContext();
            return;
        }

        throw new PlatformNotSupportedException(
            "Trusting the connection string's CA certificate is not supported on this OS (Apple's " +
            "LDAP.framework rejects OpenSSL-style trust options). Set " +
            $"{nameof(OpenLdapClientSettings.TrustConnectionStringCaCertificate)} to false to use " +
            "the system trust store, after adding the CA to it out of band.");
    }

    private X509Certificate2? LoadCaCertificate()
    {
        if (string.IsNullOrWhiteSpace(_connectionString.CaCertFile))
        {
            return null;
        }
        if (!File.Exists(_connectionString.CaCertFile))
        {
            return null;
        }

        return OpenLdapCertificateValidation.LoadPemCertificate(_connectionString.CaCertFile);
    }
}

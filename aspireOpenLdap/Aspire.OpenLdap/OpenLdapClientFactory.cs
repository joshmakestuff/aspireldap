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

    public OpenLdapClientFactory(OpenLdapConnectionStringBuilder connectionString, OpenLdapClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(settings);
        _connectionString = connectionString;
        _settings = settings;
        _caCertificate = new Lazy<X509Certificate2?>(LoadCaCertificate);
    }

    public OpenLdapConnectionStringBuilder ConnectionString => _connectionString;

    /// <summary>Creates a new <see cref="LdapConnection"/>. Caller owns disposal.</summary>
    public LdapConnection CreateConnection()
    {
        var endpoint = _connectionString.Endpoint;
        var identifier = new LdapDirectoryIdentifier(
            endpoint.Host,
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
                // Chain must reach the connection string's CA, and the certificate must name the
                // host we dialed — unless the caller explicitly opted out of hostname validation.
                var expectedHost = _settings.DisableTlsHostnameValidation ? null : endpoint.Host;
                connection.SessionOptions.VerifyServerCertificate = (_, serverCert) =>
                    OpenLdapCertificateValidation.ValidateAgainstCustomRoot(serverCert, ca, expectedHost);
            }
        }

        return connection;
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

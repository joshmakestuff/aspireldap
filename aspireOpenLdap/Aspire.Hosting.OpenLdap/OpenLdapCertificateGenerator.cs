using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Generates a self-signed CA and a server certificate for local OpenLDAP TLS.
/// Certs are cached under {appHostDir}/obj/aspire-openldap-certs/{resourceName}/
/// and regenerated only when missing or near expiry.
/// </summary>
internal static class OpenLdapCertificateGenerator
{
    private const int KeySizeBits = 2048;
    private static readonly TimeSpan CertLifetime = TimeSpan.FromDays(365 * 2);
    private static readonly TimeSpan RegenWithinExpiry = TimeSpan.FromDays(30);

    public readonly record struct GeneratedCertificates(
        string Directory,
        string CaCertPath,
        string ServerCertPath,
        string ServerKeyPath);

    public static GeneratedCertificates EnsureCertificates(string appHostDir, string resourceName)
    {
        var dir = Path.Combine(appHostDir, "obj", "aspire-openldap-certs", resourceName);
        System.IO.Directory.CreateDirectory(dir);

        var caPath = Path.Combine(dir, "ca.crt");
        var serverCertPath = Path.Combine(dir, "server.crt");
        var serverKeyPath = Path.Combine(dir, "server.key");

        if (CertsAreFresh(resourceName, caPath, serverCertPath, serverKeyPath))
        {
            return new GeneratedCertificates(dir, caPath, serverCertPath, serverKeyPath);
        }

        Generate(resourceName, caPath, serverCertPath, serverKeyPath);
        return new GeneratedCertificates(dir, caPath, serverCertPath, serverKeyPath);
    }

    /// <summary>
    /// A cached set is reused only when ALL THREE files are individually valid AND consistent
    /// with each other: the key matches the server certificate, the server certificate chains
    /// to the CA, both are inside their validity window, and the expected SANs are present.
    /// Checking only the server certificate would let a corrupt CA or mismatched key survive
    /// as "fresh" for up to two years, producing a persistent opaque LDAPS failure.
    /// </summary>
    private static bool CertsAreFresh(string resourceName, string caPath, string serverCertPath, string serverKeyPath)
    {
        if (!File.Exists(caPath) || !File.Exists(serverCertPath) || !File.Exists(serverKeyPath))
        {
            return false;
        }

        try
        {
            // The container's non-root user must be able to read the key across the bind
            // mount; treat a cached key without world-read as stale so regeneration heals it
            // (a set written while permissions were restricted would otherwise fail forever).
            if (!OperatingSystem.IsWindows() &&
                !File.GetUnixFileMode(serverKeyPath).HasFlag(UnixFileMode.OtherRead))
            {
                return false;
            }

            using var caCert = Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.LoadPemCertificate(caPath);
            // Pairing the certificate with the key file throws when they don't correspond.
            using var serverCert = X509Certificate2.CreateFromPemFile(serverCertPath, serverKeyPath);

            var now = DateTime.UtcNow;
            if (caCert.NotBefore > now || caCert.NotAfter - now <= RegenWithinExpiry ||
                serverCert.NotBefore > now || serverCert.NotAfter - now <= RegenWithinExpiry)
            {
                return false;
            }

            if (!Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.ValidateAgainstCustomRoot(
                    serverCert, caCert, expectedHost: null))
            {
                return false;
            }

            // The health check dials localhost and containers dial the resource name.
            return Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.MatchesHost(serverCert, "localhost")
                && Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.MatchesHost(serverCert, resourceName)
                && Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.MatchesHost(serverCert, "127.0.0.1");
        }
        catch
        {
            return false;
        }
    }

    private static void Generate(string resourceName, string caPath, string serverCertPath, string serverKeyPath)
    {
        var now = DateTimeOffset.UtcNow;
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.Add(CertLifetime);

        // CA
        using var caKey = RSA.Create(KeySizeBits);
        var caRequest = new CertificateRequest(
            "CN=Aspire OpenLDAP Local CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));

        using var caCert = caRequest.CreateSelfSigned(notBefore, notAfter);

        // Server
        using var serverKey = RSA.Create(KeySizeBits);
        var serverRequest = new CertificateRequest(
            $"CN={resourceName}",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(resourceName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        serverRequest.CertificateExtensions.Add(sanBuilder.Build());

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);
        using var serverCert = serverRequest.Create(caCert, notBefore, notAfter, serialNumber);

        // Write the whole set to temp files first, then move into place — an interruption can
        // no longer leave a mixed old/new set behind, and CertsAreFresh rejects any mix that
        // does slip through (e.g. a crash between the moves).
        //
        // The private key deliberately keeps default (umask, typically world-readable)
        // permissions: the container bind-mounts it and slapd reads it as the non-root
        // `openldap` user, whose uid differs from the host user's — 0600 makes TLS setup fail
        // with err=80 on Linux hosts (Docker Desktop hides this by exposing mounts
        // world-readable). It's a locally-generated localhost-only dev certificate.
        WriteAtomically(caPath, ExportCertificatePem(caCert));
        WriteAtomically(serverCertPath, ExportCertificatePem(serverCert));
        WriteAtomically(serverKeyPath, ExportPrivateKeyPem(serverKey));
    }

    private static void WriteAtomically(string path, string content)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ExportCertificatePem(X509Certificate2 cert)
    {
        return new string(PemEncoding.Write("CERTIFICATE", cert.RawData));
    }

    private static string ExportPrivateKeyPem(RSA key)
    {
        return new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey()));
    }
}

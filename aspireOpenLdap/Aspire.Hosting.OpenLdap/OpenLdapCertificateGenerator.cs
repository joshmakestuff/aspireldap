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

        if (CertsAreFresh(caPath, serverCertPath, serverKeyPath))
        {
            return new GeneratedCertificates(dir, caPath, serverCertPath, serverKeyPath);
        }

        Generate(resourceName, caPath, serverCertPath, serverKeyPath);
        return new GeneratedCertificates(dir, caPath, serverCertPath, serverKeyPath);
    }

    private static bool CertsAreFresh(string caPath, string serverCertPath, string serverKeyPath)
    {
        if (!File.Exists(caPath) || !File.Exists(serverCertPath) || !File.Exists(serverKeyPath))
        {
            return false;
        }

        try
        {
            using var cert = X509Certificate2.CreateFromPemFile(serverCertPath);
            return cert.NotAfter - DateTime.UtcNow > RegenWithinExpiry;
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

        File.WriteAllText(caPath, ExportCertificatePem(caCert));
        File.WriteAllText(serverCertPath, ExportCertificatePem(serverCert));
        File.WriteAllText(serverKeyPath, ExportPrivateKeyPem(serverKey));
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

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Compiled into both assemblies under distinct namespaces so the two internal copies never
// collide (CS0433) in a project that can see both assemblies' internals (e.g. the test project).
#if ASPIRE_HOSTING_OPENLDAP
namespace Aspire.Hosting.OpenLdap;
#else
namespace Aspire.OpenLdap;
#endif

/// <summary>
/// Server-certificate validation for LDAPS connections that trust a custom (connection-string
/// supplied) CA: the certificate must chain to that root AND match the endpoint host, so a
/// certificate minted by the right CA for the wrong server is still rejected.
/// </summary>
internal static class OpenLdapCertificateValidation
{
    /// <summary>
    /// Validates <paramref name="serverCert"/> against <paramref name="trustedRoot"/> and, unless
    /// <paramref name="expectedHost"/> is null (explicit dev opt-out), against the host we dialed.
    /// </summary>
    public static bool ValidateAgainstCustomRoot(
        System.Security.Cryptography.X509Certificates.X509Certificate serverCert,
        X509Certificate2 trustedRoot,
        string? expectedHost)
    {
        using var cert = X509CertificateLoader.LoadCertificate(serverCert.GetRawCertData());
        if (!ChainsTo(cert, trustedRoot))
        {
            return false;
        }
        return expectedHost is null || MatchesHost(cert, expectedHost);
    }

    private static bool ChainsTo(X509Certificate2 cert, X509Certificate2 trustedRoot)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        return chain.Build(cert);
    }

    /// <summary>
    /// Hostname verification in the spirit of RFC 6125: SAN dNSName entries (with single-label
    /// left-most wildcards) and SAN iPAddress entries. Falls back to the subject CN only when
    /// the certificate carries no SAN extension at all.
    /// </summary>
    internal static bool MatchesHost(X509Certificate2 cert, string host)
    {
        // Uri.Host renders IPv6 literals in brackets; strip them for comparison.
        host = host.Trim('[', ']');

        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return MatchesDnsName(cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false), host);
        }

        if (IPAddress.TryParse(host, out var hostIp))
        {
            foreach (var ip in san.EnumerateIPAddresses())
            {
                if (ip.Equals(hostIp))
                {
                    return true;
                }
            }
            return false;
        }

        foreach (var dns in san.EnumerateDnsNames())
        {
            if (MatchesDnsName(dns, host))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesDnsName(string? pattern, string host)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            // "*.example.org" matches exactly one extra label: "a.example.org",
            // not "example.org" and not "a.b.example.org".
            var suffix = pattern[1..];
            if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var prefix = host[..^suffix.Length];
            return prefix.Length > 0 && !prefix.Contains('.');
        }
        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Loads the first certificate from a PEM file.</summary>
    public static X509Certificate2 LoadPemCertificate(string path)
    {
        var pemText = File.ReadAllText(path);
        var fields = PemEncoding.Find(pemText);
        var derBytes = Convert.FromBase64String(pemText[fields.Base64Data]);
        return X509CertificateLoader.LoadCertificate(derBytes);
    }
}

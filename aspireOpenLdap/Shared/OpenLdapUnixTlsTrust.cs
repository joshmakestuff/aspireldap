using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// Compiled into both assemblies under distinct namespaces so the two internal copies never
// collide (CS0433) in a project that can see both assemblies' internals (e.g. the test project).
#if ASPIRE_HOSTING_OPENLDAP
namespace Aspire.Hosting.OpenLdap;
#else
namespace Aspire.OpenLdap;
#endif

/// <summary>
/// Stages a CA certificate into an OpenSSL hash-named directory so libldap on Linux can trust
/// it via <c>LDAP_OPT_X_TLS_CACERTDIR</c> (<c>LdapSessionOptions.TrustedCertificatesDirectory</c>).
/// OpenSSL looks certificates up by <c>{subject_hash:x8}.{n}</c> file name — the same naming
/// <c>openssl rehash</c> / <c>c_rehash</c> produce.
/// </summary>
internal static class OpenLdapUnixTlsTrust
{
    /// <summary>
    /// Ensures a directory containing <paramref name="caPemPath"/> under its OpenSSL
    /// subject-hash name and returns that directory's path. The directory is content-addressed
    /// under the system temp path, so repeated calls for the same CA file are idempotent and
    /// different CAs never collide.
    /// </summary>
    public static string EnsureTrustDirectory(string caPemPath)
    {
        var pemBytes = File.ReadAllBytes(caPemPath);

        using var ca = OpenLdapCertificateValidation.LoadPemCertificate(caPemPath);
        var subjectHash = ComputeOpenSslSubjectHash(ca);

        var contentKey = Convert.ToHexStringLower(SHA256.HashData(pemBytes))[..16];
        var trustDir = Path.Combine(Path.GetTempPath(), "aspire-openldap-truststore", contentKey);
        var hashedPath = Path.Combine(trustDir, $"{subjectHash:x8}.0");

        if (!File.Exists(hashedPath))
        {
            Directory.CreateDirectory(trustDir);
            // Write-then-move so a concurrent process never observes a partial certificate.
            var tempPath = Path.Combine(trustDir, $".{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempPath, pemBytes);
            File.Move(tempPath, hashedPath, overwrite: true);
        }

        return trustDir;
    }

    /// <summary>
    /// Returns the host to dial for a TLS connection through libldap. When
    /// <paramref name="host"/> is a DNS name, libldap validates the certificate against the
    /// REVERSE-DNS canonical name of the connected peer address rather than the dialed name —
    /// on typical Linux NSS stacks (including CI runners), 127.0.0.1 reverse-resolves to the
    /// machine's own hostname, so a dial of <c>localhost</c> can never match the certificate.
    /// Dialing the IP literal sidesteps that: libldap then checks the address against the
    /// certificate's IP SANs directly (the generated certificates carry 127.0.0.1 and ::1).
    /// IPv4 is preferred; an IPv6-only result falls back to the original name because
    /// <c>LdapDirectoryIdentifier</c> does not bracket IPv6 literals in the connection URI.
    /// </summary>
    public static string ResolveDialHost(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            return host;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? host;
        }
        catch (SocketException)
        {
            // Unresolvable now — keep the name; the connection will surface the real error.
            return host;
        }
    }

    /// <summary>
    /// Computes OpenSSL's <c>X509_NAME_hash</c> for the certificate's subject: SHA-1 over the
    /// canonical name encoding, first four bytes read little-endian. This is the value
    /// <c>openssl x509 -subject_hash</c> prints and the hash-dir lookup requires.
    /// </summary>
    internal static uint ComputeOpenSslSubjectHash(X509Certificate2 certificate)
    {
        var canonical = CanonicalizeName(certificate.SubjectName.RawData);
        // SHA-1 is OpenSSL's fixed file-naming convention here, not a security decision.
        var digest = SHA1.HashData(canonical);
        return (uint)(digest[0] | digest[1] << 8 | digest[2] << 16 | digest[3] << 24);
    }

    /// <summary>
    /// Re-encodes an X.500 Name the way OpenSSL's <c>x509_name_canon</c> does: every directory
    /// string becomes a UTF8String whose bytes are ASCII-lowercased with ASCII whitespace
    /// trimmed and inner runs collapsed to a single space, and the RDN SETs are concatenated
    /// without the outer RDNSequence SEQUENCE header.
    /// </summary>
    private static byte[] CanonicalizeName(byte[] nameDer)
    {
        var reader = new AsnReader(nameDer, AsnEncodingRules.DER);
        var rdnSequence = reader.ReadSequence();
        reader.ThrowIfNotEmpty();

        using var canonical = new MemoryStream();
        while (rdnSequence.HasData)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSetOf())
            {
                var rdn = rdnSequence.ReadSetOf();
                while (rdn.HasData)
                {
                    var attribute = rdn.ReadSequence();
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(attribute.ReadObjectIdentifier());
                        WriteCanonicalValue(writer, attribute);
                    }
                    attribute.ThrowIfNotEmpty();
                }
            }
            canonical.Write(writer.Encode());
        }

        return canonical.ToArray();
    }

    private static void WriteCanonicalValue(AsnWriter writer, AsnReader attributeValue)
    {
        // Types outside OpenSSL's ASN1_MASK_CANON (and UniversalString, which System.Formats.Asn1
        // cannot decode) are copied through unchanged, matching asn1_string_canon's fallback.
        var tag = attributeValue.PeekTag();
        if (tag.TagClass != TagClass.Universal ||
            (UniversalTagNumber)tag.TagValue is not (
                UniversalTagNumber.UTF8String or
                UniversalTagNumber.PrintableString or
                UniversalTagNumber.IA5String or
                UniversalTagNumber.VisibleString or
                UniversalTagNumber.T61String or
                UniversalTagNumber.BMPString))
        {
            writer.WriteEncodedValue(attributeValue.ReadEncodedValue().Span);
            return;
        }

        var text = attributeValue.ReadCharacterString((UniversalTagNumber)tag.TagValue);
        var utf8 = Encoding.UTF8.GetBytes(text);
        writer.WriteCharacterString(
            UniversalTagNumber.UTF8String,
            Encoding.UTF8.GetString(CanonicalizeString(utf8)));
    }

    /// <summary>
    /// OpenSSL's in-place string canonicalization: strip leading/trailing ASCII whitespace,
    /// collapse internal ASCII whitespace runs to one space, lowercase ASCII letters. Operates
    /// on UTF-8 bytes; multi-byte sequences pass through untouched.
    /// </summary>
    private static byte[] CanonicalizeString(byte[] utf8)
    {
        static bool IsAsciiSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

        int start = 0, end = utf8.Length;
        while (start < end && IsAsciiSpace(utf8[start]))
        {
            start++;
        }
        while (end > start && IsAsciiSpace(utf8[end - 1]))
        {
            end--;
        }

        var result = new List<byte>(end - start);
        for (var i = start; i < end; i++)
        {
            if (IsAsciiSpace(utf8[i]))
            {
                result.Add((byte)' ');
                while (i + 1 < end && IsAsciiSpace(utf8[i + 1]))
                {
                    i++;
                }
            }
            else
            {
                var b = utf8[i];
                result.Add(b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b);
            }
        }

        return [.. result];
    }
}

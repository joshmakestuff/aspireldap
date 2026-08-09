using LdifDotNet;
using LdifDotNet.Schema;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// Which attribute types the server's schema says carry octets, and which it knows about at
/// all. Built once from a <see cref="LdapSchema"/> and then queried per attribute description,
/// so an entry's representation depends on the schema — never on the bytes a particular entry
/// happens to hold.
/// </summary>
internal sealed class LdapBinaryAttributes
{
    /// <summary>
    /// The empty set, used when the subschema subentry could not be read. Every attribute then
    /// falls back to UTF-8 validity, which is what the server can still prove on its own.
    /// </summary>
    public static readonly LdapBinaryAttributes Unknown = new([], []);

    /// <summary>
    /// Syntaxes whose values are arbitrary octets rather than a UTF-8 string. Every other
    /// syntax RFC 4517 §3.3 defines encodes as a string, so an OID absent from here is text
    /// unless the server declares otherwise.
    /// <para>
    /// Provenance differs per entry and is not uniform: Fax, JPEG and Octet String are
    /// RFC 4517 §3.3; the certificate syntaxes are RFC 4523 §2, which requires DER transfer;
    /// Audio and Binary are RFC 2252, dropped by RFC 4517 but still published by OpenLDAP,
    /// so they are honoured for the servers that emit them; and the PKCS#8 OID is OpenLDAP's
    /// own, listed because it names a private key.
    /// </para>
    /// <para>
    /// This floor and the server's own <c>X-NOT-HUMAN-READABLE</c> / <c>X-BINARY-TRANSFER-REQUIRED</c>
    /// declarations are unioned, because neither suffices alone: slapd flags syntaxes no RFC
    /// names (X.509 AttributeCertificate), while it flags neither Octet String nor Fax even
    /// though both carry arbitrary octets — <c>userPassword</c> is an Octet String.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> BinarySyntaxOids = new(StringComparer.Ordinal)
    {
        "1.3.6.1.4.1.1466.115.121.1.4",  // Audio                (RFC 2252; not in RFC 4517)
        "1.3.6.1.4.1.1466.115.121.1.5",  // Binary               (RFC 2252; not in RFC 4517)
        "1.3.6.1.4.1.1466.115.121.1.8",  // Certificate          (RFC 4523 §2.1)
        "1.3.6.1.4.1.1466.115.121.1.9",  // Certificate List     (RFC 4523 §2.2)
        "1.3.6.1.4.1.1466.115.121.1.10", // Certificate Pair     (RFC 4523 §2.3)
        "1.3.6.1.4.1.1466.115.121.1.23", // Fax, a G3 fax image  (RFC 4517 §3.3.12)
        "1.3.6.1.4.1.1466.115.121.1.28", // JPEG                 (RFC 4517 §3.3.17)
        "1.3.6.1.4.1.1466.115.121.1.40", // Octet String         (RFC 4517 §3.3.25)
        "1.3.6.1.4.1.1466.115.121.1.49", // Supported Algorithm  (RFC 4523 §2.4)
        "1.2.840.113549.1.8.1.1",        // PKCS#8 private key   (OpenLDAP's pKCS8PrivateKey)
    };

    private readonly HashSet<string> _binary;
    private readonly HashSet<string> _known;

    private LdapBinaryAttributes(HashSet<string> binary, HashSet<string> known)
    {
        _binary = binary;
        _known = known;
    }

    /// <summary>
    /// Indexes <paramref name="schema"/> by every name and OID an attribute type answers to,
    /// so a server that returns <c>jpegPhoto</c> and one that returns <c>0.9.2342…</c> both
    /// resolve. Syntax resolution walks the SUP chain via
    /// <see cref="LdapSchema.ResolveSyntaxOid"/> — OpenLDAP publishes <c>cn</c> as
    /// <c>SUP name</c> with no SYNTAX of its own, so reading the definition's own SYNTAX
    /// leaves most of the schema unclassified.
    /// </summary>
    public static LdapBinaryAttributes FromSchema(LdapSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        HashSet<string> declaredBinary = new(StringComparer.Ordinal);
        foreach (var syntax in schema.Syntaxes)
        {
            if (syntax.NotHumanReadable || syntax.BinaryTransferRequired)
            {
                declaredBinary.Add(syntax.Oid);
            }
        }

        HashSet<string> binary = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in schema.AttributeTypes)
        {
            known.Add(definition.Oid);
            foreach (var name in definition.Names)
            {
                known.Add(name);
            }

            var syntaxOid = schema.ResolveSyntaxOid(definition);
            if (syntaxOid is null
                || (!BinarySyntaxOids.Contains(syntaxOid) && !declaredBinary.Contains(syntaxOid)))
            {
                continue;
            }

            binary.Add(definition.Oid);
            foreach (var name in definition.Names)
            {
                binary.Add(name);
            }
        }

        return new(binary, known);
    }

    /// <summary>
    /// True when the schema says this attribute description carries octets. Options are
    /// stripped first: the schema defines <c>cn</c>, never <c>cn;lang-en</c>.
    /// </summary>
    public bool IsBinary(string attributeDescription) =>
        _binary.Contains(AttributeDescription.TypeOf(attributeDescription));

    /// <summary>
    /// True when the schema defines this attribute description's type at all. Distinguishes
    /// "the schema says text" from "the schema has never heard of this attribute", which is
    /// what separates a <see cref="LdapValueClassification.Schema"/> answer from a
    /// <see cref="LdapValueClassification.ByteInspection"/> one.
    /// </summary>
    public bool Knows(string attributeDescription) =>
        _known.Contains(AttributeDescription.TypeOf(attributeDescription));
}

using LdifDotNet;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Model-construction-time validation for the two caller-supplied DN inputs: the base DN and
/// the admin username. Invalid values fail in the AppHost — before Docker starts — with an
/// actionable error, instead of surfacing as a container bootstrap failure or a silently
/// mis-structured DN.
/// </summary>
internal static class OpenLdapDnValidation
{
    /// <summary>
    /// Root naming attributes the container bootstrap and the seed generator can build a root
    /// entry for: <c>dc</c> (dcObject/organization), <c>o</c> (organization), <c>c</c> (country).
    /// </summary>
    private static readonly string[] SupportedRootTypes = ["dc", "o", "c"];

    /// <summary>
    /// Validates a base DN: well-formed per RFC 4514, no control characters (they would let a
    /// value smuggle extra lines into shell-generated cn=config LDIF), no empty values, and a
    /// single-valued leading RDN whose type the root-entry bootstrap supports.
    /// </summary>
    public static void ValidateBaseDn(string baseDn, string paramName)
    {
        RejectControlCharacters(baseDn, paramName, "Base DN");
        RejectUnescapedSemicolons(baseDn, paramName);

        IReadOnlyList<RelativeDistinguishedName> rdns;
        try
        {
            rdns = Dn.Parse(baseDn);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Base DN '{baseDn}' is not a valid RFC 4514 DN: {ex.Message}", paramName, ex);
        }

        if (rdns.Count == 0)
        {
            throw new ArgumentException("Base DN must not be empty.", paramName);
        }

        foreach (var rdn in rdns)
        {
            foreach (var attribute in rdn.Attributes)
            {
                if (attribute.Value.Length == 0)
                {
                    throw new ArgumentException(
                        $"Base DN '{baseDn}' has an empty value for '{attribute.Type}='.", paramName);
                }
            }
        }

        var root = rdns[0];
        if (root.IsMultiValued)
        {
            throw new ArgumentException(
                $"Base DN '{baseDn}' has a multi-valued leading RDN, which is not supported: the root entry needs a single naming attribute.",
                paramName);
        }

        if (!SupportedRootTypes.Contains(root.Type, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Base DN '{baseDn}' starts with '{root.Type}=', which is not a supported root naming attribute. " +
                "The container bootstrap and the seed generator can create root entries for 'dc=', 'o=', and 'c=' bases.",
                paramName);
        }

        // The country attribute uses OpenLDAP's two-character Country String syntax; anything
        // else passes DN parsing here but fails container bootstrap with an opaque
        // "olcSuffix: value #0 invalid per syntax".
        if (string.Equals(root.Type, "c", StringComparison.OrdinalIgnoreCase))
        {
            var country = root.Value;
            if (country.Length != 2 || !char.IsAsciiLetter(country[0]) || !char.IsAsciiLetter(country[1]))
            {
                throw new ArgumentException(
                    $"Base DN '{baseDn}' uses 'c={country}' as its root, but the country naming attribute " +
                    "requires a two-letter ISO 3166 code (e.g. 'c=US').",
                    paramName);
            }
        }
    }

    /// <summary>
    /// Validates the admin username. It is one CN value, but both this integration's container
    /// init and standalone use compose <c>cn={username},{baseDn}</c> verbatim, so a value that
    /// needs RFC 4514 escaping could never bind consistently — reject it outright rather than
    /// let host and container disagree on the DN. Control characters are rejected because the
    /// value is interpolated into shell-generated cn=config LDIF.
    /// </summary>
    public static void ValidateAdminUsername(string username, string paramName)
    {
        RejectControlCharacters(username, paramName, "Admin username");

        if (!string.Equals(Dn.EscapeValue(username), username, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Admin username '{username}' contains characters that require DN escaping " +
                "(one of , + \" \\ < > ; a leading '#' or space, or a trailing space). " +
                "The container init composes the admin bind DN from this value verbatim, so such " +
                "usernames cannot bind consistently — use a username without DN special characters.",
                paramName);
        }
    }

    /// <summary>
    /// Temporary strictness patch: LdifDotNet's <c>Dn.Parse</c> currently accepts an unescaped
    /// <c>;</c> inside values (ldifdotnet#43), but RFC 4514 requires it escaped and slapd
    /// rejects such a DN as an <c>olcSuffix</c> value — an opaque mid-bootstrap death. The
    /// check must run at the string level: after parsing, <c>a\;b</c> and <c>a;b</c> are
    /// indistinguishable. Remove once LdifDotNet enforces this in the parser.
    /// </summary>
    private static void RejectUnescapedSemicolons(string baseDn, string paramName)
    {
        var escaped = false;
        foreach (var c in baseDn)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == ';')
            {
                throw new ArgumentException(
                    $"Base DN '{baseDn}' contains an unescaped ';'. RFC 4514 requires ';' to be " +
                    "escaped inside DN values (write it as '\\;'), and OpenLDAP rejects the " +
                    "unescaped form as a database suffix.", paramName);
            }
        }
    }

    private static void RejectControlCharacters(string value, string paramName, string what)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    $"{what} must not contain control characters (found U+{(int)c:X4}).", paramName);
            }
        }
    }
}

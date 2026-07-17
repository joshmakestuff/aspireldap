using System.Text;

namespace Aspire.Hosting.ApplicationModel.Seeding;

/// <summary>
/// Encoding helpers for generated LDIF (RFC 2849) and DN attribute values (RFC 4514).
/// </summary>
internal static class LdifEncoder
{
    /// <summary>
    /// Appends an <c>attr: value</c> line, switching to base64 (<c>attr:: b64</c>) whenever the
    /// value is not an RFC 2849 SAFE-STRING (leading space/colon/'&lt;', trailing space, NUL/CR/LF,
    /// or any non-ASCII character). Works for the <c>dn</c> pseudo-attribute too.
    /// </summary>
    public static void AppendAttribute(StringBuilder sb, string attribute, string value, string newline)
    {
        if (NeedsBase64(value))
        {
            sb.Append(attribute).Append(":: ")
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
        }
        else
        {
            sb.Append(attribute).Append(": ").Append(value);
        }
        sb.Append(newline);
    }

    private static bool NeedsBase64(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }
        if (value[0] is ' ' or ':' or '<' || value[^1] == ' ')
        {
            return true;
        }
        foreach (var c in value)
        {
            if (c is '\0' or '\r' or '\n' || c > 127)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Escapes a single attribute value for use inside a DN per RFC 4514: backslash-escapes
    /// <c>" + , ; &lt; &gt; \</c> anywhere, <c>#</c> and space when leading, space when trailing,
    /// and hex-escapes NUL.
    /// </summary>
    public static string EscapeDnValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0')
            {
                sb.Append("\\00");
                continue;
            }
            var escape = c is '"' or '+' or ',' or ';' or '<' or '>' or '\\'
                || (i == 0 && c is '#' or ' ')
                || (i == value.Length - 1 && c == ' ');
            if (escape)
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

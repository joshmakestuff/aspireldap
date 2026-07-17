// Compiled into both assemblies under distinct namespaces so the two internal copies never
// collide (CS0433) in a project that can see both assemblies' internals (e.g. the test project).
#if ASPIRE_HOSTING_OPENLDAP
namespace Aspire.Hosting.OpenLdap;
#else
namespace Aspire.OpenLdap;
#endif

/// <summary>
/// Quoting rules for OpenLDAP connection-string values, ADO.NET style: a value containing a
/// semicolon or double quote, or with leading/trailing whitespace, or empty, is wrapped in
/// double quotes with embedded quotes doubled. The emitter (hosting) and parser (client) both
/// compile this file so the rules cannot drift apart.
/// </summary>
internal static class ConnectionStringQuoting
{
    public static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }
        var needsQuoting = value.Contains(';')
            || value.Contains('"')
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]);
        return needsQuoting
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

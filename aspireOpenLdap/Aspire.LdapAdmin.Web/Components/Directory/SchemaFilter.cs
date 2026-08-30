namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// The Schema panel's client-side filter over one definition. One implementation backs both
/// the text filter and the rows the tables render, so the filter path and the display path
/// cannot disagree about whether a definition is a match.
/// </summary>
/// <remarks>
/// aspireldap#116 reported "filtering the schema by OID returns no results" while the
/// predicate source looked right. Rather than chase a one-off condition, the comparison is
/// single-sourced here and pinned by tests: partial and whole OID queries are part of the
/// contract, not an accident of one render path.
/// </remarks>
public static class SchemaFilter
{
    /// <summary>
    /// Whether the definition matches the query: a case-insensitive substring of any name,
    /// the OID, or the description. An empty or whitespace query is not a filter at all —
    /// everything matches.
    /// </summary>
    public static bool Matches(string query, IReadOnlyList<string> names, string oid, string? description)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return names.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
            || oid.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
namespace Aspire.LdapAdmin.Web;

/// <summary>
/// Display helpers for DNs in list views: the shared suffix every result repeats is shown
/// once above the list instead of on every row, and each row shows only its distinguishing
/// prefix. The full DN always stays available on demand (tooltip, copy, navigation) — a
/// relative DN with no stated base is ambiguous, which is the one failure mode stripping
/// must not introduce.
/// </summary>
internal static class DnDisplay
{
    /// <summary>
    /// The DN components every DN in <paramref name="dns"/> shares, from the right. Empty
    /// when there are fewer than two DNs (nothing repeats) or no shared tail. Components
    /// split on unescaped commas only: RFC 4514 allows <c>\,</c> inside a value, and a naive
    /// split would strip mid-component.
    /// </summary>
    public static string CommonSuffix(IReadOnlyList<string> dns)
    {
        if (dns.Count < 2)
        {
            return string.Empty;
        }

        var split = new string[dns.Count][];
        for (var i = 0; i < dns.Count; i++)
        {
            split[i] = SplitDn(dns[i]);
        }

        var shortest = split.Min(static c => c.Length);
        var shared = 0;
        while (shared < shortest)
        {
            var candidate = split[0][^(shared + 1)];
            if (!split.All(c => string.Equals(c[^(shared + 1)], candidate, StringComparison.Ordinal)))
            {
                break;
            }
            shared++;
        }

        // A shared tail that is the whole DN of some entry (the search base itself in the
        // results) stays visible on that row: Relative only strips a strict suffix.
        return shared == 0 ? string.Empty : string.Join(',', split[0][^shared..]);
    }

    /// <summary>Relative form for display; a DN equal to the suffix keeps its full form.</summary>
    public static string Relative(string dn, string commonSuffix)
        => commonSuffix.Length > 0 && dn.EndsWith("," + commonSuffix, StringComparison.Ordinal)
            ? dn[..^(commonSuffix.Length + 1)]
            : dn;

    /// <summary>Splits a DN on unescaped commas only.</summary>
    private static string[] SplitDn(string dn)
    {
        List<string> parts = [];
        var start = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == ',' && (i == 0 || dn[i - 1] != '\\'))
            {
                parts.Add(dn[start..i]);
                start = i + 1;
            }
        }
        parts.Add(dn[start..]);
        return [.. parts];
    }
}

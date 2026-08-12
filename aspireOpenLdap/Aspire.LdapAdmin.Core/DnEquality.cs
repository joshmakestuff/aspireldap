using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

// TODO(ldifdotnet#73): delete when LdifDotNet grows a Dn equivalence API.
/// <summary>
/// Decides whether two DN strings name the same entry. Comparison is on parsed RDN
/// components, so escaping spellings, whitespace around separators, attribute-type case and
/// multi-valued RDN component order do not matter. Values compare case-insensitively — the
/// naming attributes OpenLDAP uses are caseIgnoreMatch types, and this layer has no schema
/// to tell the exceptions apart.
/// </summary>
public static class DnEquality
{
    /// <summary>
    /// True when both strings parse as DNs that name the same entry. A side that is null,
    /// empty or unparsable is equivalent to nothing.
    /// </summary>
    public static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        IReadOnlyList<RelativeDistinguishedName> leftRdns;
        IReadOnlyList<RelativeDistinguishedName> rightRdns;
        try
        {
            leftRdns = Dn.Parse(left);
            rightRdns = Dn.Parse(right);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (leftRdns.Count == 0 || leftRdns.Count != rightRdns.Count)
        {
            return false;
        }

        for (var i = 0; i < leftRdns.Count; i++)
        {
            if (!RdnsEquivalent(leftRdns[i], rightRdns[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="descendant"/> names an entry strictly beneath
    /// <paramref name="ancestor"/> — its RDN sequence ends with the ancestor's, with more in
    /// front. Equality is not "under"; unparsable or empty input is under nothing.
    /// </summary>
    public static bool IsUnder(string? descendant, string? ancestor)
    {
        if (string.IsNullOrWhiteSpace(descendant) || string.IsNullOrWhiteSpace(ancestor))
        {
            return false;
        }

        IReadOnlyList<RelativeDistinguishedName> descendantRdns;
        IReadOnlyList<RelativeDistinguishedName> ancestorRdns;
        try
        {
            descendantRdns = Dn.Parse(descendant);
            ancestorRdns = Dn.Parse(ancestor);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ancestorRdns.Count == 0 || descendantRdns.Count <= ancestorRdns.Count)
        {
            return false;
        }

        var offset = descendantRdns.Count - ancestorRdns.Count;
        for (var i = 0; i < ancestorRdns.Count; i++)
        {
            if (!RdnsEquivalent(descendantRdns[offset + i], ancestorRdns[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RdnsEquivalent(RelativeDistinguishedName left, RelativeDistinguishedName right)
    {
        if (left.Attributes.Count != right.Attributes.Count)
        {
            return false;
        }

        // Multi-valued RDNs (a=1+b=2) are unordered sets; match each component to one
        // unconsumed counterpart. The sets are 1-3 components, so quadratic is fine.
        var consumed = new bool[right.Attributes.Count];
        foreach (var attribute in left.Attributes)
        {
            var matched = false;
            for (var i = 0; i < right.Attributes.Count; i++)
            {
                if (!consumed[i] && ComponentsEquivalent(attribute, right.Attributes[i]))
                {
                    consumed[i] = true;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ComponentsEquivalent(AttributeTypeAndValue left, AttributeTypeAndValue right) =>
        string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
}

using Aspire.LdapAdmin.Core;
using LdifDotNet.Schema;

namespace Aspire.LdapAdmin.Web;

public enum GroupMemberValueKind
{
    DistinguishedName,
    Uid,
}

/// <summary>A membership attribute whose role is confirmed by the loaded schema.</summary>
public sealed record GroupMembershipAttribute(string Name, GroupMemberValueKind ValueKind, bool Required);

/// <summary>
/// Finds supported membership forms from the entry's actual object classes and the loaded
/// schema. Object-class names are deliberately not classified here: a class qualifies only
/// when its effective MUST/MAY set contains a supported attribute with the expected syntax.
/// </summary>
public static class GroupMembership
{
    private const string DistinguishedNameSyntax = "1.3.6.1.4.1.1466.115.121.1.12";
    private const string NameAndOptionalUidSyntax = "1.3.6.1.4.1.1466.115.121.1.34";
    private const string Ia5StringSyntax = "1.3.6.1.4.1.1466.115.121.1.26";

    private static readonly (string Name, GroupMemberValueKind Kind, string Syntax)[] Supported =
    [
        ("member", GroupMemberValueKind.DistinguishedName, DistinguishedNameSyntax),
        ("uniqueMember", GroupMemberValueKind.DistinguishedName, NameAndOptionalUidSyntax),
        ("memberUid", GroupMemberValueKind.Uid, Ia5StringSyntax),
    ];

    public static IReadOnlyList<GroupMembershipAttribute> Detect(LdapSchema? schema, LdapEntry entry)
    {
        if (schema is null)
        {
            return [];
        }

        var objectClasses = entry.Attributes.FirstOrDefault(static attribute =>
            attribute.Name.Equals("objectClass", StringComparison.OrdinalIgnoreCase))?.Values ?? [];
        var (must, may) = SchemaGuide.EffectiveSets(schema, objectClasses);
        var required = new HashSet<string>(must, StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(must.Concat(may), StringComparer.OrdinalIgnoreCase);

        List<GroupMembershipAttribute> result = [];
        foreach (var supported in Supported)
        {
            if (!allowed.Contains(supported.Name)
                || schema.FindAttributeType(supported.Name) is not { } type
                || !string.Equals(schema.ResolveSyntaxOid(type), supported.Syntax, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new GroupMembershipAttribute(supported.Name, supported.Kind, required.Contains(supported.Name)));
        }
        return result;
    }

    public static bool ContainsEquivalentValue(
        GroupMembershipAttribute membership, IEnumerable<string> existing, string candidate) =>
        membership.ValueKind == GroupMemberValueKind.DistinguishedName
            ? existing.Any(value => DnEquality.AreEquivalent(value, candidate))
            : existing.Contains(candidate, StringComparer.Ordinal);
}

// TODO(ldifdotnet#78): replace with LdifDotNet's RFC 4515 assertion-value encoder.
internal static class GroupMemberSearch
{
    internal const int ResultLimit = 50;

    internal static string EscapeAssertionValue(string value) => value
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);

    internal static string Filter(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return LdapFilterConditions.MatchEverything;
        }

        var value = EscapeAssertionValue(trimmed);
        return $"(|(uid=*{value}*)(cn=*{value}*)(mail=*{value}*))";
    }

    internal static string? ValueFor(GroupMembershipAttribute membership, LdapEntry entry) =>
        membership.ValueKind == GroupMemberValueKind.DistinguishedName
            ? entry.Dn
            : entry.Attributes.FirstOrDefault(static attribute =>
                attribute.Name.Equals("uid", StringComparison.OrdinalIgnoreCase))?.Values.FirstOrDefault();
}

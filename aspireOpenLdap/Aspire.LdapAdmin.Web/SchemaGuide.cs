using LdifDotNet.Schema;

namespace Aspire.LdapAdmin.Web;

/// <summary>
/// One attribute type presented as guidance in a picker or form: resolved
/// schema facts only — the server stays authoritative, this is never enforcement.
/// </summary>
public sealed record AttributeGuidance(
    LdapAttributeType Type,
    string Name,
    bool Required,
    bool SingleValued,
    bool NoUserModification,
    string SyntaxLabel);

/// <summary>
/// UI-side composition over <see cref="LdapSchema"/> for the schema-aware dialogs. The
/// schema walking itself (SUP chains for MUST/MAY and syntax) is the library's
/// (<c>RequiredAttributeNames</c>/<c>OptionalAttributeNames</c>/<c>ResolveSyntaxOid</c>);
/// this only unions across an entry's several object classes and shapes the result for
/// pickers. Unknown classes and attributes are skipped, never errors — a live server's
/// schema is what it is.
/// </summary>
public static class SchemaGuide
{
    /// <summary>
    /// True only when a loaded schema says at least one of the entry's effective object
    /// classes permits the attribute. Missing schema and unknown classes never become a guess.
    /// </summary>
    public static bool PermitsAttribute(
        LdapSchema? schema, IEnumerable<string> objectClasses, string attributeName)
    {
        if (schema is null || schema.FindAttributeType(attributeName) is null)
        {
            return false;
        }

        var (must, may) = EffectiveSets(schema, objectClasses);
        return must.Contains(attributeName, StringComparer.OrdinalIgnoreCase)
            || may.Contains(attributeName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The effective attribute sets for a set of object classes: MUST as the union of every
    /// class's required names (superiors included), MAY as the union of optional names minus
    /// anything some class requires.
    /// </summary>
    public static (IReadOnlyList<string> Must, IReadOnlyList<string> May) EffectiveSets(
        LdapSchema schema, IEnumerable<string> objectClasses)
    {
        List<string> must = [];
        List<string> may = [];
        HashSet<string> seenMust = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenMay = new(StringComparer.OrdinalIgnoreCase);
        foreach (var className in objectClasses)
        {
            if (schema.FindObjectClass(className) is not { } objectClass)
            {
                continue;
            }
            foreach (var name in schema.RequiredAttributeNames(objectClass))
            {
                if (seenMust.Add(name))
                {
                    must.Add(name);
                }
            }
            foreach (var name in schema.OptionalAttributeNames(objectClass))
            {
                if (seenMay.Add(name))
                {
                    may.Add(name);
                }
            }
        }
        may.RemoveAll(seenMust.Contains);
        return (must, may);
    }

    /// <summary>
    /// The superior closure of the selected classes, superiors first (so <c>top</c> leads):
    /// an LDAP entry carries its whole object-class chain, so creating an
    /// <c>inetOrgPerson</c> means <c>top</c>, <c>person</c>, <c>organizationalPerson</c>
    /// too — the wizard chains them automatically rather than letting an entry be composed
    /// without them. Classes the schema does not know pass through unchanged.
    /// </summary>
    public static IReadOnlyList<string> WithSuperiors(LdapSchema schema, IEnumerable<string> objectClasses)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        void Add(string name)
        {
            if (!seen.Add(name))
            {
                return;
            }
            if (schema.FindObjectClass(name) is { } objectClass)
            {
                foreach (var superior in objectClass.SuperiorNames)
                {
                    Add(superior);
                }
                result.Add(objectClass.Name ?? name);
            }
            else
            {
                result.Add(name);
            }
        }
        foreach (var name in objectClasses)
        {
            Add(name);
        }
        return result;
    }

    /// <summary>Resolves one attribute name to guidance, or null when the schema does not know it.</summary>
    public static AttributeGuidance? Describe(LdapSchema schema, string name, bool required)
    {
        if (schema.FindAttributeType(name) is not { } type)
        {
            return null;
        }
        return new AttributeGuidance(
            type,
            type.Name ?? name,
            required,
            type.SingleValue,
            type.NoUserModification,
            SyntaxLabel(schema, type));
    }

    /// <summary>The syntax as words ("Directory String" beats a dotted OID), following SUP for inheritance.</summary>
    public static string SyntaxLabel(LdapSchema schema, LdapAttributeType type)
    {
        if (schema.ResolveSyntaxOid(type) is not { } oid)
        {
            return string.Empty;
        }
        return schema.FindSyntax(oid)?.Description is { Length: > 0 } description ? description : oid;
    }

    /// <summary>
    /// Picker candidates for adding an attribute to an entry: the MUST/MAY set of its
    /// object classes, minus <c>objectClass</c> itself, minus NO-USER-MODIFICATION types,
    /// minus attributes already present that are single-valued (a second value is a
    /// guaranteed refusal). Required-first, then by name.
    /// </summary>
    public static IReadOnlyList<AttributeGuidance> AddCandidates(
        LdapSchema schema, IEnumerable<string> objectClasses, IEnumerable<string> presentAttributes)
    {
        var present = new HashSet<string>(presentAttributes, StringComparer.OrdinalIgnoreCase);
        var (must, may) = EffectiveSets(schema, objectClasses);
        List<AttributeGuidance> result = [];
        foreach (var (names, required) in new[] { (must, true), (may, false) })
        {
            foreach (var name in names)
            {
                if (name.Equals("objectClass", StringComparison.OrdinalIgnoreCase)
                    || Describe(schema, name, required) is not { } guidance
                    || guidance.NoUserModification
                    || (present.Contains(name) && guidance.SingleValued))
                {
                    continue;
                }
                result.Add(guidance);
            }
        }
        result.Sort(static (a, b) => a.Required != b.Required
            ? (a.Required ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Attributes that every selected entry's effective schema permits a user to modify.</summary>
    public static IReadOnlyList<AttributeGuidance> SharedEditableCandidates(
        LdapSchema schema, IEnumerable<IEnumerable<string>> objectClassesByEntry)
    {
        var effective = objectClassesByEntry.Select(classes => EffectiveSets(schema, classes)).ToList();
        if (effective.Count == 0)
        {
            return [];
        }

        var shared = new HashSet<string>(effective[0].Must.Concat(effective[0].May), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in effective.Skip(1))
        {
            shared.IntersectWith(entry.Must.Concat(entry.May));
        }

        List<AttributeGuidance> result = [];
        foreach (var name in shared)
        {
            var required = effective.Any(entry => entry.Must.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (!name.Equals("objectClass", StringComparison.OrdinalIgnoreCase)
                && !RequiresBinaryTransfer(schema, name)
                && Describe(schema, name, required) is { NoUserModification: false } guidance)
            {
                result.Add(guidance);
            }
        }
        result.Sort(static (a, b) => a.Required != b.Required
            ? (a.Required ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool RequiresBinaryTransfer(LdapSchema schema, string name) =>
        schema.FindAttributeType(name) is { } type
        && schema.ResolveSyntaxOid(type) is { } syntaxOid
        && schema.FindSyntax(syntaxOid) is { } syntax
        && (syntax.NotHumanReadable || syntax.BinaryTransferRequired);
}

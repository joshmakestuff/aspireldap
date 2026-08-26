namespace Aspire.LdapAdmin.Core;

/// <summary>
/// The broad kind of an entry, decided from its objectClass values. Drives which Overview
/// field set and which stat tiles the detail pane renders (design handoff § 1); it never
/// gates what an entry may contain — the schema does that.
/// </summary>
public enum LdapEntryKind
{
    /// <summary>A person/account entry (inetOrgPerson or posixAccount).</summary>
    Person,

    /// <summary>A group entry (groupOfNames, groupOfUniqueNames or posixGroup).</summary>
    Group,

    /// <summary>Anything else — OUs, domains, and entries this layer has no field set for.</summary>
    Container,
}

/// <summary>Classifies entries into <see cref="LdapEntryKind"/>s.</summary>
public static class LdapEntryKinds
{
    private static readonly string[] PersonClasses = ["inetOrgPerson", "posixAccount"];
    private static readonly string[] GroupClasses = ["groupOfNames", "groupOfUniqueNames", "posixGroup"];

    /// <summary>
    /// Classifies by objectClass values, case-insensitively. Person wins over group when an
    /// entry somehow carries both.
    /// </summary>
    public static LdapEntryKind Classify(IEnumerable<string> objectClasses)
    {
        var kind = LdapEntryKind.Container;
        foreach (var objectClass in objectClasses)
        {
            if (PersonClasses.Contains(objectClass, StringComparer.OrdinalIgnoreCase))
            {
                return LdapEntryKind.Person;
            }

            if (GroupClasses.Contains(objectClass, StringComparer.OrdinalIgnoreCase))
            {
                kind = LdapEntryKind.Group;
            }
        }

        return kind;
    }

    /// <summary>Classifies a full entry by its objectClass attribute; absent means container.</summary>
    public static LdapEntryKind Classify(LdapEntry entry)
    {
        var objectClass = entry.Attributes.FirstOrDefault(static a =>
            !a.IsBinary && string.Equals(a.Name, "objectClass", StringComparison.OrdinalIgnoreCase));
        return objectClass is null ? LdapEntryKind.Container : Classify(objectClass.Values);
    }
}

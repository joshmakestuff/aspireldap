using Aspire.LdapAdmin.Core;

namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// The detail pane's stat strip, made entry-semantic where the entry's own data answers.
/// A count the directory cannot answer from this entry — group memberships, operational
/// timestamps — is simply absent, never invented.
/// </summary>
internal static class EntryStats
{
    internal readonly record struct Tile(string Value, string Label);

    internal static IReadOnlyList<Tile> For(LdapEntry entry)
    {
        var tiles = new List<Tile>();
        switch (LdapEntryKinds.Classify(entry))
        {
            case LdapEntryKind.Person:
                if (FirstValue(entry, "loginShell") is { } shell)
                {
                    tiles.Add(new Tile(IsNoLoginShell(shell) ? "no-login" : "active", "status"));
                }
                if (FirstValue(entry, "uidNumber") is { } uid)
                {
                    tiles.Add(new Tile(uid, "uid number"));
                }
                break;
            case LdapEntryKind.Group:
                if (MemberCount(entry) is { } members)
                {
                    tiles.Add(new Tile(members.ToString(System.Globalization.CultureInfo.InvariantCulture), "members"));
                }
                break;
        }

        tiles.Add(new Tile(entry.Attributes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "attributes"));
        tiles.Add(new Tile(entry.Attributes.Sum(static a => a.Values.Count).ToString(System.Globalization.CultureInfo.InvariantCulture), "values"));
        tiles.Add(new Tile(ObjectClassCount(entry).ToString(System.Globalization.CultureInfo.InvariantCulture), "object classes"));
        return tiles;
    }

    /// <summary>The membership attribute's value count — whichever one this group uses.</summary>
    private static int? MemberCount(LdapEntry entry) =>
        Find(entry, "member")?.Values.Count
        ?? Find(entry, "uniqueMember")?.Values.Count
        ?? Find(entry, "memberUid")?.Values.Count;

    private static bool IsNoLoginShell(string shell) =>
        shell.EndsWith("/nologin", StringComparison.Ordinal)
        || shell.EndsWith("/false", StringComparison.Ordinal);

    private static int ObjectClassCount(LdapEntry entry) =>
        Find(entry, "objectClass")?.Values.Count ?? 0;

    private static string? FirstValue(LdapEntry entry, string name) =>
        Find(entry, name) is { IsBinary: false } attribute ? attribute.Values.FirstOrDefault() : null;

    private static LdapAttributeValues? Find(LdapEntry entry, string name) =>
        entry.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

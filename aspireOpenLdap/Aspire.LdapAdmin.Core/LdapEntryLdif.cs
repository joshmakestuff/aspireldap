using System.DirectoryServices.Protocols;
using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// The per-entry LDIF surface: renders an entry as a content record, and turns an edited
/// draft back into the <see cref="LdapAttributeChange"/>s that make the entry match it.
/// Shared by the entry detail's LDIF tab (#113) and the LDIF view (#110).
/// </summary>
public static class LdapEntryLdif
{
    private static readonly LdifWriterOptions WriterOptions = new() { IncludeVersionLine = false };

    /// <summary>
    /// The entry as one LDIF content record — objectClass values first, then the remaining
    /// attributes in server order; binary values as base64.
    /// </summary>
    public static string Write(LdapEntry entry) =>
        LdifWriter.WriteToString([ToRecord(entry)], WriterOptions);

    /// <summary>The entry as a content record; shared with the LDIF view's subtree export.</summary>
    public static LdifContentRecord ToRecord(LdapEntry entry) =>
        new(entry.Dn, entry.Attributes
            .OrderBy(static a => IsObjectClass(a.Name) ? 0 : 1)
            .Select(static a => new LdifAttribute(a.Name, a.Values.Select(value => a.IsBinary
                ? LdifValue.FromBytes(Convert.FromBase64String(value))
                : LdifValue.FromString(value)))));

    /// <summary>
    /// Diffs an edited draft against the entry it was generated from. On success,
    /// <see cref="LdifDraftDiff.Changes"/> holds the replaces and deletes that make the
    /// entry match the draft — possibly none, when the draft is equivalent. Anything the
    /// diff cannot honestly apply (unparsable LDIF, several records, a changetype record, a
    /// different DN) is an <see cref="LdifDraftDiff.Error"/>, never a guess.
    /// </summary>
    public static LdifDraftDiff Diff(LdapEntry entry, string draft)
    {
        IReadOnlyList<LdifRecord> records;
        try
        {
            records = LdifReader.Parse(draft);
        }
        catch (LdifParseException ex)
        {
            return LdifDraftDiff.Fail($"LDIF parse error: {ex.Message}");
        }

        if (records.Count != 1)
        {
            return LdifDraftDiff.Fail(records.Count == 0
                ? "The draft is empty."
                : "The draft must contain exactly one record.");
        }

        if (records[0] is not LdifContentRecord record)
        {
            return LdifDraftDiff.Fail(
                "The draft must stay a content record (no changetype) — it is diffed against the entry.");
        }

        if (!DnEquality.AreEquivalent(record.Dn, entry.Dn))
        {
            return LdifDraftDiff.Fail("The draft's DN must match the entry — use Rename to move it.");
        }

        return LdifDraftDiff.Ok(ComputeChanges(entry, record));
    }

    private static List<LdapAttributeChange> ComputeChanges(LdapEntry entry, LdifContentRecord record)
    {
        // Draft attributes, merged case-insensitively by name (LDIF may repeat a name).
        var draftAttributes = new Dictionary<string, List<LdifValue>>(StringComparer.OrdinalIgnoreCase);
        var draftOrder = new List<string>();
        foreach (var attribute in record.Attributes)
        {
            if (!draftAttributes.TryGetValue(attribute.Name, out var values))
            {
                values = [];
                draftAttributes[attribute.Name] = values;
                draftOrder.Add(attribute.Name);
            }

            values.AddRange(attribute.Values);
        }

        var changes = new List<LdapAttributeChange>();

        // Removed attributes first: present on the entry, absent from the draft.
        foreach (var attribute in entry.Attributes)
        {
            if (!draftAttributes.ContainsKey(attribute.Name))
            {
                changes.Add(new LdapAttributeChange(DirectoryAttributeOperation.Delete, attribute.Name, []));
            }
        }

        // Changed or added attributes, in draft order.
        var entryByName = entry.Attributes.ToDictionary(
            static a => a.Name, static a => a, StringComparer.OrdinalIgnoreCase);
        foreach (var name in draftOrder)
        {
            var draftValues = draftAttributes[name];
            entryByName.TryGetValue(name, out var current);
            if (current is not null && SameValues(current, draftValues))
            {
                continue;
            }

            // A binary attribute — or a draft value with no text form — travels as base64.
            var binary = current?.IsBinary == true || draftValues.Any(static v => v.IsBinary);
            changes.Add(new LdapAttributeChange(
                DirectoryAttributeOperation.Replace,
                name,
                draftValues
                    .Select(v => binary ? Convert.ToBase64String(v.AsBytes()) : v.AsString())
                    .ToList(),
                IsBase64: binary));
        }

        return changes;
    }

    /// <summary>Unordered byte-level comparison — LDAP attribute values are a set.</summary>
    private static bool SameValues(LdapAttributeValues current, List<LdifValue> draft)
    {
        if (current.Values.Count != draft.Count)
        {
            return false;
        }

        static IEnumerable<string> Sorted(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal);
        var currentKeys = Sorted(current.Values.Select(v => current.IsBinary
            ? v
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v))));
        var draftKeys = Sorted(draft.Select(static v => Convert.ToBase64String(v.AsBytes())));
        return currentKeys.SequenceEqual(draftKeys, StringComparer.Ordinal);
    }

    private static bool IsObjectClass(string name) =>
        string.Equals(name, "objectClass", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The outcome of diffing an LDIF draft: exactly one of <see cref="Changes"/> (possibly
/// empty — the draft matches the entry) or <see cref="Error"/> is non-null.
/// </summary>
public sealed record LdifDraftDiff(IReadOnlyList<LdapAttributeChange>? Changes, string? Error)
{
    internal static LdifDraftDiff Ok(IReadOnlyList<LdapAttributeChange> changes) => new(changes, null);

    internal static LdifDraftDiff Fail(string error) => new(null, error);
}

using System.DirectoryServices.Protocols;
using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// The LDIF view's service layer (#110): subtree export as content records, and change-record
/// import applied through the existing write operations — so every guard the app enforces
/// (bind-identity protection, DN validation, outcome modelling) applies to imported records
/// exactly as it does to dialog edits. All LDIF reading/writing is LdifDotNet's; nothing here
/// hand-rolls the format.
/// </summary>
public sealed class LdapLdifService(LdapDirectoryService directory)
{
    private static readonly LdifWriterOptions WriterOptions = new() { IncludeVersionLine = false };

    /// <summary>
    /// The export cap. An export is a file a person reads or re-imports, not a replication
    /// mechanism; past this the result says truncated and the caller must narrow the base.
    /// </summary>
    public const int ExportLimit = 1000;

    /// <summary>
    /// Exports a subtree as LDIF content records, parents before children so the output
    /// re-imports in order. Truncation is carried on the result, never silent.
    /// </summary>
    public async Task<LdifExportResult> ExportSubtreeAsync(string? baseDn = null, CancellationToken cancellationToken = default)
    {
        var result = await directory.SearchAsync(new LdapSearchOptions
        {
            BaseDn = string.IsNullOrWhiteSpace(baseDn) ? null : baseDn,
            Filter = "(objectClass=*)",
            Scope = SearchScope.Subtree,
            Limit = ExportLimit,
        }, cancellationToken).ConfigureAwait(false);

        var records = result.Entries
            .OrderBy(static e => DnDepth(e.Dn))
            .ThenBy(static e => e.Dn, StringComparer.OrdinalIgnoreCase)
            .Select(LdapEntryLdif.ToRecord);
        return new LdifExportResult(
            LdifWriter.WriteToString(records, WriterOptions),
            result.Entries.Count,
            result.Truncated);
    }

    /// <summary>
    /// Parses import text into an apply plan. A parse failure is a plan-level error; a
    /// record the importer cannot apply (an increment modification) is refused here, before
    /// anything runs, not midway through an apply.
    /// </summary>
    public static LdifImportPlan ParsePlan(string ldif)
    {
        IReadOnlyList<LdifRecord> records;
        try
        {
            records = LdifReader.Parse(ldif);
        }
        catch (LdifParseException ex)
        {
            return new LdifImportPlan([], $"LDIF parse error: {ex.Message}");
        }

        if (records.Count == 0)
        {
            return new LdifImportPlan([], "The import text contains no records.");
        }

        var items = new List<LdifImportItem>();
        foreach (var record in records)
        {
            if (record is LdifModifyRecord modify
                && modify.Modifications.Any(static m => m.Type == LdifModificationType.Increment))
            {
                return new LdifImportPlan([], $"modify {record.Dn}: increment modifications are not supported here.");
            }

            items.Add(new LdifImportItem(record, ChangeTypeLabel(record), record.Dn, DetailCount(record)));
        }

        return new LdifImportPlan(items, null);
    }

    /// <summary>
    /// Applies a parsed plan in record order, stopping at the first failure — the records
    /// before it are applied and stay applied, and the result says exactly how far it got
    /// and why it stopped. ldapmodify's own contract, because half-applied silence is worse
    /// than a visible stop.
    /// </summary>
    public async Task<LdifApplyResult> ApplyAsync(IReadOnlyList<LdifImportItem> items, CancellationToken cancellationToken = default)
    {
        var applied = 0;
        foreach (var item in items)
        {
            LdapOperationResult outcome;
            try
            {
                outcome = await ApplyOneAsync(item.Record, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException ex)
            {
                // Transport death mid-apply still reports how far the apply got.
                outcome = new LdapOperationResult(LdapOperationStatus.Failed, null,
                    $"The directory could not be reached ({ex.GetType().Name}): {ex.Message}");
            }

            if (!outcome.Succeeded)
            {
                return new LdifApplyResult(applied, items.Count, item.Dn, outcome);
            }

            applied++;
        }

        return new LdifApplyResult(applied, items.Count, null, LdapOperationResult.Ok());
    }

    private Task<LdapOperationResult> ApplyOneAsync(LdifRecord record, CancellationToken cancellationToken) => record switch
    {
        LdifAddRecord add => directory.AddEntryAsync(
            new LdapNewEntry(add.Dn, [.. add.Attributes.Select(ToNewAttribute)]), cancellationToken),
        // A content record imports as an add — ldapmodify -a semantics.
        LdifContentRecord content => directory.AddEntryAsync(
            new LdapNewEntry(content.Dn, [.. content.Attributes.Select(ToNewAttribute)]), cancellationToken),
        LdifModifyRecord modify => directory.ModifyEntryAsync(
            modify.Dn, [.. modify.Modifications.Select(ToChange)], cancellationToken),
        LdifDeleteRecord delete => directory.DeleteEntryAsync(delete.Dn, cancellationToken),
        LdifModDnRecord modDn => RenameAsync(modDn, cancellationToken),
        _ => Task.FromResult(LdapOperationResult.Invalid($"Unsupported record type {record.GetType().Name}.")),
    };

    private async Task<LdapOperationResult> RenameAsync(LdifModDnRecord record, CancellationToken cancellationToken)
    {
        var rename = await directory.RenameEntryAsync(
            record.Dn, record.NewRdn, record.NewSuperior, record.DeleteOldRdn, cancellationToken).ConfigureAwait(false);
        return rename.Outcome;
    }

    private static LdapNewAttribute ToNewAttribute(LdifAttribute attribute)
    {
        var (values, isBase64) = MapValues(attribute.Values);
        return new LdapNewAttribute(attribute.Name, values, isBase64);
    }

    private static LdapAttributeChange ToChange(LdifModification modification)
    {
        var (values, isBase64) = MapValues(modification.Values);
        return new LdapAttributeChange(modification.Type switch
        {
            LdifModificationType.Add => DirectoryAttributeOperation.Add,
            LdifModificationType.Delete => DirectoryAttributeOperation.Delete,
            _ => DirectoryAttributeOperation.Replace,
        }, modification.AttributeName, values, isBase64);
    }

    /// <summary>A value list with any binary member travels wholly as base64.</summary>
    private static (IReadOnlyList<string> Values, bool IsBase64) MapValues(IReadOnlyList<LdifValue> values)
    {
        var binary = values.Any(static v => v.IsBinary);
        return ([.. values.Select(v => binary ? Convert.ToBase64String(v.AsBytes()) : v.AsString())], binary);
    }

    private static string ChangeTypeLabel(LdifRecord record) => record switch
    {
        LdifAddRecord or LdifContentRecord => "add",
        LdifModifyRecord => "modify",
        LdifDeleteRecord => "delete",
        LdifModDnRecord => "moddn",
        _ => "unknown",
    };

    private static int DetailCount(LdifRecord record) => record switch
    {
        LdifAddRecord add => add.Attributes.Count,
        LdifContentRecord content => content.Attributes.Count,
        LdifModifyRecord modify => modify.Modifications.Count,
        _ => 0,
    };

    private static int DnDepth(string dn)
    {
        try
        {
            return Dn.Parse(dn).Count;
        }
        catch (ArgumentException)
        {
            return int.MaxValue; // Unparsable sorts last; the server will name the problem.
        }
    }
}

/// <summary>A subtree export: the LDIF text, how many entries it holds, and whether the cap cut it short.</summary>
public sealed record LdifExportResult(string Ldif, int EntryCount, bool Truncated);

/// <summary>One plan row: the record itself plus what the plan table shows about it.</summary>
public sealed record LdifImportItem(LdifRecord Record, string ChangeType, string Dn, int DetailCount);

/// <summary>A parsed import: exactly one of a non-empty <see cref="Items"/> or an <see cref="Error"/>.</summary>
public sealed record LdifImportPlan(IReadOnlyList<LdifImportItem> Items, string? Error);

/// <summary>
/// How an apply ended: <see cref="Applied"/> of <see cref="Total"/> records ran; when they
/// differ, <see cref="FailedDn"/> and <see cref="Outcome"/> say where and why it stopped.
/// </summary>
public sealed record LdifApplyResult(int Applied, int Total, string? FailedDn, LdapOperationResult Outcome)
{
    /// <summary>True when every record applied.</summary>
    public bool Succeeded => Applied == Total;
}

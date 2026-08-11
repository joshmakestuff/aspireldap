using Aspire.LdapAdmin.Core;

namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// Content models for the entry-editing dialogs. Each carries its form state plus the save
/// delegate the shell page provides: the dialog calls it while open, shows a returned error
/// inline, and closes only on a null (success) result — so a failed write never silently
/// dismisses the user's input.
/// </summary>
public sealed class AttributeDialogModel
{
    public bool IsNew { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ValuesText { get; set; } = string.Empty;
    public bool IsBinary { get; set; }

    /// <summary>
    /// The entry being edited, snapshotted when the dialog opens: its object classes and
    /// present attributes feed the picker. A snapshot, not the shell's live field — the
    /// save delegate's own reselect nulls that field mid-save, and the dialog's lifetime
    /// must not depend on it (#117).
    /// </summary>
    public required LdapEntry Entry { get; init; }

    public required Func<AttributeDialogModel, Task<string?>> SaveAsync { get; init; }
}

/// <summary>
/// The new-entry wizard's contract (#105): the wizard composes the whole
/// <see cref="Aspire.LdapAdmin.Core.LdapNewEntry"/> (chained object classes, RDN, fields)
/// and hands it to the shell's save delegate.
/// </summary>
public sealed class NewEntryModel
{
    public required string ParentDn { get; init; }
    public required Func<Aspire.LdapAdmin.Core.LdapNewEntry, Task<string?>> SaveAsync { get; init; }
}

/// <summary>
/// The rename/move dialog's contract (#106): guided RDN attribute + value inputs (prefilled
/// with the entry's current RDN by the shell) instead of a raw RDN string — the dialog
/// composes the escaped RDN via <c>Dn.Rdn</c>, so a typed comma is a value, never structure.
/// Multi-valued current RDNs prefill from their first component; composing one is not
/// possible here (accepted trade-off).
/// </summary>
public sealed class RenameDialogModel
{
    public required string Dn { get; init; }

    /// <summary>The DN's tail, for the resulting-DN preview when renaming in place.
    /// Null when the entry is a directory root.</summary>
    public required string? CurrentParentDn { get; init; }

    /// <summary>
    /// The entry being renamed, snapshotted when the dialog opens: its object classes and
    /// value counts feed the delete-old-RDN hazard prediction. A snapshot for the same
    /// reason as <see cref="AttributeDialogModel.Entry"/> (#117).
    /// </summary>
    public required LdapEntry Entry { get; init; }

    public string RdnAttribute { get; set; } = string.Empty;
    public string RdnValue { get; set; } = string.Empty;
    public string NewParentDn { get; set; } = string.Empty;
    public bool DeleteOldRdn { get; set; } = true;
    public required Func<RenameDialogModel, Task<string?>> SaveAsync { get; init; }
}

public sealed class DeleteDialogModel
{
    public required string Dn { get; init; }

    /// <summary>Delete the whole subtree, children first (client-side recursion — the
    /// bundled server has no Tree Delete control). Off by default: a plain delete of a
    /// non-leaf is refused by the server, never silently widened.</summary>
    public bool Subtree { get; set; }

    public required Func<DeleteDialogModel, Task<string?>> SaveAsync { get; init; }
}

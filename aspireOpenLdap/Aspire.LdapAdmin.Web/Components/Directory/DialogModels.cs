namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// Content models for the entry-editing dialogs. Each carries its form state plus the save
/// delegate the shell page provides: the dialog calls it while open, shows a returned error
/// inline, and closes only on a null (success) result — so a failed write never silently
/// dismisses the user's input. Public because they are the TContent of public
/// IDialogContentComponent components.
/// </summary>
public sealed class AttributeDialogModel
{
    public bool IsNew { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ValuesText { get; set; } = string.Empty;
    public bool IsBinary { get; set; }
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

public sealed class RenameDialogModel
{
    public required string Dn { get; init; }
    public string NewRdn { get; set; } = string.Empty;
    public string NewParentDn { get; set; } = string.Empty;
    public bool DeleteOldRdn { get; set; } = true;
    public required Func<RenameDialogModel, Task<string?>> SaveAsync { get; init; }
}

public sealed class DeleteDialogModel
{
    public required string Dn { get; init; }
    public required Func<DeleteDialogModel, Task<string?>> SaveAsync { get; init; }
}

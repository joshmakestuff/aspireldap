using System.DirectoryServices.Protocols;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// How <see cref="LdapAttributeValues.IsBinary"/> was decided for one attribute. A directory
/// browser that reports a value as text when it is not corrupts it, so the basis of the
/// decision travels with the value instead of being an invisible property of the process.
/// </summary>
public enum LdapValueClassification
{
    /// <summary>
    /// The server's schema settled it: the attribute type resolves to an octet-carrying
    /// syntax, or to a string-carrying one. Stable across entries and across value edits.
    /// </summary>
    Schema,

    /// <summary>
    /// The attribute description carries the RFC 4522 <c>;binary</c> transfer option, which is
    /// authoritative on its own — the schema need not have been readable.
    /// </summary>
    TransferOption,

    /// <summary>
    /// Neither of the above applied — the schema was unreadable, or does not define this
    /// attribute type — so the bytes decided. This can only ever report binary where text
    /// would have been reported (a value that is not valid UTF-8 has no string form at all),
    /// so no bytes are lost, but the answer can change when values are added or removed.
    /// </summary>
    ByteInspection,
}

/// <summary>
/// One attribute of an entry. When <see cref="IsBinary"/> is true every entry in
/// <see cref="Values"/> is base64; otherwise every entry is the value's UTF-8 text. The two
/// forms are never mixed within one attribute. <see cref="Classification"/> says what decided
/// it.
/// </summary>
public sealed record LdapAttributeValues(
    string Name,
    bool IsBinary,
    IReadOnlyList<string> Values,
    LdapValueClassification Classification);

/// <summary>A full directory entry.</summary>
public sealed record LdapEntry(string Dn, IReadOnlyList<LdapAttributeValues> Attributes);

/// <summary>A direct child of a tree node, shaped for rendering a browse tree.</summary>
public sealed record LdapChildEntry(string Dn, string Rdn, IReadOnlyList<string> ObjectClasses, bool HasChildren);

/// <summary>
/// Children of a tree node, capped at the request's limit. <see cref="Truncated"/> is true
/// when the node had more children than were returned — never silently dropped, because a
/// short child list is indistinguishable from a complete one to the person reading the tree.
/// </summary>
public sealed record LdapChildrenResult(IReadOnlyList<LdapChildEntry> Children, bool Truncated);

/// <summary>
/// Search results, capped at the request's limit. <see cref="Truncated"/> is true when the
/// server had more matches than were returned, including when the server's own size limit —
/// not the request's — cut the search short.
/// </summary>
public sealed record LdapSearchResult(IReadOnlyList<LdapEntry> Entries, bool Truncated);

/// <summary>What to search for. <see cref="Filter"/> is an RFC 4515 filter the caller owns.</summary>
public sealed record LdapSearchOptions
{
    /// <summary>Search base; defaults to the directory's base DN when null.</summary>
    public string? BaseDn { get; init; }

    /// <summary>An RFC 4515 filter string, passed to the server verbatim.</summary>
    public string Filter { get; init; } = "(objectClass=*)";

    /// <summary>Search scope. Defaults to <see cref="SearchScope.Subtree"/>.</summary>
    public SearchScope Scope { get; init; } = SearchScope.Subtree;

    /// <summary>Maximum entries to return; the result is flagged truncated when more matched.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Attributes to return; empty means all user attributes.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

/// <summary>An attribute for a new entry. Base64-encode binary values and set <see cref="IsBase64"/>.</summary>
public sealed record LdapNewAttribute(string Name, IReadOnlyList<string> Values, bool IsBase64 = false);

/// <summary>A new entry to add to the directory.</summary>
public sealed record LdapNewEntry(string Dn, IReadOnlyList<LdapNewAttribute> Attributes);

/// <summary>
/// One modification to an existing entry. For <see cref="DirectoryAttributeOperation.Delete"/>
/// an empty value list removes the whole attribute. Base64-encode binary values and set
/// <see cref="IsBase64"/>.
/// </summary>
public sealed record LdapAttributeChange(
    DirectoryAttributeOperation Operation,
    string Name,
    IReadOnlyList<string> Values,
    bool IsBase64 = false);

/// <summary>
/// The outcome class of a write. Modelled as data rather than as an exception because every
/// one of these is a normal answer a directory gives to a person editing it — "you may not",
/// "that already exists", "your schema forbids it" — and a UI has to render each differently.
/// </summary>
public enum LdapOperationStatus
{
    /// <summary>The server applied the change.</summary>
    Success,

    /// <summary>The target entry (or a parent it needs) does not exist.</summary>
    NotFound,

    /// <summary>An entry with that DN already exists.</summary>
    AlreadyExists,

    /// <summary>The bound identity is not permitted to do this — the server's ACL said no.</summary>
    AccessDenied,

    /// <summary>The entry has children, so it cannot be deleted or moved as a leaf.</summary>
    NotAllowedOnNonLeaf,

    /// <summary>The change contradicts the server's schema (object class, attribute type, syntax).</summary>
    SchemaViolation,

    /// <summary>The change violates a server constraint (single-value, duplicate value, missing value).</summary>
    ConstraintViolation,

    /// <summary>The request was rejected before it reached the server, or as malformed by it.</summary>
    InvalidRequest,

    /// <summary>The server understood the request and refused it (policy, extension unsupported).</summary>
    Refused,

    /// <summary>The server reported a result code this layer does not model.</summary>
    Failed,
}

/// <summary>The result of a write. <see cref="Message"/> carries the server's own diagnostic when it sent one.</summary>
public sealed record LdapOperationResult(
    LdapOperationStatus Status,
    ResultCode? ResultCode = null,
    string? Message = null)
{
    /// <summary>True when the server applied the change.</summary>
    public bool Succeeded => Status == LdapOperationStatus.Success;

    /// <summary>The result of a write the server applied.</summary>
    public static LdapOperationResult Ok() =>
        new(LdapOperationStatus.Success, System.DirectoryServices.Protocols.ResultCode.Success);

    /// <summary>A request this layer rejected before sending it, with the reason.</summary>
    public static LdapOperationResult Invalid(string message) =>
        new(LdapOperationStatus.InvalidRequest, null, message);
}

/// <summary>
/// The result of a rename/move. <see cref="NewDn"/> is the entry's DN afterwards, and is null
/// exactly when <see cref="Outcome"/> did not succeed.
/// </summary>
public sealed record LdapRenameResult(LdapOperationResult Outcome, string? NewDn);

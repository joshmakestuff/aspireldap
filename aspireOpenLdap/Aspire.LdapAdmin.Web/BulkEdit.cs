using System.Collections.Immutable;
using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;

namespace Aspire.LdapAdmin.Web;

public enum BulkEditOperation
{
    AddToGroup,
    RemoveFromGroup,
    Attribute,
}

public enum BulkAttributeOperation
{
    Add,
    Replace,
    Delete,
}

/// <summary>The immutable form values from which one exact bulk-operation plan is built.</summary>
public sealed record BulkEditDraft(
    BulkEditOperation Operation,
    ImmutableArray<LdapEntry> Entries,
    LdapEntry? Group,
    GroupMembershipAttribute? Membership,
    BulkAttributeOperation AttributeOperation,
    string AttributeName,
    ImmutableArray<string> AttributeValues);

/// <summary>One independently dispatched LDAP modify and the selected DN it represents.</summary>
public sealed record BulkEditPlanItem(
    string SelectedDn,
    string TargetDn,
    LdapAttributeChange Change)
{
    public string Preview => string.Equals(TargetDn, SelectedDn, StringComparison.Ordinal)
        ? $"{SelectedDn}: {OperationText(Change.Operation)} {Change.Name}{ValuesText(Change.Values)}"
        : $"{SelectedDn}: {OperationText(Change.Operation)} {Change.Name}{ValuesText(Change.Values)} on {TargetDn}";

    private static string OperationText(DirectoryAttributeOperation operation) => operation switch
    {
        DirectoryAttributeOperation.Add => "add",
        DirectoryAttributeOperation.Replace => "replace",
        DirectoryAttributeOperation.Delete => "delete",
        _ => operation.ToString().ToLowerInvariant(),
    };

    private static string ValuesText(IReadOnlyList<string> values) => values.Count == 0
        ? " (entire attribute)"
        : $" = {string.Join(", ", values)}";
}

/// <summary>
/// The sole source for preview and execution. Building is pure: it validates a draft and
/// snapshots every target/change without reading LDAP or retaining mutable collections.
/// </summary>
public sealed record BulkEditPlan(BulkEditOperation Operation, ImmutableArray<BulkEditPlanItem> Items)
{
    public static BulkEditPlanBuildResult Build(BulkEditDraft draft)
    {
        if (draft.Entries.IsDefaultOrEmpty)
        {
            return BulkEditPlanBuildResult.Invalid("Select at least one fetched entry.");
        }

        return draft.Operation switch
        {
            BulkEditOperation.AddToGroup => BuildGroup(draft, DirectoryAttributeOperation.Add),
            BulkEditOperation.RemoveFromGroup => BuildGroup(draft, DirectoryAttributeOperation.Delete),
            BulkEditOperation.Attribute => BuildAttribute(draft),
            _ => BulkEditPlanBuildResult.Invalid("Choose a supported operation."),
        };
    }

    private static BulkEditPlanBuildResult BuildGroup(
        BulkEditDraft draft,
        DirectoryAttributeOperation operation)
    {
        if (draft.Group is not { } group || draft.Membership is not { } membership)
        {
            return BulkEditPlanBuildResult.Invalid("Search for and select a schema-recognized group.");
        }

        var missing = draft.Entries
            .Where(entry => GroupMemberSearch.ValueFor(membership, entry) is null)
            .Select(static entry => entry.Dn)
            .ToImmutableArray();
        if (missing.Length > 0)
        {
            return BulkEditPlanBuildResult.Invalid(
                $"{membership.Name} needs a readable uid for: {string.Join(", ", missing)}");
        }

        var items = draft.Entries.Select(entry => new BulkEditPlanItem(
            entry.Dn,
            group.Dn,
            new LdapAttributeChange(operation, membership.Name, [GroupMemberSearch.ValueFor(membership, entry)!])))
            .ToImmutableArray();
        return BulkEditPlanBuildResult.Valid(new BulkEditPlan(draft.Operation, items));
    }

    private static BulkEditPlanBuildResult BuildAttribute(BulkEditDraft draft)
    {
        var name = draft.AttributeName.Trim();
        if (name.Length == 0)
        {
            return BulkEditPlanBuildResult.Invalid("Enter an attribute name.");
        }
        if (draft.AttributeOperation is not BulkAttributeOperation.Delete && draft.AttributeValues.IsDefaultOrEmpty)
        {
            return BulkEditPlanBuildResult.Invalid("Enter at least one value for Add or Replace.");
        }

        var operation = draft.AttributeOperation switch
        {
            BulkAttributeOperation.Add => DirectoryAttributeOperation.Add,
            BulkAttributeOperation.Replace => DirectoryAttributeOperation.Replace,
            BulkAttributeOperation.Delete => DirectoryAttributeOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(draft)),
        };
        var values = draft.AttributeValues.IsDefault ? ImmutableArray<string>.Empty : draft.AttributeValues;
        var items = draft.Entries.Select(entry => new BulkEditPlanItem(
            entry.Dn,
            entry.Dn,
            new LdapAttributeChange(operation, name, values)))
            .ToImmutableArray();
        return BulkEditPlanBuildResult.Valid(new BulkEditPlan(draft.Operation, items));
    }
}

public sealed record BulkEditPlanBuildResult(BulkEditPlan? Plan, string? Error)
{
    public static BulkEditPlanBuildResult Valid(BulkEditPlan plan) => new(plan, null);

    public static BulkEditPlanBuildResult Invalid(string error) => new(null, error);
}

public enum BulkEditItemStatus
{
    Pending,
    Succeeded,
    Failed,
}

public sealed record BulkEditItemOutcome(BulkEditPlanItem Item, BulkEditItemStatus Status, string? Reason = null);

public sealed record BulkEditRunResult(ImmutableArray<BulkEditItemOutcome> Outcomes, bool Cancelled)
{
    public int CompletedCount => Outcomes.Count(static outcome => outcome.Status == BulkEditItemStatus.Succeeded);
    public int FailedCount => Outcomes.Count(static outcome => outcome.Status == BulkEditItemStatus.Failed);
    public int PendingCount => Outcomes.Count(static outcome => outcome.Status == BulkEditItemStatus.Pending);
    public bool AllSucceeded => Outcomes.Length > 0 && CompletedCount == Outcomes.Length;
}

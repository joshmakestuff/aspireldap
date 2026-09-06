using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using LdifDotNet.Schema;

namespace Aspire.LdapAdmin.Web;

internal static class LdapAdminApi
{
    internal const string Prefix = "/api/v1";
    private const int LdapFilterError = 87;

    internal static bool Map(WebApplication app, LdapAdminSettings settings)
    {
        if (!settings.EnableRestApi)
        {
            return false;
        }

        var api = app.MapGroup(Prefix)
            .AddEndpointFilter<ApiExceptionFilter>();

        api.MapGet("/directory", GetDirectory);
        api.MapGet("/directory/children", GetChildren);
        api.MapPost("/directory/search", Search);
        api.MapGet("/entries", GetEntry);
        api.MapPost("/entries", AddEntry);
        api.MapPatch("/entries", ModifyEntry);
        api.MapDelete("/entries", DeleteEntry);
        api.MapPost("/entries/rename", RenameEntry);
        api.MapPost("/entries/password", SetPassword);
        api.MapGet("/schema", GetSchema);
        api.MapGet("/ldif/export", ExportLdif);
        api.MapPost("/ldif/plan", PlanLdif);
        api.MapPost("/ldif/import", ImportLdif);
        return true;
    }

    private static IResult GetDirectory(LdapDirectoryService directory) =>
        TypedResults.Ok(new DirectoryMetadataResponse(directory.BaseDn));

    private static async Task<IResult> GetChildren(
        LdapDirectoryService directory,
        HttpContext context,
        string? dn,
        int limit = 200)
    {
        var result = await directory.GetChildrenAsync(dn, limit, context.RequestAborted).ConfigureAwait(false);
        return TypedResults.Ok(new ChildrenResponse(
            [.. result.Children.Select(static child => new ChildEntryResponse(
                child.Dn, child.Rdn, child.ObjectClasses, child.HasChildren))],
            result.Truncated));
    }

    private static async Task<IResult> Search(
        LdapDirectoryService directory,
        SearchRequest request,
        HttpContext context)
    {
        if (!TryParseScope(request.Scope, out var scope))
        {
            return InvalidTransport("scope");
        }

        var result = await directory.SearchAsync(new LdapSearchOptions
        {
            BaseDn = request.BaseDn,
            Filter = request.Filter ?? "(objectClass=*)",
            Scope = scope,
            Limit = request.Limit,
            Attributes = request.Attributes ?? [],
        }, context.RequestAborted).ConfigureAwait(false);
        return TypedResults.Ok(new EntriesResponse([.. result.Entries.Select(ToEntry)], result.Truncated));
    }

    private static async Task<IResult> GetEntry(
        LdapDirectoryService directory,
        string dn,
        string[]? attributes,
        HttpContext context)
    {
        var entry = await directory.GetEntryAsync(dn, attributes, context.RequestAborted).ConfigureAwait(false);
        return entry is null
            ? ToProblem(new LdapOperationResult(LdapOperationStatus.NotFound, ResultCode.NoSuchObject))
            : TypedResults.Ok(new EntryReadResponse(ToEntry(entry), Truncated: false));
    }

    private static async Task<IResult> AddEntry(
        LdapDirectoryService directory,
        AddEntryRequest request,
        HttpContext context)
    {
        if ((request.Attributes ?? []).Any(static attribute => attribute is null))
        {
            return InvalidTransport("attributes");
        }

        var result = await directory.AddEntryAsync(new LdapNewEntry(
            request.Dn ?? string.Empty,
            [.. (request.Attributes ?? []).Select(static attribute => new LdapNewAttribute(
                attribute!.Name ?? string.Empty, attribute.Values ?? [], attribute.IsBase64))]), context.RequestAborted)
            .ConfigureAwait(false);
        return result.Succeeded
            ? TypedResults.Json(ToSuccess(result), statusCode: StatusCodes.Status201Created)
            : ToProblem(result);
    }

    private static async Task<IResult> ModifyEntry(
        LdapDirectoryService directory,
        ModifyEntryRequest request,
        HttpContext context)
    {
        List<LdapAttributeChange> changes = [];
        foreach (var change in request.Changes ?? [])
        {
            if (change is null || !TryParseModification(change.Operation, out var operation))
            {
                return InvalidTransport("operation");
            }
            changes.Add(new LdapAttributeChange(
                operation, change.Name ?? string.Empty, change.Values ?? [], change.IsBase64));
        }

        var result = await directory.ModifyEntryAsync(request.Dn ?? string.Empty, changes, context.RequestAborted)
            .ConfigureAwait(false);
        return result.Succeeded ? TypedResults.Ok(ToSuccess(result)) : ToProblem(result);
    }

    private static async Task<IResult> DeleteEntry(
        LdapDirectoryService directory,
        HttpContext context,
        string dn,
        bool subtree = false)
    {
        var result = await directory.DeleteEntryAsync(dn, subtree, context.RequestAborted).ConfigureAwait(false);
        return result.Succeeded ? TypedResults.Ok(ToSuccess(result)) : ToProblem(result);
    }

    private static async Task<IResult> RenameEntry(
        LdapDirectoryService directory,
        RenameEntryRequest request,
        HttpContext context)
    {
        var result = await directory.RenameEntryAsync(
            request.Dn ?? string.Empty,
            request.NewRdn ?? string.Empty,
            request.NewParentDn,
            request.DeleteOldRdn,
            context.RequestAborted).ConfigureAwait(false);
        return result.Outcome.Succeeded
            ? TypedResults.Ok(new RenameResponse(
                result.NewDn!,
                WireName(result.Outcome.Status),
                ResultCodeName(result.Outcome.ResultCode)))
            : ToProblem(result.Outcome);
    }

    private static async Task<IResult> SetPassword(
        LdapDirectoryService directory,
        PasswordRequest request,
        HttpContext context)
    {
        var result = await directory.SetPasswordAsync(
            request.Dn ?? string.Empty, request.NewPassword ?? string.Empty, context.RequestAborted).ConfigureAwait(false);
        return result.Succeeded ? TypedResults.Ok(ToSuccess(result)) : ToProblem(result);
    }

    private static async Task<IResult> GetSchema(
        LdapSchemaService schemaService,
        HttpContext context)
    {
        var result = await schemaService.GetSchemaAsync(context.RequestAborted).ConfigureAwait(false);
        var schema = result.Schema;
        return TypedResults.Ok(new SchemaResponse(
            result.Available,
            result.UnavailableReason,
            [.. schema.AttributeTypes.Select(ToAttributeType)],
            [.. schema.ObjectClasses.Select(ToObjectClass)],
            [.. schema.Syntaxes.Select(ToSyntax)],
            [.. schema.UnparsedDefinitions.Select(static definition => new UnparsedDefinitionResponse(
                WireName(definition.Kind), definition.Definition, definition.Error))],
            Truncated: false));
    }

    private static async Task<IResult> ExportLdif(
        LdapLdifService ldif,
        string? baseDn,
        HttpContext context)
    {
        var result = await ldif.ExportSubtreeAsync(baseDn, context.RequestAborted).ConfigureAwait(false);
        return TypedResults.Ok(new LdifExportResponse(result.Ldif, result.EntryCount, result.Truncated));
    }

    private static IResult PlanLdif(LdifRequest request)
    {
        var plan = LdapLdifService.ParsePlan(request.Ldif ?? string.Empty);
        return plan.Error is not null
            ? InvalidTransport("ldif")
            : TypedResults.Ok(new LdifPlanResponse(
                [.. plan.Items.Select(static item => new LdifPlanItemResponse(
                    item.ChangeType, item.Dn, item.DetailCount))],
                Truncated: false));
    }

    private static async Task<IResult> ImportLdif(
        LdapLdifService ldif,
        LdifRequest request,
        HttpContext context)
    {
        var plan = LdapLdifService.ParsePlan(request.Ldif ?? string.Empty);
        if (plan.Error is not null)
        {
            return InvalidTransport("ldif");
        }

        var result = await ldif.ApplyAsync(plan.Items, context.RequestAborted).ConfigureAwait(false);
        if (!result.Outcome.Succeeded)
        {
            Dictionary<string, object?> progress = new(StringComparer.Ordinal)
            {
                ["applied"] = result.Applied,
                ["total"] = result.Total,
                ["failedDn"] = result.FailedDn,
            };
            return ToProblem(result.Outcome, progress);
        }

        return TypedResults.Ok(new LdifImportResponse(
            result.Applied,
            result.Total,
            WireName(result.Outcome.Status),
            ResultCodeName(result.Outcome.ResultCode)));
    }

    internal static IResult ToProblem(
        LdapOperationResult result,
        IReadOnlyDictionary<string, object?>? additionalExtensions = null)
    {
        var problem = ProblemFor(result.Status);
        Dictionary<string, object?> extensions = new(StringComparer.Ordinal)
        {
            ["operationStatus"] = WireName(result.Status),
        };
        if (result.ResultCode is { } resultCode)
        {
            extensions["resultCode"] = ResultCodeName(resultCode);
        }
        if (result.DeletedCount > 0)
        {
            extensions["deletedCount"] = result.DeletedCount;
        }
        if (additionalExtensions is not null)
        {
            foreach (var extension in additionalExtensions)
            {
                extensions[extension.Key] = extension.Value;
            }
        }

        return TypedResults.Problem(
            statusCode: problem.StatusCode,
            type: $"urn:aspireldap:problem:ldap:{problem.Slug}",
            title: problem.Title,
            detail: problem.Detail,
            extensions: extensions);
    }

    private static IResult InvalidTransport(string field) => TypedResults.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        type: "urn:aspireldap:problem:transport:invalid-request",
        title: "Invalid API request",
        detail: $"The '{field}' value could not be parsed.",
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["operationStatus"] = "invalidRequest" });

    private static OperationResponse ToSuccess(LdapOperationResult result) =>
        new(WireName(result.Status), ResultCodeName(result.ResultCode), result.DeletedCount);

    private static EntryResponse ToEntry(LdapEntry entry) => new(
        entry.Dn,
        [.. entry.Attributes.Select(static attribute => new AttributeResponse(
            attribute.Name,
            attribute.IsBinary,
            attribute.Values,
            WireName(attribute.Classification)))]);

    private static AttributeTypeResponse ToAttributeType(LdapAttributeType attribute) => new(
        attribute.Oid,
        attribute.Names,
        attribute.Description,
        attribute.Obsolete,
        attribute.SuperiorName,
        attribute.Equality,
        attribute.Ordering,
        attribute.Substring,
        attribute.Syntax,
        attribute.SyntaxLength,
        attribute.SingleValue,
        attribute.Collective,
        attribute.NoUserModification,
        attribute.Usage,
        attribute.Extensions);

    private static ObjectClassResponse ToObjectClass(LdapObjectClass objectClass) => new(
        objectClass.Oid,
        objectClass.Names,
        objectClass.Description,
        objectClass.Obsolete,
        objectClass.SuperiorNames,
        WireName(objectClass.Kind),
        objectClass.Must,
        objectClass.May,
        objectClass.Extensions);

    private static SyntaxResponse ToSyntax(LdapSyntax syntax) => new(
        syntax.Oid,
        syntax.Names,
        syntax.Description,
        syntax.NotHumanReadable,
        syntax.BinaryTransferRequired,
        syntax.Extensions);

    private static bool TryParseScope(string? value, out SearchScope scope)
    {
        scope = value?.ToLowerInvariant() switch
        {
            "base" => SearchScope.Base,
            "onelevel" => SearchScope.OneLevel,
            "subtree" or null => SearchScope.Subtree,
            _ => (SearchScope)(-1),
        };
        return Enum.IsDefined(scope);
    }

    private static bool TryParseModification(string? value, out DirectoryAttributeOperation operation)
    {
        operation = value?.ToLowerInvariant() switch
        {
            "add" => DirectoryAttributeOperation.Add,
            "delete" => DirectoryAttributeOperation.Delete,
            "replace" => DirectoryAttributeOperation.Replace,
            _ => (DirectoryAttributeOperation)(-1),
        };
        return Enum.IsDefined(operation);
    }

    private static string? ResultCodeName(ResultCode? resultCode) =>
        resultCode is null ? null : WireName(resultCode.Value);

    private static string WireName<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static ProblemDefinition ProblemFor(LdapOperationStatus status) => status switch
    {
        LdapOperationStatus.Success => new(200, "success", "Operation succeeded", "The directory operation succeeded."),
        LdapOperationStatus.NotFound => new(404, "not-found", "Directory entry not found", "The requested directory entry was not found."),
        LdapOperationStatus.AlreadyExists => new(409, "already-exists", "Directory entry already exists", "A directory entry with that name already exists."),
        LdapOperationStatus.AccessDenied => new(403, "access-denied", "Directory access denied", "The directory denied this operation."),
        LdapOperationStatus.NotAllowedOnNonLeaf => new(409, "non-leaf", "Operation requires a leaf entry", "The directory entry has children that prevent this operation."),
        LdapOperationStatus.SchemaViolation => new(422, "schema-violation", "Directory schema violation", "The directory schema rejected this operation."),
        LdapOperationStatus.ConstraintViolation => new(409, "constraint-violation", "Directory constraint violation", "A directory constraint rejected this operation."),
        LdapOperationStatus.InvalidRequest => new(400, "invalid-request", "Invalid directory request", "The directory request was invalid."),
        LdapOperationStatus.Refused => new(422, "refused", "Directory operation refused", "The directory understood and refused this operation."),
        LdapOperationStatus.Cancelled => new(499, "cancelled", "Directory operation cancelled", "The directory operation was cancelled."),
        LdapOperationStatus.Failed => new(502, "failed", "Directory operation failed", "The directory did not complete this operation."),
        _ => new(500, "unknown-status", "Unknown directory result", "The directory returned an unknown operation status."),
    };

    private sealed class ApiExceptionFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToProblem(LdapOperationResult.Cancelled("cancelled"));
            }
            catch (ArgumentException)
            {
                return ToProblem(LdapOperationResult.Invalid("invalid"));
            }
            catch (LdapException exception) when (exception.ErrorCode == LdapFilterError)
            {
                return ToProblem(LdapOperationResult.Invalid("invalid filter"));
            }
            catch (DirectoryException)
            {
                return ToProblem(new LdapOperationResult(LdapOperationStatus.Failed));
            }
            catch (Exception)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    type: "urn:aspireldap:problem:internal",
                    title: "Internal server error",
                    detail: "The API request could not be completed.");
            }
        }
    }

    private sealed record ProblemDefinition(int StatusCode, string Slug, string Title, string Detail);
}

internal sealed record DirectoryMetadataResponse(string BaseDn);
internal sealed record ChildEntryResponse(string Dn, string Rdn, IReadOnlyList<string> ObjectClasses, bool HasChildren);
internal sealed record ChildrenResponse(IReadOnlyList<ChildEntryResponse> Children, bool Truncated);
internal sealed record SearchRequest(string? BaseDn, string? Filter, string? Scope, int Limit = 200, IReadOnlyList<string>? Attributes = null);
internal sealed record EntriesResponse(IReadOnlyList<EntryResponse> Entries, bool Truncated);
internal sealed record EntryReadResponse(EntryResponse Entry, bool Truncated);
internal sealed record EntryResponse(string Dn, IReadOnlyList<AttributeResponse> Attributes);
internal sealed record AttributeResponse(string Name, bool IsBinary, IReadOnlyList<string> Values, string Classification);
internal sealed record AddEntryRequest(string? Dn, IReadOnlyList<NewAttributeRequest?>? Attributes);
internal sealed record NewAttributeRequest(string? Name, IReadOnlyList<string>? Values, bool IsBase64 = false);
internal sealed record ModifyEntryRequest(string? Dn, IReadOnlyList<AttributeChangeRequest?>? Changes);
internal sealed record AttributeChangeRequest(string? Operation, string? Name, IReadOnlyList<string>? Values, bool IsBase64 = false);
internal sealed record RenameEntryRequest(string? Dn, string? NewRdn, string? NewParentDn = null, bool DeleteOldRdn = true);
internal sealed record PasswordRequest(string? Dn, string? NewPassword);
internal sealed record OperationResponse(string OperationStatus, string? ResultCode, int DeletedCount);
internal sealed record RenameResponse(string Dn, string OperationStatus, string? ResultCode);
internal sealed record SchemaResponse(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<AttributeTypeResponse> AttributeTypes,
    IReadOnlyList<ObjectClassResponse> ObjectClasses,
    IReadOnlyList<SyntaxResponse> Syntaxes,
    IReadOnlyList<UnparsedDefinitionResponse> UnparsedDefinitions,
    bool Truncated);
internal sealed record AttributeTypeResponse(
    string Oid,
    IReadOnlyList<string> Names,
    string? Description,
    bool Obsolete,
    string? SuperiorName,
    string? Equality,
    string? Ordering,
    string? Substring,
    string? Syntax,
    int? SyntaxLength,
    bool SingleValue,
    bool Collective,
    bool NoUserModification,
    string? Usage,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Extensions);
internal sealed record ObjectClassResponse(
    string Oid,
    IReadOnlyList<string> Names,
    string? Description,
    bool Obsolete,
    IReadOnlyList<string> SuperiorNames,
    string Kind,
    IReadOnlyList<string> Must,
    IReadOnlyList<string> May,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Extensions);
internal sealed record SyntaxResponse(
    string Oid,
    IReadOnlyList<string> Names,
    string? Description,
    bool NotHumanReadable,
    bool BinaryTransferRequired,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Extensions);
internal sealed record UnparsedDefinitionResponse(string Kind, string Definition, string Error);
internal sealed record LdifExportResponse(string Ldif, int EntryCount, bool Truncated);
internal sealed record LdifRequest(string? Ldif);
internal sealed record LdifPlanItemResponse(string ChangeType, string Dn, int DetailCount);
internal sealed record LdifPlanResponse(IReadOnlyList<LdifPlanItemResponse> Items, bool Truncated);
internal sealed record LdifImportResponse(int Applied, int Total, string OperationStatus, string? ResultCode);

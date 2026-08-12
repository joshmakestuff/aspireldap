using System.DirectoryServices.Protocols;
using System.Formats.Asn1;
using System.Text;
using System.Text.Unicode;
using Aspire.OpenLdap;
using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// Directory browse, search, read and write for the admin UI, over the connection the AppHost
/// published. Every operation binds with those credentials — there is no per-user identity and
/// no login; who may do what is the server ACL's call, reported as
/// <see cref="LdapOperationStatus.AccessDenied"/> rather than thrown.
/// </summary>
/// <remarks>
/// Safe to hold for the application's lifetime: each operation takes its own
/// <see cref="OpenLdapClient"/> from <paramref name="factory"/> and disposes it, because a
/// client is no more thread-safe than the connection it wraps.
/// </remarks>
public sealed class LdapDirectoryService(OpenLdapClientFactory factory, LdapSchemaService schema)
{
    /// <summary>RFC 3062 Password Modify extended operation.</summary>
    private const string PasswordModifyOid = "1.3.6.1.4.1.4203.1.11.1";

    /// <summary>
    /// RFC 2696 page size ceiling. Paging exists so a large result set arrives in bounded
    /// chunks; a page larger than this defeats that without reducing round trips meaningfully.
    /// </summary>
    private const int MaxPageSize = 500;

    /// <summary>The directory's base DN, as published in the AppHost's connection string.</summary>
    public string BaseDn => factory.ConnectionString.BaseDn;

    /// <summary>Reads a single entry, or null when it does not exist (or the bind cannot see it).</summary>
    /// <exception cref="ArgumentException"><paramref name="dn"/> is not a valid RFC 4514 DN.</exception>
    public async Task<LdapEntry?> GetEntryAsync(
        string dn,
        IReadOnlyList<string>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dn);
        EnsureValidDn(dn, nameof(dn));

        var binaryAttributes = await schema.GetBinaryAttributesAsync(cancellationToken).ConfigureAwait(false);
        using var client = factory.CreateClient();

        var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, [.. attributes ?? []]);
        try
        {
            var response = (SearchResponse)await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Entries.Count == 0 ? null : ToEntry(response.Entries[0], binaryAttributes);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists up to <paramref name="limit"/> direct children of <paramref name="dn"/> (the base
    /// DN when null), each carrying whether it has children of its own (the
    /// <c>hasSubordinates</c> operational attribute). The result flags truncation instead of
    /// silently dropping children past the limit.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dn"/> is not a valid RFC 4514 DN.</exception>
    public async Task<LdapChildrenResult> GetChildrenAsync(
        string? dn = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var searchBase = dn ?? BaseDn;
        EnsureValidDn(searchBase, nameof(dn));

        using var client = factory.CreateClient();
        var request = new SearchRequest(
            searchBase,
            "(objectClass=*)",
            SearchScope.OneLevel,
            "objectClass",
            "hasSubordinates")
        {
            SizeLimit = limit,
        };

        SearchResponse response;
        var truncated = false;
        try
        {
            response = (SearchResponse)await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (
            ex.Response is SearchResponse partial && partial.ResultCode == ResultCode.SizeLimitExceeded)
        {
            // The node has more children than the limit; keep what the server returned and say so.
            response = partial;
            truncated = true;
        }

        List<LdapChildEntry> children = new(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries)
        {
            children.Add(new LdapChildEntry(
                entry.DistinguishedName,
                FirstRdn(entry.DistinguishedName),
                GetStringValues(entry, "objectClass"),
                string.Equals(
                    GetStringValues(entry, "hasSubordinates").FirstOrDefault(),
                    "TRUE",
                    StringComparison.OrdinalIgnoreCase)));
        }

        return new LdapChildrenResult(children, truncated);
    }

    /// <summary>
    /// Searches up to <see cref="LdapSearchOptions.Limit"/> entries. RFC 2696 paging cookies
    /// are connection-scoped, so the whole paging loop runs on one client here, and the result
    /// says whether the server had more — including when the server's own size limit, rather
    /// than the request's, cut the search short.
    /// </summary>
    /// <exception cref="ArgumentException">The search base is not a valid RFC 4514 DN.</exception>
    public async Task<LdapSearchResult> SearchAsync(
        LdapSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Limit, 1, nameof(options));
        var searchBase = options.BaseDn ?? BaseDn;
        EnsureValidDn(searchBase, nameof(options));

        var binaryAttributes = await schema.GetBinaryAttributesAsync(cancellationToken).ConfigureAwait(false);
        using var client = factory.CreateClient();

        List<LdapEntry> entries = [];
        var truncated = false;
        byte[] cookie = [];

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new SearchRequest(
                searchBase,
                options.Filter,
                options.Scope,
                [.. options.Attributes]);

            // One more than the caller asked for: the extra entry is how "there were more" is
            // learned without a second round trip, and it is never handed back.
            request.Controls.Add(new PageResultRequestControl(
                Math.Min(options.Limit - entries.Count + 1, MaxPageSize))
            {
                Cookie = cookie,
            });

            SearchResponse response;
            try
            {
                response = (SearchResponse)await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryOperationException ex) when (
                ex.Response is SearchResponse partial && partial.ResultCode == ResultCode.SizeLimitExceeded)
            {
                // slapd's own sizelimit stopped the search; keep what it did return.
                Append(entries, partial, options.Limit, ref truncated, binaryAttributes);
                return new LdapSearchResult(entries, Truncated: true);
            }

            Append(entries, response, options.Limit, ref truncated, binaryAttributes);
            cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
        }
        while (cookie.Length > 0 && !truncated);

        return new LdapSearchResult(entries, truncated || cookie.Length > 0);
    }

    /// <summary>Adds a new entry. Validation failures are reported, not thrown.</summary>
    public async Task<LdapOperationResult> AddEntryAsync(
        LdapNewEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (TryInvalidDn(entry.Dn, out var dnError))
        {
            return dnError;
        }
        if (entry.Attributes.Count == 0)
        {
            return LdapOperationResult.Invalid("An entry must be created with at least one attribute (objectClass).");
        }

        var request = new AddRequest(entry.Dn);
        foreach (var attribute in entry.Attributes)
        {
            if (!TryBuildValues(attribute.Name, attribute.Values, attribute.IsBase64, out var built, out var error))
            {
                return error;
            }

            var directoryAttribute = new DirectoryAttribute { Name = attribute.Name };
            AddValues(directoryAttribute, built);
            request.Attributes.Add(directoryAttribute);
        }

        return await SendWriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies attribute modifications to an existing entry.</summary>
    public async Task<LdapOperationResult> ModifyEntryAsync(
        string dn,
        IReadOnlyList<LdapAttributeChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (TryInvalidDn(dn, out var dnError))
        {
            return dnError;
        }
        if (changes.Count == 0)
        {
            return LdapOperationResult.Invalid("A modify must carry at least one change.");
        }

        // The #94 password guard, at its second door: a userPassword modification on the
        // bind identity is the same silent self-brick the RFC 3062 path refuses.
        if (changes.Any(static c => c.Name.Equals("userPassword", StringComparison.OrdinalIgnoreCase))
            && DnEquality.AreEquivalent(dn, factory.ConnectionString.BindDn))
        {
            return LdapOperationResult.Invalid(
                $"'{dn}' is the console's bind identity; its password cannot be changed from here.");
        }

        var request = new ModifyRequest(dn);
        foreach (var change in changes)
        {
            if (!Enum.IsDefined(change.Operation))
            {
                return LdapOperationResult.Invalid($"Unknown modification operation '{change.Operation}'.");
            }
            if (!TryBuildValues(change.Name, change.Values, change.IsBase64, out var built, out var error))
            {
                return error;
            }

            var modification = new DirectoryAttributeModification { Name = change.Name, Operation = change.Operation };
            AddValues(modification, built);
            request.Modifications.Add(modification);
        }

        return await SendWriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an entry. A plain delete requires a leaf — the server refuses non-leaves.
    /// With <paramref name="subtree"/>, deletes the entry's whole subtree depth-first,
    /// children before parents: the bundled OpenLDAP does not advertise the Tree Delete
    /// control (1.2.840.113556.1.4.805; root DSE measured 2026-08-10, workspace
    /// findings.md), so the recursion is client-side, as <c>ldapdelete -r</c> does. A
    /// server sizelimit does not stop the walk: a size-limited listing's partial batch is
    /// deleted and the walk re-lists until the container empties (#118). The walk stops at
    /// the first refusal and the result names the DN that failed — a partial delete is
    /// never silent.
    /// </summary>
    public Task<LdapOperationResult> DeleteEntryAsync(string dn, CancellationToken cancellationToken = default) =>
        DeleteEntryAsync(dn, subtree: false, cancellationToken);

    /// <inheritdoc cref="DeleteEntryAsync(string, CancellationToken)"/>
    public async Task<LdapOperationResult> DeleteEntryAsync(
        string dn,
        bool subtree,
        CancellationToken cancellationToken = default)
    {
        if (TryInvalidDn(dn, out var dnError))
        {
            return dnError;
        }

        // Deleting the bind identity severs every connection the console makes from then on;
        // refused without a round trip (#136). The subtree walk gets the ancestor check too,
        // because its children are deleted through raw DeleteRequests that never re-enter
        // this method — without it the walk would sweep the identity away mid-recursion. A
        // plain delete of an ancestor needs no guard: the server refuses non-leaves.
        if (DnEquality.AreEquivalent(dn, factory.ConnectionString.BindDn))
        {
            return LdapOperationResult.Invalid(
                $"'{dn}' is the console's bind identity; it cannot be deleted from here.");
        }
        if (subtree && DnEquality.IsUnder(factory.ConnectionString.BindDn, dn))
        {
            return LdapOperationResult.Invalid(
                $"'{dn}' contains the console's bind identity; its subtree cannot be deleted from here.");
        }

        if (!subtree)
        {
            return await SendWriteAsync(new DeleteRequest(dn), cancellationToken).ConfigureAwait(false);
        }

        // One client for the whole walk: paging cookies are connection-scoped, and a
        // subtree delete is one logical operation.
        using var client = factory.CreateClient();
        return await DeleteSubtreeAsync(client, dn, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LdapOperationResult> DeleteSubtreeAsync(
        OpenLdapClient client,
        string dn,
        CancellationToken cancellationToken)
    {
        // Children first. Re-list after each sweep — the final DeleteRequest below is the
        // authoritative "now empty" check either way.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<string> children = [];
            byte[] cookie = [];
            do
            {
                // "1.1" = no attributes; only the DNs matter here.
                var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.OneLevel, "1.1");
                request.Controls.Add(new PageResultRequestControl(MaxPageSize) { Cookie = cookie });
                SearchResponse response;
                try
                {
                    response = (SearchResponse)await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (DirectoryOperationException ex) when (
                    ex.Response is SearchResponse partial && partial.ResultCode == ResultCode.SizeLimitExceeded)
                {
                    // slapd's sizelimit cut the listing short; keep what it did return and
                    // delete that batch — this outer sweep loop re-lists and converges, so
                    // a container larger than the limit still empties sweep by sweep (#118).
                    foreach (SearchResultEntry entry in partial.Entries)
                    {
                        children.Add(entry.DistinguishedName);
                    }
                    break; // the paging cookie is dead after the error; next sweep re-lists
                }
                catch (DirectoryOperationException ex) when (ex.Response is not null)
                {
                    var code = ex.Response.ResultCode;
                    return new LdapOperationResult(Classify(code), code,
                        $"listing children of '{dn}': {ex.Response.ErrorMessage ?? ex.Message}");
                }
                foreach (SearchResultEntry entry in response.Entries)
                {
                    children.Add(entry.DistinguishedName);
                }
                cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
            }
            while (cookie.Length > 0);

            if (children.Count == 0)
            {
                break;
            }
            foreach (var child in children)
            {
                var result = await DeleteSubtreeAsync(client, child, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return result;
                }
            }
        }

        return await DeleteOneAsync(client, dn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One delete on the walk's shared client; the result names the DN on failure.</summary>
    private static async Task<LdapOperationResult> DeleteOneAsync(
        OpenLdapClient client,
        string dn,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendAsync(new DeleteRequest(dn), cancellationToken).ConfigureAwait(false);
            return response.ResultCode == ResultCode.Success
                ? LdapOperationResult.Ok()
                : new LdapOperationResult(Classify(response.ResultCode), response.ResultCode,
                    $"deleting '{dn}': {response.ErrorMessage}");
        }
        catch (DirectoryOperationException ex) when (ex.Response is not null)
        {
            var code = ex.Response.ResultCode;
            return new LdapOperationResult(Classify(code), code,
                $"deleting '{dn}': {ex.Response.ErrorMessage ?? ex.Message}");
        }
    }

    /// <summary>
    /// Renames and/or moves an entry, returning its new DN on success. When
    /// <paramref name="newParentDn"/> is null the entry stays under its current parent.
    /// </summary>
    public async Task<LdapRenameResult> RenameEntryAsync(
        string dn,
        string newRdn,
        string? newParentDn = null,
        bool deleteOldRdn = true,
        CancellationToken cancellationToken = default)
    {
        if (TryInvalidDn(dn, out var dnError))
        {
            return new LdapRenameResult(dnError, null);
        }
        if (newParentDn is not null && TryInvalidDn(newParentDn, out var parentError))
        {
            return new LdapRenameResult(parentError, null);
        }

        // Renaming the bind identity — or a container it lives under, which renames it just
        // as surely — desyncs the AppHost's declared credentials from the directory; refused
        // here without a round trip, like the password guard (#94, #136).
        if (DnEquality.AreEquivalent(dn, factory.ConnectionString.BindDn))
        {
            return new LdapRenameResult(LdapOperationResult.Invalid(
                $"'{dn}' is the console's bind identity; it cannot be renamed from here."), null);
        }
        if (DnEquality.IsUnder(factory.ConnectionString.BindDn, dn))
        {
            return new LdapRenameResult(LdapOperationResult.Invalid(
                $"'{dn}' contains the console's bind identity; renaming it would rename the identity too."), null);
        }

        IReadOnlyList<RelativeDistinguishedName> parsedRdn;
        try
        {
            parsedRdn = Dn.Parse(newRdn ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            return new LdapRenameResult(LdapOperationResult.Invalid($"'{newRdn}' is not a valid RDN: {ex.Message}"), null);
        }
        if (parsedRdn.Count != 1)
        {
            return new LdapRenameResult(
                LdapOperationResult.Invalid($"'{newRdn}' must be exactly one RDN, not {parsedRdn.Count}."),
                null);
        }

        // Re-rendered from the parse rather than concatenated as given, so the DN handed back is
        // escaped the one way LdifDotNet escapes DNs.
        var rdn = parsedRdn[0].ToString();
        var request = new ModifyDNRequest(dn, newParentDn, rdn) { DeleteOldRdn = deleteOldRdn };
        var outcome = await SendWriteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return new LdapRenameResult(outcome, null);
        }

        var parent = newParentDn ?? ParentDn(dn);
        return new LdapRenameResult(outcome, Dn.Combine(rdn, parent ?? string.Empty));
    }

    /// <summary>
    /// Changes an entry's password via the RFC 3062 Password Modify extended operation: the
    /// server picks the storage scheme and enforces any password policy, so this never controls
    /// the stored hash format.
    /// </summary>
    public async Task<LdapOperationResult> SetPasswordAsync(
        string dn,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);

        if (TryInvalidDn(dn, out var dnError))
        {
            return dnError;
        }

        // A password change aimed at the console's own bind identity is refused here, without
        // a round trip: it would sever every connection the console makes from then on, and
        // silently desync the AppHost's declared credentials from the directory (#94).
        if (DnEquality.AreEquivalent(dn, factory.ConnectionString.BindDn))
        {
            return LdapOperationResult.Invalid(
                $"'{dn}' is the console's bind identity; its password cannot be changed from here.");
        }

        // PasswdModifyRequestValue ::= SEQUENCE {
        //     userIdentity [0] OCTET STRING OPTIONAL, newPasswd [2] OCTET STRING OPTIONAL }
        var value = new AsnWriter(AsnEncodingRules.BER);
        using (value.PushSequence())
        {
            value.WriteOctetString(Encoding.UTF8.GetBytes(dn), new Asn1Tag(TagClass.ContextSpecific, 0));
            value.WriteOctetString(Encoding.UTF8.GetBytes(newPassword), new Asn1Tag(TagClass.ContextSpecific, 2));
        }

        return await SendWriteAsync(new ExtendedRequest(PasswordModifyOid, value.Encode()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LdapOperationResult> SendWriteAsync(
        DirectoryRequest request,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        try
        {
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ResultCode == ResultCode.Success
                ? LdapOperationResult.Ok()
                : new LdapOperationResult(Classify(response.ResultCode), response.ResultCode, response.ErrorMessage);
        }
        catch (DirectoryOperationException ex) when (ex.Response is not null)
        {
            var code = ex.Response.ResultCode;
            return new LdapOperationResult(Classify(code), code, ex.Response.ErrorMessage ?? ex.Message);
        }
    }

    /// <summary>
    /// Maps a server result code onto the outcome classes a UI has to render differently.
    /// Codes with no class here arrive as <see cref="LdapOperationStatus.Failed"/> carrying the
    /// code itself, so an unmodelled answer is still reported rather than flattened into
    /// "something went wrong".
    /// </summary>
    private static LdapOperationStatus Classify(ResultCode code) => code switch
    {
        ResultCode.Success => LdapOperationStatus.Success,
        ResultCode.NoSuchObject => LdapOperationStatus.NotFound,
        ResultCode.EntryAlreadyExists => LdapOperationStatus.AlreadyExists,
        ResultCode.InsufficientAccessRights => LdapOperationStatus.AccessDenied,
        ResultCode.NotAllowedOnNonLeaf => LdapOperationStatus.NotAllowedOnNonLeaf,
        ResultCode.ObjectClassViolation
            or ResultCode.ObjectClassModificationsProhibited
            or ResultCode.UndefinedAttributeType
            or ResultCode.InvalidAttributeSyntax => LdapOperationStatus.SchemaViolation,
        ResultCode.ConstraintViolation
            or ResultCode.AttributeOrValueExists
            or ResultCode.NoSuchAttribute
            or ResultCode.NotAllowedOnRdn => LdapOperationStatus.ConstraintViolation,
        ResultCode.InvalidDNSyntax or ResultCode.ProtocolError => LdapOperationStatus.InvalidRequest,
        ResultCode.UnwillingToPerform => LdapOperationStatus.Refused,
        _ => LdapOperationStatus.Failed,
    };

    private static void Append(
        List<LdapEntry> entries,
        SearchResponse response,
        int limit,
        ref bool truncated,
        LdapBinaryAttributes binaryAttributes)
    {
        foreach (SearchResultEntry entry in response.Entries)
        {
            if (entries.Count >= limit)
            {
                truncated = true;
                return;
            }

            entries.Add(ToEntry(entry, binaryAttributes));
        }
    }

    /// <summary>
    /// Projects a result entry, deciding text-or-base64 per attribute. The schema decides first
    /// and the bytes only break ties, because deciding from the bytes alone makes an attribute's
    /// representation depend on the values it happens to hold: a binary value whose bytes are
    /// valid UTF-8 comes back as text, and adding a non-UTF-8 sibling value silently re-encodes
    /// every other value in the same attribute.
    /// </summary>
    private static LdapEntry ToEntry(SearchResultEntry entry, LdapBinaryAttributes binaryAttributes)
    {
        List<LdapAttributeValues> attributes = new(entry.Attributes.Count);
        foreach (DirectoryAttribute attribute in entry.Attributes.Values)
        {
            var raw = attribute.GetValues(typeof(byte[])).Cast<byte[]>().ToArray();
            var (isBinary, classification) = ClassifyValues(attribute.Name, raw, binaryAttributes);

            IReadOnlyList<string> values = isBinary
                ? [.. raw.Select(Convert.ToBase64String)]
                : [.. raw.Select(static value => Encoding.UTF8.GetString(value))];
            attributes.Add(new LdapAttributeValues(attribute.Name, isBinary, values, classification));
        }

        return new LdapEntry(entry.DistinguishedName, attributes);
    }

    /// <summary>
    /// Decides whether an attribute's values travel as base64 or as text, in priority order:
    /// the schema's syntax, then an explicit RFC 4522 <c>;binary</c> transfer option (which
    /// slapd adds to the name it returns, so it survives a schema that could not be read), and
    /// only then the bytes. That last clause is a necessity rather than a heuristic — a value
    /// that is not valid UTF-8 has no string form to send at all — and it can only ever turn an
    /// attribute binary, never text.
    /// </summary>
    private static (bool IsBinary, LdapValueClassification Classification) ClassifyValues(
        string attributeDescription,
        IReadOnlyList<byte[]> values,
        LdapBinaryAttributes binaryAttributes)
    {
        if (binaryAttributes.IsBinary(attributeDescription))
        {
            return (true, LdapValueClassification.Schema);
        }
        if (AttributeDescription.HasOption(attributeDescription, "binary"))
        {
            return (true, LdapValueClassification.TransferOption);
        }
        foreach (var value in values)
        {
            if (!Utf8.IsValid(value))
            {
                return (true, LdapValueClassification.ByteInspection);
            }
        }

        return (false, binaryAttributes.Knows(attributeDescription)
            ? LdapValueClassification.Schema
            : LdapValueClassification.ByteInspection);
    }

    /// <summary>
    /// Validates one attribute's name and values, yielding each value in the form
    /// <see cref="DirectoryAttribute"/> takes it — <see cref="string"/> for text, byte[] for
    /// a base64-carried binary value. Both write paths share it so an add and a modify can
    /// never disagree about what a caller is allowed to send.
    /// </summary>
    private static bool TryBuildValues(
        string name,
        IReadOnlyList<string> values,
        bool isBase64,
        out object[] built,
        out LdapOperationResult error)
    {
        built = [];
        if (string.IsNullOrEmpty(name) || !AttributeDescription.IsValid(name))
        {
            error = LdapOperationResult.Invalid($"'{name}' is not a valid attribute description.");
            return false;
        }

        var result = new object[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            if (!isBase64)
            {
                result[i] = values[i];
                continue;
            }

            try
            {
                result[i] = Convert.FromBase64String(values[i]);
            }
            catch (FormatException ex)
            {
                error = LdapOperationResult.Invalid($"A value of '{name}' is not valid base64: {ex.Message}");
                return false;
            }
        }

        built = result;
        error = LdapOperationResult.Ok();
        return true;
    }

    /// <summary>
    /// <see cref="DirectoryAttribute.Add"/> is overloaded per value type rather than taking an
    /// object, and <see cref="DirectoryAttributeModification"/> derives from it, so this one
    /// dispatch serves both write paths.
    /// </summary>
    private static void AddValues(DirectoryAttribute target, object[] values)
    {
        foreach (var value in values)
        {
            if (value is byte[] bytes)
            {
                target.Add(bytes);
            }
            else
            {
                target.Add((string)value);
            }
        }
    }

    /// <summary>The first RDN of a DN, in escaped RFC 4514 form.</summary>
    private static string FirstRdn(string dn)
    {
        var rdns = Dn.Parse(dn);
        return rdns.Count == 0 ? dn : rdns[0].ToString();
    }

    /// <summary>Everything above the first RDN, or null for a DN with one RDN or none.</summary>
    private static string? ParentDn(string dn)
    {
        var rdns = Dn.Parse(dn);
        return rdns.Count <= 1 ? null : Dn.Combine([.. rdns.Skip(1).Select(static rdn => rdn.ToString())]);
    }

    private static void EnsureValidDn(string dn, string paramName)
    {
        try
        {
            Dn.Parse(dn);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{dn}' is not a valid distinguished name: {ex.Message}", paramName, ex);
        }
    }

    /// <summary>
    /// True (with <paramref name="error"/> set) when the DN cannot be used. Writes report a
    /// malformed DN instead of throwing: it is usually something a person typed, which is an
    /// answer to render, not a bug to crash on.
    /// </summary>
    private static bool TryInvalidDn(string dn, out LdapOperationResult error)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            error = LdapOperationResult.Invalid("A distinguished name is required.");
            return true;
        }

        try
        {
            if (Dn.Parse(dn).Count == 0)
            {
                error = LdapOperationResult.Invalid("A distinguished name is required.");
                return true;
            }
        }
        catch (ArgumentException ex)
        {
            error = LdapOperationResult.Invalid($"'{dn}' is not a valid distinguished name: {ex.Message}");
            return true;
        }

        error = LdapOperationResult.Ok();
        return false;
    }

    private static IReadOnlyList<string> GetStringValues(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute)
            ? [.. entry.Attributes[attribute].GetValues(typeof(string)).Cast<string>()]
            : [];
}

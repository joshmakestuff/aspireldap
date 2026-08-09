using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using LdifDotNet.Schema;
using Microsoft.Extensions.Logging;

namespace Aspire.LdapAdmin.Core;

/// <summary>
/// The server's schema, or an explicit statement that it could not be read. The unreadable
/// case carries an empty <see cref="Schema"/>, which is indistinguishable from a directory
/// that defines nothing — so it is flagged rather than left for the caller to infer, the same
/// reason a truncated result set is flagged.
/// </summary>
/// <param name="Schema">
/// The parsed subschema. <see cref="LdapSchema.UnparsedDefinitions"/> carries any definition
/// the parser could not handle, so nothing is silently dropped.
/// </param>
/// <param name="Available">False when the subschema subentry could not be read.</param>
/// <param name="UnavailableReason">Why, when <paramref name="Available"/> is false; otherwise null.</param>
public sealed record LdapSchemaResult(LdapSchema Schema, bool Available, string? UnavailableReason);

/// <summary>
/// Reads the server's schema from the subschema subentry advertised by the root DSE, and
/// caches it for the life of the process. The parsed <see cref="LdapSchema"/> is handed to
/// consumers as-is: there is no second schema model to keep in step with it.
/// </summary>
/// <remarks>
/// A directory can gain schema at runtime (OpenLDAP's <c>cn=config</c> allows it), so the
/// cache can go stale; for a dev-time tool against a container the AppHost started, that is
/// worth one search instead of one per entry read, and a restart picks the change up.
/// Only a schema that actually parsed is cached — see <see cref="LoadAsync"/>.
/// </remarks>
public sealed class LdapSchemaService(OpenLdapClientFactory factory, ILogger<LdapSchemaService> logger)
{
    /// <summary>RFC 4512 §5.1.6: where the root DSE names its subschema, and the fallback DN.</summary>
    private const string SubschemaSubentryAttribute = "subschemaSubentry";
    private const string DefaultSubschemaDn = "cn=Subschema";

    private static readonly LdapSchema EmptySchema = LdapSchema.ParseSubschema([], [], []);

    private volatile SchemaCache? _cache;

    /// <summary>Reads (or returns the cached) server schema.</summary>
    public async Task<LdapSchemaResult> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// The binary-attribute classification derived from the same schema read, so an entry
    /// projection and a schema page can never disagree about what an attribute holds.
    /// </summary>
    internal async Task<LdapBinaryAttributes> GetBinaryAttributesAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).BinaryAttributes;

    private async Task<SchemaCache> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        SchemaCache loaded;
        try
        {
            loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            // Deliberately not cached: this bind may simply lack read access to the subschema,
            // and a later attempt can still succeed. Until one does, classification falls back
            // to UTF-8 validity — which can only under-report binary, never corrupt a value —
            // so this degrades rather than fails, and says so instead of degrading silently.
            logger.LogWarning(
                ex,
                "Could not read the subschema subentry, so attribute values are being classified by UTF-8 validity alone. Binary attributes whose values happen to be valid UTF-8 will be reported as text until the schema becomes readable.");
            return Unavailable($"the subschema subentry could not be read ({ex.GetType().Name}).");
        }

        if (!loaded.Result.Available)
        {
            // An ACL-hidden subschema answers with zero entries rather than an error, and the
            // empty schema that produces classifies nothing — indistinguishable, once cached,
            // from a directory in which no attribute is binary. Keep it out of the cache so a
            // later read can still succeed, exactly as the exception path does.
            logger.LogWarning(
                "The subschema subentry returned no attribute types, so attribute values are being classified by UTF-8 validity alone ({Reason})",
                loaded.Result.UnavailableReason);
            return loaded;
        }

        if (loaded.Result.Schema.UnparsedDefinitions.Count > 0)
        {
            // A definition the parser could not handle is one this cache will never classify.
            // That is recoverable — the values still travel intact — but it is not something to
            // discover only by diffing an entry, so it is said out loud. The raw text stays on
            // LdapSchema.UnparsedDefinitions for a caller that wants to show it.
            logger.LogWarning(
                "{Count} schema definition(s) could not be parsed and are excluded from binary-attribute classification; any attribute they define falls back to UTF-8 validity.",
                loaded.Result.Schema.UnparsedDefinitions.Count);
        }

        _cache = loaded;
        return loaded;
    }

    private async Task<SchemaCache> ReadAsync(CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();

        var subschemaDn = await ReadSubschemaDnAsync(client, cancellationToken).ConfigureAwait(false);

        // RFC 4512 §4.2: subschema entries are only returned to a base-scope search with this
        // exact filter; (objectClass=*) comes back empty.
        var request = new SearchRequest(
            subschemaDn,
            "(objectClass=subschema)",
            SearchScope.Base,
            "objectClasses",
            "attributeTypes",
            "ldapSyntaxes");
        var response = (SearchResponse)await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Entries.Count == 0)
        {
            return Unavailable($"a base search of '{subschemaDn}' returned no subschema entry.");
        }

        var entry = response.Entries[0];

        // The lenient aggregate, deliberately: the package's strict per-definition Parse methods
        // throw on unknown non-X- keywords, whereas a live server may publish any extension it
        // likes. ParseSubschema skips those and routes a definition it genuinely cannot read
        // into UnparsedDefinitions.
        var schema = LdapSchema.ParseSubschema(
            GetStringValues(entry, "attributeTypes"),
            GetStringValues(entry, "objectClasses"),
            GetStringValues(entry, "ldapSyntaxes"));

        return schema.AttributeTypes.Count == 0
            ? Unavailable($"the subschema entry '{subschemaDn}' published no attributeTypes.")
            : new SchemaCache(new LdapSchemaResult(schema, Available: true, null), LdapBinaryAttributes.FromSchema(schema));
    }

    private static async Task<string> ReadSubschemaDnAsync(OpenLdapClient client, CancellationToken cancellationToken)
    {
        var rootDse = (SearchResponse)await client.SendAsync(
            new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, SubschemaSubentryAttribute),
            cancellationToken).ConfigureAwait(false);

        if (rootDse.Entries.Count == 0)
        {
            return DefaultSubschemaDn;
        }

        var values = GetStringValues(rootDse.Entries[0], SubschemaSubentryAttribute);
        return values.Count > 0 ? values[0] : DefaultSubschemaDn;
    }

    private static SchemaCache Unavailable(string reason) =>
        new(new LdapSchemaResult(EmptySchema, Available: false, reason), LdapBinaryAttributes.Unknown);

    private static IReadOnlyList<string> GetStringValues(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute)
            ? [.. entry.Attributes[attribute].GetValues(typeof(string)).Cast<string>()]
            : [];

    private sealed record SchemaCache(LdapSchemaResult Result, LdapBinaryAttributes BinaryAttributes);
}

using System.Globalization;
using LdifDotNet;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// How the <c>memberof</c> overlay treats member values that do not resolve to an existing
/// entry (<c>olcMemberOfDangling</c>).
/// </summary>
public enum OpenLdapMemberOfDanglingPolicy
{
    /// <summary>Leave the dangling reference in place (the OpenLDAP default).</summary>
    Ignore,

    /// <summary>Silently drop the dangling value from the group entry.</summary>
    Drop,

    /// <summary>Reject the operation that would create the dangling reference.</summary>
    Error,
}

/// <summary>
/// A typed OpenLDAP overlay declaration. Overlays are opt-in: declare them with
/// <c>WithOverlay(...)</c> and the resource emits the corresponding <c>cn=config</c> entries
/// (module load + overlay config) into the slapd bootstrap before the data load.
///
/// Construct via the factory methods (e.g. <see cref="MemberOf"/>); add more factories as
/// other overlays are needed (refint, unique, ppolicy, …) without changing the wiring.
/// Custom overlays can still be built with an object initializer — the declaration is
/// validated at the <c>WithOverlay(...)</c> call, before any container starts.
/// </summary>
public sealed class OpenLdapOverlay
{
    /// <summary>Overlay name as used in <c>olcOverlay: &lt;name&gt;</c> (e.g. "memberof").</summary>
    public required string Name { get; init; }

    /// <summary>Modules to load for this overlay (e.g. "memberof.so").</summary>
    /// <remarks>Snapshotted on init: a caller-retained list mutated after
    /// <c>WithOverlay(...)</c> validated the declaration must not reach LDIF generation.</remarks>
    public IReadOnlyList<string> ModuleLoads
    {
        get;
        init => field = [.. value ?? throw new ArgumentNullException(nameof(value))];
    } = [];

    /// <summary>The overlay's config objectClass (e.g. "olcMemberOf").</summary>
    public required string OverlayObjectClass { get; init; }

    /// <summary>Ordered <c>olc*</c> attributes for the overlay entry.</summary>
    /// <remarks>Snapshotted on init, for the same reason as <see cref="ModuleLoads"/>.</remarks>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes
    {
        get;
        init => field = [.. value ?? throw new ArgumentNullException(nameof(value))];
    } = [];

    /// <summary>
    /// The <c>memberof</c> overlay (slapo-memberof): maintains a reverse-membership
    /// <paramref name="memberOfAttribute"/> on member entries from
    /// <paramref name="groupObjectClass"/> groups' <paramref name="memberAttribute"/>.
    /// </summary>
    /// <param name="groupObjectClass">Group objectClass holding the member attribute (e.g. "groupOfNames").</param>
    /// <param name="memberAttribute">Membership attribute on the group (e.g. "member").</param>
    /// <param name="memberOfAttribute">Reverse attribute written on members. Default "memberOf".</param>
    /// <param name="referentialIntegrity">Keep memberOf consistent on member rename/delete. Default true.</param>
    /// <param name="dangling">How to treat members that don't resolve. Default <see cref="OpenLdapMemberOfDanglingPolicy.Ignore"/>.</param>
    public static OpenLdapOverlay MemberOf(
        string groupObjectClass,
        string memberAttribute,
        string memberOfAttribute = "memberOf",
        bool referentialIntegrity = true,
        OpenLdapMemberOfDanglingPolicy dangling = OpenLdapMemberOfDanglingPolicy.Ignore)
    {
        RequireLdapToken(groupObjectClass, nameof(groupObjectClass));
        RequireLdapToken(memberAttribute, nameof(memberAttribute));
        RequireLdapToken(memberOfAttribute, nameof(memberOfAttribute));
        var danglingValue = dangling switch
        {
            OpenLdapMemberOfDanglingPolicy.Ignore => "ignore",
            OpenLdapMemberOfDanglingPolicy.Drop => "drop",
            OpenLdapMemberOfDanglingPolicy.Error => "error",
            // Unreachable via the named constants; guards casts like (OpenLdapMemberOfDanglingPolicy)7.
            _ => throw new ArgumentOutOfRangeException(nameof(dangling), dangling, "Unknown dangling policy."),
        };

        return new()
        {
            Name = "memberof",
            ModuleLoads = ["memberof.so"],
            OverlayObjectClass = "olcMemberOf",
            Attributes =
            [
                new("olcMemberOfGroupOC", groupObjectClass),
                new("olcMemberOfMemberAD", memberAttribute),
                new("olcMemberOfMemberOfAD", memberOfAttribute),
                new("olcMemberOfDangling", danglingValue),
                new("olcMemberOfRefInt", referentialIntegrity ? "TRUE" : "FALSE"),
            ],
        };
    }

    /// <summary>
    /// The <c>syncprov</c> overlay (slapo-syncprov): makes the server an RFC 4533 sync provider,
    /// so any RFC 4533 client (<c>ldapsearch -E sync=rp</c> is the verified baseline) receives
    /// change notifications. Prefer the <c>WithChangeNotifications(...)</c> extension, which
    /// delegates here.
    /// </summary>
    /// <param name="checkpoint">
    /// <c>olcSpCheckpoint</c> as <c>"&lt;ops&gt; &lt;minutes&gt;"</c>; both values must be positive
    /// integers. Default <c>"1 1"</c>, the measured dev-right value: it keeps <c>contextCSN</c>
    /// durable across unclean container stops, where the production-style <c>"100 10"</c>
    /// regressed the CSN by minutes and made resuming clients replay seen changes.
    /// </param>
    /// <param name="sessionLog">
    /// <c>olcSpSessionLog</c> size. Must be at least 1. Default 100: gives delta deletes on a
    /// cookie-resumed refresh; without a session log the server falls back to present mode and
    /// the client diffs the whole directory per reconnect.
    /// </param>
    public static OpenLdapOverlay SyncProv(string checkpoint = "1 1", int sessionLog = 100)
    {
        ValidateCheckpoint(checkpoint, nameof(checkpoint));
        ArgumentOutOfRangeException.ThrowIfLessThan(sessionLog, 1);

        return new()
        {
            Name = "syncprov",
            ModuleLoads = ["syncprov.so"],
            OverlayObjectClass = "olcSyncProvConfig",
            Attributes =
            [
                new("olcSpCheckpoint", checkpoint),
                new("olcSpSessionLog", sessionLog.ToString(CultureInfo.InvariantCulture)),
            ],
        };
    }

    /// <summary>
    /// A zero-minute checkpoint (<c>"N 0"</c>) is not caught by the generic descriptor
    /// validation — the value is structurally clean LDIF — but slapd rejects it while applying
    /// the overlay, which kills container bootstrap with exit code 80 and an error against
    /// generated LDIF the user never wrote. Reject it (and any non-<c>"&lt;ops&gt; &lt;minutes&gt;"</c>
    /// shape) here, at the .NET call, with an attributable message.
    /// </summary>
    private static void ValidateCheckpoint(string checkpoint, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint, paramName);
        var parts = checkpoint.Split(' ');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ops)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || ops < 1 || minutes < 1)
        {
            throw new ArgumentException(
                $"Checkpoint '{checkpoint}' must be \"<ops> <minutes>\" with both values positive " +
                "integers (e.g. \"1 1\"). In particular slapd rejects a zero-minute checkpoint " +
                "while the container bootstraps (exit code 80), so it is rejected here instead.",
                paramName);
        }
    }

    /// <summary>
    /// Validates the whole declaration at the fluent call so a bad overlay fails at AppHost
    /// model construction with an attributable error instead of during container bootstrap,
    /// where slapadd reports it against generated LDIF the user never wrote. Covers custom
    /// overlays built with an object initializer, which bypass the validated factories.
    /// (The list properties are snapshotted on init, so what is validated here is exactly
    /// what LDIF generation later reads.)
    /// </summary>
    internal void Validate()
    {
        RequireDescriptorProperty(Name, "Name");
        RequireDescriptorProperty(OverlayObjectClass, "OverlayObjectClass");
        foreach (var module in ModuleLoads)
        {
            if (module is null || !IsModuleName(module))
            {
                throw InvalidDeclaration(
                    $"ModuleLoads entry '{module}' must be a module file name (letters, digits, '.', '_', '-')");
            }
        }
        foreach (var attribute in Attributes)
        {
            RequireDescriptorProperty(attribute.Key, "attribute name");
            if (attribute.Value is null)
            {
                throw InvalidDeclaration($"attribute '{attribute.Key}' must not have a null value");
            }
        }
    }

    private void RequireDescriptorProperty(string? value, string what)
    {
        if (value is null || !IsLdapDescriptor(value))
        {
            throw InvalidDeclaration(
                $"{what} '{value}' must be an LDAP descriptor (leading letter then letters/digits/'-') or a numeric OID");
        }
    }

    private DistributedApplicationException InvalidDeclaration(string reason) =>
        new($"Invalid overlay declaration '{Name}': {reason}.");

    /// <summary>
    /// Overlay names, objectClasses, and attribute names are consumed by slapd as RFC 4512
    /// descriptors — <c>keystring = leadkeychar *keychar</c> (leading ALPHA, then
    /// ALPHA/DIGIT/HYPHEN) — or numeric OIDs. Anything looser dies inside the container:
    /// either slapadd rejects the generated cn=config LDIF with an error against text the
    /// user never wrote, or (for the overlay name, which is spliced into a DN) a character
    /// like ',' silently restructures the DN. Derive the rule from what the consumer
    /// enforces instead of merely banning whitespace.
    /// </summary>
    private static bool IsLdapDescriptor(string value) =>
        (value.Length > 0 && char.IsAsciiLetter(value[0])
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        || IsNumericOid(value);

    private static bool IsNumericOid(string value)
    {
        var parts = value.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    /// <summary>Module loads are library file names (e.g. "memberof.so", "refint.la").</summary>
    private static bool IsModuleName(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static void RequireLdapToken(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (!IsLdapDescriptor(value))
        {
            throw new ArgumentException(
                $"Value '{value}' must be an LDAP descriptor (leading letter then letters/digits/'-') or a numeric OID.",
                paramName);
        }
    }

    /// <summary>Builds this overlay's <c>cn=config</c> entry against the given database DN.</summary>
    internal LdifContentRecord ToOverlayEntry(string databaseDn)
    {
        var attributes = new List<LdifAttribute>
        {
            new("objectClass", "olcOverlayConfig", OverlayObjectClass),
            new("olcOverlay", Name),
        };
        foreach (var attr in Attributes)
        {
            attributes.Add(new(attr.Key, attr.Value));
        }
        return new LdifContentRecord($"olcOverlay={Name},{databaseDn}", attributes);
    }
}

using System.Text;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A typed OpenLDAP overlay declaration. Overlays are opt-in: declare them with
/// <c>WithOverlay(...)</c> and the resource emits the corresponding <c>cn=config</c> entries
/// (module load + overlay config) into the slapd bootstrap before the data load.
///
/// Construct via the factory methods (e.g. <see cref="MemberOf"/>); add more factories as
/// other overlays are needed (refint, unique, ppolicy, …) without changing the wiring.
/// </summary>
public sealed class OpenLdapOverlay
{
    /// <summary>Overlay name as used in <c>olcOverlay: &lt;name&gt;</c> (e.g. "memberof").</summary>
    public required string Name { get; init; }

    /// <summary>Modules to load for this overlay (e.g. "memberof.so").</summary>
    public IReadOnlyList<string> ModuleLoads { get; init; } = [];

    /// <summary>The overlay's config objectClass (e.g. "olcMemberOf").</summary>
    public required string OverlayObjectClass { get; init; }

    /// <summary>Ordered <c>olc*</c> attributes for the overlay entry.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];

    /// <summary>
    /// The <c>memberof</c> overlay (slapo-memberof): maintains a reverse-membership
    /// <paramref name="memberOfAttribute"/> on member entries from
    /// <paramref name="groupObjectClass"/> groups' <paramref name="memberAttribute"/>.
    /// </summary>
    /// <param name="groupObjectClass">Group objectClass holding the member attribute (e.g. "groupOfNames").</param>
    /// <param name="memberAttribute">Membership attribute on the group (e.g. "member").</param>
    /// <param name="memberOfAttribute">Reverse attribute written on members. Default "memberOf".</param>
    /// <param name="referentialIntegrity">Keep memberOf consistent on member rename/delete. Default true.</param>
    /// <param name="dangling">How to treat members that don't resolve: "ignore" | "drop" | "error". Default "ignore".</param>
    public static OpenLdapOverlay MemberOf(
        string groupObjectClass,
        string memberAttribute,
        string memberOfAttribute = "memberOf",
        bool referentialIntegrity = true,
        string dangling = "ignore") => new()
    {
        Name = "memberof",
        ModuleLoads = ["memberof.so"],
        OverlayObjectClass = "olcMemberOf",
        Attributes =
        [
            new("olcMemberOfGroupOC", groupObjectClass),
            new("olcMemberOfMemberAD", memberAttribute),
            new("olcMemberOfMemberOfAD", memberOfAttribute),
            new("olcMemberOfDangling", dangling),
            new("olcMemberOfRefInt", referentialIntegrity ? "TRUE" : "FALSE"),
        ],
    };

    /// <summary>Renders this overlay's <c>cn=config</c> entry against the given database DN.</summary>
    internal string ToOverlayEntryLdif(string databaseDn)
    {
        var sb = new StringBuilder();
        sb.Append("dn: olcOverlay=").Append(Name).Append(',').AppendLine(databaseDn);
        sb.AppendLine("objectClass: olcOverlayConfig");
        sb.Append("objectClass: ").AppendLine(OverlayObjectClass);
        sb.Append("olcOverlay: ").AppendLine(Name);
        foreach (var attr in Attributes)
        {
            sb.Append(attr.Key).Append(": ").AppendLine(attr.Value);
        }
        return sb.ToString();
    }
}

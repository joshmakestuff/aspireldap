namespace Aspire.LdapAdmin.Web;

/// <summary>
/// The admin host's defaulted behavior, bound at startup from the <c>LdapAdmin</c>
/// configuration section — the <c>LdapAdmin__*</c> environment contract that
/// <c>WithLdapAdmin(Action&lt;LdapAdminOptions&gt;)</c> emits (the dev AppHost mirrors it).
/// Defaults here equal the hosting-side defaults, so a host started without the variables
/// (an older hosting package, a bare launch) behaves identically to one given the defaults
/// explicitly. Deliberately not user-editable: defaults are AppHost-set, and the UI grows no
/// settings pages (docs/decisions.md).
/// </summary>
public sealed class LdapAdminSettings
{
    /// <summary>The configuration section the settings bind from.</summary>
    public const string SectionName = "LdapAdmin";

    /// <summary>The UI theme; System follows the browser's color-scheme preference.</summary>
    public LdapAdminTheme Theme { get; set; } = LdapAdminTheme.System;

    /// <summary>The search page's initial size limit.</summary>
    public int DefaultSearchLimit { get; set; } = 100;

    /// <summary>What browse children and search results sort by, by default.</summary>
    public LdapAdminSortOrder DefaultSortOrder { get; set; } = LdapAdminSortOrder.ServerOrder;

    /// <summary>
    /// How many values of a many-valued attribute the entry view renders before capping —
    /// always surfaced as "N of M values" with an explicit expand, never silent.
    /// </summary>
    public int AttributeValueDisplayCap { get; set; } = 20;
}

/// <summary>
/// Mirror of the hosting library's <c>LdapAdminTheme</c>; member names are the env contract
/// (values travel as enum names) and are pinned by the hosting model tests.
/// </summary>
public enum LdapAdminTheme
{
    /// <summary>Follow the browser's color-scheme preference (the default).</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// Mirror of the hosting library's <c>LdapAdminSortOrder</c>; member names are the env
/// contract and are pinned by the hosting model tests.
/// </summary>
public enum LdapAdminSortOrder
{
    /// <summary>Entries appear in the order the server returned them (the default).</summary>
    ServerOrder,

    /// <summary>Entries sort by their relative distinguished name, ascending, case-insensitive.</summary>
    Rdn,
}

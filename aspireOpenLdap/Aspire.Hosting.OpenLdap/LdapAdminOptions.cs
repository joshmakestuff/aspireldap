namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defaulted behavior of the bundled LdapAdmin web UI, set from the AppHost via
/// <c>WithLdapAdmin(Action&lt;LdapAdminOptions&gt;)</c>. Every member has a sane default, so the
/// options object is never required; values flow to the admin container as
/// <c>LdapAdmin__*</c> environment configuration and are bound by the admin host at startup.
/// </summary>
/// <remarks>
/// These are dev-AppHost-set defaults, not end-user chrome — the admin UI deliberately grows no
/// settings pages (docs/decisions.md). This object is the single home for future defaulted
/// behavior; new knobs join it instead of becoming ad-hoc <c>WithLdapAdmin</c> parameters.
/// </remarks>
public sealed class LdapAdminOptions
{
    /// <summary>
    /// The admin UI theme. <see cref="LdapAdminTheme.System"/> (the default) follows the
    /// browser's color-scheme preference; there is no in-app theme chooser.
    /// </summary>
    public LdapAdminTheme Theme { get; set; } = LdapAdminTheme.System;

    /// <summary>
    /// The search page's initial size limit. Must be between 1 and 1000 (the range the search
    /// page itself accepts). Defaults to 100.
    /// </summary>
    public int DefaultSearchLimit { get; set; } = 100;

    /// <summary>
    /// What the browse tree's children and the search results sort by, by default.
    /// Defaults to <see cref="LdapAdminSortOrder.ServerOrder"/>.
    /// </summary>
    public LdapAdminSortOrder DefaultSortOrder { get; set; } = LdapAdminSortOrder.ServerOrder;

    /// <summary>
    /// How many values of a many-valued attribute the entry view renders before capping. The
    /// cap is always surfaced ("N of M values" plus an explicit expand), never silent. Must be
    /// at least 1. Defaults to 20.
    /// </summary>
    public int AttributeValueDisplayCap { get; set; } = 20;
}

/// <summary>The LdapAdmin UI theme, fixed by the AppHost — there is no in-app chooser.</summary>
public enum LdapAdminTheme
{
    /// <summary>Follow the browser's color-scheme preference (the default).</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>The default ordering of browse children and search results in the LdapAdmin UI.</summary>
public enum LdapAdminSortOrder
{
    /// <summary>Entries appear in the order the server returned them (the default).</summary>
    ServerOrder,

    /// <summary>Entries sort by their relative distinguished name, ascending, case-insensitive.</summary>
    Rdn,
}

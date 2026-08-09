using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

// Change notifications: server-side enablement of the RFC 4533 sync provider (syncprov overlay).
// Deliberately no first-party subscribe API — see the WithChangeNotifications remarks and
// aspireldap#88. (Class-level XML docs live on the main partial.)
public static partial class OpenLdapResourceBuilderExtensions
{
    /// <summary>
    /// Enables LDAP change notifications: loads the <c>syncprov</c> overlay so the server is an
    /// RFC 4533 sync provider (syncrepl / refreshAndPersist). Point any RFC 4533 client at the
    /// resource's endpoint — the verified CLI baseline is <c>ldapsearch -E sync=rp</c>.
    /// Equivalent to <c>WithOverlay(OpenLdapOverlay.SyncProv(...))</c>; call at most one of the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is server-side enablement only. There is deliberately no first-party subscribe API:
    /// <c>System.DirectoryServices.Protocols</c> (the client stack both packages build on) cannot
    /// implement RFC 4533 — per-entry SyncState controls are dropped on every platform and the
    /// persist stage never starts on Linux — and adopting a second LDAP client stack was
    /// rejected. Decided in <see href="https://github.com/joshmakestuff/aspireldap/issues/88">aspireldap#88</see>.
    /// </para>
    /// <para>
    /// Like every overlay this is part of the seed-once bootstrap: enabling it on an
    /// already-seeded data volume requires resetting the volume. Arguments are validated here, at
    /// model construction — notably a zero-minute <paramref name="checkpoint"/>, which slapd
    /// would otherwise reject during container bootstrap with exit code 80.
    /// </para>
    /// </remarks>
    /// <param name="builder">The OpenLDAP resource builder.</param>
    /// <param name="checkpoint">
    /// <c>olcSpCheckpoint</c> as <c>"&lt;ops&gt; &lt;minutes&gt;"</c>; both values must be positive
    /// integers. Default <c>"1 1"</c> keeps <c>contextCSN</c> durable across unclean container
    /// stops (the production-style <c>"100 10"</c> regressed it by minutes after a SIGKILL,
    /// making resuming clients replay seen changes). See <see cref="OpenLdapOverlay.SyncProv"/>.
    /// </param>
    /// <param name="sessionLog">
    /// <c>olcSpSessionLog</c> size, at least 1. Default 100 gives delta deletes on a
    /// cookie-resumed refresh instead of a full present-mode directory diff.
    /// </param>
    public static IResourceBuilder<OpenLdapResource> WithChangeNotifications(
        this IResourceBuilder<OpenLdapResource> builder,
        string checkpoint = "1 1",
        int sessionLog = 100)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithOverlay(OpenLdapOverlay.SyncProv(checkpoint, sessionLog));
    }
}

using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Aspire.OpenLdap;

/// <summary>
/// Makes <c>System.DirectoryServices.Protocols</c> load on Linux distros that ship OpenLDAP 2.6+.
/// The runtime hardcodes a load of <c>libldap-2.5.so.0</c> — still true on .NET 10
/// (dotnet/runtime#123676) — but Ubuntu 24.04+, Fedora, and Alpine 3.20+ ship the upstream
/// soname <c>libldap.so.2</c>, so every LDAP call fails with <c>DllNotFoundException</c>
/// unless the user hand-creates a symlink. This resolver intercepts the import and probes
/// the sonames distros actually ship.
/// </summary>
/// <remarks>
/// This file is compiled into both the hosting and the client assembly. Whichever copy
/// registers first wins; the loser's <see cref="InvalidOperationException"/> is swallowed —
/// both copies resolve identically, so it doesn't matter which one is active.
/// </remarks>
internal static class OpenLdapNativeLibraryResolver
{
    // Probe order: the exact soname the runtime asks for first (honors an existing install
    // or a user-made symlink), then the upstream soname OpenLDAP has used since 2.5
    // (verified shipped by Ubuntu 24.04, Fedora 41, and Alpine 3.20), then legacy Debian 11 /
    // Ubuntu 20.04. No liblber entry is needed: S.DS.P declares all its P/Invokes — including
    // the ber_* functions — against the single libldap image, and liblber resolves through
    // libldap's own dependency chain.
    private static readonly string[] s_libldapCandidates =
    [
        "libldap-2.5.so.0",
        "libldap.so.2",
        "libldap-2.6.so.0",
        "libldap-2.4.so.2",
    ];

    private static int s_registered;

    /// <summary>
    /// Registers the resolver on the <c>System.DirectoryServices.Protocols</c> assembly.
    /// No-op on non-Linux platforms and on repeated calls. Safe to call from any Add*
    /// registration method; must run before the first LDAP P/Invoke to take effect.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux() || Interlocked.Exchange(ref s_registered, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LdapConnection).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for S.DS.P — the companion AspireLdap package
            // in the same process, or the app itself. Leave it in place.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.StartsWith("libldap", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in s_libldapCandidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        // Nothing found: fall through to the runtime's default probing so the standard
        // "Unable to load shared library 'libldap-2.5.so.0'" error still surfaces.
        return IntPtr.Zero;
    }
}

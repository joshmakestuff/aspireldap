using Aspire.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

public class OpenLdapNativeLibraryResolverTests
{
    [Fact]
    public void EnsureRegistered_Is_Idempotent_And_Does_Not_Throw()
    {
        // No-op on Windows/macOS; on Linux the first call registers the resolver on the
        // S.DS.P assembly and the second must be swallowed (both the repeat-call guard and
        // the InvalidOperationException from a competing registration are exercised across
        // the two assemblies that compile this shared file).
        OpenLdapNativeLibraryResolver.EnsureRegistered();
        OpenLdapNativeLibraryResolver.EnsureRegistered();
    }
}

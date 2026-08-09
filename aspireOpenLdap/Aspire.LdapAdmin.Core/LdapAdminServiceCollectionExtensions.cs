using Aspire.LdapAdmin.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the LdapAdmin service layer.</summary>
public static class LdapAdminServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="LdapDirectoryService"/> and <see cref="LdapSchemaService"/>. Both are
    /// singletons: they hold no per-operation state and take a fresh
    /// <c>OpenLdapClient</c> per call, and the schema cache is worth sharing across the app.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddOpenLdapClient</c> (or <c>AddKeyedOpenLdapClient</c> resolved to the
    /// non-keyed factory) to have registered <c>OpenLdapClientFactory</c>; the connection —
    /// endpoint, base DN, and the credentials every operation binds with — comes from the
    /// AppHost's connection string, never from a login.
    /// </remarks>
    public static IServiceCollection AddLdapAdminCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LdapSchemaService>();
        services.TryAddSingleton<LdapDirectoryService>();
        return services;
    }
}

using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

/// <summary>A naming context advertised by the server, not a promise of read access.</summary>
public sealed record LdapServerContext(string Dn, string Kind);

/// <summary>Discovers server contexts using the same identity as the directory service.</summary>
public sealed class LdapServerContextService(OpenLdapClientFactory factory)
{
    public async Task<IReadOnlyList<LdapServerContext>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateClient();
        var response = (SearchResponse)await client.SendAsync(new SearchRequest(
            "", "(objectClass=*)", SearchScope.Base, "namingContexts", "configContext", "monitorContext"), cancellationToken).ConfigureAwait(false);
        List<LdapServerContext> contexts = [];
        foreach (SearchResultEntry entry in response.Entries)
        {
            foreach (var (attribute, kind) in new[] { ("namingContexts", "Directory"), ("configContext", "Configuration"), ("monitorContext", "Monitor") })
            {
                if (entry.Attributes[attribute] is not { } values)
                {
                    continue;
                }
                foreach (string value in values.GetValues(typeof(string)))
                {
                    // Use the DN parser to reject an unusable server advertisement before
                    // presenting it as a navigable root. One rejected value must not hide the
                    // others. Do not invent unadvertised roots.
                    if (IsNavigableDn(value) && !contexts.Any(context => DnEquality.AreEquivalent(context.Dn, value)))
                    {
                        contexts.Add(new LdapServerContext(value, kind));
                    }
                }
            }
        }
        return contexts;
    }

    private static bool IsNavigableDn(string value)
    {
        try
        {
            return Dn.Parse(value).Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

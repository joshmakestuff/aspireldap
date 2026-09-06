using LdifDotNet;

namespace Aspire.LdapAdmin.Core;

/// <summary>Rejects unresolved values before LDIF comparison or mutation.</summary>
internal static class LdifInputValidation
{
    internal static string? UnresolvedUrlError(LdifRecord record)
    {
        IEnumerable<(string Name, IReadOnlyList<LdifValue> Values)> attributes = record switch
        {
            LdifContentRecord content => content.Attributes.Select(static attribute => (attribute.Name, attribute.Values)),
            LdifAddRecord add => add.Attributes.Select(static attribute => (attribute.Name, attribute.Values)),
            LdifModifyRecord modify => modify.Modifications.Select(static modification => (modification.AttributeName, modification.Values)),
            _ => [],
        };

        foreach (var (name, values) in attributes)
        {
            if (values.Any(static value => value.IsUrl))
            {
                return $"{record.Dn}: attribute '{name}' contains an unresolved LDIF URL value. Supply inline text or base64 instead.";
            }
        }
        return null;
    }
}

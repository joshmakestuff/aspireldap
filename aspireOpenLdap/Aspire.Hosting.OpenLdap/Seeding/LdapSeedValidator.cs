using System.Text.RegularExpressions;

namespace Aspire.Hosting.ApplicationModel.Seeding;

internal static partial class LdapSeedValidator
{
    // Restricted to characters that need no LDAP DN escaping. Keeps generated LDIF
    // safe without needing a full RFC 4514 escaper for now.
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeNameRegex();

    public static void Validate(OpenLdapResource resource, LdapSeedModel model)
    {
        var ouSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ou in model.OrganizationalUnits)
        {
            RequireSafeName(ou.Name, "organizational unit");
            if (!ouSet.Add(ou.Name))
            {
                throw new DistributedApplicationException(
                    $"OpenLDAP resource '{resource.Name}' declares the organizational unit '{ou.Name}' more than once.");
            }
        }

        foreach (var user in model.Users)
        {
            RequireSafeName(user.Uid, "user uid");
            if (string.IsNullOrEmpty(user.Password))
            {
                throw new DistributedApplicationException(
                    $"User '{user.Uid}' on OpenLDAP resource '{resource.Name}' has an empty password.");
            }
            if (!uidSet.Add(user.Uid))
            {
                throw new DistributedApplicationException(
                    $"OpenLDAP resource '{resource.Name}' declares the user uid '{user.Uid}' more than once.");
            }
            if (user.OrganizationalUnit is { } ou && !ouSet.Contains(ou))
            {
                throw new DistributedApplicationException(
                    $"User '{user.Uid}' on OpenLDAP resource '{resource.Name}' references undeclared organizational unit '{ou}'. " +
                    SuggestionOrDeclareHint(ou, ouSet, $".WithOrganizationalUnit(\"{ou}\")"));
            }
        }

        foreach (var group in model.Groups)
        {
            RequireSafeName(group.Cn, "group cn");
            if (!groupSet.Add(group.Cn))
            {
                throw new DistributedApplicationException(
                    $"OpenLDAP resource '{resource.Name}' declares the group cn '{group.Cn}' more than once.");
            }
            if (group.OrganizationalUnit is { } ou && !ouSet.Contains(ou))
            {
                throw new DistributedApplicationException(
                    $"Group '{group.Cn}' on OpenLDAP resource '{resource.Name}' references undeclared organizational unit '{ou}'. " +
                    SuggestionOrDeclareHint(ou, ouSet, $".WithOrganizationalUnit(\"{ou}\")"));
            }
            if (group.Members.Count == 0)
            {
                throw new DistributedApplicationException(
                    $"Group '{group.Cn}' on OpenLDAP resource '{resource.Name}' must declare at least one member " +
                    "(LDAP groupOfNames requires a non-empty 'member' attribute).");
            }
            foreach (var member in group.Members)
            {
                // A member with '=' is treated as a literal DN; otherwise it must match a declared uid.
                if (member.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }
                if (!uidSet.Contains(member))
                {
                    throw new DistributedApplicationException(
                        $"Group '{group.Cn}' on OpenLDAP resource '{resource.Name}' references undeclared user uid '{member}'. " +
                        SuggestionOrDeclareHint(member, uidSet, $".WithUser(\"{member}\", ...)"));
                }
            }
        }
    }

    private static void RequireSafeName(string name, string label)
    {
        if (!SafeNameRegex().IsMatch(name))
        {
            throw new DistributedApplicationException(
                $"Invalid {label} '{name}': must match [A-Za-z0-9._-]+ (no spaces or LDAP-special characters).");
        }
    }

    private static string SuggestionOrDeclareHint(string unknown, IReadOnlyCollection<string> known, string declareSyntax)
    {
        if (known.Count == 0)
        {
            return $"Declare it with {declareSyntax}.";
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var d = LevenshteinDistance(candidate, unknown);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        var threshold = Math.Max(2, unknown.Length / 3);
        return bestDistance <= threshold
            ? $"Did you mean \"{best}\"? Otherwise declare it with {declareSyntax}."
            : $"Declared: [{string.Join(", ", known)}]. Or declare it with {declareSyntax}.";
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }
}
